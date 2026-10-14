using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using System.Drawing;
using System.Windows.Forms;

internal static class Program
{
    private const int HookKeyboardLowLevel = 13;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int SysKeyDown = 0x0104;
    private const int SysKeyUp = 0x0105;
    private static readonly ConcurrentQueue<KeySignal> KeySignals = new();
    private static readonly AutoResetEvent KeySignalReady = new(false);
    private static readonly object TestLock = new();
    private static readonly object ProgressLock = new();
    private static readonly object ServerLock = new();
    private static readonly List<Sample> ProgressSamples = [];
    private static readonly HookProc KeyboardProc = KeyboardHook;
    private static CancellationTokenSource? currentTest;
    private static HttpListener? listener;
    private static bool progressRunning;
    private static int progressRound;
    private static int progressCdcDelay;

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\iTLUBS-RabbitBridge", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("RabbitBridge가 이미 실행 중입니다.", "RabbitBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        var hook = SetWindowsHookEx(HookKeyboardLowLevel, KeyboardProc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) return;
        Application.Run(new BridgeWindow());
        StopServer();
        UnhookWindowsHookEx(hook);
    }

    internal static bool IsServerRunning
    {
        get { lock (ServerLock) return listener?.IsListening == true; }
    }

    internal static bool StartServer()
    {
        lock (ServerLock)
        {
            if (listener?.IsListening == true) return true;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:53941/");
                listener.Start();
                _ = Task.Run(() => RunServer(listener));
                return true;
            }
            catch
            {
                listener?.Close();
                listener = null;
                return false;
            }
        }
    }

    internal static void StopServer()
    {
        currentTest?.Cancel();
        lock (ServerLock)
        {
            listener?.Stop();
            listener?.Close();
            listener = null;
        }
    }

    private static async Task RunServer(HttpListener server)
    {
        try
        {
            while (server.IsListening)
            {
                var context = await server.GetContextAsync();
                _ = Task.Run(() => Handle(context));
            }
        }
        catch (HttpListenerException) when (!server.IsListening) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task Handle(HttpListenerContext context)
    {
        AddCors(context.Response);
        try
        {
            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
            }
            else if (context.Request.Url?.AbsolutePath == "/health")
            {
                await WriteJson(context.Response, new { ok = true, device = "TAB Rabbit", method = "getasynckeystate-qpc-v1", version = "1.0.0" });
            }
            else if (context.Request.Url?.AbsolutePath == "/stop")
            {
                currentTest?.Cancel();
                await WriteJson(context.Response, new { ok = true });
            }
            else if (context.Request.Url?.AbsolutePath == "/status")
            {
                object status;
                lock (ProgressLock)
                    status = new { ok = true, running = progressRunning, round = progressRound, cdcDelay = progressCdcDelay, samples = ProgressSamples.ToArray() };
                await WriteJson(context.Response, status);
            }
            else if (context.Request.Url?.AbsolutePath == "/start")
            {
                if (!Monitor.TryEnter(TestLock)) throw new InvalidOperationException("이미 측정 중입니다.");
                try
                {
                    var query = context.Request.QueryString;
                    var options = new TestOptions(
                        query["port"] ?? "COM5",
                        string.Equals(query["mode"], "he", StringComparison.OrdinalIgnoreCase),
                        ReadInt(query["rounds"], 1, 20, 4),
                        ReadInt(query["times"], 1, 500, 100),
                        ReadInt(query["interval"], 0, 60, 2));
                    lock (ProgressLock) { ProgressSamples.Clear(); progressRunning = true; progressRound = 0; progressCdcDelay = 0; }
                    currentTest = new CancellationTokenSource();
                    var result = RunTest(options, currentTest.Token);
                    await WriteJson(context.Response, result);
                }
                finally
                {
                    lock (ProgressLock) progressRunning = false;
                    currentTest?.Dispose();
                    currentTest = null;
                    Monitor.Exit(TestLock);
                }
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
        catch (OperationCanceledException)
        {
            context.Response.StatusCode = 409;
            await WriteJson(context.Response, new { ok = false, error = "측정이 중지되었습니다." });
        }
        catch (Exception error)
        {
            context.Response.StatusCode = 500;
            await WriteJson(context.Response, new { ok = false, error = error.Message });
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static TestResult RunTest(TestOptions options, CancellationToken token)
    {
        using var serial = new RabbitSerial(options.Port);
        var samples = new List<Sample>(options.Rounds * options.Times * 2);
        var cdcDelays = new List<int>(options.Rounds);
        var started = Stopwatch.GetTimestamp();
        var keyCode = DetectKey(serial, options.Hall, token);

        for (var round = 0; round < options.Rounds; round++)
        {
            token.ThrowIfCancellationRequested();
            var cdcDelay = MeasureCdcDelay(serial, token);
            cdcDelays.Add(cdcDelay);
            lock (ProgressLock) { progressRound = round + 1; progressCdcDelay = cdcDelay; }

            for (var index = -10; index < options.Times; index++)
            {
                token.ThrowIfCancellationRequested();
                var press = MeasureEdge(serial, options.Hall, true, cdcDelay, keyCode, token);
                Thread.Sleep(10);
                var release = MeasureEdge(serial, options.Hall, false, cdcDelay, keyCode, token);
                if (index >= 0)
                {
                    var pressSample = new Sample("press", samples.Count + 1, round + 1, index + 1, press.LatencyUs, press.RawLatencyUs, press.CorrectionUs);
                    var releaseSample = new Sample("release", samples.Count + 2, round + 1, index + 1, release.LatencyUs, release.RawLatencyUs, release.CorrectionUs);
                    samples.Add(pressSample);
                    samples.Add(releaseSample);
                    lock (ProgressLock) { ProgressSamples.Add(pressSample); ProgressSamples.Add(releaseSample); }
                }
                Thread.Sleep(80);
            }

            if (round + 1 < options.Rounds && options.IntervalSeconds > 0)
                Thread.Sleep(TimeSpan.FromSeconds(options.IntervalSeconds));
        }

        return new TestResult(true, "getasynckeystate-qpc-v1", "1.0.0", keyCode, ElapsedMicroseconds(started), cdcDelays, samples);
    }

    private static int MeasureCdcDelay(RabbitSerial serial, CancellationToken token)
    {
        long total = 0;
        for (var index = 0; index < 10; index++)
        {
            token.ThrowIfCancellationRequested();
            for (var attempt = 0; ; attempt++)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    serial.Command(0xB6, 0x01);
                    total += ElapsedMicroseconds(started);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    serial.ResetInput();
                }
            }
        }
        return (int)(total / 10);
    }

    private static uint DetectKey(RabbitSerial serial, bool hall, CancellationToken token)
    {
        while (KeySignals.TryDequeue(out _)) { }
        if (hall) serial.Command(0xB7, 0x1F);
        else serial.Command(0xB2, 0);
        var keyCode = WaitForEdge(true, 0, token).KeyCode;
        Thread.Sleep(10);
        if (hall) serial.Command(0xB7, 0x00);
        else serial.Command(0xB3, 0);
        WaitForEdge(false, keyCode, token);
        Thread.Sleep(80);
        return keyCode;
    }

    private static EdgeMeasurement MeasureEdge(RabbitSerial serial, bool hall, bool down, int cdcDelay, uint keyCode, CancellationToken token)
    {
        GetAsyncKeyState((int)keyCode);
        var started = Stopwatch.GetTimestamp();
        if (hall) serial.Command(0xB7, down ? (byte)0x1F : (byte)0x00);
        else serial.Command(down ? (byte)0xB2 : (byte)0xB3, 0);

        WaitForKeyState(down, keyCode, token);
        var rawLatency = ElapsedMicroseconds(started);
        var latency = Math.Max(0, rawLatency - cdcDelay);
        if (hall && latency > 400) latency -= 400;
        else if (!hall && latency > 200) latency -= 100;
        var value = (int)Math.Min(int.MaxValue, latency);
        return new EdgeMeasurement(value, (int)Math.Min(int.MaxValue, rawLatency), (int)Math.Min(int.MaxValue, rawLatency - value));
    }

    private static void WaitForKeyState(bool down, uint keyCode, CancellationToken token)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            token.ThrowIfCancellationRequested();
            var pressed = GetAsyncKeyState((int)keyCode) != 0;
            if (pressed == down) return;
        }
        throw new TimeoutException(down ? "누르기 키 입력 시간 초과" : "떼기 키 입력 시간 초과");
    }

    private static KeySignal WaitForEdge(bool down, uint keyCode, CancellationToken token)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            token.ThrowIfCancellationRequested();
            while (KeySignals.TryDequeue(out var signal))
                if (signal.Down == down && (keyCode == 0 || signal.KeyCode == keyCode)) return signal;
            KeySignalReady.WaitOne(2);
        }
        throw new TimeoutException(down ? "누르기 키 입력 시간 초과" : "떼기 키 입력 시간 초과");
    }

    private static IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var id = message.ToInt32();
            if (id is KeyDown or KeyUp or SysKeyDown or SysKeyUp)
            {
                var keyboard = Marshal.PtrToStructure<LowLevelKeyboardInput>(data);
                KeySignals.Enqueue(new KeySignal(id is KeyDown or SysKeyDown, Stopwatch.GetTimestamp(), keyboard.VirtualKey));
                KeySignalReady.Set();
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private static int ReadInt(string? text, int min, int max, int fallback) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

    private static long ElapsedMicroseconds(long started) => ToMicroseconds(Stopwatch.GetTimestamp() - started);
    private static long ToMicroseconds(long ticks) => ticks * 1_000_000 / Stopwatch.Frequency;

    private static void AddCors(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    private static async Task WriteJson(HttpListenerResponse response, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private sealed class BridgeWindow : Form
    {
        private readonly Label stateLabel;
        private readonly Label detailLabel;
        private readonly Button toggleButton;
        private readonly System.Windows.Forms.Timer timer;

        public BridgeWindow()
        {
            Text = "iTLUBS RabbitBridge";
            ClientSize = new Size(420, 240);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.FromArgb(16, 19, 18);
            ForeColor = Color.FromArgb(240, 244, 238);
            Font = new Font("Segoe UI", 10);

            var title = new Label { Text = "RABBIT BRIDGE", AutoSize = true, Location = new Point(28, 24), ForeColor = Color.FromArgb(202, 255, 56), Font = new Font(Font, FontStyle.Bold) };
            stateLabel = new Label { AutoSize = true, Location = new Point(28, 58), Font = new Font("Segoe UI", 20, FontStyle.Bold) };
            detailLabel = new Label { AutoSize = false, Location = new Point(30, 104), Size = new Size(360, 52), ForeColor = Color.FromArgb(139, 151, 144) };
            toggleButton = new Button { Location = new Point(28, 174), Size = new Size(364, 42), FlatStyle = FlatStyle.Flat, Font = new Font(Font, FontStyle.Bold), Cursor = Cursors.Hand };
            toggleButton.Click += (_, _) => { if (IsServerRunning) StopServer(); else StartServer(); RefreshState(); };
            Controls.AddRange([title, stateLabel, detailLabel, toggleButton]);

            timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (_, _) => RefreshState();
            Shown += (_, _) => { StartServer(); timer.Start(); RefreshState(); };
            FormClosed += (_, _) => timer.Dispose();
        }

        private void RefreshState()
        {
            var online = IsServerRunning;
            int pairs;
            bool measuring;
            int round;
            lock (ProgressLock) { pairs = ProgressSamples.Count / 2; measuring = progressRunning; round = progressRound; }
            stateLabel.Text = online ? (measuring ? "MEASURING" : "ONLINE") : "OFFLINE";
            stateLabel.ForeColor = online ? Color.FromArgb(103, 229, 155) : Color.FromArgb(255, 112, 72);
            detailLabel.Text = online
                ? $"127.0.0.1:53941 · COM5\n{(measuring ? $"라운드 {round} · {pairs}쌍 수신" : "웹 측정 요청 대기 중")}"
                : "브리지가 꺼져 있습니다.\n측정하려면 아래 버튼을 눌러주세요.";
            toggleButton.Text = online ? "브리지 끄기" : "브리지 켜기";
            toggleButton.BackColor = online ? Color.FromArgb(255, 112, 72) : Color.FromArgb(202, 255, 56);
            toggleButton.ForeColor = Color.FromArgb(16, 19, 18);
            toggleButton.FlatAppearance.BorderColor = toggleButton.BackColor;
        }
    }

    private sealed class RabbitSerial : IDisposable
    {
        private readonly SafeFileHandle handle;

        public RabbitSerial(string port)
        {
            handle = CreateFile($@"\\.\{port}", 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException($"{port}를 열 수 없습니다. NeonTester와 브라우저의 직렬 연결을 종료하세요.");
            var dcb = new Dcb { Length = Marshal.SizeOf<Dcb>() };
            if (!GetCommState(handle, ref dcb)) throw new IOException("COM 설정을 읽을 수 없습니다.");
            dcb.BaudRate = 921600; dcb.ByteSize = 8; dcb.Parity = 0; dcb.StopBits = 0;
            if (!SetCommState(handle, ref dcb)) throw new IOException("COM 설정을 적용할 수 없습니다.");
            var timeouts = new CommTimeouts { ReadIntervalTimeout = 20, ReadTotalTimeoutConstant = 500, WriteTotalTimeoutConstant = 500 };
            if (!SetCommTimeouts(handle, ref timeouts)) throw new IOException("COM 시간 제한을 설정할 수 없습니다.");
            SetupComm(handle, 4096, 4096);
            PurgeComm(handle, 0x000F);
        }

        public void ResetInput() => PurgeComm(handle, 0x000A);

        public void Command(byte command, byte value)
        {
            var request = new byte[64]; request[0] = command; request[1] = value;
            if (!WriteFile(handle, request, request.Length, out var written, IntPtr.Zero) || written != request.Length)
                throw new IOException($"TAB Rabbit 명령 전송 실패: 0x{command:X2}");
            var response = new byte[64];
            var offset = 0;
            while (offset < response.Length)
            {
                var chunk = new byte[response.Length - offset];
                if (!ReadFile(handle, chunk, chunk.Length, out var read, IntPtr.Zero) || read == 0)
                    throw new IOException($"TAB Rabbit 응답 실패: 0x{command:X2}");
                Buffer.BlockCopy(chunk, 0, response, offset, read);
                offset += read;
            }
            if (response[0] != command) throw new IOException($"TAB Rabbit 응답 불일치: 0x{response[0]:X2} != 0x{command:X2}");
        }

        public void Dispose() => handle.Dispose();
    }

    private readonly record struct KeySignal(bool Down, long Timestamp, uint KeyCode);
    private readonly record struct TestOptions(string Port, bool Hall, int Rounds, int Times, int IntervalSeconds);
    private readonly record struct EdgeMeasurement(int LatencyUs, int RawLatencyUs, int CorrectionUs);
    private readonly record struct Sample(string Edge, int Report, int Round, int Index, int LatencyUs, int RawLatencyUs, int CorrectionUs);
    private readonly record struct TestResult(bool Ok, string Method, string Version, uint KeyCode, long ElapsedUs, List<int> CdcDelays, List<Sample> Samples);

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public IntPtr Hwnd; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput { public uint VirtualKey; public uint ScanCode; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public int Length;
        public int BaudRate;
        public uint Flags;
        public ushort Reserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public sbyte XonChar;
        public sbyte XoffChar;
        public sbyte ErrorChar;
        public sbyte EofChar;
        public sbyte EvtChar;
        public ushort Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetCommState(SafeFileHandle file, ref Dcb dcb);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetCommState(SafeFileHandle file, ref Dcb dcb);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetCommTimeouts(SafeFileHandle file, ref CommTimeouts timeouts);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetupComm(SafeFileHandle file, int input, int output);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PurgeComm(SafeFileHandle file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, int bytes, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int bytes, out int written, IntPtr overlapped);
}
