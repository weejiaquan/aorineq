# AorinEQ reference

The long-form manual. The [README](../README.md) is the tour; this is the detail, and it is the
authoritative copy for anything a program has to agree with — the skin format, the `aorineq://`
contract and the share payload. The website carries the same material in a friendlier shape at
[aorineq-web.vercel.app/docs](https://aorineq-web.vercel.app/docs).

- [Requirements](#requirements)
- [Volume modes](#volume-modes)
- [Volume model](#volume-model)
- [Volume keys in games](#volume-keys-in-games-run-as-administrator)
- [First-run setup](#first-run-setup)
- [The OSD](#the-osd)
- [Skins](#skins)
- [Skin designer](#skin-designer)
- [Equalizer](#equalizer)
- [`aorineq://` links](#aorineq-links)
- [Auto-update](#auto-update)
- [Files AorinEQ owns](#files-aorineq-owns)
- [Notes and limitations](#notes-and-limitations)

## Requirements

- Windows 10 or 11, x64.
- Nothing else for **system** volume mode.
- [Equalizer APO](https://equalizerapo.com) for **Equalizer APO preamp** mode, and for the
  equalizer in either mode. AorinEQ can install it for you — see
  [First-run setup](#first-run-setup).

Releases are self-contained single-file builds: no .NET runtime install required.

## Volume modes

Set at first run, and changeable any time in Settings → Volume.

**Replace Windows volume (`system`)** — the volume keys drive the real Windows endpoint volume,
exactly as if you had moved the system slider, and AorinEQ just gives you its own OSD, its own
step size and its own tray icon. The preamp AorinEQ writes for volume is parked at 0 dB. Pick this
unless your hardware ignores Windows volume.

**Equalizer APO preamp (`eapo`)** — the volume keys apply digital attenuation in the Equalizer APO
chain instead, before audio reaches the device. This exists for USB DACs that advertise USB
hardware volume and then ignore the host's volume commands — the Windows slider moves and nothing
changes (known example: the HiBy FC5). Anything with the symptom *per-app volume works, master
volume does nothing* is a candidate.

The equalizer works in **both** modes; only the volume component differs.

Switching modes hands the mute duty over explicitly in both directions, and adopts the state that
is already in force, so nothing jumps and nothing is unmuted behind your back.

### Per-device volume

AorinEQ remembers a volume (and mute) per playback device and follows the Windows default device.
Plug in headphones, get the level you last used on them; unplug, get the speakers' level back.
Device switches update the tray silently — no OSD, matching Windows' own behaviour. Settings shows
how many devices are remembered.

## Volume model

`0% = mute (−120 dB)` · `1% = −50 dB` · `100% = 0 dB`, linear in dB (≈0.5 dB per %) in Equalizer
APO mode. It never exceeds 0 dB, so it cannot clip. Keys step 1, 2 (default) or 5 % per press.

In system mode, the percentage maps straight onto the Windows slider scalar.

## Volume keys in games (run as administrator)

Windows does not deliver keystrokes to normal apps while an elevated window — many games and
anticheats — has focus, so volume keys appear dead in-game. Fix: Settings → **Run as
administrator**. AorinEQ relaunches elevated (one UAC prompt), and "Start with Windows"
automatically switches from the registry Run value to a scheduled task so elevated autostart stays
silent at boot.

Because that scheduled task runs at the highest run level, only an elevated program can remove it
— and the uninstaller deliberately never elevates. **Turn "Start with Windows" off before you
uninstall.** A leftover task is inert (Task Scheduler logs a failed start and stops), and you can
delete it by hand in Task Scheduler.

Known limitation: on laptops, Windows' scheduled-task defaults can keep the elevated autostart
task from starting on battery power. Plug in before rebooting, or start the app manually.

## First-run setup

On first run AorinEQ asks which volume mode you want, preselecting from what it finds on the
machine. If you choose Equalizer APO mode and Equalizer APO is not installed, it opens a **setup
guide**: it downloads the official installer from Equalizer APO's own home (AorinEQ never bundles
it — it is GPLv2, AorinEQ is MIT), starts it, and tells you exactly what to do in the one step
that needs you — ticking your headphones or speakers in Equalizer APO's Configurator. Afterwards
it verifies the install against your current playback device and offers a one-click **audio
restart** as a reboot substitute.

Reopen it any time from Settings → **Setup guide…**, which also shows a live status line and an
**Open Configurator** shortcut for enabling other devices.

The first time AorinEQ needs to write to the Equalizer APO config folder it elevates **once** to
create `aorineq.txt`, add `Include: aorineq.txt` to `config.txt`, and grant your user account
write access to that folder. Every later run is non-elevated.

## The OSD

Four styles, in Settings → On-screen display:

- **Dark pill** — dark rounded rectangle, white percentage. The default.
- **Windows 11** — follows your system theme and accent colour.
- **Minimal bar** — a fill-level bar flush to its edge, no text.
- **Custom skin** — your own artwork, from `%APPDATA%\AorinEQ\skins\`.

All four support:

- **Anchor** — 8 positions: four corners plus four edge midpoints. Dead centre is deliberately not
  offered.
- **Offset X / Y** — pixel nudge from the anchor.
- **Hide delay** — 0.5 to 5 seconds. Hovering the OSD holds it open.
- **Animation** — fade on/off, 50–500 ms.
- **Volume step** — 1, 2 or 5 % per key press.

The OSD never takes focus, and drag/click/scroll on it set the volume.

## Skins

A skin is a folder in `%APPDATA%\AorinEQ\skins\<name>\`:

| File | |
| --- | --- |
| `empty.png` or `empty.gif` | **required** — artwork at 0 % |
| `full.png` or `full.gif` | **required** — artwork at 100 % |
| `muted.png` or `muted.gif` | optional — shown alone while muted |
| `skin.json` | optional — everything below |

`.png` wins over `.gif` if both exist. All layers must share one logical frame size.

### How fill works

`full` is revealed left-to-right through a rectangular clip whose width tracks the volume; `empty`
shows everywhere the fill is not, so a translucent `full` never double-darkens. The clip always
follows the x-axis whatever the artwork's shape — a circular skin fills left-to-right, not
radially.

Hit-testing is per pixel, from the union of every frame's opaque pixels of both layers:
transparent pixels click through to whatever is beneath, opaque pixels take clicks, drags and the
wheel. Clicking jumps straight to that position like a normal slider.

When the bar occupies only part of a wider decorative image, set `fillStartX`/`fillEndX` (or drag
the two coloured handles in the designer). 0 % and 100 % then land exactly on the bar's pixel
edges, for the fill and for clicks alike; clicks in the decorative margins clamp to the ends. Keep
`full`'s lit pixels inside that range and put static decoration in `empty`.

Skins render in display-scaled units like the rest of the UI: on a 150 %-scaled display, a 300 px
skin draws at 450 physical pixels.

### `skin.json`

Every field is optional and field names are case-insensitive. Omit the file entirely for a plain
static skin with no number.

```jsonc
{
  // --- appearance ---
  "scale": 1.5,          // zoom multiplier, clamped 0.25–4.0 (default 1.0)
  "fillStartX": 120,     // image-pixel x where 0% sits   (default 0)
  "fillEndX": 680,       // image-pixel x where 100% sits (default the image width)
  "mutedDim": 0.6,       // opacity of the dimmed empty layer while muted, 0–1
                         // (default 0.6; ignored when the skin has a muted layer)

  // --- animation ---
  "fps": 12,             // sprite-sheet playback rate, clamped 1–60 (default 10)
  "emptyFrames": 1,      // sprite-sheet frame count for the empty layer (default 1)
  "fullFrames": 8,       // ...for the full layer
  "mutedFrames": 1,      // ...for the muted layer

  // --- the percentage number ---
  "percentText": {
    "show": true,
    "x": 10,                     // anchor x, in image pixels
    "y": 5,                      // top y, in image pixels
    "align": "center",           // what x anchors: "left" (default), "center", "right"
    "color": "#FFF47F9E",        // #AARRGGBB or #RRGGBB (default opaque white)
    "fontFamily": "Segoe UI",    // any installed font
    "fontSize": 80,              // clamped 4–200 (default 14)
    "bold": false,
    "outlineColor": "#FF000000", // omit for no outline
    "outlineWidth": 2,           // clamped 0–20
    "shadowColor": "#80000000",  // omit for no shadow
    "shadowBlur": 4,             // clamped 0–50
    "shadowDepth": 2             // clamped 0–50
  },

  // --- credits, used by the skin picker and the gallery ---
  "title": "mika bar",
  "author": "your name",
  "description": "what it is",
  "version": "1",                // yours for this skin, not AorinEQ's
  "tags": ["pink", "bar"],       // up to 12, de-duplicated
  "sourceUrl": "https://example.com/mika-bar"   // https only, dropped otherwise
}
```

An unparseable `skin.json` fails the whole skin, and AorinEQ falls back to the Dark pill style
with a tray warning. Metadata is normalised on load: trimmed, length-capped by text element, and
stripped of control characters and bidi overrides — a credit line renders in the picker and on a
public page, so it is never allowed to lie about itself.

### Animated skins

Each layer can animate, three ways — identical once loaded:

- **GIF** — name the file `empty.gif` / `full.gif` / `muted.gif`. Frame timing comes from the GIF.
  Note GIF transparency is 1-bit: hard edges, no soft shadows.
- **Sprite-sheet PNG** — stack equal-height frames vertically in one PNG and declare the frame
  count plus `fps`. Full 8-bit alpha.
- **PNG frame sequence** — in the designer, **Frames…** assembles a sheet from your exported
  frames (e.g. Photoshop's *Export Layers to Files*).

Layers animate independently and loop; a static layer beside an animated one is fine. Animation
only runs while the OSD is on screen. APNG is not supported (WPF has no decoder for it) — use a
sprite sheet for full-alpha animation.

### Sharing skins

Skins travel as plain zip files. **Export…** in the designer writes one (regenerating a
`preview.png` from the installed pixels — a bundled preview in someone else's zip is never
trusted, let alone extracted). **Import skin…** in Settings, or **Import…** in the designer,
installs one. Only the known file names are ever extracted, from the archive root or one folder
deep, so a zip cannot write outside the skin folder; the old folder is moved aside and restored if
anything fails. Zips are capped at 20 MB.

**Share…** in the designer exports the zip *and* copies a ready-to-fill
[`aorineq://install-skin`](#install-a-skin) link template to your clipboard.

## Skin designer

Settings → **Skin designer…**, or `aorineq://open?page=designer`. It builds and edits skins
without hand-editing anything:

- Pick the empty / full / muted layers (**Browse…**), or assemble a sprite sheet from a frame
  sequence (**Frames…**).
- Scrub the fill slider to preview any level; **Preview mute** shows the muted state, with the
  **Mute dim** slider for skins with no muted artwork.
- Drag the two coloured handles to set the fill range, or type the pixel values.
- Drag the percentage number where you want it, and set font, size, bold, colour, alignment,
  outline and shadow.
- Set the scale, and fill in the optional details (title, author, description, version, tags,
  source URL) that the picker and the gallery show.
- **Test on desktop** shows the draft as a real OSD at your configured position — clicking,
  dragging and scrolling behave exactly as they will in use, and drive the designer's fill slider.
- **Save** under a name. Changing the name before saving makes a copy. The skin appears in the
  picker immediately, and if you edited the skin currently on screen, the live OSD reloads.

## Equalizer

Tray → **Open equalizer…**, Settings → Equalizer, or `aorineq://open?page=eq`. It writes real
Equalizer APO filters.

**Scopes.** Each scope — **Global**, plus one tab per playback device — has its own chain, its own
preset and its own on/off switch. Global filters apply on top of every device. The tab for the
device you are listening on is marked.

**Two faces**, switched in the header and remembered per profile:

- **Simple** — Bass / Mid / Treble sliders (±12 dB), the preset picker, EQ on/off, Flatten,
  auto-preamp and the level meters, with the response curve shown read-only.
- **Advanced** — the full editor: draggable curve, per-band strip, Edit-as-text, preset
  management, AutoEq import and the live post-EQ spectrum.

Both edit the *same* bands. The Simple sliders own three reserved filters (low shelf 100 Hz, peak
1 kHz, high shelf 8 kHz, all Q 0.7) at the end of the chain; switching to Advanced just reveals
them as ordinary bands. Ownership is recorded in AorinEQ's own settings, never inferred from the
shape of your chain, so a chain that merely ends in those three filters is never seized. If a
scope already has other bands — an AutoEq import, say — Simple adjusts on top of them and says so;
it never discards or reorders them. Merely opening Simple mode writes nothing: the trio is created
by your first slider move.

**The curve.** Log frequency 20 Hz – 20 kHz over a live post-EQ spectrum. Drag a node to move its
frequency and gain; wheel over it to change Q; right-click for the filter type; double-click empty
space to add a band, or a node to remove it. The dB scale cycles ±12 / ±24 / ±30.

**The band strip** below is the same chain as typed columns — type, frequency, gain, Q — with a
`+` to append. Out-of-range values clamp with an inline cue instead of being rejected;
unparseable text reverts.

**Filters**: Peak, low shelf, high shelf, notch, low pass, high pass — Equalizer APO's `PK`,
`LSC`, `HSC`, `NO`, `LPQ`, `HPQ`. Up to 64 bands per scope. Fc 10–24000 Hz, gain ±30 dB, Q
0.1–50, preamp −60…+20 dB. `BW Oct` is converted to Q on import.

**Preamp.** Each scope has its own preset preamp; **Auto-preamp** sets it to cancel the chain's
maximum boost. What lands in the config file is the sum of the volume preamp, the device preset
preamp and the global preset preamp (Equalizer APO sums sequential preamps in dB).

**Presets** are plain Equalizer APO ParametricEQ `.txt` files in `%APPDATA%\AorinEQ\presets`, so
they interchange directly with AutoEq, Peace and anything else that speaks that format. Save, Save
As, Delete, Import file… — and the tray has a preset submenu. An edited chain shows as `(custom)`.

**AutoEq…** searches [AutoEq](https://github.com/jaakkopasanen/AutoEq)'s published index by model
and measurement source and saves the profile byte-for-byte as a preset.

**Edit as text…** takes a whole ParametricEQ block pasted in. A bad line fails the whole parse
with its line number and nothing is applied.

**Flatten** zeroes every gain and the scope preamp, keeping type/Fc/Q. **Clear all** removes every
band. **EQ enabled** bypasses the scope.

**Meters** show the post-EQ output level (L/R RMS with peak ticks) and a latching clip indicator,
from a WASAPI loopback capture of the device you are listening on.

## `aorineq://` links

A website, a forum post or a chat message can hand someone a skin or a tuning with one click.
AorinEQ registers the scheme per user at startup; turn it off with Settings → **Enable
`aorineq://` links**.

`apo-volume://` — the scheme this app used before v3.0.0 — stays registered as an alias and
resolves identically, so old links keep working. Write new links with `aorineq://`.

General rules for every action: links are capped at 4000 characters; any `url` must be `https`,
without credentials in it; percent-encode a URL when you embed it in a link; and nothing is
downloaded, applied or saved without a confirmation dialog. A malformed link produces only a tray
balloon. Control actions (`set-volume`, `mute`) are deliberately **not** implemented: any page
could use them to nuisance-toggle your audio, and a confirmation dialog would make them pointless.

### Install a skin

```
aorineq://install-skin?url=<https URL to the skin zip>&name=<skin name>&sha256=<hex>
```

- `url` — **required.** Direct https link to the zip. Zips over 20 MB are rejected.
- `name` — optional. Folder name to install as; defaults to the zip's filename stem. Same rules as
  names in the designer.
- `sha256` — optional but recommended. Hex SHA-256 of the zip; a mismatch is rejected.

The dialog names the skin and the host, and warns about an overwrite, before anything is
downloaded. Choices: **Install & Use** / **Install only** / **Cancel**.

```html
<a href="aorineq://install-skin?url=https%3A%2F%2Fexample.com%2Fskins%2Fneon-bar.zip&sha256=…">
  Install the neon-bar skin
</a>
```

### Apply an EQ preset

A preset **inside the link** — what the editor's **Copy share link** button produces, no hosting
needed:

```
aorineq://apply-preset?type=eq&data=<base64url payload>&name=<preset name>&scope=device|global
```

A **hosted** preset file:

```
aorineq://apply-preset?type=eq&url=<https URL to a ParametricEQ .txt>&name=<preset name>&scope=device|global&sha256=<hex>
```

- `type` — **required**, currently `eq`. Other values report "needs a newer version".
- `data` / `url` — **exactly one.** A link carrying both is rejected. `url` is capped at 1 MB of
  text that must parse fully as Equalizer APO filter lines.
- `name` — optional. Preset file name to save as; defaults to the URL's filename stem, or
  "Shared preset" for a `data` link.
- `scope` — optional, `device` (default) or `global`. With no active playback device a `device`
  link lands on the global chain and the dialog says so.
- `sha256` — optional, hosted links only. Refused on `data` links, which carry no separate file.

The dialog shows the source, the target scope, the band count, the preamp and the **response
curve**. For a hosted preset the file is fetched only when you press **Preview** (which just
draws it) or one of the two accept buttons — a link on its own never makes AorinEQ touch the
network. Choices: **Apply & Save** / **Save** / **Cancel**.

#### The `data` payload format

UTF-8 text, base64url-encoded (`-`/`_`, padding stripped):

```
v1|<preamp dB>|<TYPE>,<Fc Hz>,<gain dB>,<Q>;<TYPE>,<Fc Hz>,<gain dB>,<Q>;…
```

`TYPE` is an Equalizer APO filter token: `PK`, `LSC`, `HSC`, `NO`, `LPQ`, `HPQ`. Numbers are
invariant (`.` decimal separator) and shortest-round-trippable, which makes encoding exactly
lossless while staying about a third the size of the equivalent ParametricEQ text; gain is written
for every band and ignored for the types that have none. Up to 64 bands.

For example, `v1|-6.1|LSC,105,-1.4,0.7;PK,3200,2.6,1.8` encodes a preamp of −6.1 dB and two
filters, and becomes `djF8LTYuMXxMU0MsMTA1LC0xLjQsMC43O1BLLDMyMDAsMi42LDEuOA`.

Anything that does not decode cleanly — wrong alphabet, invalid UTF-8, an unknown version, a bad
number, too many bands — is rejected as a malformed link. Values that decode but sit out of range
are clamped to the editor's own limits.

A 13-band chain is about a 333-character link and 24 bands fit comfortably; the editor tells you
if a chain is too big to share this way, in which case host it and use `url`.

### Other deep links

```
aorineq://autoeq?model=<headphone model>          opens AutoEq import, pre-searched
aorineq://open?page=eq|settings|designer|skins    opens that window
```

`autoeq` only fills in the search box — you still pick the profile and press Import. `open`
changes nothing, so it asks nothing. An unknown `page` reports "needs a newer version".

## Auto-update

AorinEQ keeps itself up to date from this repo's GitHub Releases, checked at startup and every 24
hours. A release only counts if it is newer, is not a prerelease, and ships **both** `AorinEQ.exe`
and `AorinEQ.exe.sha256`; the download is https-only, size-capped, checked for an executable
header and verified against that sidecar hash.

It applies in place: the running exe is renamed to `AorinEQ.exe.old`, the new build takes its
path, and the app restarts itself. With **Run as administrator** on it finishes on the next start
instead, to avoid a surprise UAC prompt. If the exe's folder is not writable, a clickable tray
balloon opens the release page instead — which is why the installer is per-user and not in
Program Files.

Opt out at first run or in Settings → **Keep AorinEQ up to date**. **Check now** checks on demand.

## Files AorinEQ owns

| Path | |
| --- | --- |
| `%APPDATA%\AorinEQ\settings.json` | all settings, per-device volumes and EQ chains |
| `%APPDATA%\AorinEQ\skins\` | your skins |
| `%APPDATA%\AorinEQ\presets\` | your EQ presets, as ParametricEQ `.txt` |
| `<Equalizer APO>\config\aorineq.txt` | the only Equalizer APO file AorinEQ writes |
| `<Equalizer APO>\config\config.txt` | one `Include: aorineq.txt` line, re-added if removed |

Uninstalling asks whether to keep `%APPDATA%\AorinEQ` and keeps it unless you say otherwise.

Upgrading from before v3.0.0 migrates `%APPDATA%\apo-volume`, `apo-volume.txt` and its include
line, and the autostart entry, once and automatically.

## Notes and limitations

- Exclusive-mode audio (ASIO / WASAPI exclusive) bypasses Equalizer APO entirely and is
  unaffected — in Equalizer APO mode the volume keys will not work there.
- Volume keys are intercepted system-wide while AorinEQ runs; quitting restores normal handling.
- Peace's own pre-amp slider stacks additively with AorinEQ's preamp — keep Peace's at 0 dB. If
  Peace rewrites `config.txt`, AorinEQ restores its include line.
- If `aorineq.txt` becomes unwritable (e.g. after an Equalizer APO reinstall), a tray balloon
  warns you instead of failing silently.
- There is no pixel-perfect (unscaled) skin rendering mode yet.
