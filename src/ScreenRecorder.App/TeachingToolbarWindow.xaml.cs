using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ScreenRecorder.App;

public partial class TeachingToolbarWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private readonly OverlayWindow _overlay;
    private static readonly Brush ActiveBtnBg = (Brush)new BrushConverter().ConvertFromString("#4F46E5")!;
    private static readonly Brush ActiveBtnBorder = (Brush)new BrushConverter().ConvertFromString("#818CF8")!;
    private static readonly Brush InactiveBtnBg = (Brush)new BrushConverter().ConvertFromString("#27272A")!;
    private static readonly Brush InactiveBtnBorder = (Brush)new BrushConverter().ConvertFromString("#3F3F46")!;
    private static readonly Brush ActiveTextFg = Brushes.White;
    private static readonly Brush InactiveTextFg = (Brush)new BrushConverter().ConvertFromString("#E4E4E7")!;

    public TeachingToolbarWindow(OverlayWindow overlay)
    {
        InitializeComponent();
        _overlay = overlay;

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Exclude the floating toolbar UI from video capture so only screen + annotations are recorded
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            }
        }
        catch { }

        // Center toolbar horizontally near top of screen
        var primaryWidth = SystemParameters.PrimaryScreenWidth;
        Left = Math.Max(20, (primaryWidth - ActualWidth) / 2);
        Top = 24;

        UpdateToolButtonsVisual(_overlay.CurrentTool);
        UpdateColorButtonsVisual(_overlay.CurrentColor);
    }

    private void OnDragToolbar(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_overlay.CurrentTool != "pointer")
            {
                SelectTool("pointer");
            }
            else
            {
                OnDone(sender, e);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            OnUndo(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.C)
        {
            SelectTool("pointer");
            e.Handled = true;
        }
        else if (e.Key == Key.A)
        {
            SelectTool("arrow");
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            SelectTool("rectangle");
            e.Handled = true;
        }
        else if (e.Key == Key.U)
        {
            SelectTool("underline");
            e.Handled = true;
        }
        else if (e.Key == Key.P)
        {
            SelectTool("pen");
            e.Handled = true;
        }
    }

    public void SelectTool(string tool)
    {
        _overlay.SetTool(tool);
        UpdateToolButtonsVisual(tool);
    }

    public void SelectColor(string color)
    {
        _overlay.SetColor(color);
        UpdateColorButtonsVisual(color);
    }

    public void UpdateToolButtonsVisual(string activeTool)
    {
        SetButtonVisual(BtnPointer, activeTool == "pointer");
        SetButtonVisual(BtnArrow, activeTool == "arrow");
        SetButtonVisual(BtnRect, activeTool == "rectangle");
        SetButtonVisual(BtnUnderline, activeTool == "underline");
        SetButtonVisual(BtnPen, activeTool == "pen");
    }

    private static void SetButtonVisual(Button btn, bool isActive)
    {
        btn.Background = isActive ? ActiveBtnBg : InactiveBtnBg;
        btn.BorderBrush = isActive ? ActiveBtnBorder : InactiveBtnBorder;
        btn.Foreground = isActive ? ActiveTextFg : InactiveTextFg;
    }

    public void UpdateColorButtonsVisual(string activeColor)
    {
        SetColorButtonVisual(BtnColorRed, activeColor.Equals("#EF4444", StringComparison.OrdinalIgnoreCase));
        SetColorButtonVisual(BtnColorYellow, activeColor.Equals("#EAB308", StringComparison.OrdinalIgnoreCase));
        SetColorButtonVisual(BtnColorGreen, activeColor.Equals("#22C55E", StringComparison.OrdinalIgnoreCase));
        SetColorButtonVisual(BtnColorBlue, activeColor.Equals("#3B82F6", StringComparison.OrdinalIgnoreCase));
    }

    private static void SetColorButtonVisual(Button btn, bool isActive)
    {
        btn.BorderBrush = isActive ? Brushes.White : Brushes.Transparent;
        btn.BorderThickness = isActive ? new Thickness(2.5) : new Thickness(1);
    }

    private void OnSelectTool_Pointer(object sender, RoutedEventArgs e) => SelectTool("pointer");
    private void OnSelectTool_Arrow(object sender, RoutedEventArgs e) => SelectTool("arrow");
    private void OnSelectTool_Rect(object sender, RoutedEventArgs e) => SelectTool("rectangle");
    private void OnSelectTool_Underline(object sender, RoutedEventArgs e) => SelectTool("underline");
    private void OnSelectTool_Pen(object sender, RoutedEventArgs e) => SelectTool("pen");

    private void OnSelectColor_Red(object sender, RoutedEventArgs e) => SelectColor("#EF4444");
    private void OnSelectColor_Yellow(object sender, RoutedEventArgs e) => SelectColor("#EAB308");
    private void OnSelectColor_Green(object sender, RoutedEventArgs e) => SelectColor("#22C55E");
    private void OnSelectColor_Blue(object sender, RoutedEventArgs e) => SelectColor("#3B82F6");

    private void OnUndo(object sender, RoutedEventArgs e) => _overlay.Undo();
    private void OnClear(object sender, RoutedEventArgs e) => _overlay.Clear();
    private void OnDone(object sender, RoutedEventArgs e) => _overlay.Hide();
}
