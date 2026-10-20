namespace ScreenRecorder.App.Models;

public class RecordingItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string DurationFormatted { get; set; } = "00:00";
    public string Resolution { get; set; } = "1920x1080";
    public string FileSizeFormatted { get; set; } = "0 MB";
}
