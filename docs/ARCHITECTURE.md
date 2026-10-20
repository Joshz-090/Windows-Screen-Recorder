# Professional Windows Screen Recorder with Teaching Mode - Architecture Design

## 1. System Overview

The application is an offline-first, high-performance Windows desktop screen recorder designed for software tutorials, educational lessons, and programming demonstrations.

It balances two primary design requirements:
1. **Uncompromised Recording Engine**: Low latency, synchronized audio/video, high detail (crisp text/code), low CPU utilization on modest hardware (Intel HD Graphics 520, 8 GB RAM, Core i5).
2. **Teaching Mode**: Interactive desktop annotation overlay (arrows, rectangles, underlines, pens) that renders seamlessly above arbitrary Windows applications without blocking desktop clicks when not in active drawing mode, while reliably compositing annotations onto recorded frames.

```
+-------------------------------------------------------------------------+
|                              Desktop UI                                 |
|      (React 18 + TypeScript + Vite + Tailwind/Modern Dark Theme)        |
+------------------------------------+------------------------------------+
                                     |
                                     | Tauri 2 IPC / Rust Core
                                     v
+-------------------------------------------------------------------------+
|                           Tauri Rust Bridge                             |
|  - Process lifecycle management (Engine process runner)                 |
|  - Bidirectional JSON-RPC / IPC pipe communication                     |
|  - Transparent native Overlay Window management                         |
+------------------------------------+------------------------------------+
                                     | Standard I/O (JSON-RPC)
                                     v
+-------------------------------------------------------------------------+
|                  Native Recording Engine (.NET 8.0)                     |
|                                                                         |
|  +-----------------------+  +----------------------+                    |
|  | WindowsGraphicsCapture|  | WASAPI Audio Capture |                    |
|  | (D3D11 Texture / GDI) |  | (Mic + Loopback Mix) |                    |
|  +-----------+-----------+  +----------+-----------+                    |
|              |                         |                                |
|              v                         v                                |
|  +-----------------------+             |                                |
|  | Annotation Compositor |             |                                |
|  | (GDI/D3D Frame Blit)  |             |                                |
|  +-----------+-----------+             |                                |
|              |                         |                                |
|              v                         v                                |
|  +-------------------------------------------------+                    |
|  | FFmpeg / Media Foundation Real-time Pipeline    |                    |
|  | (Intel QSV h264_qsv / libx264 software fallback)|                    |
|  +-------------------------+-----------------------+                    |
|                            |                                            |
|                            v                                            |
|                    Final MP4 Output                                     |
+-------------------------------------------------------------------------+
```

---

## 2. Core Subsystems

### A. Native Capture Engine (.NET 8.0)
- **Target Framework**: `net8.0-windows10.0.19041.0` (or `net8.0` with Win32 interop)
- **Video Capture Pipeline**:
  - Primary API: Windows Graphics Capture (WGC) via Direct3D 11 (`ID3D11Device`, `IDXGIResource`, `ID3D11Texture2D`).
  - Fallback API: High-speed GDI/Direct3D Desktop Duplication / BitBlt interop for standard displays.
  - Frame delivery: Dedicated capture thread feeding a ring buffer with high-precision timestamps (`QueryPerformanceCounter`).
- **Audio Capture Pipeline**:
  - Library: `NAudio` (WASAPI).
  - Devices:
    - Default/Selected Microphone capture (`WasapiCapture`).
    - System Loopback capture (`WasapiLoopbackCapture`).
  - Mixing & Synchronization:
    - Real-time 32-bit float audio resampler and mixer (combining mic + system audio into stereo 48kHz PCM).
    - Monotonic audio timestamp alignment with video frames to prevent drift over long sessions.
- **Encoding & Multiplexing**:
  - Hardware Acceleration: Intel Quick Sync Video (`h264_qsv`) detected dynamically.
  - Fallback: High-efficiency `libx264` configured with `ultrafast` / `veryfast` preset and CRF 22-24 for low CPU utilization.
  - Audio: AAC stereo at 192 kbps.
  - Container: MP4 with `-movflags +faststart` for immediate playback compatibility.

### B. Teaching Mode & Overlay Architecture
- **Overlay Window**:
  - Transparent, frameless, topmost desktop window.
  - Window Styles: `WS_EX_LAYERED`, `WS_EX_TRANSPARENT` (click-through) when in passthrough mode.
  - Active Drawing Mode: Click-through disabled temporarily when the user selects an annotation tool or presses the annotation shortcut.
  - Annotations: Vector-based drawing elements (arrows, rectangles, underlines, step markers, freehand lines) stored as coordinate sequences with timestamps.
- **Recording Compositor**:
  - Annotations are rendered into an offscreen drawing layer matching display coordinates and resolution.
  - During frame encoding, active annotations are blitted onto the captured frame buffer before piping to the encoder.
  - Future-proof design: Vectors are also serialized as `.teaching.json` alongside `.mp4` for non-destructive post-recording adjustments.

### C. Desktop UI & Shell (Tauri 2 + React)
- **Tauri Shell**:
  - Manages the native engine process: start, stop, pause, resume, status polling.
  - Provides system tray integration, global keyboard shortcuts, and file system dialogs.
- **Frontend (React + Vite + TypeScript)**:
  - Clean, restrained dark theme tailored for creators.
  - Responsive audio level meters (VU meters).
  - Source selector (Monitors, Windows).
  - Recording library with local thumbnail/metadata browser.
  - Settings manager with local persistence via SQLite/local JSON store.

---

## 3. Communication Protocol (UI <-> Engine)

Communication operates over standard input/output using a JSON-RPC / newline-delimited JSON stream:

### UI to Engine Commands
- `{"id": 1, "method": "get_sources"}` -> returns list of displays and windows.
- `{"id": 2, "method": "detect_encoders"}` -> returns hardware/software encoder support (e.g. `qsv_available: true`).
- `{"id": 3, "method": "get_audio_devices"}` -> lists microphones and loopback endpoints.
- `{"id": 4, "method": "start", "params": { "source_id": "display:0", "fps": 30, "resolution": "1920x1080", "encoder": "h264_qsv", "mic_device_id": "...", "record_system_audio": true, "output_path": "..." }}`
- `{"id": 5, "method": "pause"}`
- `{"id": 6, "method": "resume"}`
- `{"id": 7, "method": "stop"}`
- `{"id": 8, "method": "add_annotation", "params": { "tool": "arrow", "points": [...], "color": "#FF0000", "width": 4 }}`
- `{"id": 9, "method": "clear_annotations"}`

### Engine to UI Events
- `{"event": "status", "data": { "state": "recording", "duration_ms": 12450, "fps": 30.1, "dropped_frames": 0, "mic_level": 0.42, "sys_level": 0.15 }}`
- `{"event": "error", "data": { "code": "ENCODER_ERROR", "message": "Intel QSV failed, falling back to software" }}`
- `{"event": "finalized", "data": { "output_path": "...", "file_size": 24810234, "duration_sec": 65.4 }}`

---

## 4. Hardware and Performance Strategy (8 GB RAM / Intel HD 520)

1. **Memory Budget**:
   - Engine process memory capped at < 250 MB.
   - Circular buffer for captured frames limited to 15 frames (~120 MB uncompressed at 1080p).
   - If buffer is full, frames are dropped gracefully with a logged warning, preventing unbounded allocation.
2. **CPU Management**:
   - Zero-copy Direct3D surface sharing where possible.
   - Multithreaded pipeline: Thread 1 (Capture), Thread 2 (Audio WASAPI), Thread 3 (Compositing & Encoder Pipe).
3. **Graceful Degradation**:
   - If Intel QSV initialization fails or device lacks hardware h264 session, engine automatically switches to `libx264` with fast preset without terminating the session.
