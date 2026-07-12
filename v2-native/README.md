# Codex Monitor V2.1

V2 replaces the PowerShell/WPF visual layer with a native C# Avalonia + Skia renderer.

## Rendering

- Renders the bundled sun/moon scene first, captures that internal backdrop, then keeps all foreground text and controls out of the refracted snapshot.
- Sends the snapshot through an `SKRuntimeEffect` pipeline for rounded-SDF refraction, subtle turbulence displacement, numeric RGB dispersion, frost, Fresnel light, and a single animated pearl rim.
- Exposes `Light`, `Refraction`, `Depth`, `Dispersion`, `Frost`, and `Splay` on `LiquidGlassSurface`.
- Uses ANGLE/Direct3D on integrated or discrete GPUs and falls back to Skia software rendering for unsupported or remote environments.

## Data

- Reads active Codex tasks and cumulative tokens from the current `state_*.sqlite` database.
- Reads task titles from `session_index.jsonl`.
- Reads 5-hour and weekly limits from `codex app-server --stdio`.
- Reads the model catalog dynamically with `model/list`.
- Reads `config/read` and uses `config/batchWrite` only when the user changes the new-task default model or reasoning level. Running-task badges are read-only because another client cannot safely hot-switch an in-progress turn.

## Build

Install the .NET 8 SDK, then run:

```powershell
.\v2-native\build.ps1
```

The self-contained executable is written to `dist\CodexMonitorV2\CodexMonitorV2.exe`.

Run the small shader/data smoke check with:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\v2-native\ShaderSmoke\ShaderSmoke.csproj -c Release
```

## Attribution

The liquid-glass renderer is based on the MIT-licensed
[KaranocaVe/LiquidGlassAvaloniaUI](https://github.com/KaranocaVe/LiquidGlassAvaloniaUI).
Its license is included as `LICENSE.LiquidGlassAvaloniaUI`.
