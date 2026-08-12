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

## First-run setup

apo-volume needs [Equalizer APO](https://equalizerapo.com) (free, open-source, GPLv2). If it
isn't installed, apo-volume opens a **setup guide** on first run: it downloads the official
installer for you, starts it, and tells you exactly what to do in the one step that needs you —
ticking your speakers/headphones in Equalizer APO's Configurator. Afterwards it verifies the
install against your current playback device and offers a one-click **audio restart** (a reboot
substitute). The guide can be reopened anytime from Settings → **Setup guide…**, which also
shows a live status line and an **Open Configurator** shortcut for enabling other devices.
apo-volume never bundles Equalizer APO — the installer always comes from its official home.

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

- **Position anchor** — 8 positions: the four corners plus the four edge midpoints
  (top-left, top-center, top-right, left-center, right-center, bottom-left, bottom-center,
  bottom-right). Dead-center is intentionally not offered.
- **Offset X, Y** — Pixel adjustments from the anchor (useful for multi-monitor or edge spacing).
- **Hide delay** — Seconds until OSD fades automatically (0.5 to 5 seconds).
- **Animation** — Toggle on/off. When on, fade-in and fade-out transitions apply.
- **Animation duration** — Speed of fade transitions (100 ms to 1000 ms).
- **Volume step** — Keys increment by 1%, 2%, or 5% per press.

### Custom skin format

Create a folder at `%APPDATA%\apo-volume\skins\<your-skin-name>\` containing:

- **empty.png** — Image representing 0% volume. Any size and shape (e.g. a cat, bar, circle).
- **full.png** — Image representing 100% volume. Must be identical dimensions to empty.png.
- **skin.json** (optional) — JSON configuration file. Field names are case-insensitive.
  Supported fields:
  ```json
  {
    "percentText": {
      "show": true,   // whether to display the percentage number
      "x": 10,        // pixel offset of text from left edge
      "y": 5,         // pixel offset of text from top edge
      "color": "#FFFFFFFF",     // text color (#AARRGGBB or #RRGGBB); default white
      "fontFamily": "Segoe UI", // any installed font
      "fontSize": 14,           // clamped 4–200
      "bold": true,
      "outlineColor": "#FF000000", // omit for no outline
      "outlineWidth": 2,           // clamped 0–20
      "shadowColor": "#80000000",  // omit for no shadow
      "shadowBlur": 4,             // clamped 0–50
      "shadowDepth": 2             // clamped 0–50
    },
    "scale": 1.5,       // zoom multiplier (1.0 = original size, clamped to 0.25–4.0)
    "fps": 12,          // sprite-sheet playback rate (default 10, clamped to 1–60)
    "emptyFrames": 1,   // sprite-sheet frame count for empty.png (default 1)
    "fullFrames": 8,    // sprite-sheet frame count for full.png (default 1)
    "fillStartX": 120,  // image-pixel x where 0% sits (default 0) — for bars that occupy
    "fillEndX": 680     // only part of a wider image; default is the image width
  }
  ```
  Omitting `percentText` hides the percentage number. Omitting `scale` defaults to `1.0`.

### Animated skins

Each layer can animate, three ways — all behave identically once loaded:

- **GIF** — name the file `empty.gif`/`full.gif` instead of `.png` (a `.png` with the same
  name wins if both exist). Frame timing comes from the GIF itself. Note GIF transparency is
  1-bit: hard edges, no soft shadows.
- **Sprite-sheet PNG** — stack the frames vertically in one PNG (equal heights) and declare
  `emptyFrames`/`fullFrames` + `fps` in skin.json. Full 8-bit alpha.
- **PNG frame sequence** — in the skin designer, click **Frames…** and multi-select your
  exported frames (e.g. Photoshop's *Export Layers to Files*); the sheet is assembled for you.

Layers animate independently and loop; a static layer plus an animated one is fine. The two
layers' *frame* sizes must match, and everything else (fill clip, mute, click-through and
hit-testing, the percent number) works exactly as for static skins — the clickable shape is the
union of every frame's opaque pixels. Animation only runs while the OSD is on screen.
APNG is not supported (WPF has no decoder for it); use a sprite sheet for full-alpha animation.

### Sharing skins

Skins travel as plain zip files: **Export…** in the skin designer produces one, and
**Import skin…** (Settings) or **Import…** (designer) installs one — the skin's name is taken
from the zip filename. Only the known skin files are ever extracted from a zip.

#### `apo-volume://` install links (for websites and forums)

Sites sharing skins can offer a one-click install link. The URL contract:

```
apo-volume://install-skin?url=<https URL to the skin zip>&name=<skin name>&sha256=<hex>
```

- `url` — **required.** Direct `https` link to the skin zip (percent-encode it when embedding
  in the link). Plain `http`, `file`, credentials in the URL, and zips over 20 MB are rejected.
- `name` — optional. Skin (folder) name to install as; defaults to the zip's filename stem.
  Follows the same rules as names in the skin designer.
- `sha256` — optional but recommended. Hex SHA-256 of the zip; the download is rejected if the
  bytes don't match.

Clicking a link never installs anything by itself: apo-volume always shows a confirmation
dialog naming the skin and the host first, with **Install & Use** / **Install only** /
**Cancel**. Malformed links produce only a tray balloon. The scheme is registered per-user at
startup and can be turned off with Settings → **Enable apo-volume:// links**. Actions other
than `install-skin` are reserved for future versions.

Example (HTML):

```html
<a href="apo-volume://install-skin?url=https%3A%2F%2Fexample.com%2Fskins%2Fneon-bar.zip&sha256=…">
  Install the neon-bar skin
</a>
```
  An invalid (unparseable) `skin.json` fails the whole skin, which then falls back per
  [Fallback behavior](#fallback-behavior) below.

### Skin designer

Settings → OSD → **Skin designer…** opens a studio for building skins without hand-editing
files: pick the two PNGs, scrub a fill slider to preview any volume level, drag the percent
number where you want it (or hide it), set the scale, and save under a name — the skin appears
in the picker immediately, and if you edited the skin currently on screen the live OSD reloads
on save. **Test on desktop** shows the draft as a real OSD window at your configured position:
clicking, dragging and scrolling it behave exactly like the real thing and drive the designer's
fill slider. Editing an existing skin: pick it in the designer's dropdown; change the name
before saving to create a copy instead.

### How fill works

The OSD fills from left to right as volume increases: full.png is revealed via a rectangular
clip whose width is proportional to volume — at 50%, the left half of full.png is visible; at
100%, all of it. This clip always follows the x-axis, regardless of the artwork's shape (a
circular skin fills left-to-right, not radially like a pie chart). Transparent pixels in the
images are click-through (clicks and drags pass through to whatever is beneath the OSD).
Opaque pixels respond to clicks and drags to set volume — see below.

The whole bar is clickable, not just the current fill: the clickable/draggable shape is the
union of empty.png's and full.png's opaque pixels, independent of the current volume level.
Clicking anywhere on that shape jumps the volume straight to that position, like a normal
slider — it does not nudge the fill incrementally.

When the bar occupies only part of a wider decorative image, set `fillStartX`/`fillEndX` (or
drag the two colored range handles in the skin designer): 0% and 100% then map exactly onto
the bar's pixel edges, both for the fill and for clicks — clicks in the decorative margins
clamp to 0/100. Keep `full.png`'s lit pixels inside that range; static decoration belongs in
`empty.png`.

Skins render in Windows' display-scaled units, the same as the rest of the UI: on a 150%-scaled
display, a skin whose PNGs are 300px wide renders at 450 physical pixels. There is no
pixel-perfect (unscaled) rendering mode yet; it's on the roadmap.

### Skin shape examples

Skins are not limited to bars. Examples:
- **Bar** — Classic horizontal or vertical progress bar.
- **Cat** — A cat outline in empty.png, filled with color in full.png, lighting up as volume rises.
- **Custom art** — Any PNG shape; the fill always follows the x-axis regardless of shape (see
  "How fill works" above — there is no radial/pie-chart fill mode).

### Fallback behavior

If a custom skin folder is invalid or missing required images, apo-volume falls back to the
Dark pill style and displays a warning in the system tray. Check `%APPDATA%\apo-volume\` for
a log file if skins fail to load.

## Volume model

0% = mute (−120 dB) · 1% = −50 dB · 100% = 0 dB, linear in dB (≈0.5 dB per %).
Keys step 2% per press. Never exceeds 0 dB, so no digital clipping.

## Auto-update

apo-volume keeps itself up to date from this repo's GitHub Releases (checked at startup and
every 24 hours). Updates are verified — the release's `ApoVolume.exe.sha256` must match the
downloaded exe — and applied in place: the running exe is renamed to `ApoVolume.exe.old`, the
new build takes its path, and the app restarts itself (when running as administrator it
finishes on the next start instead, to avoid a surprise UAC prompt). If the exe's folder isn't
writable, a tray balloon links to the release page instead. Opt out at first run or via
Settings → **Keep apo-volume up to date automatically**; **Check now** checks on demand.

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
