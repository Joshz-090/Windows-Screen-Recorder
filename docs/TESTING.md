# Testing and Quality Assurance Guide

## Test Methodology

Quality and reliability are the primary priorities of this project. Every capability must be physically verified against the target environment:
- **OS**: Windows 11 Pro 64-bit (Build 22631)
- **CPU**: Intel Core i5-6300U @ 2.40GHz
- **RAM**: 8.0 GB RAM
- **GPU**: Intel HD Graphics 520

---

## 1. Engine Verification Tests

### Test 1.1: Audio Device Enumeration
- Command: `ScreenRecorder.Engine.exe --list-audio`
- Criteria: Must enumerate all active WASAPI render endpoints (loopback) and capture endpoints (microphones). Must not crash on disconnected audio hardware.

### Test 1.2: Screen Source Enumeration
- Command: `ScreenRecorder.Engine.exe --list-sources`
- Criteria: Must enumerate all active monitors with correct coordinate bounds and DPI scale factors.

### Test 1.3: 10-Second Baseline Recording
- Command: Record 10 seconds of full-screen desktop at 1080p @ 30 FPS with microphone and system audio enabled.
- Criteria:
  - Generates valid `.mp4` file.
  - Playable in Windows Media Player and VLC.
  - Video length = ~10.0 seconds.
  - Audio stream present, audible, and synchronized with screen actions.
  - Zero unhandled exceptions.

### Test 1.4: Hardware Encoder Detection
- Verify whether Intel Quick Sync (`h264_qsv`) is supported by the driver/FFmpeg on this Intel HD 520 GPU.
- If supported, verify encoding works.
- If unavailable or unsupported, verify software fallback (`libx264` ultrafast/veryfast) seamlessly takes over with readable text.

### Test 1.5: Pause and Resume
- Start recording, pause after 3s, wait 3s, resume, record for 3s, stop.
- Output file should be ~6 seconds duration without freezing or audio glitching across the pause boundary.

---

## 2. Teaching Mode Overlay Tests

### Test 2.1: Passthrough Mode (Click-through)
- When overlay is visible but no drawing tool is selected, verify clicks pass through to background apps (e.g., clicking browser tabs or editor text).

### Test 2.2: Annotation Drawing
- Draw an arrow, rectangle, underline, and freehand line.
- Verify vector geometry renders cleanly with anti-aliasing.

### Test 2.3: Video Burn-in Compositing
- Verify that drawn annotations physically appear in the resulting MP4 video at the exact screen coordinates.

---

## 3. UI and Integration Tests

### Test 3.1: Tauri Shell Launch & IPC
- UI opens cleanly in dark mode.
- Starting recording updates UI status and elapsed timer.
- Live audio level VU meters bounce in response to speech.

### Test 3.2: Recording Library
- Finished recordings appear immediately in the library with thumbnail, file size, duration, and date.
- "Open Folder" opens Windows Explorer highlighting the file.
- "Play" launches playback.
