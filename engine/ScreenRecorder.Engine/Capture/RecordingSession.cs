using System.Diagnostics;
using NAudio.Wave;
using ScreenRecorder.Engine.Annotations;
using ScreenRecorder.Engine.Audio;
using ScreenRecorder.Engine.Encoder;

namespace ScreenRecorder.Engine.Capture;

public class RecordingSession : IDisposable
{
    private readonly WindowsScreenCapturer _capturer;
    private readonly AudioCaptureManager _audioManager;
    private readonly FFmpegProcessEncoder _encoder;
    private readonly AnnotationCompositor _compositor;
    private readonly int _targetFps;
    private readonly string _outputPath;

    // Audio-to-file (replaces the unreliable named pipe)
    private WaveFileWriter? _audioWriter;
    private readonly string? _tempAudioPath;
    private readonly object _audioLock = new();

    private Thread? _videoThread;
    private bool _isRunning;
    private bool _isPaused;
    private bool _isStopped;  // guard against double Stop/Dispose
    private readonly Stopwatch _elapsedStopwatch = new();
    private long _totalPausedMs = 0;
    private long _lastPauseStartMs = 0;

    public long DroppedFrames { get; private set; }
    public long EncodedFrames { get; private set; }
    public double CurrentActualFps { get; private set; }
    public float CurrentMicLevel => _audioManager.CurrentMicLevel;
    public float CurrentSystemLevel => _audioManager.CurrentSystemLevel;
    public long ElapsedMs
    {
        get
        {
            // Use Volatile.Read to prevent stale cached values when read from another thread.
            bool paused      = Volatile.Read(ref _isPaused);
            long pausedTotal = Volatile.Read(ref _totalPausedMs);
            long pauseStart  = Volatile.Read(ref _lastPauseStartMs);
            long elapsed     = _elapsedStopwatch.ElapsedMilliseconds;
            return elapsed - pausedTotal - (paused ? (elapsed - pauseStart) : 0);
        }
    }
    public bool IsPaused => _isPaused;
    public string OutputPath => _outputPath;

    public RecordingSession(
        ScreenSourceInfo source,
        int targetWidth,
        int targetHeight,
        int fps,
        string? micDeviceId,
        bool captureSystemAudio,
        string outputPath,
        string ffmpegPath,
        string encoderName,
        AnnotationCompositor compositor)
    {
        _targetFps   = fps;
        _outputPath  = outputPath;
        _compositor  = compositor;

        _capturer = new WindowsScreenCapturer(
            source.X, source.Y, source.Width, source.Height,
            targetWidth, targetHeight, _compositor);

        bool hasAudio = (!string.IsNullOrEmpty(micDeviceId) && micDeviceId != "none")
                        || captureSystemAudio;

        // Create the encoder — it will encode video-only and mux audio in Finish().
        _encoder = new FFmpegProcessEncoder(
            ffmpegPath, outputPath, targetWidth, targetHeight, fps, hasAudio, encoderName);

        _audioManager = new AudioCaptureManager();

        if (hasAudio)
        {
            // Build temp audio path alongside the output file
            var dir = Path.GetDirectoryName(outputPath) ?? ".";
            _tempAudioPath = Path.Combine(
                dir, $"_atmp_{Path.GetFileNameWithoutExtension(outputPath)}_{Guid.NewGuid():N}.wav");

            // AudioCaptureManager pumps 48 kHz stereo IEEE-float chunks
            var wavFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            _audioWriter = new WaveFileWriter(_tempAudioPath, wavFormat);

            _audioManager.AudioDataAvailable += (buffer, length) =>
            {
                if (_isRunning && !_isPaused)
                {
                    lock (_audioLock)
                    {
                        try { _audioWriter?.Write(buffer, 0, length); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[RecordingSession] Audio write error: {ex.Message}");
                        }
                    }
                }
            };

            _audioManager.Start(micDeviceId, captureSystemAudio);
        }
    }

    public void Start()
    {
        _isRunning = true;
        _elapsedStopwatch.Start();

        _videoThread = new Thread(VideoLoop)
        {
            IsBackground = true,
            Name         = "ScreenCaptureVideoLoop",
            Priority     = ThreadPriority.Highest
        };
        _videoThread.Start();
    }

    public void Pause()
    {
        if (_isRunning && !Volatile.Read(ref _isPaused))
        {
            Volatile.Write(ref _lastPauseStartMs, _elapsedStopwatch.ElapsedMilliseconds);
            Volatile.Write(ref _isPaused, true);
            _audioManager.Pause();
        }
    }

    public void Resume()
    {
        if (_isRunning && Volatile.Read(ref _isPaused))
        {
            long pauseDuration = _elapsedStopwatch.ElapsedMilliseconds - Volatile.Read(ref _lastPauseStartMs);
            Interlocked.Add(ref _totalPausedMs, pauseDuration);
            Volatile.Write(ref _isPaused, false);
            _audioManager.Resume();
        }
    }

    private byte[]? _lastFrame;

    private void VideoLoop()
    {
        double frameIntervalMs = 1000.0 / _targetFps;
        long fpsWindowStart = Stopwatch.GetTimestamp();
        int framesInWindow = 0;

        while (_isRunning)
        {
            if (_isPaused)
            {
                Thread.Sleep(30);
                continue;
            }

            try
            {
                _lastFrame = _capturer.CaptureFrame();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordingSession] Capture loop exception: {ex.Message}");
                Thread.Sleep(10);
                continue;
            }

            // Real-time synchronization:
            // Calculate how many total frames should have been sent by the current elapsed wall-clock time.
            // If capture took 66ms, we write 2 frames so 1 second of real time = 30 frames = 1 second of video!
            long currentElapsedMs = ElapsedMs;
            long expectedTotalFrames = (long)Math.Max(1, (currentElapsedMs * _targetFps) / 1000.0);

            if (expectedTotalFrames <= EncodedFrames)
            {
                expectedTotalFrames = EncodedFrames + 1;
            }

            long framesToWrite = expectedTotalFrames - EncodedFrames;
            // Cap to prevent memory/pipe flooding if OS stalled
            if (framesToWrite > 15) framesToWrite = 15;

            for (int i = 0; i < framesToWrite; i++)
            {
                _encoder.WriteVideoFrame(_lastFrame);
                EncodedFrames++;
                framesInWindow++;

                if (i > 0)
                {
                    DroppedFrames++;
                }
            }

            long now = Stopwatch.GetTimestamp();
            if (now - fpsWindowStart >= Stopwatch.Frequency)
            {
                CurrentActualFps = framesInWindow * (double)Stopwatch.Frequency / (now - fpsWindowStart);
                framesInWindow   = 0;
                fpsWindowStart   = now;
            }

            // Sleep precisely until the next expected frame timestamp
            double nextFrameDueMs = (EncodedFrames + 1) * frameIntervalMs;
            double msUntilNext = nextFrameDueMs - ElapsedMs;
            if (msUntilNext > 2)
            {
                Thread.Sleep((int)(msUntilNext - 1));
            }
            while (ElapsedMs < nextFrameDueMs && _isRunning && !_isPaused)
            {
                Thread.SpinWait(10);
            }
        }
    }

    public void Stop(Action<int, string>? onProgress = null)
    {
        if (_isStopped) return;
        _isStopped = true;

        _isRunning = false;
        _elapsedStopwatch.Stop();

        onProgress?.Invoke(5, "Stopping screen capture loop...");

        // 1. Wait for the video capture thread to exit cleanly before touching encoder or frame counts
        if (_videoThread != null && _videoThread.IsAlive)
        {
            if (!_videoThread.Join(8000))
            {
                _videoThread.Interrupt();
                _videoThread.Join(1000);
            }
        }

        // 2. Ensure that any frames remaining up to the exact moment of Stop() are written
        // so the video duration matches wall-clock elapsed time perfectly without race conditions.
        long finalElapsedMs = ElapsedMs;
        long finalTargetFrames = (long)((finalElapsedMs * _targetFps) / 1000.0);
        if (_lastFrame != null && EncodedFrames < finalTargetFrames)
        {
            long remaining = Math.Min(finalTargetFrames - EncodedFrames, 60);
            for (int r = 0; r < remaining; r++)
            {
                _encoder.WriteVideoFrame(_lastFrame);
                EncodedFrames++;
            }
        }

        onProgress?.Invoke(25, "Flushing audio data to WAV format...");

        // 2. Stop audio capture and flush the WAV file to disk
        _audioManager.Stop();
        string? audioPath = null;
        if (_tempAudioPath != null)
        {
            lock (_audioLock)
            {
                try
                {
                    _audioWriter?.Flush();
                    _audioWriter?.Dispose();
                    _audioWriter = null;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[RecordingSession] Audio flush error: {ex.Message}");
                }
            }
            audioPath = _tempAudioPath;
        }

        // 3. Finalise video encoding and mux audio into the output file.
        // Pass the actual elapsed recording time so the encoder can pad audio
        // silence to the correct video length (eliminates -shortest truncation bug).
        double recDurationSeconds = finalElapsedMs / 1000.0;
        _encoder.Finish(audioPath, onProgress, recDurationSeconds);

        // 4. Delete the temp audio WAV now that it has been muxed
        if (audioPath != null)
        {
            try { if (File.Exists(audioPath)) File.Delete(audioPath); } catch { }
        }

        onProgress?.Invoke(100, "Finished! Video saved successfully.");
    }

    public void Dispose()
    {
        Stop();  // safe to call multiple times via _isStopped guard
        _capturer.Dispose();
        _audioManager.Dispose();
        _encoder.Dispose();

        // Dispose audio writer if Stop() wasn't called
        lock (_audioLock)
        {
            _audioWriter?.Dispose();
            _audioWriter = null;
        }
    }
}
