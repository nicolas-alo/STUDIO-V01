# STUDIO V01 — Clean Rebuild

This project is an isolated clean implementation path. It does not use the legacy video pipeline.

## Local source configuration

The application can load a JSON file using **ABRIR CONFIG.JSON**. It accepts a `Sources` array with `Name`, `Type` (`RTSP`, `HTML`, or reserved `NDI`) and `Url`. Property names are case-insensitive. Invalid entries are skipped and the UI reports how many valid sources were loaded.

At startup it also looks for `config.local.json` beside the executable or in the current working directory (including a `config/` subfolder). This file is intentionally ignored by Git because RTSP URLs may contain usernames and passwords. Use `config.example.json` as a structure template and create your own local copy with real addresses and credentials.

The configuration's `CycleSeconds` value is retained by the JSON schema for compatibility, but automatic cycling is not implemented or enabled.

## Architectural rule

There is one program timeline and one frame contract: `Core/ProgramFrame.cs` (1920×1080 BGRA32). Sources will feed the compositor; the compositor will publish the final frame to `ProgramFrameBus`. Preview, NDI, SRT, and YouTube must consume that same published frame. Output adapters must not compose overlays independently.

The bus rejects frames from invalidated generations and out-of-order sequence numbers. Changing a source increments the generation so queued frames from the previous source can be discarded.

## Current state

Implemented:
- Separate WPF project and operator UI, isolated from the old entry point.
- Shared program-frame contract and thread-safe latest-frame bus.
- JSON source configuration loader, source list and selection for RTSP/HTML/reserved NDI.
- Local configuration files excluded from version control.

Still not operational:
- RTSP decoding and camera discovery.
- NDI source discovery, receiver, and sender integration.
- Four-input quad compositor.
- Persistent animated WebView2 HTML capture.
- Actual overlay rendering into program pixels.
- FFmpeg SRT/YouTube output and audio capture.
- Automated end-to-end frame parity tests and Windows hardware validation.

## Build

On Windows with .NET 8 SDK:

```powershell
dotnet build src/Studio.Rebuilt/Studio.Rebuilt.csproj -c Release
```

Do not distribute this scaffold as a finished production build.
