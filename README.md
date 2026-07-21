<img width="329" height="77" alt="Screenshot 2026-07-21 015704" src="https://github.com/user-attachments/assets/a0461ee7-8981-4633-9e07-9892f67d0496" />

# apo-volume

Native Windows tray volume control for USB DACs that ignore Windows volume (e.g. HiBy FC5),
implemented as an [Equalizer APO](https://equalizerapo.com) preamp controller.

## Why

Some USB DACs advertise USB hardware volume but ignore the host's volume commands, leaving
the Windows volume slider dead — the slider moves, nothing changes. apo-volume intercepts
your volume keys and applies a Windows-style 0–100% volume as digital attenuation in the
Equalizer APO chain, before audio ever reaches the DAC. You get working volume keys, an
on-screen flyout, and a tray slider again. Works alongside
[Peace](https://sourceforge.net/projects/peace-equalizer-apo-extension/) (EQ stays in
Peace, volume lives here).

Known affected hardware: HiBy FC5 (all firmware versions as of mid-2026). Any DAC with the
same symptom — per-app volume works, master volume does nothing — should benefit.

## Download

Grab `ApoVolume.exe` from the [latest release](../../releases/latest). Self-contained
single file — no .NET install required.

Or build from source: .NET 8 SDK, then `powershell -File publish.ps1` (output in `publish\`).

## Requirements

- Windows 10/11
- [Equalizer APO](https://equalizerapo.com) installed on your DAC's playback device
  (run its Configurator and tick your DAC, then reboot — that part is Equalizer APO's
  standard setup, not ours)

## Install

1. Run `ApoVolume.exe`. On first run it creates `apo-volume.txt` in the APO config folder
   and adds `Include: apo-volume.txt` to `config.txt` (elevating once only if needed).
2. Set your DAC's physical volume to your maximum comfortable loudness — once. From now on
   the keyboard controls loudness digitally below that ceiling.
3. Tray menu → Settings… → "Start with Windows" to run at boot.

## Usage

- **Volume Up / Volume Down / Mute keys** (keyboard media keys or USB volume knobs) — 2% per
  press, instant, no debounce even when held.
- **Tray icon**: left-click opens a draggable slider (scroll wheel works too); menu has
  Mute, Settings… (hosts OSD style, position, and animation options), Exit.
- Quitting the app restores normal (for these DACs: dead) Windows volume-key handling.

## Volume keys in games (run as administrator)

Windows does not deliver keystrokes to normal apps while an elevated (admin)
window — many games and anticheats — has focus, so volume keys appear dead
in-game. Fix: Settings → "Run as administrator". apo-volume relaunches
elevated (one UAC prompt), and "Start with Windows" automatically switches
from the registry Run key to a scheduled task so elevated autostart stays
silent at boot.

Known limitation: on laptops, Windows' scheduled-task defaults prevent the
elevated autostart task from starting on battery power (desktop setups are
unaffected). A fix is planned; until then, plug in before rebooting or start
the app manually.

## OSD styles and skins

apo-volume displays volume changes as a floating on-screen indicator (OSD). Four display styles
are available via Settings → Display:

- **Dark pill** — Default. Dark rounded rectangle with white text percentage, positioned top-right
  by default.
- **Windows 11** — Follows your system theme and accent color. Rounded rectangle matching Windows
  design language.
- **Minimal bar** — Horizontal or vertical bar showing fill level only, no text.
- **Custom skin** — Load a folder-based skin from `%APPDATA%\apo-volume\skins\`.

### Position and appearance settings

All styles (including custom skins) support:

- **Position anchor** — Top-left, top-center, top-right, center-left, center, center-right,
  bottom-left, bottom-center, bottom-right.
- **Offset X, Y** — Pixel adjustments from the anchor (useful for multi-monitor or edge spacing).
- **Hide delay** — Seconds until OSD fades automatically (0.5 to 5 seconds).
- **Animation** — Toggle on/off. When on, fade-in and fade-out transitions apply.
- **Animation duration** — Speed of fade transitions (100 ms to 1000 ms).
- **Volume step** — Keys increment by 1%, 2%, or 5% per press.

### Custom skin format

Create a folder at `%APPDATA%\apo-volume\skins\<your-skin-name>\` containing:

- **empty.png** — Image representing 0% volume. Any size and shape (e.g. a cat, bar, circle).
- **full.png** — Image representing 100% volume. Must be identical dimensions to empty.png.
- **skin.json** (optional) — JSON configuration file. Supported fields:
  ```json
  {
    "percentText": "show",  // or "hide" — display percentage number
    "percentX": 10,         // pixel offset of text from left edge
    "percentY": 5,          // pixel offset of text from top edge
    "scale": 1.5            // zoom multiplier (1.0 = original size)
  }
  ```

### How fill works

The OSD fills from left to right as volume increases. At 50% volume, the full.png is revealed
50% across its width; at 100%, fully visible. Transparent pixels in the images are click-through
(dragging the OSD doesn't change volume). Opaque pixels respond to clicks and drags to set volume.

### Skin shape examples

Skins are not limited to bars. Examples:
- **Bar** — Classic horizontal or vertical progress bar.
- **Cat** — A cat outline in empty.png, filled with color in full.png, lighting up as volume rises.
- **Radial** — Circular OSD filling like a pie chart.
- **Custom art** — Any PNG shape; the fill follows the x-axis regardless of shape.

### Fallback behavior

If a custom skin folder is invalid or missing required images, apo-volume falls back to the
Dark pill style and displays a warning in the system tray. Check `%APPDATA%\apo-volume\` for
a log file if skins fail to load.

## Volume model

0% = mute (−120 dB) · 1% = −50 dB · 100% = 0 dB, linear in dB (≈0.5 dB per %).
Keys step 2% per press. Never exceeds 0 dB, so no digital clipping.

## Notes

- Exclusive-mode audio (ASIO / WASAPI exclusive) bypasses Equalizer APO and is unaffected.
- Keys are intercepted system-wide while the app runs; quitting restores normal handling.
- Peace's own pre-amp slider stacks additively with apo-volume's preamp (both `Preamp:` lines
  apply) — keep Peace's pre-amp at 0 dB.
- If Peace rewrites `config.txt`, apo-volume automatically restores its include line.
- If `apo-volume.txt` becomes unwritable (e.g. after an Equalizer APO reinstall), a tray
  balloon warns you instead of failing silently.

## License

[MIT](LICENSE)
