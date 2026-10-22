using System.Diagnostics;
using System.Text.Json;
using ScreenRecorder.Engine.Annotations;
using ScreenRecorder.Engine.Audio;
using ScreenRecorder.Engine.Capture;

namespace ScreenRecorder.Engine;

public class Program
{
    private static RecordingSession? _currentSession;
    private static readonly AnnotationCompositor _compositor = new();
    private static readonly object _sessionLock = new();

    public static async Task Main(string[] args)
    {
        // 1. Standalone CLI direct test mode
        if (args.Length > 0)
        {
            await HandleCliArgs(args);
            return;
        }

        // 2. Interactive JSON-RPC loop (for Tauri/UI integration)
        await RunIpcLoop();
    }

    private static async Task HandleCliArgs(string[] args)
    {
        if (args[0] == "--list-sources")
        {
            var sources = WindowsScreenCapturer.EnumerateDisplays();
            Console.WriteLine(JsonSerializer.Serialize(sources, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        if (args[0] == "--list-audio")
        {
            var mics = AudioCaptureManager.EnumerateMicrophones();
            var outputs = AudioCaptureManager.EnumerateSystemOutputs();
            Console.WriteLine(JsonSerializer.Serialize(new { Microphones = mics, SystemOutputs = outputs }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        if (args[0] == "--record-test-pause")
        {
            string outputPath = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_pause_recording.mp4");
            Console.WriteLine($"Starting pause-resume test recording to: {outputPath}");

            var sources = WindowsScreenCapturer.EnumerateDisplays();
            var primary = sources.FirstOrDefault(s => s.IsPrimary) ?? sources[0];
            string ffmpegPath = FindFFmpegPath();

            var session = new RecordingSession(
                source: primary,
                targetWidth: primary.Width,
                targetHeight: primary.Height,
                fps: 30,
                micDeviceId: "default",
                captureSystemAudio: true,
                outputPath: outputPath,
                ffmpegPath: ffmpegPath,
                encoderName: "libx264",
                compositor: _compositor
            );

            session.Start();
            Console.WriteLine("[CLI] Recording phase 1 (2s)...");
            await Task.Delay(2000);

            Console.WriteLine("[CLI] Pausing for 2s...");
            session.Pause();
            await Task.Delay(2000);

            Console.WriteLine("[CLI] Resuming phase 2 (2s)...");
            session.Resume();
            await Task.Delay(2000);

            Console.WriteLine("[CLI] Stopping session...");
            session.Stop();
            session.Dispose();

            var fileInfo = new FileInfo(outputPath);
            Console.WriteLine($"[CLI] Pause-resume recording completed! File size: {fileInfo.Length / 1024} KB.");
            return;
        }

        if (args[0] == "--record-test")
        {
            int durationSec = args.Length > 1 && int.TryParse(args[1], out int d) ? d : 5;
            string outputPath = args.Length > 2 ? args[2] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_recording.mp4");

            Console.WriteLine($"Starting {durationSec}-second test recording to: {outputPath}");

            var sources = WindowsScreenCapturer.EnumerateDisplays();
            var primary = sources.FirstOrDefault(s => s.IsPrimary) ?? sources[0];

            string ffmpegPath = FindFFmpegPath();

            Console.WriteLine($"Using display: {primary.Name} ({primary.Width}x{primary.Height})");
            Console.WriteLine($"FFmpeg path: {ffmpegPath}");

            string encoder = args.Length > 3 ? args[3] : "libx264";
            Console.WriteLine($"Selected encoder: {encoder}");

            var session = new RecordingSession(
                source: primary,
                targetWidth: primary.Width,
                targetHeight: primary.Height,
                fps: 30,
                micDeviceId: "default",
                captureSystemAudio: true,
                outputPath: outputPath,
                ffmpegPath: ffmpegPath,
                encoderName: encoder,
                compositor: _compositor
            );

            session.Start();

            // Simulate teaching annotation halfway through
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                _compositor.AddAnnotation(new AnnotationItem
                {
                    Tool = "arrow",
                    Color = "#FF0000",
                    StrokeWidth = 6,
                    Points = new List<PointD>
                    {
                        new() { X = primary.Width / 2 - 200, Y = primary.Height / 2 - 150 },
                        new() { X = primary.Width / 2, Y = primary.Height / 2 }
                    }
                });
                _compositor.AddAnnotation(new AnnotationItem
                {
                    Tool = "rectangle",
                    Color = "#00FF00",
                    StrokeWidth = 4,
                    Points = new List<PointD>
                    {
                        new() { X = primary.Width / 2 - 100, Y = primary.Height / 2 - 80 },
                        new() { X = primary.Width / 2 + 100, Y = primary.Height / 2 + 80 }
                    }
                });
                Console.WriteLine("[CLI] Added test teaching annotations (arrow + rectangle).");
            });

            for (int i = 0; i < durationSec; i++)
            {
                await Task.Delay(1000);
                Console.WriteLine($"Elapsed: {session.ElapsedMs / 1000}s | FPS: {session.CurrentActualFps:F1} | Encoded: {session.EncodedFrames} | Dropped: {session.DroppedFrames} | Mic: {session.CurrentMicLevel:F2} | Sys: {session.CurrentSystemLevel:F2}");
            }

            session.Stop();
            session.Dispose();

            var fileInfo = new FileInfo(outputPath);
            Console.WriteLine($"[CLI] Recording completed! File size: {fileInfo.Length / 1024} KB. Playable file generated.");
        }
    }

    private static async Task RunIpcLoop()
    {
        using var reader = new StreamReader(Console.OpenStandardInput());

        // Emit ready event
        SendEvent("ready", new { version = "1.0.0", encoders = new[] { "h264_qsv", "libx264" } });

        // Status update background loop (emits ~4 times per second during recording)
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(250);
                lock (_sessionLock)
                {
                    if (_currentSession != null)
                    {
                        SendEvent("status", new
                        {
                            isRecording = true,
                            isPaused = _currentSession.IsPaused,
                            elapsedMs = _currentSession.ElapsedMs,
                            fps = _currentSession.CurrentActualFps,
                            encodedFrames = _currentSession.EncodedFrames,
                            droppedFrames = _currentSession.DroppedFrames,
                            micLevel = _currentSession.CurrentMicLevel,
                            systemLevel = _currentSession.CurrentSystemLevel
                        });
                    }
                }
            }
        });

        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                int id = root.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                string method = root.GetProperty("method").GetString() ?? "";
                JsonElement paramsEl = root.TryGetProperty("params", out var p) ? p : default;

                HandleMethod(id, method, paramsEl);
            }
            catch (Exception ex)
            {
                SendError(0, ex.Message);
            }
        }
    }

    private static void HandleMethod(int id, string method, JsonElement p)
    {
        switch (method)
        {
            case "ping":
                SendResponse(id, new { pong = true });
                break;

            case "get_sources":
                var displays = WindowsScreenCapturer.EnumerateDisplays();
                SendResponse(id, new { sources = displays });
                break;

            case "get_audio_devices":
                var mics = AudioCaptureManager.EnumerateMicrophones();
                var outputs = AudioCaptureManager.EnumerateSystemOutputs();
                SendResponse(id, new { microphones = mics, systemOutputs = outputs });
                break;

            case "detect_encoders":
                bool qsvAvailable = TestQsvEncoder();
                SendResponse(id, new
                {
                    hardware = qsvAvailable ? new[] { "h264_qsv" } : Array.Empty<string>(),
                    software = new[] { "libx264" },
                    preferred = qsvAvailable ? "h264_qsv" : "libx264"
                });
                break;

            case "start":
                lock (_sessionLock)
                {
                    if (_currentSession != null)
                    {
                        SendError(id, "Recording already active.");
                        return;
                    }

                    string sourceId = p.TryGetProperty("source_id", out var sid) ? sid.GetString() ?? "display:0" : "display:0";
                    int fps = p.TryGetProperty("fps", out var fpsProp) ? fpsProp.GetInt32() : 30;
                    string res = p.TryGetProperty("resolution", out var resProp) ? resProp.GetString() ?? "1920x1080" : "1920x1080";
                    string? micId = p.TryGetProperty("mic_device_id", out var mProp) ? mProp.GetString() : "default";
                    bool recordSystem = p.TryGetProperty("record_system_audio", out var rProp) && rProp.GetBoolean();
                    string outputPath = p.TryGetProperty("output_path", out var outProp) ? outProp.GetString() ?? "" : "";
                    string encoderName = p.TryGetProperty("encoder", out var encProp) ? encProp.GetString() ?? "h264_qsv" : "h264_qsv";

                    if (string.IsNullOrEmpty(outputPath))
                    {
                        string videosDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                        string recDir = Path.Combine(videosDir, "ScreenRecorder");
                        Directory.CreateDirectory(recDir);
                        outputPath = Path.Combine(recDir, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                    }

                    var sources = WindowsScreenCapturer.EnumerateDisplays();
                    var source = sources.FirstOrDefault(s => s.Id == sourceId) ?? sources[0];

                    int width = source.Width;
                    int height = source.Height;
                    if (res != "native" && res.Contains('x'))
                    {
                        var parts = res.Split('x');
                        if (int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                        {
                            width = w;
                            height = h;
                        }
                    }

                    _compositor.Clear();

                    _currentSession = new RecordingSession(
                        source: source,
                        targetWidth: width,
                        targetHeight: height,
                        fps: fps,
                        micDeviceId: micId,
                        captureSystemAudio: recordSystem,
                        outputPath: outputPath,
                        ffmpegPath: FindFFmpegPath(),
                        encoderName: encoderName,
                        compositor: _compositor
                    );

                    _currentSession.Start();
                    SendResponse(id, new { success = true, outputPath = outputPath, width = width, height = height, fps = fps });
                }
                break;

            case "pause":
                lock (_sessionLock)
                {
                    _currentSession?.Pause();
                    SendResponse(id, new { success = true, isPaused = true });
                }
                break;

            case "resume":
                lock (_sessionLock)
                {
                    _currentSession?.Resume();
                    SendResponse(id, new { success = true, isPaused = false });
                }
                break;

            case "stop":
                string? finalPath = null;
                long finalSize = 0;
                long durationMs = 0;
                lock (_sessionLock)
                {
                    if (_currentSession != null)
                    {
                        finalPath = _currentSession.OutputPath;
                        durationMs = _currentSession.ElapsedMs;
                        _currentSession.Stop();
                        _currentSession.Dispose();
                        _currentSession = null;

                        if (File.Exists(finalPath))
                        {
                            finalSize = new FileInfo(finalPath).Length;
                        }
                    }
                }
                SendResponse(id, new
                {
                    success = true,
                    outputPath = finalPath,
                    durationMs = durationMs,
                    fileSizeBytes = finalSize
                });
                break;

            case "add_annotation":
                var ann = JsonSerializer.Deserialize<AnnotationItem>(p.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (ann != null)
                {
                    _compositor.AddAnnotation(ann);
                    SendResponse(id, new { success = true, id = ann.Id });
                }
                else
                {
                    SendError(id, "Invalid annotation data");
                }
                break;

            case "undo_annotation":
                _compositor.Undo();
                SendResponse(id, new { success = true });
                break;

            case "clear_annotations":
                _compositor.Clear();
                SendResponse(id, new { success = true });
                break;

            default:
                SendError(id, $"Unknown method: {method}");
                break;
        }
    }

    private static bool TestQsvEncoder()
    {
        try
        {
            string ffmpeg = FindFFmpegPath();
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-f lavfi -i testsrc=duration=1:size=320x240:rate=10 -c:v h264_qsv -f null -",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string FindFFmpegPath()
    {
        string[] candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "bin", "ffmpeg.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "ffmpeg.exe"),
            @"D:\projects\Bili\coding\Windows Screen Recorder\engine\bin\ffmpeg.exe"
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        return "ffmpeg.exe";
    }

    private static void SendResponse(int id, object result)
    {
        var resp = new { id = id, result = result };
        Console.WriteLine(JsonSerializer.Serialize(resp));
    }

    private static void SendError(int id, string message)
    {
        var resp = new { id = id, error = new { message = message } };
        Console.WriteLine(JsonSerializer.Serialize(resp));
    }

    private static void SendEvent(string eventName, object data)
    {
        var ev = new { @event = eventName, data = data };
        Console.WriteLine(JsonSerializer.Serialize(ev));
    }
}
