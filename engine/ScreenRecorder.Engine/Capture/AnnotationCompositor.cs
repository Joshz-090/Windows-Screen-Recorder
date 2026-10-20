using System.Drawing;
using System.Drawing.Drawing2D;
using ScreenRecorder.Engine.Annotations;

namespace ScreenRecorder.Engine.Capture;

public class AnnotationCompositor
{
    private readonly List<AnnotationItem> _annotations = new();
    private readonly object _lock = new();

    public bool HasAnnotations
    {
        get
        {
            lock (_lock) return _annotations.Count > 0;
        }
    }

    public void AddAnnotation(AnnotationItem item)
    {
        lock (_lock)
        {
            _annotations.Add(item);
        }
    }

    public void Undo()
    {
        lock (_lock)
        {
            if (_annotations.Count > 0)
            {
                _annotations.RemoveAt(_annotations.Count - 1);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _annotations.Clear();
        }
    }

    public void Composite(Graphics g, int captureX, int captureY, float scaleX = 1.0f, float scaleY = 1.0f)
    {
        lock (_lock)
        {
            if (_annotations.Count == 0) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;

            foreach (var ann in _annotations)
            {
                if (ann.Points.Count < 2 && ann.Tool != "point") continue;

                var color = ColorTranslator.FromHtml(ann.Color);
                using var pen = new Pen(color, ann.StrokeWidth * scaleX)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };

                if (ann.Tool == "arrow")
                {
                    DrawArrow(g, pen, ann, captureX, captureY, scaleX, scaleY);
                }
                else if (ann.Tool == "rectangle")
                {
                    DrawRectangle(g, pen, ann, captureX, captureY, scaleX, scaleY);
                }
                else if (ann.Tool == "underline")
                {
                    DrawUnderline(g, pen, ann, captureX, captureY, scaleX, scaleY);
                }
                else if (ann.Tool == "pen" && ann.Points.Count >= 2)
                {
                    DrawFreehand(g, pen, ann, captureX, captureY, scaleX, scaleY);
                }
            }
        }
    }

    private void DrawArrow(Graphics g, Pen pen, AnnotationItem ann, int offsetX, int offsetY, float sx, float sy)
    {
        var p1 = ann.Points[0];
        var p2 = ann.Points[ann.Points.Count - 1];

        float x1 = (float)(p1.X - offsetX) * sx;
        float y1 = (float)(p1.Y - offsetY) * sy;
        float x2 = (float)(p2.X - offsetX) * sx;
        float y2 = (float)(p2.Y - offsetY) * sy;

        // Draw shaft
        g.DrawLine(pen, x1, y1, x2, y2);

        // Draw arrowhead
        double angle = Math.Atan2(y2 - y1, x2 - x1);
        float arrowSize = Math.Max(16.0f, ann.StrokeWidth * 3.5f) * sx;
        float angleOffset = (float)(Math.PI / 6.0); // 30 degrees

        PointF head1 = new(
            x2 - arrowSize * (float)Math.Cos(angle - angleOffset),
            y2 - arrowSize * (float)Math.Sin(angle - angleOffset)
        );
        PointF head2 = new(
            x2 - arrowSize * (float)Math.Cos(angle + angleOffset),
            y2 - arrowSize * (float)Math.Sin(angle + angleOffset)
        );

        using var brush = new SolidBrush(pen.Color);
        g.FillPolygon(brush, new[] { new PointF(x2, y2), head1, head2 });
    }

    private void DrawRectangle(Graphics g, Pen pen, AnnotationItem ann, int offsetX, int offsetY, float sx, float sy)
    {
        var p1 = ann.Points[0];
        var p2 = ann.Points[ann.Points.Count - 1];

        float x1 = (float)(p1.X - offsetX) * sx;
        float y1 = (float)(p1.Y - offsetY) * sy;
        float x2 = (float)(p2.X - offsetX) * sx;
        float y2 = (float)(p2.Y - offsetY) * sy;

        float rx = Math.Min(x1, x2);
        float ry = Math.Min(y1, y2);
        float rw = Math.Abs(x2 - x1);
        float rh = Math.Abs(y2 - y1);

        g.DrawRectangle(pen, rx, ry, rw, rh);
    }

    private void DrawUnderline(Graphics g, Pen pen, AnnotationItem ann, int offsetX, int offsetY, float sx, float sy)
    {
        var p1 = ann.Points[0];
        var p2 = ann.Points[ann.Points.Count - 1];

        float x1 = (float)(p1.X - offsetX) * sx;
        float y1 = (float)(p1.Y - offsetY) * sy;
        float x2 = (float)(p2.X - offsetX) * sx;
        float y2 = (float)(p2.Y - offsetY) * sy;

        g.DrawLine(pen, x1, y1, x2, y2);
    }

    private void DrawFreehand(Graphics g, Pen pen, AnnotationItem ann, int offsetX, int offsetY, float sx, float sy)
    {
        var pts = ann.Points.Select(p => new PointF((float)(p.X - offsetX) * sx, (float)(p.Y - offsetY) * sy)).ToArray();
        if (pts.Length >= 2)
        {
            g.DrawLines(pen, pts);
        }
    }
}
