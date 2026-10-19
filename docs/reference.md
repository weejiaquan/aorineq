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
- [AirPlay](#airplay)
- [HUD widgets](#hud-widgets)
- [Finding your way around](#finding-your-way-around)
- [Languages](#languages)
- [`aorineq://` links](#aorineq-links)
- [Auto-update](#auto-update)
- [Files AorinEQ owns](#files-aorineq-owns)
- [Dev builds](#dev-builds)
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
Device switches do not show the volume OSD, matching Windows' own behaviour — they show the
[device notice](#the-osd) instead. Settings shows how many devices are remembered.

### The tray icon

Settings → Volume → **Tray icon**.

**Scroll to adjust volume** (on by default) rolls the wheel over the tray icon to change the
volume, the way you can over the Windows volume icon. Hold **Ctrl** for 1 % steps or **Shift** for
10 %; without a modifier it moves by your configured step size, one step per wheel detent. It also
works over a HUD volume widget, and **Invert scroll direction** flips it if you scroll the other
way round.

Windows does not offer this to third-party tray icons — the shell forwards clicks and mouse moves
to an icon's owner but keeps wheel input for its own system icons, which is why the Windows volume
icon can do it out of the box and nothing else can. AorinEQ gets there with a low-level mouse
hook, installed only while the setting is on, that claims a wheel notch when the cursor is over its
own icon and passes every other mouse message straight through.

**Click** and **middle-click** are bindable: open the volume slider (the default, and what a left
click has always done), open Settings, open the equalizer, mute/unmute, or do nothing. Right click
always opens the menu. There is no double-click binding on purpose — Windows raises a single click
before a double one, so having both would mean delaying every single click by the double-click
timeout.

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
- **Announce device changes** — on by default. Whenever the Windows default playback device
  changes, whether Windows moved it or you did, the new device's name appears where the OSD does,
  for four seconds, or the hide delay if that is longer. A name longer than 48 characters is cut with an ellipsis; "No output
  device" is shown when nothing is left to play through. Its look follows the OSD style: *Dark
  pill* a dark rounded plate, *Minimal bar* a smaller, tighter one, *Windows 11* a light or dark
  plate that follows Windows' own theme (re-read every time the notice shows), and *Custom skin*
  whatever [the skin describes](#the-device-notice), or the dark pill when it describes nothing, so
  skins need no artwork for it. It is AorinEQ's own window rather than a Windows notification, so
  Do Not Disturb does not suppress it. Starting AorinEQ announces nothing, and one switch is one
  notice.

The OSD never takes focus, and drag/click/scroll on it set the volume.

## Skins

A skin is a folder in `%APPDATA%\AorinEQ\skins\<name>\`:

| File | |
| --- | --- |
| `empty.png` or `empty.gif` | **required** — artwork at 0 % |
| `full.png` or `full.gif` | **required** — artwork at 100 % |
| `muted.png` or `muted.gif` | optional — shown alone while muted |
| `airplay-empty.png` or `.gif` | optional — the AirPlay strip at 0 % |
| `airplay-full.png` or `.gif` | optional — the AirPlay strip at 100 % |
| `notice.png` or `.gif` | optional — the device-changed notice's artwork |
| `skin.json` | optional — everything below |

`.png` wins over `.gif` if both exist. `empty`, `full` and `muted` must share one logical frame size; the AirPlay strip's two layers share their own, and the notice art has its own.

### The skins that ship

Four are written into your skins folder the first time they are missing, and are **never**
overwritten afterwards - one you have edited stays yours.

| Skin | |
| --- | --- |
| **AorinEQ** | The house style, and what *Custom skin* shows before you pick anything. |
| **Windows 11** | The Windows 11 volume flyout: rounded, translucent, thin accent bar. |
| **Windows 10** | The Windows 10 flyout: square and opaque, an icon block and a big number. |
| **Windows XP** | XP shipped no volume OSD, so this is its tray volume popup - Luna, a trackbar with ticks, and a Mute box that ticks via the `muted` layer. |

Each is a different size and layout, not one shape recoloured, so between them they demonstrate
most of what the format can express. Copy the folder of whichever is closest to what you want.

### How fill works

`full` is revealed left-to-right through a rectangular clip whose width tracks the volume; `empty`
shows everywhere the fill is not, so a translucent `full` never double-darkens. The clip always
follows the x-axis whatever the artwork's shape — a circular skin fills left-to-right, not
radially.

A **thumb that tracks the level is not expressible**, and this is the one thing worth knowing
before you design: nothing about the artwork depends on the volume except which side of a single
vertical edge a pixel falls on. Draw a filled bar, not a knob on a rail. (The real Windows volume
OSD is a plain filled bar in both 10 and 11 for what it is worth — the knob belongs to the taskbar
flyout, which is a different control.)

Anything `empty` draws **between `fillStartX` and `fillEndX`** is replaced by `full` as the level
rises, across the full height of the image — so keep glyphs, labels and decoration outside that
range unless both layers draw them identically.

Hit-testing is per pixel, from the union of every frame's opaque pixels of both layers:
transparent pixels click through to whatever is beneath, opaque pixels take clicks, drags and the
wheel. Clicking jumps straight to that position like a normal slider.

When the bar occupies only part of a wider decorative image, set `fillStartX`/`fillEndX` (or drag
the two fill-range lines in the designer). 0 % and 100 % then land exactly on the bar's pixel
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
    "weight": 700,               // OpenType weight, rounded to 100s, clamped 100–900 (default 600)
    "italic": false,
    "letterSpacing": 2,          // extra px between characters, scales with the skin; −20–100 (default 0)
    "outlineColor": "#FF000000", // omit for no outline
    "outlineWidth": 2,           // clamped 0–20
    "shadowColor": "#80000000",  // omit for no shadow
    "shadowBlur": 4,             // clamped 0–50
    "shadowDepth": 2             // clamped 0–50
  },

  // --- the AirPlay strip (optional; omit the whole block for a volume-only skin) ---
  "airplay": {
    "fillStartX": 16,            // as above, but for the strip's own artwork
    "fillEndX": 248,
    "emptyFrames": 1,            // sprite-sheet frame counts, as above
    "fullFrames": 1,
    "nameText":    { "show": true, "x": 16,  "y": 5 },   // the receiver's name
    "percentText": { "show": true, "x": 248, "y": 5, "align": "right" },
    "dropdownHit": { "x": 258, "y": 4, "w": 30, "h": 24 }  // where a click opens the device list
  },

  // --- the device notice (optional; omit for the dark pill) ---
  "notice": {
    "background": "#F2202020",   // plate colour (no notice.png)
    "cornerRadius": 12,
    "text": { "color": "#FFFFFFFF", "fontSize": 14, "weight": 600 },
    "glyph": { "show": true, "size": 18 }
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

A `percentText` with `"show": false` hides the number but keeps its position and styling, so
turning it back on is not starting over. `nameText` and `percentText` in the `airplay` block take
every field the volume bar's `percentText` takes — outline and shadow included — and are drawn by
the same renderer; their default `weight` is 400 (normal), as the labels have always been drawn,
where the number's is 600 (semibold).

`weight` replaces the older `"bold"`: a skin that says `"bold": true` and no `weight` loads as 700,
and `"bold": false` (or neither) as the text's default; when both are there, `weight` wins. The
Skin Designer writes `weight`, only when it differs from the default, and never `bold`. Letter
spacing goes between characters (a character with combining marks, or an emoji, stays whole),
never after the last, so alignment still measures the text as drawn. The gallery preview draws
weights 700 and up bold and lighter ones regular — the most its renderer can say — so a skin
previews as it did before weights.

### The AirPlay strip

A second, optional bar drawn **under** the volume bar whenever the OSD is showing AirPlay — see
Settings ▸ AirPlay ▸ *AirPlay bar in the OSD* for when that is. It shows the receiver's name and
**the receiver's own volume**, which is a different number from your PC's: dragging or scrolling
the strip changes the speaker, not the preamp.

It has its own size and is not tied to the volume bar's. Both its layers paint the **whole** image
and differ only in what is lit, exactly like `empty`/`full`.

`dropdownHit` is the rectangle that opens the device list instead of setting a volume; it is
clamped into the artwork, and omitting it makes the whole strip open the list. Draw your own
chevron (or whatever says "this opens something") inside that rectangle in `airplay-empty`.

`connectHit` is an optional second rectangle: **one tap connects or disconnects**, without going
through the list. It is opt-in and has **no default** — unlike `dropdownHit`, omitting it means no
power button at all, because a hidden switch under every pixel of every older skin is not a good
way to connect to a speaker. It is tested *before* `dropdownHit`, so it can sit inside a strip that
declares no dropdown region; keep the two from overlapping or the power button eats the chevron.

**Leave the space empty in your artwork.** AorinEQ draws the power glyph itself, the way it draws
the name and the level, because what it shows changes with the session and a PNG cannot: dim while
tapping would connect, lit while tapping would disconnect, and hidden entirely when no receiver has
been chosen — then a tap there falls through to the list, which is where a first one gets picked.
`connectColor` is the lit colour; the dim state uses `nameText`'s colour. Both default to white.

A skin **without** these files is not missing anything — AorinEQ draws its own plain AirPlay strip
under your artwork instead, so every skin written before this existed keeps working untouched. A
skin whose AirPlay artwork is broken or half-written loses only the strip: the volume bar is
unaffected and the plain strip is used, because a partly-drawn extra should never cost you the
control you actually use. The four skins that ship with AorinEQ (*AorinEQ*, *Windows 11*,
*Windows 10*, *Windows XP*) all include one and are the easiest thing to copy.

An unparseable `skin.json` fails the whole skin, and AorinEQ falls back to the Dark pill style
with a tray warning. Metadata is normalised on load: trimmed, length-capped by text element, and
stripped of control characters and bidi overrides — a credit line renders in the picker and on a
public page, so it is never allowed to lie about itself.

### The device notice

The line that names the new playback device when Windows' default moves (see
[the OSD](#the-osd)). A skin may describe it in an optional `notice` block, in one of two ways:

- **A plate** — no `notice.png`. AorinEQ draws a rounded rectangle, the device name and a small
  device glyph, using the block's colours, corner, padding and font. Text and glyph are laid out by
  the padding, so `text.x`/`text.y`, `text.align`, `glyph.y` and `textWidth` have no effect here.
  The skin's `scale` does not resize a plate.
- **Art** — a `notice.png` (or `.gif`) is drawn instead of the plate, and the block places the
  text and glyph on it. The art is drawn at its own size
  times the skin's `scale`, and the text and glyph sizes and positions scale with it. A GIF shows
  its **first frame** only; the notice does not animate. A PNG sprite sheet needs `frames`.

Every field is optional; field names are case-insensitive.

| Field | Default | |
| --- | --- | --- |
| `background` | `#F2202020` | plate colour, `#AARRGGBB` or `#RRGGBB`; unparseable falls back to the default. Plate only |
| `border` | none | plate outline colour, 1 px; unparseable falls back to the default. Plate only |
| `cornerRadius` | 12 | clamped 0–64. Plate only |
| `padding.x`, `padding.y` | 16, 10 | clamped 0–64. Plate only |
| `text.color` | white | unparseable falls back to white |
| `text.fontFamily` | Segoe UI | left at the default, it inherits AorinEQ's own font chain, so CJK device names still draw |
| `text.fontSize` | 14 | clamped 4–200 |
| `text.weight` | 600 | OpenType weight, rounded to 100s and clamped 100–900; the legacy `text.bold: true` reads as 700 |
| `text.italic` | false | |
| `text.letterSpacing` | 0 | extra pixels between characters, scaled with the art; clamped −20–100 |
| `text.outlineColor`, `text.outlineWidth` | none, 0 | as `percentText`'s: omit the colour for no outline; width clamped 0–20 |
| `text.shadowColor`, `text.shadowBlur`, `text.shadowDepth` | none, 4, 2 | as `percentText`'s: omit the colour for no shadow; blur and depth clamped 0–50 |
| `text.align` | `left` | art only; `left`, `center` or `right`: aligns the name inside the `textWidth` box that starts at `text.x` |
| `text.x`, `text.y` | 48, centred | art only; image pixels, clamped into the art |
| `textWidth` | to the art's right edge less 12 | art only; bounds the line so a long name cannot run off the art, clamped to what is left right of `text.x` |
| `glyph.show` | true | on a plate, `false` keeps the icon column: the text does not shift left |
| `glyph.size` | 18 | clamped 4–200 |
| `glyph.color` | the text colour | |
| `glyph.x`, `glyph.y` | 16, centred | art only; image pixels, clamped into the art |
| `frames` | 1 | art only; sprite-sheet frames in `notice.png`, so the height must divide evenly |

The notice text is drawn by the same renderer as `percentText` and the AirPlay labels, so its
outline, shadow, weight, italic and letter spacing look the same as theirs; a name too long for
its box still ends in an ellipsis.

A skin with neither a `notice` block nor `notice.*` art gets the dark pill. A `notice` block with
no art is simply a plate. Art that exists but is not a valid image, or whose height does not match
`frames`, fails **soft**: the volume skin is unaffected, the notice falls back to the dark pill, and
the reason is kept with the skin. A `notice` value that is not an object is treated as if the block
were absent, so `notice.png`/`.gif`, if present, is still used with the defaults above.
`"notice": false` is how the Skin Designer records a notice that was turned off.

The four shipped skins each draw their own. A copy of one made before notices existed is topped up
**additively** the next time AorinEQ starts: only when the folder has no `notice.*` file and
`skin.json` has no `notice` key (in any letter case, any value — `false` included), it gains
`notice.png` and the block, and nothing else in it is touched. Skins you have already given a
notice, or turned it off for, are left alone. A folder the top-up cannot read or write — a
`skin.json` that is not valid JSON or repeats a key, an unwritable folder — is skipped.

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
deep (including `notice.png`/`.gif`, so a shared notice arrives with its artwork), so a zip
cannot write outside the skin folder; the old folder is moved aside and restored if anything fails. Zips are capped at 20 MB.

**Share…** in the designer exports the zip *and* copies a ready-to-fill
[`aorineq://install-skin`](#install-a-skin) link template to your clipboard.

## Skin designer

Settings → **Skin designer…**, or `aorineq://open?page=designer`. It builds and edits every part of
a skin without hand-editing anything, laid out like an image editor: **Layers** on the left, the
skin in the middle, and the selected layer's **Properties** on the right. Drag the dividers to
resize the side panels; the designer remembers their widths.

- **Layers** mirrors the skin: *Skin info*; the *Volume bar* with its empty, full and (optional)
  muted art and the percent text; the *AirPlay strip* with its art, name and level text, dropdown
  area and optional power-button area; and the *Device notice* with its optional art, text and
  icon. Layers not in use are dimmed, and a layer with a problem carries ⚠ — hover it for the
  reason. A skin without a strip offers **Add AirPlay strip…**, which asks for its empty and full
  art.
- **Properties** shows only what the selected layer has. Every text — the percent number, the
  AirPlay labels and the notice name — has the same type controls: font, size, weight (Thin 100
  to Black 900), italic, letter spacing and colour, and its own Outline and Shadow sections.
  Number boxes scrub when you drag their
  label, step with ↑/↓ (Shift: 10) and pull out-of-range values into range; anything that is not
  a number turns the box red and puts the last value back. Colours open a picker with opacity, hex
  (`#RGB`, `#RRGGBB`, `#AARRGGBB`), your twelve recent colours — shared with the HUD widget menu —
  and an eyedropper: it samples the canvas pixel under the pointer when you release the button,
  and Esc cancels it. **Frames…** builds a sprite sheet from a numbered PNG sequence.
- **The canvas** draws the skin with the same code as the OSD: the volume bar, the AirPlay strip
  directly under it, and the notice below. Drag the percent text, the AirPlay labels, the text
  (and its right edge, for its width) and icon on notice art, the fill-range lines, and the
  dropdown and power areas (their corners resize them). Within 4 screen pixels they snap to the
  art's edges and centre, its fill lines and each other, shown by magenta guides; hold **Alt** not
  to snap. Arrows nudge the selection 1 px (Shift: 10), Delete removes the selected muted art,
  notice art or power area, and Esc deselects. **Ctrl+wheel** zooms at the pointer (25–800 %),
  **Ctrl+0** fits, **Ctrl+1** is 100 %, and Space+drag or the middle button pans; the plain wheel
  scrolls. The toolbar and zoom buttons don't take the keyboard focus, so the canvas keeps its keys
  after you click one. The strip under the canvas previews any fill level, mute, the AirPlay strip
  connected, disconnected or with no receiver, the receiver's level and a sample device name —
  none of it is saved.
- **Undo / redo**: Ctrl+Z, and Ctrl+Y or Ctrl+Shift+Z, up to 200 steps back (in a text box they
  are the box's own). A drag, a run of typing in one box and one use of the colour picker are one
  step each. The title shows **•** while there are unsaved changes. Switching skin, **New**,
  **Import…**, closing the designer, the tray's **Exit**, restarting for an update and relaunching
  as administrator all ask first: **Save**, **Don't save** or **Cancel**. Windows logging off or
  shutting down closes the designer without asking, and its unsaved changes are lost.
- **Save** (Ctrl+S) is disabled while the skin has any problem, and its tooltip lists them by
  layer. It looks at the art on disk again before writing, so art deleted or broken since the last
  edit stops the save instead of being saved over. Saving under another existing skin's name asks
  before overwriting it; changing a loaded skin's name saves a copy. The skin appears in the picker
  at once, and if it is the skin on screen, the live OSD reloads.
- **Test on desktop** shows the draft as a real OSD at your configured position, with the AirPlay
  strip in the preview's state; clicking, dragging and scrolling it drive the preview's fill.
  **Show notice** puts the device notice on the desktop with the sample device name. Every problem
  that blocks Save blocks these too — the tooltip says which — except a name that can't be used:
  the test copy is saved under a name of its own.

Turning the notice off for a skin that had one, or for one of the shipped skins, writes
`"notice": false`, so the shipped notice is not restored on the next start. If a skin has AirPlay
strip or notice art that can't be loaded, the designer leaves it out of the drawing, marks its
layer with ⚠ saying why, and keeps **Save** off until you decide: **Remove AirPlay strip** (or
**Add AirPlay strip…** to replace it), or **Turn the notice off** (or **Choose new notice art…**).
Nothing is deleted from the skin until you have.

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

## AirPlay

Sends this PC's audio to a HomePod, an Apple TV, or any other AirPlay receiver on your network.
Built in, so a second application is not needed. Settings -> AirPlay.

It needs nothing but the receiver and a shared network. There is no pairing step, no Apple account
and no AirPlay 2 - AorinEQ speaks the original unencrypted RAOP protocol, which every AirPlay
receiver still accepts.

**Experimental.** It works, and it has been run for sustained sessions against real hardware, but it
has had far less exposure than the rest of the app.

### Enable AirPlay

The master switch. While it is off there is no AirPlay bar on the OSD whichever *AirPlay bar in
the OSD* mode is picked — turning the feature off should not leave its bar sitting there with
nothing behind it.

You do not have to reach for it first: **connecting to a receiver switches it on**, from wherever
you connect — this page, the tray menu, or the bar itself.

### Receiver

Receivers are discovered automatically over mDNS. Press **Refresh** if yours is not listed - some
devices take a few seconds to answer the first query.

**Connect** takes roughly half a second, and the window does not respond while it happens. That is
the RTSP handshake running on the UI thread; a known rough edge rather than a hang.

### Source

Which of this PC's playback devices is captured and sent.

Left on the default, whatever you are already listening to is what gets streamed - so it plays in
both places at once. To hear it **only** on the receiver, turn on **AirPlay only** below; it needs
VB-Audio's free virtual cable. While AirPlay only is on, Source is locked to VB-Cable, and your own
choice here comes back when you turn it off.

### AirPlay only

Plays AirPlay on the receiver and nowhere else, by routing rather than muting. While a stream runs,
AorinEQ makes **CABLE Input (VB-Audio Virtual Cable)** Windows' default output for all three roles
(console, multimedia, communications) - every app plays into the virtual device, nothing reaches
your speakers - and the stream captures that device. When the stream ends (Disconnect, the receiver
dropping, an idle hang-up, an error, or quitting AorinEQ) each role goes back to the device it was
on. A role whose device has gone in the meantime is moved to another active device rather than left
on the cable.

- **You pick another output mid-stream:** AorinEQ treats it as your decision and stops managing the
  default; the stream keeps capturing the cable.
- **You turn AirPlay only on mid-stream:** it takes effect at once - AorinEQ reconnects to the same
  receiver (a brief gap) so the stream captures VB-Cable and your speakers go quiet.
- **You turn AirPlay only off mid-stream:** your output comes back at once. The stream keeps
  capturing the cable until it ends, so the receiver goes quiet until the next connection.
- **VB-Cable disappears mid-stream** (disabled or removed): your original devices are put back at once.
- **AorinEQ is killed mid-stream:** the original defaults were saved to `settings.json` before the
  switch and are put back on the next start.
- **Windows refuses the switch:** the stream still captures VB-Cable, but the default stays where it
  was, so this once you also hear it locally.
- The switch and the switch back are AorinEQ's own, so they raise no device notice, no Equalizer APO
  health warning (neither "isn't running" for the cable nor "working again" afterwards) and no repair
  prompt.

The cable is recognised by its hardware id (`VBAudioVACWDM`), not its name, so renaming it in Sound
settings changes nothing.

**Installing VB-Cable.** Without it the page shows **Install VB-Cable (free, 1 MB)**. AorinEQ reads
VB-Audio's page for the newest driver pack (falling back to Pack45), downloads it from
`download.vb-audio.com` only, refuses any archive whose entries are not plain file names, and runs
the setup only if its Authenticode signature is intact **and** names VB-Audio's publisher
(`BUREL VINCENT Entrepreneur individuel`). VB-Audio's own installer then runs - you accept their
licence, Windows asks for administrator permission. If the device appears within ten seconds you
are offered AirPlay only. If the driver installed but the device hasn't appeared, Windows needs a
restart: the card says so, and AorinEQ tells you when the cable is ready (it stops waiting after three
starts). If nothing was installed (you cancelled inside VB-Audio's installer), the card says **Install
cancelled** and no restart is asked for. A package that can't be found or
downloaded, or an installer that can't be started, opens VB-Audio's page instead; a download that
fails verification is never run. A cable that is installed but disabled gets **Open Sound
settings** rather than a reinstall (a cable whose device is disabled is turned back on in Device
Manager). VB-Cable is donationware by VB-Audio.

### EQ the AirPlay stream

VB-Cable is its own playback device, so Equalizer APO has to be switched on for it separately. The
toggle runs the same elevated, backed-up, verified repair as the Volume page's button, aimed at the
cable; turning it off undoes exactly that repair. Each device keeps its own undo record, so
repairing the cable never touches the undo held for your speakers. The repair restarts Windows'
audio, so a running stream drops out for a few seconds and then carries on (AorinEQ re-attaches it
after any repair or undo). **Edit EQ** opens the equalizer
on the cable's tab, and the Global chain applies to it as well. If Equalizer APO is not installed,
the toggle opens the setup guide instead. Turning it off is refused when Equalizer APO was switched
on for the cable with its own Configurator rather than by this toggle: there is no AorinEQ backup to
undo from, so switch it off in the Configurator.

### Playback modes

How much audio is queued ahead of the receiver:

| Mode | Queue |
|---|---|
| Real-time | 250 ms |
| Normal | 1000 ms |
| Buffered | 2000 ms |
| Custom | whatever you type |

**These numbers are uncalibrated.** They are reasoned guesses, not measurements. The receiver
reports `Audio-Latency: 1886` (about 43 ms at 44.1 kHz), which is demonstrably not what it actually
buffers, so there is no honest way to derive the real figures from the protocol - they need
measuring by ear. On a healthy network the modes can be hard to tell apart, because the resend rate
is 0% even at 250 ms.

Watch the **resends** figure in Session details. A number that climbs means the network is not
keeping up, and a longer queue is the fix.

### Volume, and which device the keys drive

The stream is captured from a loopback tap that sits **after** Equalizer APO. That inverts what you
would expect:

- **In Equalizer APO preamp mode**, with Equalizer APO on the device being streamed, the preamp is
  upstream of the tap, so the volume keys already attenuate the audio before it is packetised. Set
  the receiver to 100% and the existing preamp controls it, exactly like a DAC. Retargeting the keys
  at the receiver as well would attenuate twice, so the setting greys itself out and says so. Without
  Equalizer APO on that device (VB-Cable with EQ off, say) the preamp reaches nothing, so with
  **Volume keys drive the receiver** on, the keys drive the receiver instead.
- **In Windows volume mode**, endpoint volume reaches the tap only on devices without hardware
  volume, which Microsoft documents as not contractual. Since it is unsafe to rely on in either
  direction, AorinEQ drives the receiver's own volume explicitly.

### Dithered silence

Writes a plus/minus 1 LSB noise floor over otherwise-silent packets. Some receivers put their output
stage to sleep during digital silence and clip the first instant of the next track; an inaudible
noise floor keeps it awake. On by default, and it does not touch the music itself.

### Standby and idle hang-up

Keeps the session open through silence so playback resumes instantly. The cost is that the receiver
stays claimed by this PC and another device cannot take it over, so there is an optional idle
timeout that hangs up after a period of quiet.

### Mute this PC while streaming

Silences the local endpoint so sound comes only from the receiver. **Off by default, deliberately.**
Whether endpoint mute reaches the loopback tap is a hardware lottery: on the losing side it mutes
the AirPlay stream too, and you get silence everywhere. **AirPlay only** is the route that always
works, and while it is on this switch is hidden - there is nothing left for it to do.

### Session details

Live counters for the running session - queue depth, resends requested/served/missed, and the number
of packets that carried no source audio. Silent packets at the very start are normal: they are the
gap before WASAPI delivers its first buffer.

### Limitations

- **No multi-room.** One receiver at a time.
- **No simultaneous local + AirPlay with independent EQ chains.** That is an audio-router problem,
  not an AirPlay one.
- **No AirPlay 2**, and no pairing-based receivers.
- Connecting blocks the UI for the duration of the handshake.

## HUD widgets

Small always-on-top panels that show your own audio. Settings -> HUD widgets.

Four kinds:

- **Spectrum** - the frequencies in what you are hearing, taken *after* your EQ, so a bass boost
  shows up here.
- **Levels** - output level and how close it is to clipping. Worth watching after raising any EQ
  band: if it lights up, lower the preamp rather than the volume.
- **EQ curve** - your current tuning as a single line, without opening the editor.
- **Volume** - a permanent read-out; scroll on it to change the volume if scrolling is enabled.

They stay out of the way: never in Alt+Tab, never focused, and click-through - a click over one
reaches whatever is underneath. Turn on **Arrange widgets** to make them solid so you can drag,
resize and right-click them, then turn it back off.

**Hide while a fullscreen app is in front** is on by default. Overlays over exclusive-fullscreen
games either flicker or do not draw at all, so hiding them beats showing them badly. Turn it off if
your games run borderless-windowed.

One timer drives every widget, so the refresh rate is the total cost regardless of how many you
have.

## Finding your way around

AorinEQ has more in it than fits on one screen, so three things exist to make that findable.

**Discover** is the first page in Settings. One card per feature - what it is, whether it is
currently doing anything, and a button that takes you to it. It is where Settings opens until you
have seen it once, after which Settings opens on Volume.

**The (?) beside a setting** opens a paragraph explaining what that setting does, when you would
want it, and what happens if you get it wrong. Every settings row has one. Controls in the EQ editor
carry the same text as a tooltip instead, and that window has a (?) in its title bar listing every
topic for it.

**The search box** above the sidebar searches the help text, not just labels - so "homepod" finds
the AirPlay receiver row even though no visible label contains that word. It searches your language
and English at the same time, because most audio documentation online is in English.

## Languages

AorinEQ ships in English, Simplified Chinese, Traditional Chinese, Japanese and Korean. The picker
is the first card on the Discover page, and switching applies immediately - no restart.

The default, **Follow Windows**, resolves from your Windows display language. Chinese resolves by
*script*, not country: `zh-TW`, `zh-HK` and `zh-MO` get Traditional, and everything else Chinese
gets Simplified. Any language AorinEQ does not ship falls back to English.

### The four translations are machine-drafted

Only the English is written by a native speaker. The other four are machine-drafted and shipped that
way on purpose - an unreviewed translation is more useful than no translation, and audio terminology
is exactly where it goes wrong.

**Corrections are welcome and easy.** Every non-English language card carries a *Suggest a
correction* link that opens a prefilled GitHub issue with your version, language and Windows build
already filled in. If you would rather fix it directly:

1. Edit `src/AorinEQ.Core/Strings/strings.<code>.json` - `zh-Hans`, `zh-Hant`, `ja` or `ko`.
2. The keys match `strings.en.json` exactly. Change values, never keys.
3. Keep the `{0}`, `{1}` placeholders: they are substituted at runtime, and dropping one loses an
   argument or throws.

No C# is involved, and the build fails if a key goes missing or a placeholder does not match its
English original.

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

## Dev builds

Only builds made by `publish.ps1` (`-p:AorinEQBuildKind=release`) are releases. Every other build
— `dotnet build`, the IDE, CI's gate — is a dev build and says so, so it cannot be mistaken for the
installed one: Settings → About shows a **DEV BUILD** badge with the commit, branch and build
time; the tray icon carries an amber dot; and its tooltip ends in "(dev)". The branch is read from
`.git/HEAD`, and is left out in a git worktree or on a detached HEAD. A release shows none of this.

## Notes and limitations

- Exclusive-mode audio (ASIO / WASAPI exclusive) bypasses Equalizer APO entirely and is
  unaffected — in Equalizer APO mode the volume keys will not work there.
- Volume keys are intercepted system-wide while AorinEQ runs; quitting restores normal handling.
- Peace's own pre-amp slider stacks additively with AorinEQ's preamp — keep Peace's at 0 dB. If
  Peace rewrites `config.txt`, AorinEQ restores its include line.
- If `aorineq.txt` becomes unwritable (e.g. after an Equalizer APO reinstall), a tray balloon
  warns you instead of failing silently.
- There is no pixel-perfect (unscaled) skin rendering mode yet.
