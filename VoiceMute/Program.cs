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
                Arguments = _sttEnabled ? "--background --stt" : "--background",
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
            Console.WriteLine("  VoiceMute             Run in foreground (Ctrl+C to stop)");
            Console.WriteLine("  VoiceMute --stt       Enable speech-to-text (requires Whisper on port 2022)");
            Console.WriteLine("  VoiceMute -d          Run as background daemon");
            Console.WriteLine("  VoiceMute -d --stt    Daemon with speech-to-text");
            Console.WriteLine("  VoiceMute --kill      Stop background daemon");
            Console.WriteLine();
        }

        _deviceEnumerator = new MMDeviceEnumerator();

        var messageThreadId = GetCurrentThreadId();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _fadeCts?.Cancel();
            StopRecording();
            RestoreVolume();
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

            if (_sttEnabled && _isRecording)
            {
                StopRecordingAndTranscribe();
            }

            if (_isMuted)
            {
                Unmute();
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
