# Codex Monitor V2

V2 replaces the PowerShell/WPF visual layer with a native C# Avalonia + Skia renderer.

## Rendering

- Captures the top-level window directly behind the widget with `PrintWindow`; the widget itself is never captured.
- Sends that live backdrop texture through an `SKRuntimeEffect` pipeline for rounded-lens refraction, chromatic dispersion, blur, Fresnel-style highlights, and soft shadows.
- Does not use `WDA_EXCLUDEFROMCAPTURE`, so the widget remains visible through ToDesk and other remote-desktop software.
- Falls back to the normal transparent window material when a protected application rejects capture.

## Data

- Reads active Codex tasks and cumulative tokens from the current `state_*.sqlite` database.
- Reads task titles from `session_index.jsonl`.
- Reads 5-hour and weekly limits from `codex app-server --stdio`.

## Build

Install the .NET 8 SDK, then run:

```powershell
.\v2-native\build.ps1
```

The self-contained executable is written to `dist\CodexMonitorV2\CodexMonitorV2.exe`.

## Attribution

The liquid-glass renderer is based on the MIT-licensed
[KaranocaVe/LiquidGlassAvaloniaUI](https://github.com/KaranocaVe/LiquidGlassAvaloniaUI).
Its license is included as `LICENSE.LiquidGlassAvaloniaUI`.
