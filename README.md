# EQ-Streamer Server

A **real-time audio analyzer** for Windows that captures system output through **WASAPI loopback**, performs FFT-based spectral analysis, and streams per-band volume levels via UDP.
Designed to serve LED visualizers or other client devices that react to live audio.

---

## 📦 Overview

| Component                 | Description                                                                                                                                     |
| ------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| **Server (`Program.cs`)** | C#/.NET 8 console app that captures system audio, computes FFT (RMS + dBFS normalization), and broadcasts frequency-band levels as UDP packets. |
| **clients_test/**         | Experimental / development clients for visualization and debugging. These are not part of the production server.                                |
| **UDP Payload**           | `[ 'E','Q', 1, <B0>, <B1>, ... <Bn> ]` where each byte (0–255) represents a normalized band level.                                              |

---

## ⚙️ How It Works

1. Uses **WASAPI loopback** to capture mixed system output (`WasapiLoopbackCapture`).
2. Applies **Hann window**, computes **FFT (1024-point)**.
3. Groups bins into **log-spaced bands** (default: 32 bands, 80 Hz–20 kHz).
4. Computes **RMS power per band** → converts to **dBFS** → normalizes to 0–255.
5. Sends the array as a UDP broadcast (`255.255.255.255:31337`).

---

## 🚀 Run (Server)

### Requirements

* Windows 10 / 11
* [.NET 8 SDK](https://dotnet.microsoft.com/download)
* Speakers or output device (for loopback capture)

### Build & Run

```bash
dotnet build
dotnet run
```

Expected output:

```
Loopback: 48000 Hz, 2 ch, IEEEFloat
Streaming 32 bands via UDP broadcast :31337. Press ENTER to stop.
```

---

## 🧪 Test Clients

All visualization and debugging tools are kept under `clients_test/`.
They are **not** part of the server logic.

| File                      | Description                                                                                          |
| ------------------------- | ---------------------------------------------------------------------------------------------------- |
| `eqClientTUI_Curses.py`   | Curses-based square TUI visualizer (Windows-compatible). Displays tall bars in a near-square layout. |

### Example

```bash
python -m pip install numpy windows-curses
python clients_test/eqClientTUI_Curses.py
```

Press **q** to quit.

---

## 📡 Packet Format

| Byte(s) | Meaning                                  |
| ------- | ---------------------------------------- |
| 0–1     | `'E','Q'` header                         |
| 2       | Protocol version (currently `1`)         |
| 3..N    | Band levels (0–255) low → high frequency |

---

## 🔧 Configuration Knobs (in `Program.cs`)

| Constant            | Default    | Description                          |
| ------------------- | ---------- | ------------------------------------ |
| `BANDS`             | 32         | Number of frequency bands            |
| `F_MIN`             | 80 Hz      | Lower frequency bound                |
| `F_MAX`             | 20 kHz     | Upper frequency bound                |
| `MIN_DB` / `MAX_DB` | −60 / 0 dB | Mapping range for dBFS normalization |
| `FFT_SIZE`          | 1024       | FFT window size                      |

---

## 🦩 Commit Convention

This repository follows **Conventional Commits**.
Example:

```bash
git commit -m "feat(eq-streamer): use RMS + dBFS normalization for band levels"
```

---

## 🗂️ Directory Layout

```
.
├── Program.cs              # Main server
├── clients_test/           # Experimental / visualization clients
└── README.md
```

