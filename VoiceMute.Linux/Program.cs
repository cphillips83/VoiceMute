using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VoiceMute;

class Program
{
    // State
    static bool _sttEnabled;
    static string _whisperUrl = "http://127.0.0.1:2022/inference";
    static string? _whisperModel;
    static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    static CancellationTokenSource? _broadcastCts;
    const int DISCOVERY_PORT = 5766;
    const string DISCOVERY_MAGIC = "VOICEMUTE_WHISPER_DISCOVER";

    // Recording state
    static bool _isRecording;
    static bool _isMuted;
    static bool _keyHeld;
    static Timer? _holdTimer;
    static int _holdDelayMs = 200;
    static Process? _recordProcess;
    static string? _tempWavPath;
    static readonly object _lock = new();

    // Saved volume for restore
    static float _savedVolume;

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
            StopWhisperService();
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
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var proc = Process.Start(psi);
            Console.WriteLine($"VoiceMute running in background (PID {proc?.Id})");
            return;
        }

        if (!backgroundMode)
        {
            Console.WriteLine("=== VoiceMute (Linux) ===");
            Console.WriteLine($"Hold Right Ctrl to: mute speakers{(_sttEnabled ? " + record mic + transcribe on release" : "")}");
            Console.WriteLine($"Hold delay: {_holdDelayMs}ms");
            if (_sttEnabled)
                Console.WriteLine($"Whisper: {_whisperUrl}");
            Console.WriteLine("Usage:");
            Console.WriteLine("  VoiceMute.Linux                          Run in foreground, mute only (Ctrl+C to stop)");
            Console.WriteLine("  VoiceMute.Linux --stt                    Enable speech-to-text (auto-starts Whisper)");
            Console.WriteLine("  VoiceMute.Linux --stt --model large-v3   Use a specific Whisper model");
            Console.WriteLine("  VoiceMute.Linux --stt --whisper-url URL  Use a remote Whisper server");
            Console.WriteLine("  VoiceMute.Linux -d [--stt]               Run as background daemon");
            Console.WriteLine("  VoiceMute.Linux --kill                   Stop background daemon");
            Console.WriteLine();
        }

        // Single instance check via file lock
        using var lockFile = AcquireLock();
        if (lockFile == null)
        {
            Console.Error.WriteLine("VoiceMute is already running. Use --kill to stop the existing instance first.");
            return;
        }

        if (_sttEnabled)
        {
            bool whisperReady = false;

            if (_whisperUrl != "http://127.0.0.1:2022/inference")
            {
                Console.WriteLine($"[Whisper] Using: {_whisperUrl}");
                whisperReady = true;
            }
            else if (StartWhisperService())
            {
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

        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (!backgroundMode)
            Console.WriteLine("Listening for Right Ctrl...");

        try
        {
            RunInputLoop(cts.Token);
        }
        finally
        {
            StopRecording();
            RestoreVolume();
            StopBroadcasting();
            if (_whisperUrl == "http://127.0.0.1:2022/inference")
                StopWhisperService();
        }
    }

    // === Single Instance ===

    static string GetLockDir() =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();

    static FileStream? AcquireLock()
    {
        var lockPath = Path.Combine(GetLockDir(), "voicemute.lock");
        try
        {
            var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            fs.Lock(0, 0);
            File.WriteAllText(lockPath + ".pid", Environment.ProcessId.ToString());
            return fs;
        }
        catch (IOException)
        {
            return null;
        }
    }

    static void KillExisting()
    {
        var pidFile = Path.Combine(GetLockDir(), "voicemute.lock.pid");
        if (File.Exists(pidFile))
        {
            if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            {
                try
                {
                    var proc = Process.GetProcessById(pid);
                    proc.Kill();
                    proc.WaitForExit(3000);
                    Console.WriteLine($"Killed existing VoiceMute (PID {pid})");
                }
                catch { }
            }
            try { File.Delete(pidFile); } catch { }
        }

        var lockPath = Path.Combine(GetLockDir(), "voicemute.lock");
        try { File.Delete(lockPath); } catch { }
    }

    // === Input via XDG GlobalShortcuts Portal (Wayland) ===

    static void RunInputLoop(CancellationToken ct)
    {
        GlobalShortcut.RunAsync(OnKeyDown, OnKeyUp, ct).GetAwaiter().GetResult();
    }

    // === Key Handlers ===

    static void OnKeyDown()
    {
        lock (_lock)
        {
            if (_keyHeld) return;
            _keyHeld = true;

            if (_isRecording)
            {
                Console.WriteLine("[STT] Aborting stuck recording");
                StopRecording();
            }

            if (_isMuted)
            {
                Console.WriteLine("[Mute] Resetting stuck mute");
                RestoreVolume();
            }

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

            try
            {
                if (_sttEnabled && _isRecording)
                    StopRecordingAndTranscribe();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[STT] Error during stop/transcribe: {ex.Message}");
                StopRecording();
            }

            if (_isMuted)
            {
                try { Unmute(); }
                catch { RestoreVolume(); }
            }
        }
    }

    // === Volume Control via wpctl (PipeWire) or pactl (PulseAudio) ===

    static bool _useWpctl;
    static bool _usePactl;
    static bool _volumeDetected;

    static void DetectVolumeControl()
    {
        if (_volumeDetected) return;
        _volumeDetected = true;

        if (RunCommand("which", "wpctl") != null)
        {
            _useWpctl = true;
            Console.WriteLine("[Volume] Using: wpctl (PipeWire/WirePlumber)");
        }
        else if (RunCommand("which", "pactl") != null)
        {
            _usePactl = true;
            Console.WriteLine("[Volume] Using: pactl (PulseAudio)");
        }
        else
        {
            Console.Error.WriteLine("[Volume] No volume control found. Install wireplumber (wpctl) or pulseaudio (pactl).");
        }
    }

    static void Mute()
    {
        if (_isMuted) return;
        DetectVolumeControl();

        try
        {
            if (_useWpctl)
            {
                var output = RunCommand("wpctl", "get-volume @DEFAULT_AUDIO_SINK@");
                if (output != null)
                {
                    // Output: "Volume: 0.50" or "Volume: 0.50 [MUTED]"
                    var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vol))
                        _savedVolume = vol;
                }
                RunCommand("wpctl", "set-mute @DEFAULT_AUDIO_SINK@ 1");
            }
            else if (_usePactl)
            {
                RunCommand("pactl", "set-sink-mute @DEFAULT_SINK@ 1");
            }

            _isMuted = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mute] Failed: {ex.Message}");
        }
    }

    static void Unmute()
    {
        if (!_isMuted) return;

        if (_useWpctl)
            RunCommand("wpctl", "set-mute @DEFAULT_AUDIO_SINK@ 0");
        else if (_usePactl)
            RunCommand("pactl", "set-sink-mute @DEFAULT_SINK@ 0");

        _isMuted = false;
    }

    static void RestoreVolume()
    {
        try
        {
            if (_useWpctl)
            {
                RunCommand("wpctl", "set-mute @DEFAULT_AUDIO_SINK@ 0");
                if (_savedVolume > 0)
                    RunCommand("wpctl", $"set-volume @DEFAULT_AUDIO_SINK@ {_savedVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            else if (_usePactl)
            {
                RunCommand("pactl", "set-sink-mute @DEFAULT_SINK@ 0");
            }

            _isMuted = false;
        }
        catch { }
    }

    // === Recording via pw-record / parecord / arecord ===

    static string? _recordCommand;
    static string? _recordArgs;

    static bool DetectRecordCommand()
    {
        if (_recordCommand != null) return true;

        // Prefer PipeWire, fall back to PulseAudio, then ALSA
        if (RunCommand("which", "pw-record") != null)
        {
            _recordCommand = "pw-record";
            _recordArgs = "--format=s16 --rate=16000 --channels=1";
        }
        else if (RunCommand("which", "parecord") != null)
        {
            _recordCommand = "parecord";
            _recordArgs = "--format=s16le --rate=16000 --channels=1 --file-format=wav";
        }
        else if (RunCommand("which", "arecord") != null)
        {
            _recordCommand = "arecord";
            _recordArgs = "-f S16_LE -r 16000 -c 1 -t wav";
        }
        else
        {
            Console.Error.WriteLine("[STT] No audio recorder found. Install pipewire (pw-record), pulseaudio (parecord), or alsa-utils (arecord).");
            return false;
        }

        Console.WriteLine($"[STT] Using: {_recordCommand}");
        return true;
    }

    static void StartRecording()
    {
        if (_isRecording) return;
        if (!DetectRecordCommand()) return;

        try
        {
            _tempWavPath = Path.Combine(Path.GetTempPath(), $"voicemute_{Environment.ProcessId}.wav");

            _recordProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _recordCommand!,
                    Arguments = $"{_recordArgs} \"{_tempWavPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            _recordProcess.Start();
            _isRecording = true;
            Console.WriteLine("[STT] Recording...");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[STT] Failed to start recording: {ex.Message}");
            _recordProcess?.Dispose();
            _recordProcess = null;
        }
    }

    static void StopRecording()
    {
        if (!_isRecording) return;

        try
        {
            if (_recordProcess != null && !_recordProcess.HasExited)
            {
                // pw-record stops cleanly on SIGINT and writes WAV header
                RunCommand("kill", $"-INT {_recordProcess.Id}");
                _recordProcess.WaitForExit(3000);
                if (!_recordProcess.HasExited)
                    _recordProcess.Kill();
            }
            _recordProcess?.Dispose();
            _recordProcess = null;
            _isRecording = false;
        }
        catch
        {
            _isRecording = false;
        }
    }

    static void StopRecordingAndTranscribe()
    {
        if (_recordProcess == null || _tempWavPath == null) return;

        try
        {
            if (!_recordProcess.HasExited)
            {
                RunCommand("kill", $"-INT {_recordProcess.Id}");
                _recordProcess.WaitForExit(3000);
                if (!_recordProcess.HasExited)
                    _recordProcess.Kill();
            }
            _recordProcess.Dispose();
            _recordProcess = null;
            _isRecording = false;

            if (!File.Exists(_tempWavPath) || new FileInfo(_tempWavPath).Length == 0)
            {
                Console.WriteLine("[STT] No audio recorded.");
                return;
            }

            var fileSize = new FileInfo(_tempWavPath).Length;
            Console.WriteLine($"[STT] Captured {fileSize / 1024}KB, transcribing...");

            var wavPath = _tempWavPath;
            _tempWavPath = null;

            Task.Run(async () =>
            {
                try
                {
                    var wavBytes = await File.ReadAllBytesAsync(wavPath);
                    var text = await TranscribeAsync(wavBytes);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
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
                finally
                {
                    try { File.Delete(wavPath); } catch { }
                }
            });
        }
        catch
        {
            _isRecording = false;
        }
    }

    // === Whisper Client ===

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

    // === Text Injection via ydotool ===

    static void TypeText(string text)
    {
        // ydotool works on Wayland (and X11) — requires ydotoold running
        var psi = new ProcessStartInfo
        {
            FileName = "ydotool",
            Arguments = $"type --key-delay=0 --key-hold=0 -- \"{text.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // ydotoold socket location varies: check XDG_RUNTIME_DIR first, then /tmp
        var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var socketCandidates = new[]
        {
            xdgRuntime != null ? Path.Combine(xdgRuntime, ".ydotool_socket") : null,
            "/tmp/.ydotool_socket",
            $"/run/user/{getuid()}/.ydotool_socket",
        };
        var socket = socketCandidates.FirstOrDefault(s => s != null && File.Exists(s));
        if (socket != null)
            psi.Environment["YDOTOOL_SOCKET"] = socket;

        try
        {
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            if (proc?.ExitCode != 0)
            {
                Console.Error.WriteLine($"[STT] ydotool exit code: {proc?.ExitCode}. Is ydotoold running?");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[STT] Failed to type text: {ex.Message}");
        }
    }

    // === Whisper Service Management via systemd ===

    static string SystemctlUserArgs(string action)
    {
        // When running as root (sudo), target the actual user's systemd instance
        var sudoUid = Environment.GetEnvironmentVariable("SUDO_UID");
        if (sudoUid != null)
            return $"--machine={sudoUid}@ --user {action}";
        return $"--user {action}";
    }

    static bool StartWhisperService()
    {
        Console.WriteLine("[Whisper] Starting systemd service...");
        var result = RunCommand("systemctl", SystemctlUserArgs("start whisper-server"));

        // Wait for server to be ready
        Console.Write("[Whisper] Waiting for server");
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(500);
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

    static void StopWhisperService()
    {
        RunCommand("systemctl", SystemctlUserArgs("stop whisper-server"));
        Console.WriteLine("[Whisper] Stopped");
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

    // === UDP Discovery (same protocol as Windows version) ===

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

    // === Helpers ===

    [System.Runtime.InteropServices.DllImport("libc")]
    static extern uint getuid();

    static string? RunCommand(string command, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd();
            proc?.WaitForExit(5000);
            return output?.Trim();
        }
        catch { return null; }
    }
}
