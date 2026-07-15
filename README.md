# Codex Monitor

<p align="center">
  <strong>A compact liquid-glass desktop monitor for Codex on Windows.</strong><br>
  Active tasks, real model and reasoning state, cumulative tokens, and usage limits in one local-first panel.
</p>

<p align="center">
  <img src="https://img.shields.io/github/v/release/Yxianshe/Codex-Monitor?display_name=tag&style=flat-square" alt="Release">
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-4B8BFF?style=flat-square" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/Architecture-x64-6EA8FF?style=flat-square" alt="Windows x64">
  <img src="https://img.shields.io/github/license/Yxianshe/Codex-Monitor?style=flat-square" alt="MIT License">
</p>

<p align="center">
  <a href="https://github.com/Yxianshe/Codex-Monitor/releases/download/v2.1.4/Codex-Monitor-v2.1.4.exe"><strong>Download V2.1.4</strong></a>
  ·
  <a href="https://github.com/Yxianshe/Codex-Monitor/releases">All releases</a>
  ·
  <a href="#build-from-source">Build from source</a>
</p>

<p align="center">
  <img src="assets/readme/codex-monitor-v2.1.3-hero.png" width="100%" alt="Codex Monitor V2.1.3 showing the static sun scene, moon scene, and background settings page">
</p>

## V2.1.4 at a glance

V2.1.4 keeps the polished **static-scene architecture** and adds an image-free glass mode. The new neutral material uses a low-saturation gray-blue optical foundation instead of a black fallback, while the thicker full-window and card lenses improve white-text clarity, rim light, and perceived depth.

This release also includes the V2.1.3 maintenance fixes: expanded title dragging, effective vertical positioning at `1.00x`, local usage-limit fallback, large-rollout active-task detection, safe scrollbar spacing, a fixed purple pin state, and priority lightning inside the reasoning capsule. V2.1.3 remains available unchanged in Releases.

<p align="center">
  <img src="assets/readme/codex-monitor-v2.1.4-glass-only.png" width="76%" alt="Codex Monitor V2.1.4 in the neutral glass-only mode">
</p>

- **Active task monitor** — concise task titles with the real current model and reasoning level.
- **Per-task selection** — select a task, then set the model and reasoning preference used by its next turn.
- **Priority indicator** — a purple lightning mark identifies the 1.5× priority path.
- **Cumulative tokens** — the `Token` control switches task rows between model state and token totals.
- **Usage limits** — 5-hour and weekly remaining quota, used percentage, countdown, and exact reset time.
- **Static sun and moon scenes** — automatic day/night selection plus instant manual switching.
- **Glass-only mode** — remove the scene image while retaining a bright neutral frosted/refractive material.
- **Background settings** — disable imagery, choose an image, restore the built-in scene, and adjust zoom and position with explicit saving.
- **English / 中文** — English by default, with one-click language switching.
- **Native desktop behavior** — always-on-top, minimize, close, fast title dragging, and resizing from every edge.
- **Monochrome glass icon** — a thicker, high-contrast application mark that stays readable over either scene.

> Animated sun and moon rendering is intentionally reserved for **V2.2.0** so it can be designed and optimized as a separate rendering system.

## Liquid-glass design

The interface is built as a layered material rather than a flat blur:

1. a static high-detail scene;
2. a bounded refractive capture inside each glass surface;
3. restrained frost and tint;
4. convex depth, inner shading, and soft physical shadow;
5. a continuous highlight rim with subtle spectral dispersion;
6. crisp text and icons rendered above the distorted sampling pass.

The main cards are optically thicker than the inset controls, while dropdowns, buttons, and sliders use a lighter frosted treatment. Window corners use one native outer clip to avoid double borders, black halos, and rectangular remnants.

## Background settings

Open the gear button to configure the current sun or moon scene:

- choose a custom PNG, JPG, WebP, or BMP;
- switch to an image-free neutral glass material;
- restore the built-in image;
- adjust planet size;
- move the image horizontally or vertically without exposing an empty edge;
- save the current values as the next-launch default.

Sun and moon settings are stored separately in `%LOCALAPPDATA%\CodexMonitorV2\scene-settings.json`.

## Download and run

1. Download [`Codex-Monitor-v2.1.4.exe`](https://github.com/Yxianshe/Codex-Monitor/releases/download/v2.1.4/Codex-Monitor-v2.1.4.exe).
2. Double-click the executable. It is a self-contained Windows x64 build; no separate .NET installation is required.
3. Keep Codex Desktop signed in and use at least one task so local status data is available.

Windows SmartScreen may warn about an unsigned personal-development build. Verify that the file came from this repository before running it.

## Controls

| Control | Action |
|---|---|
| Gear | Open background settings |
| `Token` | Toggle model/reasoning details and cumulative task tokens |
| Scene button | Switch between the static sun and moon scenes |
| `中` / `EN` | Switch between English and Chinese |
| Pin | Toggle always-on-top; the pinned state is highlighted in purple |
| `—` | Minimize |
| `×` | Exit |

Drag the open title area to move the window. All four edges and corners support native resizing.

## Local data and privacy

| Information | Local source |
|---|---|
| Task titles | Codex `session_index.jsonl` |
| Active state and cumulative tokens | Codex `state_5.sqlite` |
| Model, reasoning level, and detailed tokens | Local Codex rollout logs |
| Usage limits and reset time | Local Codex App Server rate-limit state, with a local rollout-log fallback |
| New-task model and reasoning defaults | Local Codex configuration, written only after selection |

Codex Monitor includes no telemetry, analytics SDK, account upload, or third-party data service. Task titles, token totals, and usage data remain on the computer.

## Build from source

Requirements: Windows 10/11, .NET 8 SDK, PowerShell 5.1 or newer.

```powershell
cd .\v2-native
.\build.ps1
```

The self-contained single-file application is written to `dist/CodexMonitorV2/CodexMonitorV2.exe`.

Main projects:

- `v2-native/CodexMonitorV2` — application UI, local data reader, settings, and Windows interaction.
- `v2-native/LiquidGlassAvaloniaUI` — reusable Skia/Avalonia liquid-glass material and lens renderer.
- `tools/build_readme_hero.py` — reproducible README product-image compositor using anonymized application captures.

## Roadmap

### V2.2.0 — dynamic scene rendering

The next rendering release will explore independently designed animated sun and moon scenes with a smooth GPU path, integrated-graphics support, remote-desktop fallback, reduced-motion behavior, and stable composition during drag and resize.

## Open-source acknowledgements

- [LiquidGlassAvaloniaUI](v2-native/LICENSE.LiquidGlassAvaloniaUI)
- SDF lens ideas informed by [Cloudy](https://github.com/skydoves/Cloudy) and [FletchMcKee/liquid](https://github.com/FletchMcKee/liquid)

## License

[MIT License](LICENSE). Contributions and derivative projects are welcome.
