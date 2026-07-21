# apo-volume

Native Windows tray volume control for DACs that ignore Windows volume (e.g. HiBy FC5),
implemented as an [Equalizer APO](https://equalizerapo.com) preamp controller.

## Why

Some USB DACs advertise USB hardware volume but ignore the host's volume commands, leaving
the Windows volume slider dead. apo-volume intercepts your volume keys and applies a
Windows-style 0–100% volume as digital attenuation in the Equalizer APO chain — before
audio reaches the DAC. Works alongside Peace (EQ stays in Peace, volume lives here).

## Requirements

- Windows 10/11, Equalizer APO installed on your DAC's playback device.

## Install

1. Run `ApoVolume.exe`. On first run it creates `apo-volume.txt` in the APO config folder
   and adds `Include: apo-volume.txt` to `config.txt` (elevating once only if needed).
2. Set your DAC's physical volume to your maximum comfortable loudness — once.
3. Tray menu → "Start with Windows" to run at boot.

## Volume model

0% = mute (−120 dB) · 1% = −50 dB · 100% = 0 dB, linear in dB (≈0.5 dB per %).
Keys step 2% per press. Never exceeds 0 dB, so no digital clipping.

## Notes

- Exclusive-mode audio (ASIO / WASAPI exclusive) bypasses Equalizer APO and is unaffected.
- Keys are intercepted system-wide while the app runs; quitting restores normal handling.
- Peace's own pre-amp slider stacks additively with apo-volume's preamp (both `Preamp:` lines
  apply) — keep Peace's pre-amp at 0 dB.
