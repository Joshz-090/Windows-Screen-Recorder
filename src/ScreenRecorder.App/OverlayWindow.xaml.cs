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
    private TeachingToolbarWindow? _toolbar;

    private string _currentTool = "arrow"; // arrow, rectangle, underline, pen, pointer
    private string _currentColor = "#EF4444";
    private float _currentStrokeWidth = 5.0f;

    private System.Windows.Point? _startPoint;
    private readonly List<PointD> _activeStrokePoints = new();
    private Polyline? _activePolyline;

    public string CurrentTool => _currentTool;
    public string CurrentColor => _currentColor;

    public OverlayWindow(AnnotationCompositor compositor)
    {
        InitializeComponent();
        _compositor = compositor;

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureToolbarCreated();
        _toolbar?.Show();
        UpdateWindowStyles();
    }

    private void EnsureToolbarCreated()
    {
        if (_toolbar == null)
        {
            _toolbar = new TeachingToolbarWindow(this)
            {
                Owner = this
            };
        }
    }

    public void EnablePassThrough(bool enable)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

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

    public void UpdateWindowStyles()
    {
        // When pointer (cursor) mode is selected, mouse passes through to underlying desktop
        bool isPointer = _currentTool == "pointer";
        EnablePassThrough(isPointer);

        if (isPointer)
        {
            Cursor = Cursors.Arrow;
        }
        else if (_currentTool == "pen")
        {
            Cursor = Cursors.Pen;
        }
        else
        {
            Cursor = Cursors.Cross;
        }
    }

    public void SetTool(string tool)
    {
        _currentTool = tool;
        UpdateWindowStyles();
        _toolbar?.UpdateToolButtonsVisual(tool);
    }

    public void SetColor(string color)
    {
        _currentColor = color;
        _toolbar?.UpdateColorButtonsVisual(color);
    }

    public new void Show()
    {
        base.Show();
        EnsureToolbarCreated();
        _toolbar?.Show();
        UpdateWindowStyles();
    }

    public new void Hide()
    {
        _toolbar?.Hide();
        base.Hide();
    }

    public new void Close()
    {
        _toolbar?.Close();
        base.Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_currentTool != "pointer")
            {
                SetTool("pointer");
            }
            else
            {
                Hide();
            }
            e.Handled = true;
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _currentTool == "pointer") return;

        RootGrid.CaptureMouse();
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
        RootGrid.ReleaseMouseCapture();

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

            // Render single crisp vector annotation onto visual canvas.
            // Screen capturer captures the screen directly via BitBlt, so we do not
            // double-burn it through the GDI+ compositor (which caused duplicate/doubled lines).
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
        var group = new Canvas();

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
        group.Children.Add(line);

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
        group.Children.Add(polygon);

        canvas.Children.Add(group);
    }

    public void Undo()
    {
        _compositor?.Undo();
        if (DrawingCanvas.Children.Count > 0)
        {
            DrawingCanvas.Children.RemoveAt(DrawingCanvas.Children.Count - 1);
        }
    }

    public void Clear()
    {
        _compositor?.Clear();
        DrawingCanvas.Children.Clear();
        PreviewCanvas.Children.Clear();
    }
}
