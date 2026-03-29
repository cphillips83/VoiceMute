using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceMute;

class Program
{
    // Win32 constants
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;
    const int VK_RCONTROL = 0xA3;

    // SendInput constants
    const uint INPUT_KEYBOARD = 1;
    const ushort KEYEVENTF_UNICODE = 0x0004;
    const ushort KEYEVENTF_KEYUP = 0x0002;

    // Win32 imports
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    const uint WM_QUIT = 0x0012;

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int x;
        public int y;
    }

    // State
    static IntPtr _hookId = IntPtr.Zero;
    static LowLevelKeyboardProc _proc = HookCallback; // prevent GC
    static CancellationTokenSource? _fadeCts;
    static bool _isMuted;
    static bool _keyHeld;
    static float _savedVolume;
    static MMDeviceEnumerator? _deviceEnumerator;
    static readonly object _lock = new();
    static int _holdDelayMs = 200;
    static int _fadeOutMs = 100;
    static int _fadeInMs = 250;
    static int _fadeSteps = 10;

    // STT recording
    static bool _sttEnabled;
    static bool _isRecording;
    static WasapiCapture? _capture;
    static MemoryStream? _audioBuffer;
    static WaveFormat? _captureFormat;
    static Timer? _holdTimer;
    static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    static string _whisperUrl = "http://127.0.0.1:2022/inference";
    static string? _whisperModel;
    static Process? _whisperProcess;
    static CancellationTokenSource? _broadcastCts;
    const int DISCOVERY_PORT = 5766;
    const string DISCOVERY_MAGIC = "VOICEMUTE_WHISPER_DISCOVER";

    static readonly HashSet<string> _terminalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal",
        "cmd",
        "powershell",
        "pwsh",
        "ConEmu",
        "ConEmuC",
        "ConEmuC64",
        "Hyper",
        "Alacritty",
        "wezterm-gui",
        "mintty",
        "GitBash",
    };

    static void Main(string[] args)
    {
        bool daemonMode = args.Contains("-d", StringComparer.OrdinalIgnoreCase);
        bool backgroundMode = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        bool killMode = args.Contains("--kill", StringComparer.OrdinalIgnoreCase);
        _sttEnabled = args.Contains("--stt", StringComparer.OrdinalIgnoreCase);

        var modelIdx = Array.FindIndex(args, a => a.Equals("--model", StringComparison.OrdinalIgnoreCase));
        if (modelIdx >= 0 && modelIdx + 1 < args.Length)
            _whisperModel = args[modelIdx + 1];

        var urlIdx = Array.FindIndex(args, a => a.Equals("--whisper-url", StringComparison.OrdinalIgnoreCase));
        if (urlIdx >= 0 && urlIdx + 1 < args.Length)
            _whisperUrl = args[urlIdx + 1];

        if (killMode)
        {
            KillExisting();
            Console.WriteLine("VoiceMute stopped.");
            return;
        }

        if (daemonMode)
        {
            KillExisting();

            var exe = Environment.ProcessPath!;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(" ", new[] {
                    "--background",
                    _sttEnabled ? "--stt" : null,
                    _whisperModel != null ? $"--model {_whisperModel}" : null,
                    _whisperUrl != "http://127.0.0.1:2022/inference" ? $"--whisper-url {_whisperUrl}" : null,
                }.Where(x => x != null)),
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
            };

            var proc = Process.Start(psi);
            Console.WriteLine($"VoiceMute running in background (PID {proc?.Id})");
            return;
        }

        if (!backgroundMode)
        {
            Console.WriteLine("=== VoiceMute ===");
            Console.WriteLine($"Hold Right Ctrl to: mute speakers{(_sttEnabled ? " + record mic + transcribe on release" : "")}");
            Console.WriteLine($"Hold delay: {_holdDelayMs}ms | Fade out: {_fadeOutMs}ms | Fade in: {_fadeInMs}ms");
            if (_sttEnabled)
                Console.WriteLine($"Whisper: {_whisperUrl}");
            Console.WriteLine("Usage:");
            Console.WriteLine("  VoiceMute                    Run in foreground, mute only (Ctrl+C to stop)");
            Console.WriteLine("  VoiceMute --stt              Enable speech-to-text (auto-starts Whisper)");
            Console.WriteLine("  VoiceMute --stt --model base       Use a specific Whisper model");
            Console.WriteLine("  VoiceMute --stt --whisper-url URL   Use a remote Whisper server");
            Console.WriteLine("  VoiceMute -d [--stt]               Run as background daemon");
            Console.WriteLine("  VoiceMute --kill                   Stop background daemon");
            Console.WriteLine();
        }

        // Single instance check — prevent duplicate processes from double-typing
        using var mutex = new Mutex(true, "Global\\VoiceMute_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Console.Error.WriteLine("VoiceMute is already running. Use --kill to stop the existing instance first.");
            return;
        }

        _deviceEnumerator = new MMDeviceEnumerator();

        if (_sttEnabled)
        {
            bool whisperReady = false;

            if (_whisperUrl != "http://127.0.0.1:2022/inference")
            {
                // Explicit URL provided — use it directly
                Console.WriteLine($"[Whisper] Using: {_whisperUrl}");
                whisperReady = true;
            }
            else if (StartWhisperServer())
            {
                // Local whisper started — broadcast its LAN address for other machines
                whisperReady = true;
                var lanIp = GetLanIp();
                if (lanIp != null)
                {
                    var lanUrl = $"http://{lanIp}:2022/inference";
                    StartBroadcasting(lanUrl);
                }
            }
            else
            {
                // No local whisper — try to find one on the network
                var discovered = DiscoverWhisper();
                if (discovered != null)
                {
                    _whisperUrl = discovered;
                    whisperReady = true;
                }
            }

            if (!whisperReady)
            {
                Console.Error.WriteLine("STT requires Whisper. Falling back to mute-only mode.");
                _sttEnabled = false;
            }
        }

        var messageThreadId = GetCurrentThreadId();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _fadeCts?.Cancel();
            StopRecording();
            RestoreVolume();
            StopBroadcasting();
            StopWhisperServer();
            PostThreadMessage(messageThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        };

        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

        if (_hookId == IntPtr.Zero)
        {
            Console.Error.WriteLine($"Failed to install keyboard hook. Error: {Marshal.GetLastWin32Error()}");
            return;
        }

        if (!backgroundMode)
            Console.WriteLine("Keyboard hook installed. Listening...");

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hookId);
        _holdTimer?.Dispose();
        _fadeCts?.Cancel();
        StopRecording();
        RestoreVolume();
        StopWhisperServer();
        _deviceEnumerator?.Dispose();
    }

    static void KillExisting()
    {
        var currentPid = Environment.ProcessId;
        var name = Process.GetCurrentProcess().ProcessName;

        foreach (var proc in Process.GetProcessesByName(name))
        {
            if (proc.Id != currentPid)
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                    Console.WriteLine($"Killed existing VoiceMute (PID {proc.Id})");
                }
                catch { }
            }
            proc.Dispose();
        }
    }

    static string? FindWhisperServer()
    {
        // Check common locations
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "voicemode", "whisper", "CudaRelease", "Release", "whisper-server.exe"),
            Path.Combine(home, "voicemode", "whisper", "Release", "whisper-server.exe"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    static string? FindWhisperModel()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var modelDir = Path.Combine(home, "voicemode", "whisper");

        // If --model was specified, resolve it
        if (_whisperModel != null)
        {
            // Absolute path
            if (Path.IsPathRooted(_whisperModel) && File.Exists(_whisperModel))
                return _whisperModel;

            // Relative to model dir (e.g. "base" or "ggml-base.bin")
            var name = _whisperModel;
            if (!name.StartsWith("ggml-")) name = $"ggml-{name}";
            if (!name.EndsWith(".bin")) name = $"{name}.bin";
            var path = Path.Combine(modelDir, name);
            if (File.Exists(path)) return path;

            Console.Error.WriteLine($"[Whisper] Model not found: {_whisperModel} (looked for {path})");
            return null;
        }

        if (!Directory.Exists(modelDir)) return null;

        // Prefer larger models
        var modelPreference = new[] { "ggml-large-v2.bin", "ggml-large-v3.bin", "ggml-small.bin", "ggml-base.bin", "ggml-tiny.en.bin" };
        foreach (var model in modelPreference)
        {
            var resolved = Path.Combine(modelDir, model);
            if (File.Exists(resolved)) return resolved;
        }

        // Fall back to any ggml model file
        return Directory.GetFiles(modelDir, "ggml-*.bin").FirstOrDefault();
    }

    static string? FindFfmpeg()
    {
        // Check if ffmpeg is on PATH
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
            if (p?.ExitCode == 0) return null; // already on PATH, no extra dir needed
        }
        catch { }

        // Search winget install location
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wingetDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetDir))
        {
            try
            {
                var ffmpegExe = Directory.GetFiles(wingetDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (ffmpegExe != null) return Path.GetDirectoryName(ffmpegExe);
            }
            catch { }
        }

        return null;
    }

    static bool IsWhisperRunning()
    {
        try
        {
            using var response = _httpClient.GetAsync("http://127.0.0.1:2022/").Result;
            return true;
        }
        catch { return false; }
    }

    static void KillExistingWhisper()
    {
        foreach (var proc in Process.GetProcessesByName("whisper-server"))
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(3000);
                Console.WriteLine($"[Whisper] Killed existing whisper-server (PID {proc.Id})");
            }
            catch { }
            proc.Dispose();
        }
    }

    static bool StartWhisperServer()
    {
        // Kill any orphaned whisper-server so we own the process
        KillExistingWhisper();

        var serverPath = FindWhisperServer();
        if (serverPath == null)
        {
            Console.Error.WriteLine("[Whisper] whisper-server.exe not found. Install whisper.cpp to ~/voicemode/whisper/");
            return false;
        }

        var modelPath = FindWhisperModel();
        if (modelPath == null)
        {
            Console.Error.WriteLine("[Whisper] No model found. Download a ggml model to ~/voicemode/whisper/");
            return false;
        }

        var ffmpegDir = FindFfmpeg();

        Console.WriteLine($"[Whisper] Starting: {Path.GetFileName(serverPath)}");
        Console.WriteLine($"[Whisper] Model: {Path.GetFileName(modelPath)}");

        var psi = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = $"--model \"{modelPath}\" --host 0.0.0.0 --port 2022 --convert",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (ffmpegDir != null)
        {
            psi.Environment["PATH"] = ffmpegDir + ";" + Environment.GetEnvironmentVariable("PATH");
        }

        try
        {
            _whisperProcess = Process.Start(psi);

            // Log stdout/stderr
            _whisperProcess!.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.WriteLine($"[Whisper] {e.Data}");
            };
            _whisperProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.WriteLine($"[Whisper] {e.Data}");
            };
            _whisperProcess.BeginOutputReadLine();
            _whisperProcess.BeginErrorReadLine();

            // Wait for server to be ready
            Console.Write("[Whisper] Waiting for server");
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(500);
                if (_whisperProcess.HasExited)
                {
                    Console.WriteLine(" FAILED (process exited)");
                    return false;
                }
                if (IsWhisperRunning())
                {
                    Console.WriteLine(" ready!");
                    return true;
                }
                Console.Write(".");
            }

            Console.WriteLine(" TIMEOUT");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Whisper] Failed to start: {ex.Message}");
            return false;
        }
    }

    static void StopWhisperServer()
    {
        if (_whisperProcess != null && !_whisperProcess.HasExited)
        {
            try
            {
                _whisperProcess.Kill();
                _whisperProcess.WaitForExit(3000);
                Console.WriteLine("[Whisper] Stopped");
            }
            catch { }
            _whisperProcess.Dispose();
            _whisperProcess = null;
        }
    }

    // === UDP Discovery ===

    static string? GetLanIp()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var ep = socket.LocalEndPoint as System.Net.IPEndPoint;
            return ep?.Address.ToString();
        }
        catch { return null; }
    }

    static void StartBroadcasting(string whisperUrl)
    {
        _broadcastCts = new CancellationTokenSource();
        var ct = _broadcastCts.Token;

        Task.Run(async () =>
        {
            using var udp = new System.Net.Sockets.UdpClient();
            udp.EnableBroadcast = true;
            var payload = System.Text.Encoding.UTF8.GetBytes($"{DISCOVERY_MAGIC}|{whisperUrl}");
            var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast, DISCOVERY_PORT);

            Console.WriteLine($"[Discovery] Broadcasting whisper at {whisperUrl} on UDP port {DISCOVERY_PORT}");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await udp.SendAsync(payload, payload.Length, endpoint);
                    await Task.Delay(3000, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, ct);
    }

    static void StopBroadcasting()
    {
        _broadcastCts?.Cancel();
        _broadcastCts = null;
    }

    static string? DiscoverWhisper(int timeoutMs = 5000)
    {
        Console.Write("[Discovery] Searching for whisper server on network");

        try
        {
            using var udp = new System.Net.Sockets.UdpClient(DISCOVERY_PORT);
            udp.Client.ReceiveTimeout = timeoutMs;

            var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
            var data = udp.Receive(ref remote);
            var msg = System.Text.Encoding.UTF8.GetString(data);

            if (msg.StartsWith(DISCOVERY_MAGIC + "|"))
            {
                var url = msg.Substring(DISCOVERY_MAGIC.Length + 1);
                Console.WriteLine($" found: {url}");
                return url;
            }
        }
        catch (System.Net.Sockets.SocketException)
        {
            Console.WriteLine(" not found.");
        }

        return null;
    }

    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            var msg = (int)wParam;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (vkCode == VK_RCONTROL)
            {
                if (isDown) OnKeyDown();
                else if (isUp) OnKeyUp();
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    static void OnKeyDown()
    {
        lock (_lock)
        {
            if (_keyHeld) return;
            _keyHeld = true;

            // If previous recording is stuck, abort it
            if (_isRecording)
            {
                Console.WriteLine("[STT] Aborting stuck recording");
                try { _capture?.StopRecording(); } catch { }
                _capture?.Dispose();
                _capture = null;
                _audioBuffer?.Dispose();
                _audioBuffer = null;
                _captureFormat = null;
                _isRecording = false;
            }

            // If mute is stuck, force unmute
            if (_isMuted)
            {
                Console.WriteLine("[Mute] Resetting stuck mute");
                RestoreVolume();
            }

            // Start timer — if key still held after delay, mute + record
            _holdTimer?.Dispose();
            _holdTimer = new Timer(_ =>
            {
                lock (_lock)
                {
                    if (_keyHeld)
                    {
                        Mute();
                        if (_sttEnabled) StartRecording();
                    }
                }
            }, null, _holdDelayMs, Timeout.Infinite);
        }
    }

    static void OnKeyUp()
    {
        lock (_lock)
        {
            _keyHeld = false;
            _holdTimer?.Dispose();
            _holdTimer = null;

            // Always reset state on key-up, even if something crashed
            try
            {
                if (_sttEnabled && _isRecording)
                {
                    StopRecordingAndTranscribe();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[STT] Error during stop/transcribe: {ex.Message}");
                // Force-reset recording state
                try { _capture?.StopRecording(); } catch { }
                _capture?.Dispose();
                _capture = null;
                _audioBuffer?.Dispose();
                _audioBuffer = null;
                _captureFormat = null;
                _isRecording = false;
            }

            // Always unmute — never leave speakers muted
            if (_isMuted)
            {
                try { Unmute(); }
                catch
                {
                    // Last resort: force restore volume
                    RestoreVolume();
                }
            }
        }
    }

    // === Volume control ===

    static void Mute()
    {
        if (_isMuted) return;

        _fadeCts?.Cancel();
        _fadeCts = new CancellationTokenSource();
        var ct = _fadeCts.Token;

        _isMuted = true;

        Task.Run(async () =>
        {
            try
            {
                using var device = _deviceEnumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var vol = device.AudioEndpointVolume;
                _savedVolume = vol.MasterVolumeLevelScalar;

                var stepDelay = _fadeOutMs / _fadeSteps;

                for (int i = _fadeSteps; i >= 0; i--)
                {
                    if (ct.IsCancellationRequested) return;
                    vol.MasterVolumeLevelScalar = _savedVolume * ((float)i / _fadeSteps);
                    await Task.Delay(stepDelay, ct);
                }

                vol.MasterVolumeLevelScalar = 0f;
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    static void Unmute()
    {
        if (!_isMuted) return;

        _fadeCts?.Cancel();
        _fadeCts = new CancellationTokenSource();
        var ct = _fadeCts.Token;

        _isMuted = false;

        Task.Run(async () =>
        {
            try
            {
                using var device = _deviceEnumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var vol = device.AudioEndpointVolume;
                var targetVolume = _savedVolume > 0 ? _savedVolume : 1.0f;

                var stepDelay = _fadeInMs / _fadeSteps;

                for (int i = 0; i <= _fadeSteps; i++)
                {
                    if (ct.IsCancellationRequested) return;
                    vol.MasterVolumeLevelScalar = targetVolume * ((float)i / _fadeSteps);
                    await Task.Delay(stepDelay, ct);
                }

                vol.MasterVolumeLevelScalar = targetVolume;
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    static void RestoreVolume()
    {
        try
        {
            using var device = _deviceEnumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_savedVolume > 0)
                device.AudioEndpointVolume.MasterVolumeLevelScalar = _savedVolume;
            _isMuted = false;
        }
        catch { }
    }

    // === STT recording ===

    static void StartRecording()
    {
        if (_isRecording) return;

        try
        {
            _audioBuffer = new MemoryStream();
            _capture = new WasapiCapture();
            _captureFormat = _capture.WaveFormat;

            _capture.DataAvailable += (_, e) =>
            {
                lock (_lock)
                {
                    _audioBuffer?.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };

            _capture.StartRecording();
            _isRecording = true;
            Console.WriteLine("[STT] Recording...");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[STT] Failed to start recording: {ex.Message}");
            _capture?.Dispose();
            _capture = null;
            _audioBuffer?.Dispose();
            _audioBuffer = null;
        }
    }

    static void StopRecording()
    {
        if (!_isRecording) return;

        try
        {
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;
            _audioBuffer?.Dispose();
            _audioBuffer = null;
            _captureFormat = null;
            _isRecording = false;
        }
        catch { }
    }

    static void StopRecordingAndTranscribe()
    {
        byte[] audioData;
        WaveFormat format;

        // Grab the audio data under lock, then release
        if (_audioBuffer == null || _captureFormat == null) return;

        try
        {
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;
            _isRecording = false;

            audioData = _audioBuffer.ToArray();
            format = _captureFormat;

            _audioBuffer.Dispose();
            _audioBuffer = null;
            _captureFormat = null;
        }
        catch
        {
            _isRecording = false;
            return;
        }

        if (audioData.Length == 0)
        {
            Console.WriteLine("[STT] No audio recorded.");
            return;
        }

        Console.WriteLine($"[STT] Captured {audioData.Length / 1024}KB, transcribing...");

        Task.Run(async () =>
        {
            try
            {
                var wavBytes = ConvertToWav(audioData, format);
                var text = await TranscribeAsync(wavBytes);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    // Collapse newlines/extra whitespace from Whisper
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
                    Console.WriteLine($"[STT] \"{text}\"");
                    TypeText(text);
                }
                else
                {
                    Console.WriteLine("[STT] (no speech detected)");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[STT] Transcription failed: {ex.Message}");
            }
        });
    }

    static byte[] ConvertToWav(byte[] rawAudio, WaveFormat sourceFormat)
    {
        using var output = new MemoryStream();
        var targetFormat = new WaveFormat(16000, 16, 1);

        using (var sourceStream = new RawSourceWaveStream(new MemoryStream(rawAudio), sourceFormat))
        using (var resampler = new MediaFoundationResampler(sourceStream, targetFormat))
        {
            resampler.ResamplerQuality = 60;
            WaveFileWriter.WriteWavFileToStream(output, resampler);
        }

        return output.ToArray();
    }

    static async Task<string?> TranscribeAsync(byte[] wavBytes)
    {
        using var content = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "recording.wav");
        content.Add(new StringContent("json"), "response_format");

        var response = await _httpClient.PostAsync(_whisperUrl, content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("text").GetString()?.Trim();
    }

    static void TypeText(string text)
    {
        var inputs = new INPUT[text.Length * 2];

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            inputs[i * 2] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE,
                    }
                }
            };

            inputs[i * 2 + 1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                    }
                }
            };
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Console.WriteLine($"[STT] SendInput: {sent}/{inputs.Length} events sent (error: {Marshal.GetLastWin32Error()})");
    }

    static bool IsTerminalFocused()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return _terminalProcessNames.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }
}
