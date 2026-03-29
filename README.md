# VoiceMute

Auto-mutes your speakers when holding a hotkey, with optional speech-to-text dictation. Originally designed for [Claude Code's](https://docs.anthropic.com/en/docs/claude-code) push-to-talk voice mode so background audio doesn't get picked up by your microphone. Now works as a global dictation tool in any application.

## Features

- **Speaker mute**: Hold Right Ctrl to fade out speakers, release to fade back in
- **Speech-to-text** (`--stt`): Records your microphone while held, transcribes via local Whisper, and types the result into the active window
- **Global**: Works in any application — terminals, browsers, editors, etc.
- **Daemon mode**: Runs silently in the background

## Download

Grab `VoiceMute.exe` from the [latest release](https://github.com/cphillips83/VoiceMute/releases/latest). It's a self-contained single-file executable — no .NET runtime install needed.

## Usage

```
VoiceMute             Run in foreground, mute only (Ctrl+C to stop)
VoiceMute --stt       Run with speech-to-text (requires Whisper on port 2022)
VoiceMute -d          Run as a background daemon (no console window)
VoiceMute -d --stt    Daemon with speech-to-text
VoiceMute --kill      Stop the background daemon
```

### Mute Only (default)

Just run `VoiceMute.exe`. Hold **Right Ctrl** to fade out speakers, release to fade back in. Normal key usage isn't affected — it requires a 200ms hold before activating.

### Speech-to-Text (`--stt`)

```
VoiceMute --stt
```

Hold **Right Ctrl** to:
1. Mute speakers (so Whisper doesn't hear your audio output)
2. Record from your microphone

Release **Right Ctrl** to:
1. Stop recording
2. Send audio to local Whisper for transcription
3. Type the transcribed text into the active window
4. Unmute speakers

### Daemon Mode (`-d`)

```
VoiceMute -d --stt
```

Spawns a hidden background process, then exits. Running `-d` again is safe — it kills any existing instance first.

### Stopping (`--kill`)

```
VoiceMute --kill
```

Finds and stops any running VoiceMute background process. Speaker volume is restored.

## How It Works

1. A low-level keyboard hook (`SetWindowsHookEx`) monitors Right Ctrl globally
2. When held for **200ms**, speakers fade out over **100ms** (and mic recording starts if `--stt`)
3. On release, recording stops, audio is sent to Whisper, text is typed via `SendInput`, and speakers fade back in over **250ms**

## STT Setup (Windows)

The `--stt` flag requires a local Whisper server. Here's how to set it up:

### Prerequisites

- [FFmpeg](https://www.ffmpeg.org/download.html) — `winget install ffmpeg`

### Whisper (Speech-to-Text)

Download the whisper.cpp Windows binary from the [whisper.cpp releases](https://github.com/ggerganov/whisper.cpp/releases/latest):
- `whisper-bin-x64.zip` — CPU only
- `whisper-cublas-12.4.0-bin-x64.zip` — NVIDIA GPU (recommended if you have one)

Download a model from Hugging Face:
```bash
curl -L -o ggml-base.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin
```

| Model | Size | Accuracy |
|-------|------|----------|
| `ggml-tiny.en.bin` | 39 MB | Fast, English only |
| `ggml-base.bin` | 142 MB | Good balance (default) |
| `ggml-small.bin` | 466 MB | Better accuracy |
| `ggml-large-v2.bin` | 2.9 GB | Best accuracy |

Run the server (ensure FFmpeg is on your PATH):
```bash
whisper-server.exe --model ggml-base.bin --host 127.0.0.1 --port 2022 --convert
```

### VoiceMode MCP Integration (Optional)

If you also want voice conversations with Claude Code via the [VoiceMode](https://github.com/mbailey/voicemode) MCP server, you'll need additional services. See [Windows STT Solution Analysis (issue #239)](https://github.com/mbailey/voicemode/issues/239) for the full setup:

- **LiveKit** (port 7880) — Audio capture via WebRTC, bypasses the [Windows temp file locking bug](https://github.com/mbailey/voicemode/issues/135)
- **Kokoro** (port 8880) — Local text-to-speech
- **VoiceMode MCP** — Install from the [native Windows support branch](https://github.com/kindcreator/voicemode/tree/fix/native-windows-support) (PR [#233](https://github.com/mbailey/voicemode/pull/233)) until Windows support is merged upstream

LiveKit Windows binary: [livekit releases](https://github.com/livekit/livekit/releases/latest) (`livekit_*_windows_amd64.zip`)

Kokoro: Clone [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI), install with `uv pip install -e ".[gpu]"`, run with uvicorn.

## Building from Source

Requires .NET 8.0 SDK.

```bash
# Clone
git clone https://github.com/cphillips83/VoiceMute.git
cd VoiceMute

# Run directly
dotnet run --project VoiceMute

# Run with STT
dotnet run --project VoiceMute -- --stt

# Publish self-contained exe
dotnet publish VoiceMute/VoiceMute.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/
```

## Requirements

- Windows 10/11 x64
- For `--stt`: Whisper.cpp server running on port 2022

## License

MIT
