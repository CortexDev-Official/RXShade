# Changelog

All notable changes to RXShade are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions use
`MAJOR.MINOR.PATCH` with a beta label while the app is in public testing.

## [1.0.2 Beta]

An interface and correctness pass. No shader changes: the picture RXShade
produces is identical to 1.0.1, so existing looks are unaffected.

### Fixed

- **The executable no longer asks for elevation.** An `asInvoker` execution level
  is now declared explicitly in the manifest, so the app can never silently
  demand administrator rights.

- **Filter settings are no longer lost on exit.** Sliders, switches and the
  chosen preset were held only in memory, so closing RXShade threw away
  everything the user had tuned. They are now written to
  `%LOCALAPPDATA%\RXShade\preferences.txt` and restored on launch. Writes are
  batched (750 ms) so dragging a slider does not touch the disk on every frame,
  and flushed once more on shutdown. A file written by an older build still
  loads; unknown keys are ignored individually rather than discarding the file.
- **The preset chip no longer lies.** It used to stay lit after the user edited
  a filter by hand, so the UI could read "Vivid" while showing something else.
  Brightness and presence were the same signal for two different meanings —
  "this is a preset" and "this preset is currently applied". Any manual change
  now clears the chip, because the preset has stopped describing the picture.
- **The Help panel no longer needs scrolling.** It was taller than the window at
  the default size, so reaching its own dismiss button meant scrolling. The copy
  is tighter, the panel is wider, and the button now shares a row with the GPU
  line. Measured: the card needs 387px in a 610px viewport, so it also fits at
  the minimum window height (580px).
- **"Water" now means water.** The old Water preset set Ripple to 0 and
  Reflection sharpness to 100 — a dead-flat, fully sharp mirror, which is a
  polished floor rather than water. It is now **Mirror**, and **Water** is a new
  preset that actually ripples.
- A resource lookup that could silently fail is gone: the scrollbar template no
  longer depends on a `StaticResource` that does not resolve from inside a
  `ControlTemplate`, which left the system scrollbar (17px, arrow buttons, light
  system grey) in place of the thin one.

### Known issue

- The portable single-file `RXShade.exe` unpacks four native libraries
  (`wpfgfx_cor3`, `PresentationNative_cor3`, `D3DCompiler_47_cor3`,
  `vcruntime140_cor3`) into `%TEMP%\.net\RXShade\` before it starts. This is
  unavoidable for a one-file self-contained WPF app — the libraries are native
  and cannot be loaded out of the bundle in place, and publishing without the
  extraction emits them as loose files, at which point it is no longer one file.
  If `%TEMP%` is locked down (Controlled Folder Access, a redirected or
  read-only temp, antivirus, or a cleanup tool wiping it mid-session) the process
  exits with *"Failure processing application bundle"* before any app code runs,
  so it cannot show a message. Running as administrator appears to fix it only
  because the elevated account gets a different `%TEMP%`. **Installing through
  `RXShadeSetup.exe` avoids this entirely**: the installer lays the app down as a
  normal folder and nothing is extracted at launch.

### Added

- **Dark mode**, with a sun/moon switch in the title bar. The choice is
  remembered between sessions. The whole interface reads its colours through
  `DynamicResource`, so a swap repaints every control without rebuilding a
  window: styles, templates and layout are shared by both palettes, which makes
  them consistent by construction rather than by two sets of markup. The dark
  palette is a blue-black rather than pure black, and its colours were measured
  to the same WCAG AA contrast standard as the light one.
- **Mirror** preset, split out of the previous Water preset.
- Settings persistence, described under Fixed.

### Changed

- **Clearer filter names.** Each row now says what it changes rather than
  borrowing the name of the algorithm behind it:
  `Sharpen` → **Sharpness**, `FXAA` → **Anti-aliasing**, `Wet reflection` →
  **Reflections**, `Focus` → **Depth blur**, `HDR` → **Tone mapping**,
  `Colour grading` → **Colour grade**, `Chromatic aberration` → **Colour
  fringing**. Names that were already plain English and unambiguous (Bloom, Film
  grain) were left alone, as were `CAS` and `ACES`, where the acronym is the
  term of art.
- **Clearer parameter names.** `Intensity` appeared five times across different
  filters meaning five different things. Each is now specific: **Sharpen
  amount**, **Edge smoothing**, **Glow strength**, **Brightness cutoff**,
  **Mirror strength**, **Surface gloss**, **Reflection detail**, **Blur
  amount**, **Sharp centre**, **Local contrast**, **Fringing amount**, **Grain
  amount**, **Grain size**. `Mirror strength` replaces `Floor mirror` because
  the control drives the whole mirrored band, not only the floor.
- **Water** is now a genuine wet look: wave 58, softened reflection detail,
  sheen 55, and a cool colour cast.
- Status pill distinguishes **Live** from **No filters**, so a running capture
  with every filter off no longer claims to be filtering.
- Footer names the preset currently applied, so the chips above say what is
  available and the footer says what is on.
- A little more motion, deliberately kept small: 3% press feedback on the preset
  chips, a 140ms fade with a 10px rise when the Help panel opens, and a 200ms
  opening fade on the window. All of it uses short decelerating eases with no
  bounce or overshoot, and runs on render transforms so nothing re-measures
  mid-animation.

## [1.0.1 Beta]

### Fixed

- Contrast: `TextDim` measured 3.46:1 against the background and failed WCAG AA
  at the sizes it was used (footer, adapter name, preview hint). It is now
  4.53:1. White on the Start button was 3.75:1 and is now 4.92:1. `TextSecondary`
  improved from 6.31:1 to 7.03:1.
- Removed a dead `ActiveBorderConverter` and two unreferenced theme styles.

### Changed

- Filter rows reveal their sliders only while the filter is on. With every
  filter off the controls list no longer scrolls at all; previously it ran about
  two and a half times the panel height.
- Presets are laid out as a steady two-column grid instead of a wrapped row.
- Sliders have a real pointer target (5px rule, 18px thumb, 22px of hit area).
- Added keyboard focus rings; templated controls previously drew none.
- The help panel scrim is now opaque enough to actually separate the modal from
  the controls behind it.

## [1.0.0 Beta]

First public release. Real-time GPU shader filters over the Roblox window via
`Windows.Graphics.Capture`, with a click-through overlay, nine filters and seven
presets.
