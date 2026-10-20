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

    public void Start(string? micDeviceId, bool captureSystemAudio)
    {
        lock (_lock)
        {
            if (_isRecording) return;
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
                            BufferDuration = TimeSpan.FromSeconds(2)
                        };

                        var micSamples = _micBuffer.ToSampleProvider();
                        if (micSamples.WaveFormat.SampleRate != 48000 || micSamples.WaveFormat.Channels != 2)
                        {
                            _micSampleProvider = new WdlResamplingSampleProvider(
                                micSamples.WaveFormat.Channels == 1 ? (ISampleProvider)new MonoToStereoSampleProvider(micSamples) : micSamples,
                                48000
                            );
                        }
                        else
                        {
                            _micSampleProvider = micSamples;
                        }

                        _micCapture.DataAvailable += (s, e) =>
                        {
                            _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
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
                            BufferDuration = TimeSpan.FromSeconds(2)
                        };

                        var loopbackSamples = _loopbackBuffer.ToSampleProvider();
                        if (loopbackSamples.WaveFormat.SampleRate != 48000 || loopbackSamples.WaveFormat.Channels != 2)
                        {
                            _loopbackSampleProvider = new WdlResamplingSampleProvider(
                                loopbackSamples.WaveFormat.Channels == 1 ? (ISampleProvider)new MonoToStereoSampleProvider(loopbackSamples) : loopbackSamples,
                                48000
                            );
                        }
                        else
                        {
                            _loopbackSampleProvider = loopbackSamples;
                        }

                        _loopbackCapture.DataAvailable += (s, e) =>
                        {
                            _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
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
        // Emit 20ms chunks: 48000 * 0.02 = 960 samples * 2 channels = 1920 floats = 7680 bytes
        const int samplesPerChunk = 960 * 2;
        float[] sampleBuffer = new float[samplesPerChunk];
        byte[] byteBuffer = new byte[samplesPerChunk * 4];

        while (_isRecording)
        {
            long startTicks = DateTime.UtcNow.Ticks;

            int read = _mixer?.Read(sampleBuffer, 0, samplesPerChunk) ?? 0;
            if (read > 0)
            {
                Buffer.BlockCopy(sampleBuffer, 0, byteBuffer, 0, read * 4);
                AudioDataAvailable?.Invoke(byteBuffer, read * 4);
            }

            // Sleep to pace ~20ms
            long elapsedMs = (DateTime.UtcNow.Ticks - startTicks) / TimeSpan.TicksPerMillisecond;
            int sleepTime = (int)(20 - elapsedMs);
            if (sleepTime > 0)
            {
                Thread.Sleep(sleepTime);
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

            _audioPumpingThread?.Join(500);

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
