using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using ScreenRecorder.Engine.Annotations;
using ScreenRecorder.Engine.Capture;

namespace ScreenRecorder.App;

public partial class OverlayWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private readonly AnnotationCompositor _compositor;
    private string _currentTool = "arrow"; // arrow, rectangle, underline, pen, pointer
    private string _currentColor = "#EF4444";
    private float _currentStrokeWidth = 5.0f;

    private System.Windows.Point? _startPoint;
    private readonly List<PointD> _activeStrokePoints = new();
    private Polyline? _activePolyline;

    public OverlayWindow(AnnotationCompositor compositor)
    {
        InitializeComponent();
        _compositor = compositor;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateWindowStyles();
    }

    public void EnablePassThrough(bool enable)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (enable)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
        }
        else
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
        }
    }

    private void UpdateWindowStyles()
    {
        // When pointer mode is selected, mouse passes through to underlying desktop
        EnablePassThrough(_currentTool == "pointer");
    }

    private void SetTool_Pointer(object sender, RoutedEventArgs e) { _currentTool = "pointer"; UpdateWindowStyles(); }
    private void SetTool_Arrow(object sender, RoutedEventArgs e) { _currentTool = "arrow"; UpdateWindowStyles(); }
    private void SetTool_Rect(object sender, RoutedEventArgs e) { _currentTool = "rectangle"; UpdateWindowStyles(); }
    private void SetTool_Underline(object sender, RoutedEventArgs e) { _currentTool = "underline"; UpdateWindowStyles(); }
    private void SetTool_Pen(object sender, RoutedEventArgs e) { _currentTool = "pen"; UpdateWindowStyles(); }

    private void SetColor_Red(object sender, RoutedEventArgs e) => _currentColor = "#EF4444";
    private void SetColor_Yellow(object sender, RoutedEventArgs e) => _currentColor = "#EAB308";
    private void SetColor_Green(object sender, RoutedEventArgs e) => _currentColor = "#22C55E";
    private void SetColor_Blue(object sender, RoutedEventArgs e) => _currentColor = "#3B82F6";

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _currentTool == "pointer") return;

        var pos = e.GetPosition(this);
        _startPoint = pos;
        _activeStrokePoints.Clear();
        _activeStrokePoints.Add(new PointD { X = pos.X, Y = pos.Y });

        if (_currentTool == "pen")
        {
            _activePolyline = new Polyline
            {
                Stroke = (Brush)new BrushConverter().ConvertFromString(_currentColor)!,
                StrokeThickness = _currentStrokeWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            _activePolyline.Points.Add(pos);
            PreviewCanvas.Children.Add(_activePolyline);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_startPoint == null) return;
        var cur = e.GetPosition(this);

        if (_currentTool == "pen" && _activePolyline != null)
        {
            _activeStrokePoints.Add(new PointD { X = cur.X, Y = cur.Y });
            _activePolyline.Points.Add(cur);
            return;
        }

        PreviewCanvas.Children.Clear();
        var brush = (Brush)new BrushConverter().ConvertFromString(_currentColor)!;

        if (_currentTool == "arrow")
        {
            DrawArrowVisual(PreviewCanvas, _startPoint.Value, cur, brush, _currentStrokeWidth);
        }
        else if (_currentTool == "rectangle")
        {
            double x = Math.Min(_startPoint.Value.X, cur.X);
            double y = Math.Min(_startPoint.Value.Y, cur.Y);
            double w = Math.Abs(cur.X - _startPoint.Value.X);
            double h = Math.Abs(cur.Y - _startPoint.Value.Y);

            var rect = new Rectangle
            {
                Stroke = brush,
                StrokeThickness = _currentStrokeWidth,
                Width = w,
                Height = h
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            PreviewCanvas.Children.Add(rect);
        }
        else if (_currentTool == "underline")
        {
            var line = new Line
            {
                X1 = _startPoint.Value.X,
                Y1 = _startPoint.Value.Y,
                X2 = cur.X,
                Y2 = cur.Y,
                Stroke = brush,
                StrokeThickness = _currentStrokeWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            PreviewCanvas.Children.Add(line);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_startPoint == null) return;
        var endPos = e.GetPosition(this);
        PreviewCanvas.Children.Clear();

        var points = new List<PointD>();
        if (_currentTool == "pen")
        {
            points.AddRange(_activeStrokePoints);
        }
        else
        {
            points.Add(new PointD { X = _startPoint.Value.X, Y = _startPoint.Value.Y });
            points.Add(new PointD { X = endPos.X, Y = endPos.Y });
        }

        if (points.Count >= 2)
        {
            var ann = new AnnotationItem
            {
                Tool = _currentTool,
                Color = _currentColor,
                StrokeWidth = _currentStrokeWidth,
                Points = points
            };

            // 1. Send to engine for recording video burn-in compositing
            _compositor.AddAnnotation(ann);

            // 2. Render onto visual canvas
            RenderAnnotationToCanvas(ann, DrawingCanvas);
        }

        _startPoint = null;
        _activePolyline = null;
    }

    private void RenderAnnotationToCanvas(AnnotationItem ann, Canvas canvas)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(ann.Color)!;
        if (ann.Tool == "arrow")
        {
            var p1 = new System.Windows.Point(ann.Points[0].X, ann.Points[0].Y);
            var p2 = new System.Windows.Point(ann.Points[1].X, ann.Points[1].Y);
            DrawArrowVisual(canvas, p1, p2, brush, ann.StrokeWidth);
        }
        else if (ann.Tool == "rectangle")
        {
            double x = Math.Min(ann.Points[0].X, ann.Points[1].X);
            double y = Math.Min(ann.Points[0].Y, ann.Points[1].Y);
            double w = Math.Abs(ann.Points[1].X - ann.Points[0].X);
            double h = Math.Abs(ann.Points[1].Y - ann.Points[0].Y);

            var rect = new Rectangle
            {
                Stroke = brush,
                StrokeThickness = ann.StrokeWidth,
                Width = w,
                Height = h
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            canvas.Children.Add(rect);
        }
        else if (ann.Tool == "underline")
        {
            var line = new Line
            {
                X1 = ann.Points[0].X,
                Y1 = ann.Points[0].Y,
                X2 = ann.Points[1].X,
                Y2 = ann.Points[1].Y,
                Stroke = brush,
                StrokeThickness = ann.StrokeWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            canvas.Children.Add(line);
        }
        else if (ann.Tool == "pen")
        {
            var pl = new Polyline
            {
                Stroke = brush,
                StrokeThickness = ann.StrokeWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            foreach (var pt in ann.Points)
            {
                pl.Points.Add(new System.Windows.Point(pt.X, pt.Y));
            }
            canvas.Children.Add(pl);
        }
    }

    private void DrawArrowVisual(Canvas canvas, System.Windows.Point p1, System.Windows.Point p2, Brush brush, float strokeWidth)
    {
        var line = new Line
        {
            X1 = p1.X,
            Y1 = p1.Y,
            X2 = p2.X,
            Y2 = p2.Y,
            Stroke = brush,
            StrokeThickness = strokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        canvas.Children.Add(line);

        double angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
        double arrowSize = Math.Max(16.0, strokeWidth * 3.5);
        double angleOffset = Math.PI / 6.0;

        var head1 = new System.Windows.Point(
            p2.X - arrowSize * Math.Cos(angle - angleOffset),
            p2.Y - arrowSize * Math.Sin(angle - angleOffset)
        );
        var head2 = new System.Windows.Point(
            p2.X - arrowSize * Math.Cos(angle + angleOffset),
            p2.Y - arrowSize * Math.Sin(angle + angleOffset)
        );

        var polygon = new Polygon
        {
            Fill = brush,
            Points = new PointCollection { p2, head1, head2 }
        };
        canvas.Children.Add(polygon);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        _compositor.Undo();
        if (DrawingCanvas.Children.Count > 0)
        {
            DrawingCanvas.Children.RemoveAt(DrawingCanvas.Children.Count - 1);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _compositor.Clear();
        DrawingCanvas.Children.Clear();
        PreviewCanvas.Children.Clear();
    }

    private void OnCloseOverlay(object sender, RoutedEventArgs e)
    {
        Hide();
    }
}
