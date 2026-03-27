# VoiceMute

Auto-mutes your speakers when holding spacebar in a terminal window. Designed for [Claude Code's](https://docs.anthropic.com/en/docs/claude-code) push-to-talk voice mode so background audio (videos, music, etc.) doesn't get picked up by your microphone.

## The Problem

When using Claude Code's voice dictation (hold Space to talk), your microphone picks up whatever your speakers are playing — YouTube videos, music, meetings, etc. Claude then hears a garbled mix of your voice and background audio.

VoiceMute solves this by automatically fading out your speakers when it detects you're holding spacebar in a terminal, and fading them back in when you release.

## Download

Grab `VoiceMute.exe` from the [latest release](https://github.com/cphillips83/VoiceMute/releases/latest). It's a self-contained single-file executable — no .NET runtime install needed.

## Usage

```
VoiceMute            Run in foreground (Ctrl+C to stop)
VoiceMute -d         Run as a background daemon (no console window)
VoiceMute --kill     Stop the background daemon
```

### Foreground Mode (default)

Just run `VoiceMute.exe`. You'll see a console window with status messages showing when mute/unmute events fire. Press `Ctrl+C` to stop — your volume is always restored on exit.

### Daemon Mode (`-d`)

```
VoiceMute -d
```

This spawns a hidden background process with no console window, then exits. The daemon keeps running until you log off or kill it. Running `-d` again is safe — it kills any existing instance first and starts a fresh one.

This is the recommended way to run VoiceMute day-to-day. Just run it once after you boot up.

### Stopping the Daemon (`--kill`)

```
VoiceMute --kill
```

Finds and stops any running VoiceMute background process. Your speaker volume is restored to its original level.

## How It Works

1. A low-level keyboard hook (`SetWindowsHookEx`) monitors spacebar globally
2. When spacebar is held for **200ms** and a **terminal window** is focused, speakers fade out over **100ms**
3. When spacebar is released, speakers fade back in over **250ms**
4. Normal typing isn't affected — a typical spacebar press during typing is under 150ms

### Supported Terminals

VoiceMute activates when any of these are the focused window:

- Windows Terminal
- PowerShell / pwsh
- Command Prompt (cmd)
- ConEmu
- Hyper
- Alacritty
- WezTerm
- mintty / Git Bash

## Building from Source

Requires .NET 8.0 SDK.

```bash
# Clone
git clone https://github.com/cphillips83/VoiceMute.git
cd VoiceMute

# Run directly
dotnet run --project VoiceMute

# Publish self-contained exe
dotnet publish VoiceMute/VoiceMute.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/
```

## Requirements

- Windows 10/11 x64
- That's it (self-contained build includes the .NET runtime)

## License

MIT
