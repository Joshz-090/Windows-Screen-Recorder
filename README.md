# Windows Screen Recorder Studio with Teaching Mode

A high-performance, offline-first Windows desktop screen recorder designed for software tutorials, educational lessons, programming demonstrations, and presentations.

---

## 🌟 Key Features

1. **High-Performance Desktop Capture**:
   - Zero-copy DIBSection frame capture with Direct3D/GDI acceleration.
   - Dynamic support for 1080p, 720p, and native display resolutions.
   - Low CPU overhead optimized for 8 GB RAM and integrated Intel graphics (e.g. Intel HD 520).
2. **Dual-Channel WASAPI Audio**:
   - Simultaneous Microphone and System Audio loopback capture.
   - Real-time 48 kHz stereo mixing and floating-point VU meters.
   - Drift-free audio/video timestamp alignment.
3. **Interactive Teaching Overlay**:
   - Borderless, transparent desktop overlay window.
   - Click-through (`WS_EX_TRANSPARENT`) support when not annotating, allowing uninterrupted interaction with background desktop apps.
   - High-contrast annotation tools: **Arrow**, **Rectangle**, **Underline**, and **Freehand Pen**.
   - Real-time compositing into the recorded MP4 video.
4. **Studio Interface & Recording Library**:
   - Modern dark-themed studio workstation.
   - Instant playback and "Show in Folder" navigation.
   - Configurable resolution, framerate, video encoders, and audio inputs.

---

## 📁 Repository Structure

```
├── docs/
│   ├── ARCHITECTURE.md          # Technical design, capture pipelines, IPC protocol
│   ├── IMPLEMENTATION_PLAN.md   # Phased delivery roadmap and engineering milestones
│   └── TESTING.md               # Quality assurance criteria and hardware test records
├── engine/
│   ├── bin/                     # Bundled FFmpeg essentials binaries
│   └── ScreenRecorder.Engine/   # .NET 8 Capture Engine
│       ├── Annotations/         # Teaching vector models (Arrow, Rect, Pen, Underline)
│       ├── Audio/               # WASAPI microphone and system loopback mixer
│       ├── Capture/             # DIBSection BitBlt capturer & frame compositor
│       ├── Encoder/             # FFmpeg process controller (QSV / libx264)
│       └── Program.cs           # Dual-mode CLI & JSON-RPC IPC interface
└── src/
    └── ScreenRecorder.App/      # Desktop Studio User Interface
        ├── Models/              # Recording library models
        ├── MainWindow.xaml      # Studio UI layout (sources, meters, library)
        ├── MainWindow.xaml.cs   # Studio orchestration logic
        ├── OverlayWindow.xaml   # Transparent desktop annotation overlay
        └── OverlayWindow.xaml.cs# Vector drawing & pass-through hit-test control
```

---

## 🛠 Prerequisites

- **Operating System**: Windows 10 (version 1809+) or Windows 11 (64-bit).
- **Runtime**: [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
- **Graphics**: Intel HD Graphics or dedicated GPU.
- **Memory**: Minimum 4 GB RAM (8 GB recommended).

---

## 🚀 Building & Running

### 1. Build Native Recording Engine
```powershell
cd "engine\ScreenRecorder.Engine"
dotnet build -c Release
```

### 2. Launch the Desktop Studio Application

You can launch the Studio directly using either the one-click launch scripts or dotnet:

**Option A (Recommended - Instant Launch):**
Double-click [launch_app.bat](file:///d:/projects/Bili/coding/Windows%20Screen%20Recorder/launch_app.bat) or run:
```powershell
.\launch_app.ps1
```

**Option B (From terminal with custom dotnet path):**
```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:Path = "$env:DOTNET_ROOT;$env:Path"

cd "src\ScreenRecorder.App"
dotnet run -c Release
```

### 3. Command-Line Diagnostics & Tests
You can test screen and audio device discovery or record standalone diagnostic clips:

```powershell
# Enumerate displays
dotnet exec bin/Debug/net8.0-windows10.0.19041.0/ScreenRecorder.Engine.dll --list-sources

# Enumerate microphones and system loopback outputs
dotnet exec bin/Debug/net8.0-windows10.0.19041.0/ScreenRecorder.Engine.dll --list-audio

# Test a 10-second capture with teaching annotations
dotnet exec bin/Debug/net8.0-windows10.0.19041.0/ScreenRecorder.Engine.dll --record-test 10 test_output.mp4 libx264
```

---

## 🛡 License
MIT License. Contains bundled FFmpeg builds under GNU GPL / LGPL licensing terms.
