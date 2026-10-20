namespace ScreenRecorder.Engine.Audio;

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsLoopback { get; set; }
    public bool IsDefault { get; set; }
}
