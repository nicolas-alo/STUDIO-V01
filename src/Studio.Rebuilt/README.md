# STUDIO V01 — Clean Rebuild

This project is a clean implementation path. It does not use the legacy `MainWindow.xaml.cs` pipeline.

## Architectural rule

There is one program timeline and one frame contract: `Core/ProgramFrame.cs` (1920×1080 BGRA32). Sources will feed the compositor; the compositor will publish the final frame to `ProgramFrameBus`. Preview, NDI, SRT, and YouTube must consume that same published frame. Output adapters must not compose overlays independently.

The bus rejects frames from invalidated generations and out-of-order sequence numbers. Changing a source increments the generation so queued frames from the previous source can be discarded.

## Current state

Implemented in this initial foundation:
- Separate WPF project and operator UI, isolated from the old entry point.
- Source/overlay/output state model.
- Shared program-frame contract and thread-safe latest-frame bus.
- Source generation invalidation and stale-frame rejection.
- UI controls for RTSP address, NDI placeholder, 2×2 quad selection, HTML address, overlay toggles, and output toggles.

Not implemented yet and therefore **not operational**:
- RTSP decoding and camera discovery.
- NDI source discovery, receiver, and sender integration.
- Four-input quad compositor.
- Persistent animated WebView2 HTML capture.
- Actual overlay rendering into the program pixels.
- FFmpeg SRT/YouTube output and audio capture.
- Automated end-to-end frame parity tests and Windows hardware validation.

## Build

From a Windows machine with .NET 8 SDK:

```powershell
dotnet build src/Studio.Rebuilt/Studio.Rebuilt.csproj -c Release
```

The old project remains untouched while this new implementation is developed. Do not distribute this scaffold as a finished production build.
