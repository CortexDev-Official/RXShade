# RXShade

Real-time GPU visual filters for the Roblox client window, in the spirit of the
old NVIDIA Freestyle filters.

**Developer:** CortexDev · **Version:** 1.0.0 beta · **License:** MIT

---

## What it is (and what it is not)

RXShade is a **screen-capture + GPU post-processing** tool. It asks Windows for
a copy of the pixels the Desktop Window Manager has already composited for the
selected window, runs those pixels through HLSL shaders, and draws the result
in a separate window.

This is the same mechanism used by OBS, Xbox Game Bar and the NVIDIA overlay.

RXShade **never**:

- injects a DLL into another process
- opens, reads, or writes another process's memory
- hooks any function inside another process
- modifies any game file
- intercepts or reads keyboard or mouse input

In overlay mode the window is created `WS_EX_TRANSPARENT | WS_EX_LAYERED` and
answers `WM_NCHITTEST` with `HTTRANSPARENT`, so the window manager routes every
click and keystroke to the game underneath. Input never reaches RXShade at all.

> **On reflections:** true screen-space reflection needs the depth and normal
> buffers from inside the rendering engine. A screen capture only ever receives
> the final colour image — that data does not exist here and cannot be
> recovered. The "Fake Reflection" and "Focus" filters are clearly labelled
> `APPROX` in the UI and are stylised approximations, not physically accurate.

---

## Requirements

- Windows 10 2004 (build 19041) or later — Windows 11 recommended
  (Windows 11 22000+ is required to suppress the yellow capture border)
- A Direct3D 11 capable GPU
- .NET 8 SDK (to build only; the published .exe is self-contained)

## Install (Windows)

Download `RXShadeSetup.exe` and run it. The installer is
**per-user** — it installs to `%LOCALAPPDATA%\Programs\RXShade` and needs no
administrator rights. A Start Menu entry and (optionally) a desktop shortcut
are created, and uninstalling from **Settings → Apps → Installed apps** removes
the app cleanly. Settings under `%LOCALAPPDATA%\RXShade` are intentionally left
behind so a reinstall remembers your choices.

The app is fully self-contained: no .NET runtime installation is required.

## Build

```powershell
.\build.ps1
```

Produces a single self-contained executable at `dist\RXShade.exe` (~75 MB, no
.NET runtime required on the target machine).

```powershell
.\build.ps1 -SelfTest
```

Builds, then runs the pipeline benchmark described below.

### Build the installer

```powershell
# after .\build.ps1
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\RXShade.iss
```

Writes `dist\RXShadeSetup.exe`. Inno Setup 6 is the only extra tool
needed, and only to produce the installer — the app itself builds with just the
.NET 8 SDK.

## Usage

1. Launch `RXShade.exe`. On first launch a short welcome screen explains how to
   use it; open it again any time from the help button in the title bar.
2. Pick the Roblox window from the **Roblox window** dropdown (it is
   auto-selected when the client is running — detection is by process name, so a
   browser tab about Roblox will never be listed).
3. Hit **Start**. The filtered picture is drawn in a borderless click-through
   overlay pinned over the game, and shown live in the preview panel beside the
   controls. The overlay tracks the game window's position and size, hides
   itself when the game is minimised, and only shows while the game (or RXShade)
   is the active app, so it never sits on top of your browser after Alt-Tab.
4. Pick a preset or tune individual filters.

RXShade only ever captures Roblox windows, by design. There is no
whole-display capture mode and no separate preview window — the overlay is the
only output, and the in-app panel is just a live read-out of it.

---

## Filters

Each filter is an independent HLSL pass with a toggle and 0–100% sliders.

| Filter | What it does |
| --- | --- |
| **Sharpen** | Contrast-adaptive sharpening (CAS-style). Derives a per-pixel sharpening weight from local min/max, so flat areas sharpen more and near-clipping areas are left alone. |
| **FXAA** | Luminance-based edge anti-aliasing with edge walking and a sub-pixel term. The only AA available when colour is all you have. |
| **Bloom** | Soft-knee bright-pass at quarter resolution, two separable gaussian iterations, additive composite. |
| **Fake Reflection** | `APPROX` — wet/glossy surface simulation in two parts: a screen-wide **sheen** that streaks light sources across floor, walls and ceiling, plus a **floor mirror** below a horizon line. See below. |
| **Focus** | `APPROX` — radial defocus. Blurs by distance from screen centre, not scene depth. |
| **HDR** | ACES filmic tone mapping applied **in linear light**, normalised to its own white point, plus local contrast, highlight recovery, shadow lift and vibrance. |
| **Colour Grading** | Exposure → temperature/tint → contrast → saturation → vignette, in that order. Aspect-corrected vignette. |
| **Chromatic Aberration** | Transverse-only, radius-weighted, so the centre stays sharp. Capped at ~1.2% of frame width. |
| **Film Grain** | Animated, quantised into pixel-sized cells so it looks identical at any resolution, and weighted to mid-tones the way real film grain is. |

### Getting the Fake Reflection to look right

The pass has **three independent components**, because a reflective room shows
three different things:

| | What it does | Where |
| --- | --- | --- |
| **Wet sheen** | Stretches light sources into vertical streaks | **Whole screen** — floor, walls *and* ceiling |
| **Shine** | Tight specular highlights on the objects themselves | **Whole screen** |
| **Floor mirror** | A sharp mirrored copy of the scene | Below the horizon only |

**Shine is the opposite of bloom.** Bloom *spreads* light outwards into a haze;
a glossy surface *concentrates* it into a tight highlight. Subtracting the
local surround from the local peak keeps only the core of each highlight — that
is what separates "shiny" from merely "bright".

**Sharpness** controls how much detail survives in the floor mirror. A blur
gather always loses high frequencies no matter how small the radius, so the
sharp result is recovered by blending the unblurred tap back in. High =
polished marble; low = rough wet concrete.

**Wet sheen is the one that makes a room look wet.** It needs no geometry —
only a directional gather of the bright parts of the image — so it works on
walls and ceilings where a horizon-based mirror fundamentally cannot. In a
corridor lit by wall lamps it is doing almost all of the visual work.

**Floor mirror** adds real reflected *shape* on the ground. Note its geometric
limit: mirroring about a horizontal line is only correct for a floor plane seen
edge-on. Looking straight down a corridor, ceiling lamps far ahead cannot
mirror onto the floor near your feet, because that would need actual ray
tracing against depth. Expect the mirror to contribute most in open rooms and
the sheen to dominate in corridors.

Controls, in order of impact:

- **Wet sheen** — screen-wide streak strength. Start here.
- **Shine** — specular highlights on objects. This is what makes things look
  polished rather than just lit.
- **Horizon** — the ground line, for the floor mirror. Drag it until it sits
  where objects meet the floor. If it is wrong the mirror reflects the floor
  onto itself and turns to grey smear.
- **Floor mirror** — strength of the mirrored copy.
- **Sharpness** — reflection detail. Also shortens the sheen streaks, since a
  smoother surface smears light less.
- **Ripple** — water movement. Two waves at incommensurate frequencies so it
  never visibly loops; frequency compresses towards the horizon for perspective.

### Why it cannot blow the screen out

Reflection strength is measured **relative to the receiving surface**, not
against a fixed brightness threshold. A fixed threshold meant that on a bright
screen — a game menu, a snow level — every pixel counted as a light source,
every pixel got a streak, and the frame washed out to white.

Comparing the reflection against the local surface makes it self-limiting: a
white screen has no headroom so the effect contributes nothing, while a dark
floor under a lamp has plenty so it goes strong. The render-test suite includes
a bright scene with every reflection slider at 100% specifically to keep this
regression from coming back.

### About the HDR filter

The captured frame is 8-bit SDR — the dynamic range is already gone, and no
filter can invent it back. What this pass does is the *tone mapping half* of an
HDR pipeline, which is what people are actually reacting to when they say a
shader mod "looks HDR": a filmic highlight rolloff plus local contrast.

Two details that must be right or it greys the image out:

- **ACES is defined on linear light.** The captured frame is display-encoded
  sRGB. Feeding sRGB straight into the curve is simply wrong and washes out
  every bright area. This pass converts to linear, tone maps, and converts back.
- **The curve is normalised by its own white point.** Raw ACES maps 1.0 → 0.80,
  so white would come out light grey. Dividing by `ACES(1.0)` restores white
  while keeping the shoulder.

`Punch` (local contrast) is the control that produces the signature look.

### Presets

`Crisp` · `Vivid` · `Cinematic` · `Dreamy` · `Night` · `Water` · `HDR`

Every preset writes every field, including the disables, so clicking the same
preset twice from different starting points always gives the same result.

---

## Architecture

```
Windows.Graphics.Capture  ──►  ID3D11Texture2D (already in VRAM)
                                      │
                                      ▼
                          FilterPipeline (ping-pong RGBA16F)
                                      │
              ┌───────────────────────┴───────────────────────┐
              ▼                                               ▼
      overlay swapchain                          in-app preview swapchain
```

Key decisions:

- **`Direct3D11CaptureFramePool.CreateFreeThreaded`** — the render loop runs on
  a WGC worker thread, so the WPF UI thread is never on the critical path and
  dragging a slider cannot drop a frame.
- **One device for capture and rendering** — capture frames land on the same
  D3D11 device the shaders run on. Nothing is ever copied to system memory.
- **Default DXGI adapter, deliberately** — capture frames come from DWM, which
  runs on the adapter driving the display. Forcing the discrete GPU on a hybrid
  machine would add a cross-adapter PCIe copy to every frame.
- **The live thumbnail is just another output surface** — one extra fullscreen
  blit, not a second pipeline run, and no CPU readback. (A `WriteableBitmap`
  preview would have cost a full-frame GPU→CPU transfer every frame.)
- **Bitblt swap effect for the overlay** — DXGI refuses flip-model swap chains
  on `WS_EX_LAYERED` windows, so `Presenter` falls back automatically. The
  self-test proves this path on the actual machine.
- **Runtime shader compilation** via inbox `d3dcompiler_47.dll`, with the
  shaders as embedded resources. Keeps the single-file publish clean and needs
  no Windows SDK at build time. All shaders compile in ~200 ms, once, at startup.
- **Zero CPU readback in the hot path.** The only data leaving the GPU is the
  GPU timestamp query, read back three frames late and never flushed.

---

## Self-test

```powershell
.\dist\RXShade.exe --selftest 3
```

A headless benchmark that isolates capture cost from shader cost. It measures
**delivered** frames against **rendered** frames, which is the distinction that
matters: a source producing 30 FPS cannot be turned into 60, and no amount of
shader optimisation changes that.

Phases:

1. **Capture only** — no output registered, so nothing is rendered or
   presented. This is the raw rate Windows.Graphics.Capture actually delivers,
   and it is the ceiling everything else is compared against.
2. **Passthrough**, zero filters — pure capture + present cost.
3. **Full filter chain** — all 14 passes.
4. **Click-through overlay path** — proves the layered-window swap chain works.
5. **Preset sweep** — GPU cost of every preset.

Phase 1 matters more than it looks. It is how you tell "the source only
produces N FPS" apart from "our present is costing us the frame rate". If phase
1 is already at the display's refresh rate and the later phases are not, RXShade
is the limit; if phase 1 *is* the low number, the game is. The self-test prints
both the capture ceiling and the display refresh, and calls this out explicitly
when they diverge.

Measured on an **Intel UHD 770 (integrated)** at 1920×1080:

| Phase | Result |
| --- | --- |
| Passthrough | renders 50.1 of 50.1 delivered FPS |
| Full chain (14 passes, everything on) | 6.0–10.4 ms/frame → **96–166 FPS ceiling** |
| Cinematic preset (12 passes) | 5.3–5.6 ms/frame |
| Vivid preset (9 passes) | 2.6 ms/frame |
| Crisp preset (4 passes) | 3.1 ms/frame |
| Click-through overlay | 73–76 frames in 1.5 s, bitblt swap effect |

The full-chain figure varies run to run because this iGPU is simultaneously
compositing the desktop and decoding video. No preset enables all 14 passes at
once — real usage sits in the 2.6–5.6 ms band, comfortably inside a 60 FPS
frame budget even on integrated graphics.

The ~50 FPS figure is the idle desktop's update rate, not a pipeline limit —
rendered frames exactly equal delivered frames, meaning nothing is dropped. On a
discrete mid-range GPU the shader chain costs a small fraction of this.

If the self-test reports a problem it names which half is at fault rather than
just printing a number.

To exercise the client-area crop path (which is what stops a captured title bar
appearing inside the overlay), point it at a specific window:

```powershell
.\dist\RXShade.exe --selftest 3 --window "Roblox"
```

## Recording with OBS

**Capturing the Roblox window in OBS will not show the filters**, and this
cannot be fixed. Window capture reads the target window's own surface; the
overlay is a separate window that the compositor draws on top of it. Including
it would require injecting into the game process, which RXShade never does.

One setup that works:

| OBS source | Result |
| --- | --- |
| **Display Capture** | Filters recorded, whole screen |

The RXShade window stays fully visible to capture APIs, so OBS can also record
it directly if you prefer to show the controls.

## Developer tool: offline filter render

```powershell
.\dist\RXShade.exe --rendertest .\shots
```

Runs the real shader chain over a **synthetic** scene (coloured blocks standing
on a floor, a bright sun) and writes one PNG per filter combination. It never
reads the screen, so it is safe to run anywhere.

This exists because filter work needs a repeatable visual check. It is how the
reflection rewrite was validated, and how the "does colour grading still apply
inside the reflected band?" question was answered definitively rather than by
eye. Use it before and after any shader change to confirm nothing regressed.

---

## Troubleshooting

**Unhandled errors** are written to `%LOCALAPPDATA%\RXShade\error.log`.

**The overlay does not appear** — it deliberately hides unless the captured
game (or RXShade) is the foreground app, and while the game is minimised.

**A yellow capture border appears** — this outline is drawn by **Windows, not
by RXShade**. It is an OS privacy indicator shown around anything being
captured, and OBS and Xbox Game Bar trigger the identical border.

RXShade removes it on Windows 11 (build 22000+) by requesting borderless
capture consent via `GraphicsCaptureAccess.RequestAccessAsync`, then setting
`IsBorderRequired = false`. Note that *setting the property alone does nothing*
— without the access request it fails silently and the border stays. The
self-test reports whether consent was granted:

```
Borderless capture       : granted (no yellow border)
```

On Windows 10 the border cannot be removed by any application.

**No Roblox window is listed** — the picker re-scans every time you open it, so
launching RXShade before Roblox is fine: just open the dropdown again. Only
windows whose owning process is a Roblox client are listed.

**Black output** — some windows refuse capture, and hardware-protected content
(DRM video) always captures black. Try a different Roblox window or restart the
client.

**"It says my integrated GPU, not my good graphics card"** — RXShade uses the
adapter that is *driving your display*, on purpose. Capture frames are produced
by the Desktop Window Manager, which runs on the display adapter; using any
other GPU would force every frame across PCIe twice per frame (in for shading,
back out for presentation) and be slower, not faster.

Run the self-test to see where your monitors actually are:

```
Installed adapters:
  [0] Intel(R) UHD Graphics 770       128 MB   1 monitor(s) attached  <-- drives the display
  [1] NVIDIA GeForce RTX 3080      10053 MB   no monitor attached
```

If your discrete card shows **"no monitor attached"**, your display cable is in
the motherboard rather than the graphics card. Moving it to the graphics card
makes that card drive the desktop — which speeds up the game far more than it
speeds up RXShade, and RXShade will then pick it up automatically.

**"The framerate is capped"** — the footer shows `rendered/delivered FPS
dropping` *only* when RXShade cannot keep up. A single number means it is
consuming every frame the game produces, so the ceiling is the game's own frame
cap or render performance, not RXShade's. The footer also shows the display's
refresh rate, which is the hard ceiling on anything capture-based.

**"It is stuck at exactly half my monitor's refresh rate"** — this is the most
common report, and it is almost always the game, not RXShade. Roblox ships with
a frame-rate cap, and on high-refresh panels that cap can land on half the
refresh rate (for example 50 on a 100 Hz display). RXShade is capture-only, so
it can never exceed what the game actually draws.

To confirm it, run the self-test. **Phase 1 (capture only)** registers no output
at all — no rendering, no presenting — so it reports the raw rate the game is
producing:

```powershell
.\dist\RXShade.exe --selftest 3 --window "Roblox"
```

If that number equals the footer number, RXShade is rendering every frame it is
given and the game is the limit. Raise it on the game's side:

- **Roblox in-game:** the `Frame Rate Cap` option in the Roblox settings menu
  (choose Uncapped, or a value at or above your monitor's refresh rate).
- **Client flags:** create `ClientAppSettings.json` in the Roblox version's
  `ClientSettings` folder containing
  `{ "DFIntTaskSchedulerTargetFps": 144 }` (or your refresh rate). This is a
  Roblox client file, edited by you — RXShade never touches Roblox files.

Once the game is producing more frames, RXShade will follow it up to the
display's refresh rate. There is no frame cap in RXShade itself.

---

## Project layout

```
src/RXShade/
  Capture/      WGC session, window enumeration, capture targets
  Graphics/     D3D11 device, swap chains, filter pipeline, GPU timer
  Interop/      Win32 P/Invoke, WinRT interop, raw render-surface window
  Models/       Filter settings, presets, commands, preferences
  Shaders/      HLSL, one file per pass, embedded as resources
  ViewModels/   MainViewModel
  Views/        Overlay controller, HwndHost render surface, converters
installer/      Inno Setup script for the per-user installer
tools/          make-icon.ps1 — builds RXShade.ico from the source PNG
```

---

## License

RXShade is released under the MIT License. See `LICENSE`.

Built by **CortexDev**.
