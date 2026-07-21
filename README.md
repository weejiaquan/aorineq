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
3. Tray menu → "Start with Windows" to run at boot.

## Usage

- **Volume Up / Volume Down / Mute keys** (keyboard media keys or USB volume knobs) — 2% per
  press, instant, no debounce even when held.
- **Tray icon**: left-click opens a draggable slider (scroll wheel works too); menu has
  Mute, Start with Windows, Exit.
- Quitting the app restores normal (for these DACs: dead) Windows volume-key handling.

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
