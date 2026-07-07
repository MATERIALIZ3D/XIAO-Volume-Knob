# Knob Config — Windows companion app

A small native Windows app that connects to the **XIAO Volume Knob** over
Bluetooth LE to show the **battery level** and change the **LED ring colour and
behaviour** live. Settings are saved on the knob (survive a reboot).

It talks to the knob's custom GATT service (defined in `../src/main.cpp`):

| Item | UUID | Notes |
|------|------|-------|
| Service | `5da10000-9f2b-4c7e-8a3d-2b6c1e4f7a90` | |
| Status  | `5da10001-…` | read/notify: `[u16 mV][u8 %][u8 flags]` |
| Config  | `5da10002-…` | read/write: 13-byte packed struct |

## Prerequisites

1. **Pair the knob** in Windows → *Settings → Bluetooth & devices* (it shows up
   as **Eugene's Knob**). The app finds it by that name.
2. **.NET 8 SDK** (one-time). Either:
   ```powershell
   winget install Microsoft.DotNet.SDK.8
   ```
   or download from <https://dotnet.microsoft.com/download/dotnet/8.0>.

## Build & run

From this folder:

```powershell
dotnet run
```

Or build a standalone exe:

```powershell
dotnet build -c Release
# output: bin/Release/net8.0-windows10.0.19041.0/KnobConfig.exe
```

## Controls

- **Battery** — live %/voltage, updated whenever the knob reports (≈ every 5 s,
  plus an immediate read on connect).
- **Mode** — Rainbow / Solid colour / Breathing.
- **Colour** — used by Solid and Breathing modes (ignored in Rainbow).
- **Brightness** — master ring brightness (0–255).
- **Rainbow speed / spread** — cycle rate and how much of the spectrum wraps the ring (Rainbow mode).
- **Breathing speed** — breath rate (Breathing mode).
- **Idle dim / off (s)** — battery-saver timings.
- **Low-batt (mV)** — threshold for the on-ring red warning pulse.

Changes are written to the knob ~200 ms after you stop adjusting (debounced), and
the knob persists them to flash shortly after.

## Notes / troubleshooting

- The app reuses Windows' existing BLE connection to the paired knob — it does
  **not** open a competing link, so HID volume control keeps working meanwhile.
- If it says *"Config service not found"*, the knob is running older firmware —
  reflash `../src/main.cpp`.
- If it can't find the device, confirm it's **paired** (not just powered) and that
  Bluetooth is on.
- The 13-byte config layout here must stay in lock-step with the `Config` struct
  in the firmware. If you add a field, update both.
