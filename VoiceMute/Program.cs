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
    static bool _isMuted;
    static bool _spaceHeld;
    static MMDeviceEnumerator? _deviceEnumerator;
    static readonly object _lock = new();
    static int _holdDelayMs = 1000;
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
        // Parse optional delay argument
        if (args.Length > 0 && int.TryParse(args[0], out var delayMs))
        {
            _holdDelayMs = delayMs;
        }

        Console.WriteLine("=== VoiceMute ===");
        Console.WriteLine($"Mutes speakers when Space is held for {_holdDelayMs}ms in a terminal window.");
        Console.WriteLine("Press Ctrl+C to exit.");
        Console.WriteLine();

        _deviceEnumerator = new MMDeviceEnumerator();

        // Get the message loop thread ID so we can post WM_QUIT to it
        var messageThreadId = GetCurrentThreadId();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Unmute();
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
        _deviceEnumerator?.Dispose();
        Unmute();

        Console.WriteLine("Exited cleanly.");
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
                Unmute();
            }
        }
    }

    static void Mute()
    {
        if (_isMuted) return;

        try
        {
            using var device = _deviceEnumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.Mute = true;
            _isMuted = true;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] MUTED - Voice recording detected");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to mute: {ex.Message}");
        }
    }

    static void Unmute()
    {
        if (!_isMuted) return;

        try
        {
            using var device = _deviceEnumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.Mute = false;
            _isMuted = false;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UNMUTED - Voice recording ended");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to unmute: {ex.Message}");
        }
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
