namespace ScreenRecorder.Engine.Capture;

public class ScreenSourceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }
    public string Type { get; set; } = "display"; // "display" or "window"
}
