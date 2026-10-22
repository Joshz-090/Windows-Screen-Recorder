# Windows Screen Recorder Studio with Teaching Mode

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6.svg?logo=windows)](https://github.com/Joshz-090/Windows-Screen-Recorder)
[![Framework](https://img.shields.io/badge/.NET-8.0%20WPF-512BD4.svg?logo=dotnet)](https://dotnet.microsoft.com/)
[![Release](https://img.shields.io/badge/Release-v1.0-brightgreen.svg)](https://github.com/Joshz-090/Windows-Screen-Recorder/releases)

A high-performance, offline-first Windows desktop screen recorder designed for software tutorials, educational lessons, programming demonstrations, and presentations.

---

## ⬇️ Download & Quick Start (Ready to Use)

You do **not** need to install Visual Studio, .NET SDK, or PowerShell to use the app! You can download the portable standalone package and run it immediately:

### 📦 [Download Windows-Screen-Recorder-v1.0.zip](https://github.com/Joshz-090/Windows-Screen-Recorder/releases/latest/download/Windows-Screen-Recorder-v1.0.zip)

*(Or visit all versions on the [GitHub Releases Page](https://github.com/Joshz-090/Windows-Screen-Recorder/releases))*

### 🏃 How to Run:
1. **Download** [`Windows-Screen-Recorder-v1.0.zip`](https://github.com/Joshz-090/Windows-Screen-Recorder/releases/latest/download/Windows-Screen-Recorder-v1.0.zip).
2. **Right-click** the downloaded `.zip` file and select **Extract All...**
3. Open the extracted folder and **double-click `Windows Screen Recorder.exe`**.
4. That's it! Start recording your screen and teaching with live annotations.

---

## 🌟 Key Features

1. **Interactive Teaching Overlay**:
   - **Floating Draggable Toolbar**: Reposition the teaching toolbar anywhere across your monitors.
   - **Seamless Cursor Mode**: Click-through (`WS_EX_TRANSPARENT`) support allows you to interact with your desktop apps, browser, or code editor while annotations remain visible.
   - **Vector Drawing Tools**: High-contrast **Arrow**, **Rectangle**, **Underline**, and smooth **Freehand Pen**.
   - **Color Palette & Instant Undo**: Switch between Red, Yellow, Green, and Blue; full single-step undo and clear controls.
   - **Clean Video Output**: Floating toolbar UI is automatically excluded from the recording, capturing only your content and annotations cleanly.

2. **High-Performance Desktop Capture**:
   - Zero-copy DIBSection frame capture with Direct3D/GDI acceleration.
   - 60 FPS smooth capture with dynamic support for 1080p, 720p, and native display resolutions.
   - Ultra-low CPU and RAM overhead optimized for everyday laptops and integrated GPUs.

3. **Dual-Channel WASAPI Audio**:
   - Simultaneous Microphone and System Audio loopback capture.
   - Real-time 48 kHz stereo mixing and floating-point VU meters.
   - Drift-free audio/video timestamp alignment.

4. **Studio Workstation & Library**:
   - Modern dark-themed user interface.
   - Instant recording history library with duration, file size, and "Show in Folder" navigation.
   - Pause, resume, and clean MP4 encoding powered by bundled FFmpeg.

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
        ├── OverlayWindow.xaml.cs# Vector drawing & pass-through hit-test control
        ├── TeachingToolbarWindow.xaml    # Floating draggable teaching toolbar UI
        └── TeachingToolbarWindow.xaml.cs # Tool selection & capture exclusion
```

---

## 🛠 System Requirements

- **Operating System**: Windows 10 (version 1809+) or Windows 11 (64-bit).
- **Architecture**: x64.
- **Memory**: Minimum 4 GB RAM (8 GB recommended).
- **Graphics**: Intel HD Graphics or dedicated GPU (NVIDIA / AMD).

---

## 💻 Building from Source (For Developers)

If you wish to build or customize the application yourself:

### 1. Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 or VS Code with C# Dev Kit

### 2. Build Solution
```powershell
dotnet build src/ScreenRecorder.App/ScreenRecorder.App.csproj -c Release
```

### 3. Launch App
```powershell
.\launch_app.ps1
```

### 4. Create Standalone Release Package
To compile a single self-contained executable bundled with the runtime:
```powershell
dotnet publish src/ScreenRecorder.App/ScreenRecorder.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o "publish/Windows-Screen-Recorder"
```

---

## 📜 License

This project is licensed under the **MIT License** - see the [LICENSE](LICENSE) file for details.

Contains bundled FFmpeg builds licensed under GNU GPL/LGPL.
