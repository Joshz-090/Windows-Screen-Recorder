using System.Diagnostics;

namespace ScreenRecorder.Engine.Encoder;

/// <summary>
/// Encodes raw video frames to a temp MP4 (video-only), then in Finish() muxes
/// an optional audio WAV file into the final output. This two-pass approach is
/// far more reliable than trying to feed audio via a named pipe in real-time.
/// </summary>
public class FFmpegProcessEncoder : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly string _finalOutputPath;
    private readonly string _tempVideoPath;   // intermediate video-only file
    private readonly bool _willMuxAudio;

    private Process? _ffmpegProcess;
    private Stream? _videoInputStream;
    private readonly object _lock = new();
    private bool _isDisposed;

    public string OutputPath => _finalOutputPath;
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public string EncoderName { get; }

    public FFmpegProcessEncoder(
        string ffmpegPath,
        string outputPath,
        int width,
        int height,
        int fps,
        bool hasAudio,
        string encoderName = "h264_qsv")
    {
        _ffmpegPath       = ffmpegPath;
        _finalOutputPath  = outputPath;
        _willMuxAudio     = hasAudio;
        Width             = width;
        Height            = height;
        Fps               = fps;
        EncoderName       = encoderName;

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // When audio is requested we encode video to a temp file and mux later.
        // Without audio we encode directly to the final output path.
        _tempVideoPath = hasAudio
            ? Path.Combine(
                outputDir ?? ".",
                $"_vtmp_{Path.GetFileNameWithoutExtension(outputPath)}_{Guid.NewGuid():N}.mp4")
            : outputPath;

        string videoEncoderArgs = encoderName == "h264_qsv"
            ? "-c:v h264_qsv -b:v 5000k -maxrate 8000k -bufsize 8000k -preset veryfast -look_ahead 0"
            : "-c:v libx264 -preset ultrafast -tune zerolatency -crf 23 -pix_fmt yuv420p";

        // Video-only pass — no audio input at all.
        // -fps_mode cfr:        ensure constant frame rate so timestamps are predictable.
        // -movflags +faststart: write moov atom at file start so temp file is seekable.
        string fullArgs =
            $"-y -f rawvideo -pix_fmt bgr0 -s {width}x{height} -r {fps} -i - " +
            $"-map 0:v {videoEncoderArgs} -fps_mode cfr -movflags +faststart \"{_tempVideoPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName             = ffmpegPath,
            Arguments            = fullArgs,
            UseShellExecute      = false,
            CreateNoWindow       = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        try
        {
            _ffmpegProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start FFmpeg process.");

            _videoInputStream = new BufferedStream(_ffmpegProcess.StandardInput.BaseStream, 512 * 1024);

            _ffmpegProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    lock (_stderrBuffer)
                    {
                        if (_stderrBuffer.Length > 8000) _stderrBuffer.Clear();
                        _stderrBuffer.AppendLine(e.Data);
                    }
                    Console.Error.WriteLine($"[FFmpeg] {e.Data}");
                }
            };
            _ffmpegProcess.BeginErrorReadLine();

            // Give FFmpeg 50ms to detect immediate startup errors (e.g. bad arguments or missing DLLs)
            Thread.Sleep(50);
            if (_ffmpegProcess.HasExited)
            {
                string err = GetStderrSnippet();
                throw new InvalidOperationException($"FFmpeg exited immediately with code {_ffmpegProcess.ExitCode}: {err}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Encoder] Failed to start FFmpeg: {ex.Message}");
            throw;
        }
    }

    private readonly System.Text.StringBuilder _stderrBuffer = new();

    public string GetStderrSnippet()
    {
        lock (_stderrBuffer)
        {
            return _stderrBuffer.ToString();
        }
    }

    public void WriteVideoFrame(byte[] frameData)
    {
        lock (_lock)
        {
            if (_isDisposed || _videoInputStream == null) return;
            try
            {
                _videoInputStream.Write(frameData, 0, frameData.Length);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Encoder] Error writing video frame: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Finishes video encoding. If <paramref name="audioFilePath"/> is provided and exists,
    /// runs a fast mux pass (stream-copy video + encode audio → AAC) to produce the
    /// final output file. <paramref name="videoDurationSeconds"/> is used to pad or trim
    /// audio so its duration exactly matches the video — this eliminates the -shortest
    /// truncation bug that caused output videos to be half their expected duration.
    /// </summary>
    public void Finish(string? audioFilePath = null, Action<int, string>? onProgress = null, double videoDurationSeconds = 0)
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            onProgress?.Invoke(15, "Finalizing captured video frames...");

            // 1. Signal EOF to FFmpeg — it will now flush and write the moov atom.
            try { _videoInputStream?.Flush(); } catch { }
            try { _videoInputStream?.Close(); } catch { }

            // 2. Wait for FFmpeg to cleanly exit.
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                _ffmpegProcess.WaitForExit(30_000);
                if (!_ffmpegProcess.HasExited)
                {
                    Console.Error.WriteLine("[Encoder] FFmpeg did not exit in time — killing.");
                    _ffmpegProcess.Kill();
                }
            }
        }

        onProgress?.Invoke(40, "Checking video stream integrity...");

        // Probe actual video duration from the temp file when caller did not supply it.
        // This is the authoritative duration for the mux pass.
        if (videoDurationSeconds <= 0 && File.Exists(_tempVideoPath))
        {
            videoDurationSeconds = ProbeVideoDuration(_tempVideoPath);
        }
        Console.Error.WriteLine($"[Encoder] Video duration for mux: {videoDurationSeconds:F3}s");

        // 3. Mux audio into the final file (or just rename if no audio).
        if (_willMuxAudio)
        {
            bool hasAudioFile = !string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath);

            if (hasAudioFile && File.Exists(_tempVideoPath))
            {
                try
                {
                    onProgress?.Invoke(60, "Merging audio and video tracks (WAV → MP4)...");
                    RunMuxPass(audioFilePath!, onProgress, videoDurationSeconds);
                    // Clean up temp video only after successful mux
                    TryDelete(_tempVideoPath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Encoder] Mux failed ({ex.Message}). Saving video without audio.");
                    // Fall back: move temp video-only file to final destination
                    TryMoveFile(_tempVideoPath, _finalOutputPath);
                }
            }
            else
            {
                // No audio captured — just promote the video-only temp file
                onProgress?.Invoke(70, "Finalizing video file...");
                TryMoveFile(_tempVideoPath, _finalOutputPath);
            }
        }
        else
        {
            if (File.Exists(_tempVideoPath) && _tempVideoPath != _finalOutputPath)
            {
                TryMoveFile(_tempVideoPath, _finalOutputPath);
            }
        }

        onProgress?.Invoke(95, "Verifying output file...");

        if (!File.Exists(_finalOutputPath) || new FileInfo(_finalOutputPath).Length == 0)
        {
            string err = GetStderrSnippet();
            throw new FileNotFoundException($"Output video file was not created or is empty. FFmpeg output: {err}", _finalOutputPath);
        }

        onProgress?.Invoke(100, "Finished! Video saved.");
    }

    /// <summary>
    /// Probes the duration of an encoded MP4 using ffprobe or ffmpeg -i stderr parsing.
    /// Returns 0 on failure.
    /// </summary>
    private double ProbeVideoDuration(string videoPath)
    {
        try
        {
            string ffprobePath = _ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe", StringComparison.OrdinalIgnoreCase);
            bool hasProbe = File.Exists(ffprobePath);

            var psi = new ProcessStartInfo
            {
                FileName               = hasProbe ? ffprobePath : _ffmpegPath,
                Arguments              = hasProbe
                    ? $"-v quiet -print_format json -show_entries format=duration -i \"{videoPath}\""
                    : $"-i \"{videoPath}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return 0;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            // Parse ffprobe JSON: "duration": "6.400000"
            if (stdout.Contains("\"duration\""))
            {
                int idx   = stdout.IndexOf("\"duration\"", StringComparison.Ordinal);
                int colon = stdout.IndexOf(':', idx);
                int start = stdout.IndexOfAny("0123456789".ToCharArray(), colon);
                int end   = stdout.IndexOfAny(",}\n\r\"".ToCharArray(), start);
                if (start >= 0 && end > start)
                {
                    string durStr = stdout[start..end].Trim();
                    if (double.TryParse(durStr, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double d))
                        return d;
                }
            }

            // Parse ffmpeg -i stderr: "  Duration: 00:00:06.40, start: ..."
            foreach (var line in stderr.Split('\n'))
            {
                int di = line.IndexOf("Duration:", StringComparison.OrdinalIgnoreCase);
                if (di >= 0)
                {
                    string durPart = line[(di + 9)..].Trim().Split(',')[0].Trim();
                    if (TimeSpan.TryParse(durPart, out TimeSpan ts))
                        return ts.TotalSeconds;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Encoder] Duration probe failed: {ex.Message}");
        }
        return 0;
    }

    private void RunMuxPass(string audioFilePath, Action<int, string>? onProgress, double videoDurationSeconds)
    {
        // *** PRIMARY FIX: Do NOT use -shortest. ***
        //
        // Old code used -shortest, which truncated the output to min(video_length, audio_length).
        // When audio WAV was 3.64s and video was 6.40s, output was only 3.64s.
        // This caused every recording to appear ~half its actual duration.
        //
        // Fix: use -af apad to pad audio silence to match video length,
        //      plus -t <videoDurationSeconds> to trim any audio overflow.
        //      No -shortest anywhere.

        string durationArg = videoDurationSeconds > 0
            ? $"-t {videoDurationSeconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}"
            : "";

        string muxArgs =
            $"-y -i \"{_tempVideoPath}\" -i \"{audioFilePath}\" " +
            $"-c:v copy -c:a aac -b:a 192k -af apad {durationArg} -movflags +faststart \"{_finalOutputPath}\"";

        Console.Error.WriteLine("[Encoder] Starting audio mux pass...");

        var muxInfo = new ProcessStartInfo
        {
            FileName             = _ffmpegPath,
            Arguments            = muxArgs,
            UseShellExecute      = false,
            CreateNoWindow       = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        using var mux = Process.Start(muxInfo)
            ?? throw new InvalidOperationException("Failed to start FFmpeg mux process.");

        var muxStderr = new System.Text.StringBuilder();
        mux.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (muxStderr) { muxStderr.AppendLine(e.Data); }
                Console.Error.WriteLine($"[FFmpeg Mux] {e.Data}");
            }
        };
        mux.BeginErrorReadLine();

        onProgress?.Invoke(75, "Converting WAV audio to AAC & muxing into MP4 container...");

        mux.WaitForExit(60_000);
        if (!mux.HasExited)
        {
            mux.Kill();
            throw new TimeoutException("FFmpeg mux pass timed out.");
        }

        if (mux.ExitCode != 0)
        {
            string muxErr;
            lock (muxStderr) { muxErr = muxStderr.ToString(); }
            throw new Exception($"FFmpeg mux exited with code {mux.ExitCode}: {muxErr}");
        }

        Console.Error.WriteLine("[Encoder] Mux pass complete.");
    }

    private static void TryMoveFile(string src, string dst)
    {
        try
        {
            if (File.Exists(src))
                File.Move(src, dst, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Encoder] Could not move {src} → {dst}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        Finish();
        _ffmpegProcess?.Dispose();
    }
}
