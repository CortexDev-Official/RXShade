# RXShade

Real-time visual filters for the Roblox client, inspired by NVIDIA Freestyle.

**Developer:** CortexDev · **Version:** 1.0.0 beta · **License:** MIT

## What it does

RXShade captures the Roblox window, applies GPU shaders, and displays the result in a click-through overlay. It uses Windows screen capture, similar to OBS and Xbox Game Bar.

It does **not** inject into Roblox, access another process's memory, change game files, or read your keyboard or mouse input.

Some effects are approximations. Real reflections need scene depth data, which a screen capture does not provide, so **Fake Reflection** and **Focus** are marked `APPROX`.

## Requirements

- Windows 10 version 2004 (build 19041) or later; Windows 11 recommended.
- Direct3D 11-compatible GPU.
- Windows 11 build 22000+ to hide the yellow capture border.

## Install

Run `RXShadeSetup.exe`. It installs for your Windows user and does not need administrator rights or a separate .NET runtime. You can uninstall it from **Settings → Apps → Installed apps**.

## How to use

1. Open `RXShade.exe`.
2. Choose Roblox from the **Roblox window** menu. A running client is usually selected automatically.
3. Click **Start**.
4. Pick a preset or adjust the filters.

The overlay follows the Roblox window, hides when the game is minimised, and does not stay over other apps after Alt-Tab. RXShade only captures Roblox windows; it does not capture your whole desktop.

## Filters

Each filter can be switched on or off and adjusted from 0–100%.

| Filter | Effect |
| --- | --- |
| **Sharpen** | Makes details clearer while avoiding already-bright edges. |
| **FXAA** | Smooths jagged edges. |
| **Bloom** | Adds a soft glow to bright areas. |
| **Fake Reflection** (`APPROX`) | Adds glossy highlights, light streaks, and a floor reflection. |
| **Focus** (`APPROX`) | Blurs the image more toward the screen edges. |
| **HDR** | Adds film-like highlights, contrast, and colour depth. It cannot restore detail missing from the original SDR capture. |
| **Colour Grading** | Adjusts exposure, temperature, tint, contrast, saturation, and vignette. |
| **Chromatic Aberration** | Adds a subtle colour fringe near the edges. |
| **Film Grain** | Adds moving film-style grain. |

Presets: `Crisp`, `Vivid`, `Cinematic`, `Dreamy`, `Night`, `Water`, and `HDR`. Each preset sets all filter values, so the same preset gives the same result each time.

## Fake Reflection tips

The effect has three parts: **Wet sheen** adds light streaks across the screen, **Shine** adds small highlights to objects, and **Floor mirror** reflects the scene below the horizon line.

For a good result, start with Wet sheen, adjust Shine, then place the Horizon where the floor meets objects. Use Floor mirror to set reflection strength, Sharpness for detail, and Ripple for water-like movement. The effect is most convincing in open rooms; in corridors, Wet sheen usually does more of the work.

Reflection strength adapts to the surface brightness, helping prevent bright scenes from washing out.

## How it works

```text
Windows.Graphics.Capture
          ↓
   D3D11 filter pipeline
          ↓
 Overlay + in-app preview
```

Capture and shaders use the same D3D11 device, and frames stay in GPU memory. Rendering runs on a worker thread so changing sliders does not block the UI. Shaders are compiled at startup, usually in about 200 ms. The overlay uses a bitblt swap chain because layered windows do not support the flip model used by DXGI.

## Performance test

Run this command to test capture and rendering:

```powershell
.\dist\RXShade.exe --selftest 3
```

The test checks capture alone, passthrough, all 14 filter passes, the click-through overlay, and each preset. It compares frames delivered by Windows with frames RXShade renders, which helps show whether a limit comes from the game or the filter pipeline.

Example results on Intel UHD 770 at 1920×1080:

| Test | Result |
| --- | --- |
| Passthrough | 50.1 rendered / 50.1 delivered FPS |
| All 14 passes | 6.0–10.4 ms per frame (about 96–166 FPS) |
| Cinematic | 5.3–5.6 ms per frame |
| Vivid | 2.6 ms per frame |
| Crisp | 3.1 ms per frame |
| Overlay | 73–76 frames in 1.5 seconds |

Results vary by system. The roughly 50 FPS passthrough result was the desktop's update rate, not a pipeline limit. Normal presets use fewer than 14 passes and took 2.6–5.6 ms per frame in this test.

To test cropping for a specific window, use:

```powershell
.\dist\RXShade.exe --selftest 3 --window "Roblox"
```

## Recording with OBS

OBS **Window Capture** on Roblox will not include RXShade's filters because the overlay is a separate window. Use **Display Capture** to record the filters, but note that it captures the whole screen. You can also capture the RXShade window to show its controls.

## Offline filter test

To render the filters on a sample scene without capturing your screen:

```powershell
.\dist\RXShade.exe --rendertest .\shots
```

This writes PNGs for different filter combinations and is useful for checking visual changes.

## Troubleshooting

- **Errors:** check `%LOCALAPPDATA%\RXShade\error.log`.
- **Overlay missing:** make sure Roblox is not minimised and Roblox or RXShade is the active app.
- **Yellow border:** Windows shows this capture indicator. RXShade can hide it on Windows 11 build 22000+ after capture permission is granted; it cannot be removed on Windows 10.
- **Roblox not listed:** open the window menu again after launching the game.
- **Black output:** try another Roblox window or restart the client. Protected content cannot be captured.
- **Using integrated graphics:** RXShade uses the GPU connected to your display. If your dedicated GPU says no monitor is attached in the self-test, your display cable may be plugged into the motherboard instead.
- **Low or capped FPS:** RXShade cannot render frames the game does not produce. Check the footer's rendered/delivered FPS and run the self-test. In Roblox, set **Frame Rate Cap** to **Uncapped** or at least your monitor's refresh rate. Advanced users can also set `DFIntTaskSchedulerTargetFps` in the Roblox client's `ClientAppSettings.json`; RXShade does not edit Roblox files.


Released under the MIT License. See `LICENSE`.

Built by **CortexDev**.
