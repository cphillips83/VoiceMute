using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace VoiceMute;

class Program
{
    // Win32 constants
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;
    const int VK_SPACE = 0x20;

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
    static Timer? _muteTimer;
    static Timer? _unmuteTimer;
    static CancellationTokenSource? _fadeCts;
    static bool _isMuted;
    static bool _spaceHeld;
    static float _savedVolume;
    static MMDeviceEnumerator? _deviceEnumerator;
    static readonly object _lock = new();
    static int _holdDelayMs = 200;
    static int _unmuteDelayMs = 0;
    static int _fadeOutMs = 100;
    static int _fadeInMs = 250;
    static int _fadeSteps = 10;
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

        // --kill: stop any running instance
        if (killMode)
        {
            KillExisting();
            Console.WriteLine("VoiceMute stopped.");
            return;
        }

        // -d: kill existing, then respawn self as hidden background process
        if (daemonMode)
        {
            KillExisting();

            var exe = Environment.ProcessPath!;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--background",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
            };

            var proc = Process.Start(psi);
            Console.WriteLine($"VoiceMute running in background (PID {proc?.Id})");
            return;
        }

        // Foreground or --background: run the actual hook loop
        if (!backgroundMode)
        {
            Console.WriteLine("=== VoiceMute ===");
            Console.WriteLine($"Hold delay: {_holdDelayMs}ms | Fade out: {_fadeOutMs}ms | Fade in: {_fadeInMs}ms");
            Console.WriteLine("Usage:");
            Console.WriteLine("  VoiceMute        Run in foreground (Ctrl+C to stop)");
            Console.WriteLine("  VoiceMute -d     Run as background daemon");
            Console.WriteLine("  VoiceMute --kill Stop background daemon");
            Console.WriteLine();
        }

        _deviceEnumerator = new MMDeviceEnumerator();

        var messageThreadId = GetCurrentThreadId();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _fadeCts?.Cancel();
            RestoreVolume();
            PostThreadMessage(messageThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        };

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            Console.Error.WriteLine($"Failed to install keyboard hook. Error: {Marshal.GetLastWin32Error()}");
            return;
        }

        if (!backgroundMode)
            Console.WriteLine("Keyboard hook installed. Listening...");

        // Win32 message loop (required for low-level hooks)
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // Cleanup
        UnhookWindowsHookEx(_hookId);
        _muteTimer?.Dispose();
        _fadeCts?.Cancel();
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

            if (vkCode == VK_SPACE)
            {
                var msg = (int)wParam;

                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    OnSpaceDown();
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    OnSpaceUp();
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    static void OnSpaceDown()
    {
        lock (_lock)
        {
            if (_spaceHeld) return; // Key repeat, ignore
            _spaceHeld = true;

            if (!IsTerminalFocused()) return;

            // Start timer — if space is still held after delay, mute
            _muteTimer?.Dispose();
            _muteTimer = new Timer(_ =>
            {
                lock (_lock)
                {
                    if (_spaceHeld && IsTerminalFocused())
                    {
                        Mute();
                    }
                }
            }, null, _holdDelayMs, Timeout.Infinite);
        }
    }

    static void OnSpaceUp()
    {
        lock (_lock)
        {
            _spaceHeld = false;
            _muteTimer?.Dispose();
            _muteTimer = null;

            if (_isMuted)
            {
                // Delay unmute so audio doesn't blast back immediately
                _unmuteTimer?.Dispose();
                _unmuteTimer = new Timer(_ =>
                {
                    lock (_lock)
                    {
                        _unmuteTimer = null;
                        Unmute();
                    }
                }, null, _unmuteDelayMs, Timeout.Infinite);
            }
        }
    }

    static void Mute()
    {
        if (_isMuted) return;

        // Cancel any in-progress fade
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

                // Fade out
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

        // Cancel any in-progress fade
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

                // Fade in
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
