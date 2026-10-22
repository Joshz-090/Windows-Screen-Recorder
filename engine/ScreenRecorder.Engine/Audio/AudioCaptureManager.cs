using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ScreenRecorder.Engine.Audio;

public class AudioCaptureManager : IDisposable
{
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private MixingSampleProvider? _mixer;
    private readonly object _lock = new();

    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _loopbackBuffer;
    private ISampleProvider? _micSampleProvider;
    private ISampleProvider? _loopbackSampleProvider;

    public event Action<byte[], int>? AudioDataAvailable;
    public float CurrentMicLevel { get; private set; }
    public float CurrentSystemLevel { get; private set; }

    private bool _isRecording;
    private bool _isPaused;
    private long _recordingStartTicks;
    private long _totalPausedTicks;
    private long _lastPauseStartTicks;
    private Thread? _audioPumpingThread;
    private readonly WaveFormat _targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public static List<AudioDeviceInfo> EnumerateMicrophones()
    {
        var list = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        var defaultEndpoint = enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID
            : null;

        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            list.Add(new AudioDeviceInfo
            {
                Id = endpoint.ID,
                Name = endpoint.FriendlyName,
                IsLoopback = false,
                IsDefault = endpoint.ID == defaultEndpoint
            });
        }
        return list;
    }

    public static List<AudioDeviceInfo> EnumerateSystemOutputs()
    {
        var list = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        var defaultEndpoint = enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID
            : null;

        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            list.Add(new AudioDeviceInfo
            {
                Id = endpoint.ID,
                Name = endpoint.FriendlyName,
                IsLoopback = true,
                IsDefault = endpoint.ID == defaultEndpoint
            });
        }
        return list;
    }

    private static ISampleProvider NormalizeToTarget(ISampleProvider source, int targetSampleRate = 48000)
    {
        ISampleProvider current = source;

        // 1. Ensure 2 channels (stereo)
        if (current.WaveFormat.Channels == 1)
        {
            current = new MonoToStereoSampleProvider(current);
        }
        else if (current.WaveFormat.Channels > 2)
        {
            current = new ChannelsToStereoSampleProvider(current);
        }

        // 2. Ensure target sample rate (48000 Hz)
        if (current.WaveFormat.SampleRate != targetSampleRate)
        {
            current = new WdlResamplingSampleProvider(current, targetSampleRate);
        }

        return current;
    }

    public void Start(string? micDeviceId, bool captureSystemAudio)
    {
        lock (_lock)
        {
            if (_isRecording) return;
            _recordingStartTicks = Stopwatch.GetTimestamp();
            _totalPausedTicks = 0;
            _lastPauseStartTicks = 0;
            _isPaused = false;
            _isRecording = true;

            using var enumerator = new MMDeviceEnumerator();

            // 1. Setup Microphone if requested
            if (!string.IsNullOrEmpty(micDeviceId) && micDeviceId != "none")
            {
                try
                {
                    MMDevice? micDevice = null;
                    if (micDeviceId == "default")
                    {
                        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
                            micDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    }
                    else
                    {
                        micDevice = enumerator.GetDevice(micDeviceId);
                    }

                    if (micDevice != null)
                    {
                        _micCapture = new WasapiCapture(micDevice);
                        _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
                        {
                            DiscardOnBufferOverflow = true,
                            ReadFully = true,
                            BufferDuration = TimeSpan.FromSeconds(5)
                        };

                        var micSamples = _micBuffer.ToSampleProvider();
                        _micSampleProvider = NormalizeToTarget(micSamples, 48000);

                        _micCapture.DataAvailable += (s, e) =>
                        {
                            if (!Volatile.Read(ref _isPaused))
                            {
                                _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                            }
                            CalculateLevel(e.Buffer, e.BytesRecorded, _micCapture.WaveFormat, true);
                        };
                        _micCapture.StartRecording();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Audio] Failed to initialize microphone: {ex.Message}");
                }
            }

            // 2. Setup System Loopback Audio if requested
            if (captureSystemAudio)
            {
                try
                {
                    if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    {
                        var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        _loopbackCapture = new WasapiLoopbackCapture(renderDevice);
                        _loopbackBuffer = new BufferedWaveProvider(_loopbackCapture.WaveFormat)
                        {
                            DiscardOnBufferOverflow = true,
                            ReadFully = true,
                            BufferDuration = TimeSpan.FromSeconds(5)
                        };

                        var loopbackSamples = _loopbackBuffer.ToSampleProvider();
                        _loopbackSampleProvider = NormalizeToTarget(loopbackSamples, 48000);

                        _loopbackCapture.DataAvailable += (s, e) =>
                        {
                            if (!Volatile.Read(ref _isPaused))
                            {
                                _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                            }
                            CalculateLevel(e.Buffer, e.BytesRecorded, _loopbackCapture.WaveFormat, false);
                        };
                        _loopbackCapture.StartRecording();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Audio] Failed to initialize loopback audio: {ex.Message}");
                }
            }

            // 3. Create real-time mixer
            _mixer = new MixingSampleProvider(_targetFormat)
            {
                ReadFully = true
            };

            if (_micSampleProvider != null)
                _mixer.AddMixerInput(_micSampleProvider);
            if (_loopbackSampleProvider != null)
                _mixer.AddMixerInput(_loopbackSampleProvider);

            // 4. Start audio output pump thread
            _audioPumpingThread = new Thread(PumpAudioLoop)
            {
                IsBackground = true,
                Name = "AudioMixerPumpThread",
                Priority = ThreadPriority.AboveNormal
            };
            _audioPumpingThread.Start();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_isRecording || _isPaused) return;
            Volatile.Write(ref _lastPauseStartTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _isPaused, true);
            _micBuffer?.ClearBuffer();
            _loopbackBuffer?.ClearBuffer();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (!_isRecording || !_isPaused) return;
            long pauseDurationTicks = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastPauseStartTicks);
            Interlocked.Add(ref _totalPausedTicks, pauseDurationTicks);
            _micBuffer?.ClearBuffer();
            _loopbackBuffer?.ClearBuffer();
            Volatile.Write(ref _isPaused, false);
        }
    }

    private void CalculateLevel(byte[] buffer, int bytesRecorded, WaveFormat format, bool isMic)
    {
        if (bytesRecorded == 0) return;
        float max = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            for (int i = 0; i < bytesRecorded; i += 4)
            {
                float sample = Math.Abs(BitConverter.ToSingle(buffer, i));
                if (sample > max) max = sample;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i < bytesRecorded; i += 2)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                float val = Math.Abs(sample / 32768.0f);
                if (val > max) max = val;
            }
        }

        if (isMic)
            CurrentMicLevel = max;
        else
            CurrentSystemLevel = max;
    }

    private void PumpAudioLoop()
    {
        // 48000 Hz, stereo (2 channels), 4 bytes per float sample = 384,000 bytes/sec
        // Maximum burst chunk size: 4800 frames = 100ms
        float[] sampleBuffer = new float[4800 * 2];
        byte[] byteBuffer = new byte[4800 * 2 * 4];
        long totalFramesWritten = 0;

        while (_isRecording)
        {
            if (Volatile.Read(ref _isPaused))
            {
                Thread.Sleep(10);
                continue;
            }

            long now = Stopwatch.GetTimestamp();
            long pausedTicks = Volatile.Read(ref _totalPausedTicks);
            long elapsedTicks = now - _recordingStartTicks - pausedTicks;
            if (elapsedTicks < 0) elapsedTicks = 0;

            double elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
            long targetTotalFrames = (long)(elapsedSeconds * 48000.0);
            long framesDue = targetTotalFrames - totalFramesWritten;

            if (framesDue <= 0)
            {
                Thread.Sleep(5);
                continue;
            }

            int framesToRead = (int)Math.Min(framesDue, 4800);
            int samplesToRead = framesToRead * 2;

            if (sampleBuffer.Length < samplesToRead)
            {
                sampleBuffer = new float[samplesToRead];
                byteBuffer = new byte[samplesToRead * 4];
            }

            int samplesRead = _mixer?.Read(sampleBuffer, 0, samplesToRead) ?? 0;
            if (samplesRead > 0)
            {
                Buffer.BlockCopy(sampleBuffer, 0, byteBuffer, 0, samplesRead * 4);
                AudioDataAvailable?.Invoke(byteBuffer, samplesRead * 4);
                totalFramesWritten += (samplesRead / 2);
            }
            else
            {
                Thread.Sleep(5);
            }
        }

        // On stop: drain remaining frames up to the final elapsed recording time
        // to guarantee audio duration matches wall-clock recording duration exactly.
        long finalNow = Stopwatch.GetTimestamp();
        long finalPaused = Volatile.Read(ref _totalPausedTicks);
        long finalTicks = finalNow - _recordingStartTicks - finalPaused;
        if (finalTicks > 0)
        {
            double finalSec = (double)finalTicks / Stopwatch.Frequency;
            long finalTargetFrames = (long)(finalSec * 48000.0);
            long remaining = finalTargetFrames - totalFramesWritten;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, 4800);
                int samplesToRead = toRead * 2;
                if (sampleBuffer.Length < samplesToRead)
                {
                    sampleBuffer = new float[samplesToRead];
                    byteBuffer = new byte[samplesToRead * 4];
                }
                int samplesRead = _mixer?.Read(sampleBuffer, 0, samplesToRead) ?? 0;
                if (samplesRead <= 0) break;
                Buffer.BlockCopy(sampleBuffer, 0, byteBuffer, 0, samplesRead * 4);
                AudioDataAvailable?.Invoke(byteBuffer, samplesRead * 4);
                totalFramesWritten += (samplesRead / 2);
                remaining -= (samplesRead / 2);
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRecording) return;
            _isRecording = false;

            try { _micCapture?.StopRecording(); } catch { }
            try { _loopbackCapture?.StopRecording(); } catch { }

            _audioPumpingThread?.Join(3000);

            _micCapture?.Dispose();
            _micCapture = null;
            _loopbackCapture?.Dispose();
            _loopbackCapture = null;
            _mixer = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// Helper sample provider that extracts or downmixes any multichannel stream (>2 channels)
/// to standard stereo (2 channels).
/// </summary>
internal sealed class ChannelsToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public ChannelsToStereoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesRequested = count / 2;
        int sourceSamplesNeeded = framesRequested * _sourceChannels;
        if (_sourceBuffer.Length < sourceSamplesNeeded)
            _sourceBuffer = new float[sourceSamplesNeeded];

        int sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = sourceSamplesRead / _sourceChannels;

        int outIdx = offset;
        for (int i = 0; i < framesRead; i++)
        {
            int srcIdx = i * _sourceChannels;
            buffer[outIdx++] = _sourceBuffer[srcIdx];
            buffer[outIdx++] = _sourceChannels > 1 ? _sourceBuffer[srcIdx + 1] : _sourceBuffer[srcIdx];
        }
        return framesRead * 2;
    }
}
