# VoiceMute

Auto-mutes your speakers when holding a hotkey, with optional speech-to-text dictation. Originally designed for [Claude Code's](https://docs.anthropic.com/en/docs/claude-code) push-to-talk voice mode so background audio doesn't get picked up by your microphone. Now works as a global dictation tool in any application.

## Features

- **Speaker mute**: Hold hotkey to mute speakers, release to unmute
- **Speech-to-text** (`--stt`): Records your microphone while held, transcribes via local Whisper, and types the result into the active window
- **Auto-manages Whisper**: Automatically starts and stops the Whisper server — no manual setup needed after initial install
- **Model selection** (`--model`): Choose which Whisper model to use (base, small, large-v3, etc.)
- **Network discovery**: Broadcasts Whisper server on LAN so other machines can use it
- **Global**: Works in any application — terminals, browsers, editors, etc.
- **Daemon mode**: Runs silently in the background

## Platforms

| | Windows | Linux (Wayland) |
|--|---------|-----------------|
| Project | `VoiceMute/` | `VoiceMute.Linux/` |
| Runtime | .NET 8.0 | .NET 10.0 |
| Hotkey | Right Ctrl (hold) | Right Ctrl or Super+Space |
| Audio capture | NAudio (WASAPI) | PipeWire / PulseAudio / ALSA |
| Volume control | NAudio CoreAudioApi | wpctl / pactl |
| Text injection | Win32 SendInput | ydotool |
| Keyboard input | Win32 keyboard hook | XDG GlobalShortcuts portal |
| Whisper mgmt | Spawns process | systemd user service |

## Download

Grab the latest release from [releases](https://github.com/cphillips83/VoiceMute/releases/latest).

---

## Windows

### Requirements

- Windows 10/11 x64
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (download the ".NET Desktop Runtime" for Windows x64)
- For `--stt`: whisper.cpp + FFmpeg (see [Windows STT Setup](#windows-stt-setup))

### Usage

```
VoiceMute                          Mute only (Ctrl+C to stop)
VoiceMute --stt                    Mute + speech-to-text (auto-starts Whisper)
VoiceMute --stt --model base       Use a specific Whisper model
VoiceMute -d [--stt]               Run as background daemon
VoiceMute --kill                   Stop background daemon
```

Hold **Right Ctrl** anywhere to:
1. Mute speakers (so Whisper doesn't hear your audio output)
2. Record from your microphone

Release **Right Ctrl** to:
1. Stop recording
2. Send audio to local Whisper for transcription
3. Type the transcribed text into the active window
4. Unmute speakers

The 200ms hold threshold prevents accidental activation during normal typing.

### Windows STT Setup

The `--stt` flag requires whisper.cpp to be installed. VoiceMute manages starting/stopping the server automatically.

#### 1. Install FFmpeg

```
winget install ffmpeg
```

#### 2. Install whisper.cpp

Download the Windows binary from [whisper.cpp releases](https://github.com/ggerganov/whisper.cpp/releases/latest):

| Binary | GPU | Use when |
|--------|-----|----------|
| `whisper-bin-x64.zip` | CPU only | No NVIDIA GPU |
| `whisper-cublas-12.4.0-bin-x64.zip` | NVIDIA CUDA | You have an NVIDIA GPU (recommended) |

Extract to `%USERPROFILE%\voicemode\whisper\`:

```
%USERPROFILE%\voicemode\whisper\
├── CudaRelease\Release\whisper-server.exe   (CUDA build)
├── Release\whisper-server.exe               (CPU build)
└── ggml-base.bin                            (model)
```

VoiceMute looks for the CUDA build first, then falls back to CPU.

#### 3. Download a Model

```bash
cd %USERPROFILE%\voicemode\whisper
curl -L -o ggml-base.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin
```

| Model | Size | Accuracy |
|-------|------|----------|
| `ggml-tiny.en.bin` | 39 MB | Fast, English only |
| `ggml-base.bin` | 142 MB | Good balance |
| `ggml-small.bin` | 466 MB | Better accuracy |
| `ggml-large-v3.bin` | 2.9 GB | Best accuracy |

---

## Linux (Wayland)

### Requirements

- Linux with a Wayland compositor that supports the [XDG GlobalShortcuts portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html) (Hyprland, KDE Plasma)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (or Runtime if using prebuilt binaries)
- PipeWire (or PulseAudio/ALSA) for audio recording
- WirePlumber `wpctl` (or PulseAudio `pactl`) for volume control
- `ydotool` + `ydotoold` for text injection
- `xdg-desktop-portal` + compositor portal (e.g. `xdg-desktop-portal-hyprland`)
- For `--stt`: whisper.cpp with CUDA + FFmpeg

### Usage

```
VoiceMute.Linux                          Mute only (Ctrl+C to stop)
VoiceMute.Linux --stt                    Mute + speech-to-text (auto-starts Whisper)
VoiceMute.Linux --stt --model large-v3   Use a specific Whisper model
VoiceMute.Linux --stt --whisper-url URL  Use a remote Whisper server
VoiceMute.Linux -d [--stt]               Run as background daemon
VoiceMute.Linux --kill                   Stop background daemon
```

### Linux STT Setup

#### 1. Install system dependencies

```bash
# Arch/CachyOS
sudo pacman -S dotnet-sdk cuda cudnn ffmpeg ydotool cmake xdg-desktop-portal-hyprland
```

Set up ydotoold as a system service (needs `/dev/uinput` access). Create `/etc/systemd/system/ydotoold.service`:

```ini
[Unit]
Description=ydotool virtual input daemon

[Service]
ExecStart=/usr/bin/ydotoold
ExecStartPost=/bin/bash -c 'for i in $(seq 1 10); do [ -S /tmp/.ydotool_socket ] && chmod 666 /tmp/.ydotool_socket && break; sleep 0.1; done'

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now ydotoold
```

#### 2. Build whisper.cpp with CUDA

```bash
git clone https://github.com/ggerganov/whisper.cpp.git ~/whisper-build
cd ~/whisper-build
export PATH="/opt/cuda/bin:$PATH"
cmake -B build -DGGML_CUDA=ON
cmake --build build --config Release -j$(nproc)

# Install to ~/voicemode/whisper/
mkdir -p ~/voicemode/whisper
cp build/bin/whisper-server ~/voicemode/whisper/
cp build/bin/whisper-cli ~/voicemode/whisper/
cp build/src/libwhisper.so ~/voicemode/whisper/
cp build/ggml/src/libggml.so ~/voicemode/whisper/
cp build/ggml/src/libggml-base.so ~/voicemode/whisper/
cp build/ggml/src/libggml-cpu.so ~/voicemode/whisper/
cp build/ggml/src/ggml-cuda/libggml-cuda.so ~/voicemode/whisper/
```

#### 3. Download a model

```bash
curl -L -o ~/voicemode/whisper/ggml-large-v3.bin \
  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin
```

#### 4. Set up the Whisper systemd service

Create `~/.config/systemd/user/whisper-server.service`:

```ini
[Unit]
Description=Whisper.cpp STT Server (CUDA)
After=network.target

[Service]
Type=simple
Environment=LD_LIBRARY_PATH=/home/YOUR_USER/voicemode/whisper
ExecStart=/home/YOUR_USER/voicemode/whisper/whisper-server --model /home/YOUR_USER/voicemode/whisper/ggml-large-v3.bin --host 0.0.0.0 --port 2022 --convert
Restart=on-failure
RestartSec=3

[Install]
WantedBy=default.target
```

```bash
systemctl --user daemon-reload
```

VoiceMute starts/stops this service automatically.

#### 5. Configure compositor keybinding

VoiceMute registers a GlobalShortcuts portal shortcut named `voicemute-ptt`. You need to bind a key to it in your compositor config.

**Hyprland** (`~/.config/hypr/hyprland.conf`):

```ini
# Right Ctrl (keycode 105)
bind = , code:105, global, :voicemute-ptt
# Super+Space (alternative)
bind = SUPER, space, global, :voicemute-ptt
```

**KDE Plasma**: The shortcut should appear in System Settings > Shortcuts after VoiceMute registers it.

#### 6. Run

```bash
cd VoiceMute.Linux
dotnet run -- --stt
```

### Using a Remote Whisper Server

VoiceMute broadcasts its Whisper server on the local network via UDP (port 5766). Other machines running VoiceMute will auto-discover it. You can also specify a remote server explicitly:

```
VoiceMute.Linux --stt --whisper-url http://192.168.1.100:2022/inference
```

This lets you run Whisper on a more powerful GPU and use VoiceMute from a lighter machine.

---

## Building from Source

```bash
git clone https://github.com/cphillips83/VoiceMute.git
cd VoiceMute

# Windows
dotnet run --project VoiceMute -- --stt --model base
dotnet publish VoiceMute/VoiceMute.csproj -c Release -r win-x64 --self-contained false -o artifacts/

# Linux
dotnet run --project VoiceMute.Linux -- --stt
dotnet publish VoiceMute.Linux/VoiceMute.Linux.csproj -c Release -r linux-x64 --self-contained false -o artifacts/
```

## License

MIT
