namespace ScreenRecorder.Engine.Annotations;

public class PointD
{
    public double X { get; set; }
    public double Y { get; set; }
}

public class AnnotationItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Tool { get; set; } = "arrow"; // arrow, rectangle, underline, pen
    public string Color { get; set; } = "#FF0000";
    public float StrokeWidth { get; set; } = 4.0f;
    public List<PointD> Points { get; set; } = new();
    public long TimestampMs { get; set; }
}
