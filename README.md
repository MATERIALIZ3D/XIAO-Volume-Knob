# XIAO ESP32-C3 Bluetooth Volume Knob

A wireless **Bluetooth LE media volume knob** built on a Seeed Studio XIAO ESP32-C3,
with a 12-LED WS2812 ring and a native **Windows companion app** for battery
monitoring and live LED customisation.

Rotate to change the system volume, press for transport controls, and watch the
ring react. Runs off USB or a LiPo battery, and its colours/behaviour are fully
configurable from the desktop app over the same Bluetooth link.

---

## Features

- **Volume & transport over BLE HID** — works on Windows / macOS / Linux / Android
- **Gestures** — tap = play/pause · double = next · triple = previous · medium hold = mute · **5 s hold = show battery gauge**
- **LED ring modes** — rainbow · solid colour · breathing (all configurable)
- **Status on the ring** — solid red = muted · breathing blue = waiting to pair · red pulse = low battery
- **Battery-saver** — the ring eases to a dim glow then blanks when idle, and wakes instantly on any turn/click
- **On-board battery monitoring** — LiPo voltage via a divider, with an on-ring low-battery warning
- **Windows companion app** — a modern WPF app to see live battery and change colour (HSV wheel), brightness, mode, motion speed and idle timers; **settings persist to the knob's flash**

## Hardware

| Part | Notes |
|------|-------|
| Seeed Studio XIAO ESP32-C3 | BLE-only ESP32-C3, native USB |
| Rotary encoder 16 mm 24 P/R + push switch | quadrature + button |
| 12× WS2812 (5050) LED ring | addressable RGB |
| 3.7 V LiPo + 3.7→5 V boost converter | for untethered/battery use |
| 330 Ω resistor | in series on the LED data line |
| 2× 100 kΩ resistors | battery-sense divider on BAT+ |

## Wiring

Full colour diagrams are in [`docs/`](docs):

- [`docs/wiring.svg`](docs/wiring.svg) — USB-powered
- [`docs/wiring-battery.svg`](docs/wiring-battery.svg) — battery + boost converter

**Pin map** (chosen to avoid the C3 strapping pins 2 / 8 / 9):

| Signal | XIAO | GPIO |
|--------|------|------|
| Encoder A | D4 | 6 |
| Encoder B | D5 | 7 |
| Push switch | D6 | 21 |
| WS2812 data | D10 | 10 |
| Battery sense | D1 | 3 |

Encoder/switch use internal pull-ups. Ring `DOUT` is left unconnected.

## Firmware (PlatformIO)

```
pio run -t upload
pio device monitor
```

> The platform is pinned to `espressif32@6.5.0` (Arduino core 2.0.14) on purpose —
> it's the combination `ESP32-BLE-Keyboard 0.3.x` compiles against, and it uses the
> **NimBLE** stack (`-D USE_NIMBLE`) for a stable BLE HID link on Windows 11.

Then pair **"Eugene's Knob"** from your PC's Bluetooth settings.

## Windows companion app

A native WPF app (`companion-app/`) that talks to the knob's custom GATT service.

- **Download:** grab `VolumeKnob.exe` from the [latest release](../../releases/latest) — it's self-contained, no .NET install needed.
- **First run:** pair the knob in Windows Bluetooth settings first; the app finds it by name.
- **Build from source:** `cd companion-app && dotnet run` (needs the .NET 8 SDK).

It shows live battery %/voltage and lets you set the ring colour (HSV wheel),
brightness, mode, rainbow/breathing speed, and the battery-saver timings — all
applied live and saved on the knob.

## BLE GATT protocol

Custom service alongside the standard HID service:

| Item | UUID | Access | Payload |
|------|------|--------|---------|
| Service | `5da10000-9f2b-4c7e-8a3d-2b6c1e4f7a90` | — | — |
| Status  | `5da10001-…` | read / notify | `[u16 mV][u8 %][u8 flags]` |
| Config  | `5da10002-…` | read / write  | 14-byte packed struct |

`Config` = `{ u8 mode, u8 r,g,b, u8 brightness, u8 rainbowSpeed, u8 rainbowSpread, u16 idleDimS, u16 idleOffS, u16 lowBattMv, u8 breatheSpeed }` — persisted to NVS.

## Repository layout

```
src/            firmware (main.cpp)
companion-app/  Windows WPF app (+ icon generator)
docs/           colour wiring diagrams
platformio.ini  build config
```

## Notes / limitations

HID media keys are one-way — the PC never reports real volume or play state back,
so the ring shows an internal model (which is why it uses a rainbow rather than a
play/pause colour). The battery % is a voltage-based estimate.
