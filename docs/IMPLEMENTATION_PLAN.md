# Professional Windows Screen Recorder - Implementation Plan

## Phase Breakdown

### Phase 1: Native Recording Engine Prototype & Core Pipeline
1. **Initialize .NET 8 Native Engine Project (`engine/ScreenRecorder.Engine`)**:
   - Audio Capture: NAudio WASAPI for mic + system loopback audio.
   - Screen Capture: Windows Graphics Capture / Direct3D 11 Desktop Duplication / Win32 GDI capture.
   - Encoder Integration: FFmpeg CLI pipe (STDIN raw video + audio stream -> MP4 output).
   - Intel QSV hardware encoder detection with libx264 software fallback.
   - JSON-RPC command loop (stdin/stdout) for controlling engine from desktop shell.
2. **Acceptance Test for Phase 1**:
   - Command-line test recording a real 10-second desktop video with mic audio, producing a valid, playable MP4 file.
   - Inspect output container, audio/video synchronization, and frame consistency.

### Phase 2: Quality & Performance Hardening
1. Add diagnostics: dropped frame tracking, audio VU levels calculation, memory and CPU limits.
2. Graceful recovery on abrupt stop / crash handling.
3. Configurable profiles: 1080p@30fps, 1080p@60fps, native resolution.

### Phase 3: Desktop UI & Tauri Shell
1. Initialize Tauri 2 project with React, TypeScript, and Vite in `app/`.
2. Connect Tauri Rust backend to the .NET engine process via standard I/O pipes.
3. Build the UI:
   - Source selector (Display / Window list).
   - Audio source selectors and live VU level meters.
   - Recording controls (Start, Pause, Resume, Stop, Elapsed Timer).
   - Settings view for quality, resolution, framerate, and paths.

### Phase 4: Teaching Mode (Annotation Overlay)
1. Implement a borderless transparent overlay window:
   - Mouse click-through (`WS_EX_TRANSPARENT`) when drawing is inactive.
   - Interactive drawing mode when tool is active.
2. Core annotation tools:
   - Arrow
   - Rectangle
   - Underline
   - Freehand pen
3. Compositing:
   - Feed annotation events to the engine in real-time or render onto captured frames so annotations are burned directly into the output video.

### Phase 5: Recording Library & Metadata Persistence
1. Store recording history and metadata in local SQLite or structured JSON file.
2. Features:
   - Recording list with thumbnail, date, duration, size.
   - Play video inside app or open in default player.
   - Open containing folder.
   - Rename / delete with confirmation.

### Phase 6: Packaging, Distribution & Verification
1. Bundle native engine and FFmpeg essentials with the Tauri desktop app.
2. Configure build script for self-contained installer (`.msi` / `.exe`).
3. Document all setup steps, verification procedures, and user guide in `README.md`.
