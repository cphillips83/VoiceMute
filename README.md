# VoiceMute

Auto-mutes your speakers when holding a hotkey, with optional speech-to-text dictation. Originally designed for [Claude Code's](https://docs.anthropic.com/en/docs/claude-code) push-to-talk voice mode so background audio doesn't get picked up by your microphone. Now works as a global dictation tool in any application.

## Features

- **Speaker mute**: Hold Right Ctrl to fade out speakers, release to fade back in
- **Speech-to-text** (`--stt`): Records your microphone while held, transcribes via local Whisper, and types the result into the active window
- **Auto-manages Whisper**: Automatically starts and stops the Whisper server — no manual setup needed after initial install
- **Model selection** (`--model`): Choose which Whisper model to use (base, small, large-v2, etc.)
- **Global**: Works in any application — terminals, browsers, editors, etc.
- **Daemon mode**: Runs silently in the background

## Download

Grab the latest release from [releases](https://github.com/cphillips83/VoiceMute/releases/latest).

**Requires** [.NET 8.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (download the ".NET Desktop Runtime" for Windows x64).

## Usage

```
VoiceMute                          Mute only (Ctrl+C to stop)
VoiceMute --stt                    Mute + speech-to-text (auto-starts Whisper)
VoiceMute --stt --model base       Use a specific Whisper model
VoiceMute -d [--stt]               Run as background daemon
VoiceMute --kill                   Stop background daemon
```

### Mute Only (default)

Just run `VoiceMute.exe`. Hold **Right Ctrl** to fade out speakers, release to fade back in. The 200ms hold threshold prevents accidental activation during normal typing.

### Speech-to-Text (`--stt`)

```
VoiceMute --stt
```

Hold **Right Ctrl** anywhere to:
1. Mute speakers (so Whisper doesn't hear your audio output)
2. Record from your microphone

Release **Right Ctrl** to:
1. Stop recording
2. Send audio to local Whisper for transcription
3. Type the transcribed text into the active window
4. Unmute speakers

VoiceMute auto-starts the Whisper server when you use `--stt` and stops it when you exit. If Whisper is already running on port 2022, it uses the existing instance.

### Model Selection (`--model`)

```
VoiceMute --stt --model large-v2
```

You can use short names — VoiceMute resolves them automatically:
- `base` → `ggml-base.bin`
- `small` → `ggml-small.bin`
- `large-v2` → `ggml-large-v2.bin`

Without `--model`, VoiceMute picks the best model it finds (prefers larger models).

### Daemon Mode (`-d`)

```
VoiceMute -d --stt
```

Spawns a hidden background process, then exits. Running `-d` again is safe — it kills any existing instance first.

### Stopping (`--kill`)

```
VoiceMute --kill
```

Finds and stops any running VoiceMute background process. Speaker volume is restored and Whisper is shut down.

## STT Setup

The `--stt` flag requires whisper.cpp to be installed. VoiceMute manages starting/stopping the server automatically.

### 1. Install FFmpeg

```
winget install ffmpeg
```

### 2. Install whisper.cpp

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

### 3. Download a Model

Download a model to the same directory:

```bash
cd %USERPROFILE%\voicemode\whisper
curl -L -o ggml-base.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin
```

| Model | Download | Size | Accuracy |
|-------|----------|------|----------|
| `ggml-tiny.en.bin` | [download](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin) | 39 MB | Fast, English only |
| `ggml-base.bin` | [download](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin) | 142 MB | Good balance |
| `ggml-small.bin` | [download](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin) | 466 MB | Better accuracy |
| `ggml-large-v2.bin` | [download](https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v2.bin) | 2.9 GB | Best accuracy |

### 4. Run

```
VoiceMute --stt --model base
```

VoiceMute will:
1. Find whisper-server.exe in `%USERPROFILE%\voicemode\whisper\`
2. Find ffmpeg (checks PATH, then winget install location)
3. Start the Whisper server on port 2022
4. Wait for it to be ready
5. Start listening for Right Ctrl

All Whisper output is logged with `[Whisper]` prefix.

## VoiceMode MCP Integration (Optional)

If you also want two-way voice conversations with Claude Code via the [VoiceMode](https://github.com/mbailey/voicemode) MCP server, you'll need additional services. See [Windows STT Solution Analysis (issue #239)](https://github.com/mbailey/voicemode/issues/239) for the full setup:

- **LiveKit** (port 7880) — Audio capture via WebRTC, bypasses the [Windows temp file locking bug](https://github.com/mbailey/voicemode/issues/135)
- **Kokoro** (port 8880) — Local text-to-speech
- **VoiceMode MCP** — Install from the [native Windows support branch](https://github.com/kindcreator/voicemode/tree/fix/native-windows-support) ([PR #233](https://github.com/mbailey/voicemode/pull/233)) until Windows support is merged upstream

## Building from Source

Requires [.NET 8.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

```bash
# Clone
git clone https://github.com/cphillips83/VoiceMute.git
cd VoiceMute

# Run directly
dotnet run --project VoiceMute

# Run with STT
dotnet run --project VoiceMute -- --stt --model base

# Publish (framework-dependent, requires .NET 8 runtime)
dotnet publish VoiceMute/VoiceMute.csproj -c Release -r win-x64 --self-contained false -o artifacts/
```

## Requirements

- Windows 10/11 x64
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- For `--stt`: whisper.cpp + FFmpeg (see [STT Setup](#stt-setup))

## License

MIT
