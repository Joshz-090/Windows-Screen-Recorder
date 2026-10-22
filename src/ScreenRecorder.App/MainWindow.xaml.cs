using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenRecorder.App.Models;
using ScreenRecorder.Engine.Audio;
using ScreenRecorder.Engine.Capture;

namespace ScreenRecorder.App;

public partial class MainWindow : Window
{
    private RecordingSession? _currentSession;
    private readonly AnnotationCompositor _compositor = new();
    private OverlayWindow? _overlayWindow;
    private readonly DispatcherTimer _uiTimer = new();
    private List<ScreenSourceInfo> _sources = new();
    private List<AudioDeviceInfo> _microphones = new();
    private string _videosFolder = "";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;

        _uiTimer.Interval = TimeSpan.FromMilliseconds(200);
        _uiTimer.Tick += OnUiTimerTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _videosFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder");
        if (!Directory.Exists(_videosFolder))
        {
            Directory.CreateDirectory(_videosFolder);
        }

        LoadCaptureSources();
        LoadAudioSources();
        RefreshLibrary();
    }

    private void LoadCaptureSources()
    {
        _sources = WindowsScreenCapturer.EnumerateDisplays();
        CbSources.Items.Clear();
        foreach (var s in _sources)
        {
            CbSources.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s });
        }
        if (CbSources.Items.Count > 0)
            CbSources.SelectedIndex = 0;
    }

    private void LoadAudioSources()
    {
        _microphones = AudioCaptureManager.EnumerateMicrophones();
        CbMicrophones.Items.Clear();
        CbMicrophones.Items.Add(new ComboBoxItem { Content = "None (No Microphone)", Tag = "none" });

        foreach (var mic in _microphones)
        {
            var item = new ComboBoxItem
            {
                Content = $"{mic.Name}{(mic.IsDefault ? " (Default)" : "")}",
                Tag = mic.Id
            };
            CbMicrophones.Items.Add(item);
            if (mic.IsDefault)
            {
                CbMicrophones.SelectedItem = item;
            }
        }

        if (CbMicrophones.SelectedIndex == -1 && CbMicrophones.Items.Count > 1)
        {
            CbMicrophones.SelectedIndex = 1;
        }
    }

    private void OnToggleRecord(object sender, RoutedEventArgs e)
    {
        if (_currentSession == null)
        {
            StartRecording();
        }
        else
        {
            StopRecording();
        }
    }

    private string _lastSavedVideoPath = "";
    private int _targetPercent = 0;
    private int _currentDisplayPercent = 0;
    private DispatcherTimer? _savingAnimTimer;

    private static string FindFFmpegPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\engine\bin\ffmpeg.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\engine\bin\ffmpeg.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), @"engine\bin\ffmpeg.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), @"..\engine\bin\ffmpeg.exe"),
            @"D:\projects\Bili\coding\Windows Screen Recorder\engine\bin\ffmpeg.exe"
        };

        foreach (var c in candidates)
        {
            try
            {
                string full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
            catch { }
        }

        return "ffmpeg.exe";
    }

    private void StartRecording()
    {
        try
        {
            var selectedSourceItem = CbSources.SelectedItem as ComboBoxItem;
            var source = (selectedSourceItem?.Tag as ScreenSourceInfo) ?? _sources.FirstOrDefault() ?? new ScreenSourceInfo { Width = 1920, Height = 1080 };

            int targetW = source.Width;
            int targetH = source.Height;
            if (CbResolution.SelectedIndex == 0) // 1080p
            {
                targetW = 1920;
                targetH = 1080;
            }
            else if (CbResolution.SelectedIndex == 1) // 720p
            {
                targetW = 1280;
                targetH = 720;
            }

            int fps = CbFps.SelectedIndex == 1 ? 60 : 30;
            string encoder = CbEncoder.SelectedIndex == 0 ? "h264_qsv" : "libx264";

            var selectedMicItem = CbMicrophones.SelectedItem as ComboBoxItem;
            string micId = selectedMicItem?.Tag as string ?? "default";
            bool recordSys = ChkSystemAudio.IsChecked == true;

            string outputPath = Path.Combine(_videosFolder, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            string ffmpegPath = FindFFmpegPath();

            _compositor.Clear();

            // Dismiss saving panel when starting a new recording
            SavingProgressPanel.Visibility = Visibility.Collapsed;
            PanelSavingFinished.Visibility = Visibility.Collapsed;
            PanelSavingError.Visibility = Visibility.Collapsed;

            _currentSession = new RecordingSession(
                source: source,
                targetWidth: targetW,
                targetHeight: targetH,
                fps: fps,
                micDeviceId: micId,
                captureSystemAudio: recordSys,
                outputPath: outputPath,
                ffmpegPath: ffmpegPath,
                encoderName: encoder,
                compositor: _compositor
            );

            _currentSession.Start();
            _uiTimer.Start();

            // Update UI State
            BtnRecordToggle.Content = "Stop Recording";
            BtnRecordToggle.Background = (SolidColorBrush)FindResource("RecordRed");
            BtnPause.IsEnabled = true;
            StatusDot.Fill = (SolidColorBrush)FindResource("RecordRed");
            TxtStatus.Text = "Recording Active";

            CbSources.IsEnabled = false;
            CbResolution.IsEnabled = false;
            CbFps.IsEnabled = false;
            CbEncoder.IsEnabled = false;
            CbMicrophones.IsEnabled = false;
            ChkSystemAudio.IsEnabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start recording: {ex.Message}", "Recording Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPauseResume(object sender, RoutedEventArgs e)
    {
        if (_currentSession == null) return;

        if (_currentSession.IsPaused)
        {
            _currentSession.Resume();
            BtnPause.Content = "Pause";
            TxtStatus.Text = "Recording Active";
            StatusDot.Fill = (SolidColorBrush)FindResource("RecordRed");
        }
        else
        {
            _currentSession.Pause();
            BtnPause.Content = "Resume";
            TxtStatus.Text = "Recording Paused";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(234, 179, 8)); // Yellow
        }
    }

    private void StopRecording()
    {
        if (_currentSession == null) return;

        _uiTimer.Stop();

        if (_overlayWindow != null && _overlayWindow.IsVisible)
        {
            _overlayWindow.Hide();
        }

        var session = _currentSession;
        _currentSession = null;
        string outputPath = session.OutputPath;

        // Update button state immediately
        BtnRecordToggle.Content = "Saving...";
        BtnRecordToggle.IsEnabled = false;
        BtnPause.IsEnabled = false;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(234, 179, 8)); // Yellow
        TxtStatus.Text = "Saving Video...";

        // Show the interactive saving progress panel with 1 to 100 counter
        SavingProgressPanel.Visibility = Visibility.Visible;
        PanelSavingFinished.Visibility = Visibility.Collapsed;
        PanelSavingError.Visibility = Visibility.Collapsed;
        TxtSavingTitle.Text = "Saving & Encoding Video";
        TxtSavingDetail.Text = "Step 1/4: Flushing captured video frames...";
        PbSavingProgress.Value = 1;
        TxtSavingPercentage.Text = "1%";

        _currentDisplayPercent = 1;
        _targetPercent = 10;

        // Smooth 1 to 100 counting animation timer
        _savingAnimTimer?.Stop();
        _savingAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _savingAnimTimer.Tick += (s, e) =>
        {
            if (_currentDisplayPercent < _targetPercent)
            {
                _currentDisplayPercent++;
                PbSavingProgress.Value = _currentDisplayPercent;
                TxtSavingPercentage.Text = $"{_currentDisplayPercent}%";
            }
        };
        _savingAnimTimer.Start();

        Task.Run(() =>
        {
            Exception? saveError = null;
            try
            {
                session.Stop((percent, status) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _targetPercent = Math.Clamp(percent, 5, 96);
                        TxtSavingDetail.Text = status;
                    });
                });
                session.Dispose();
            }
            catch (Exception ex)
            {
                saveError = ex;
                Console.Error.WriteLine($"[StopRecording Error] {ex}");
            }

            Dispatcher.BeginInvoke(async () =>
            {
                if (saveError == null && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                {
                    // Smoothly animate counting up to 100%
                    _targetPercent = 100;
                    while (_currentDisplayPercent < 100)
                    {
                        _currentDisplayPercent++;
                        PbSavingProgress.Value = _currentDisplayPercent;
                        TxtSavingPercentage.Text = $"{_currentDisplayPercent}%";
                        await Task.Delay(10);
                    }
                    _savingAnimTimer?.Stop();

                    _lastSavedVideoPath = outputPath;
                    var fi = new FileInfo(outputPath);

                    TxtSavingTitle.Text = "Encoding Complete";
                    TxtSavingDetail.Text = "Finished! Audio & video merged into MP4 container.";
                    TxtFinishedFileInfo.Text = $"{fi.Name} • {fi.Length / (1024.0 * 1024.0):F1} MB";
                    PanelSavingFinished.Visibility = Visibility.Visible;

                    TxtStatus.Text = "Finished - Video Saved";
                    StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Emerald green
                }
                else
                {
                    _savingAnimTimer?.Stop();
                    TxtSavingPercentage.Text = "!";
                    TxtSavingTitle.Text = "Saving Failed";
                    TxtSavingErrorDetail.Text = saveError?.Message ?? "Video file was not created or is empty.";
                    PanelSavingError.Visibility = Visibility.Visible;

                    TxtStatus.Text = "Error Saving";
                    StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                }

                // Reset recording controls
                BtnRecordToggle.Content = "Start Recording";
                BtnRecordToggle.Background = (SolidColorBrush)FindResource("AccentColor");
                BtnRecordToggle.IsEnabled = true;
                BtnPause.Content = "Pause";
                BtnPause.IsEnabled = false;

                PbMicLevel.Value = 0;
                PbSysLevel.Value = 0;
                TxtMicLevel.Text = "0%";
                TxtSysLevel.Text = "0%";

                CbSources.IsEnabled = true;
                CbResolution.IsEnabled = true;
                CbFps.IsEnabled = true;
                CbEncoder.IsEnabled = true;
                CbMicrophones.IsEnabled = true;
                ChkSystemAudio.IsEnabled = true;

                RefreshLibrary();

                // Auto-select the newly saved recording in the library
                if (LvRecordings.Items.Count > 0)
                {
                    LvRecordings.SelectedIndex = 0;
                }
            });
        });
    }

    private void OnPlayLastVideo(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastSavedVideoPath) && File.Exists(_lastSavedVideoPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _lastSavedVideoPath,
                UseShellExecute = true
            });
        }
    }

    private void OnDismissSavingPanel(object sender, RoutedEventArgs e)
    {
        SavingProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void OnToggleTeaching(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null)
        {
            _overlayWindow = new OverlayWindow(_compositor);
            _overlayWindow.IsVisibleChanged += (s, ev) =>
            {
                BtnTeaching.Background = _overlayWindow.IsVisible
                    ? (Brush)new BrushConverter().ConvertFromString("#4F46E5")!
                    : (Brush)new BrushConverter().ConvertFromString("#27272A")!;
            };
        }

        if (_overlayWindow.IsVisible)
        {
            _overlayWindow.Hide();
        }
        else
        {
            _overlayWindow.Show();
        }
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        if (_currentSession == null) return;

        var elapsed = TimeSpan.FromMilliseconds(_currentSession.ElapsedMs);
        TxtTimer.Text = elapsed.ToString(@"hh\:mm\:ss");

        float mic = _currentSession.CurrentMicLevel * 100f;
        float sys = _currentSession.CurrentSystemLevel * 100f;

        PbMicLevel.Value = Math.Min(100, mic);
        PbSysLevel.Value = Math.Min(100, sys);
        TxtMicLevel.Text = $"{(int)mic}%";
        TxtSysLevel.Text = $"{(int)sys}%";

        TxtActualFps.Text = _currentSession.CurrentActualFps.ToString("F1");
        TxtEncodedFrames.Text = _currentSession.EncodedFrames.ToString();
        TxtDroppedFrames.Text = _currentSession.DroppedFrames.ToString();
    }

    private void RefreshLibrary()
    {
        if (!Directory.Exists(_videosFolder)) return;

        var list = new List<RecordingItem>();
        var files = Directory.GetFiles(_videosFolder, "*.mp4")
                             .OrderByDescending(File.GetCreationTime);

        foreach (var file in files)
        {
            var fi = new FileInfo(file);
            if (fi.Name.StartsWith("_vtmp_") || fi.Name.StartsWith("_atmp_")) continue;

            list.Add(new RecordingItem
            {
                FilePath = file,
                FileName = fi.Name,
                CreatedAt = fi.CreationTime,
                FileSizeFormatted = $"{fi.Length / (1024.0 * 1024.0):F1} MB",
                Resolution = "1080p MP4"
            });
        }

        LvRecordings.ItemsSource = list;
        BorderEmptyLibrary.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_videosFolder))
        {
            Process.Start("explorer.exe", _videosFolder);
        }
    }

    private void OnRefreshLibrary(object sender, RoutedEventArgs e)
    {
        RefreshLibrary();
    }

    private void OnPlayItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }

    private void OnShowItemInFolder(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path && File.Exists(path))
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
    }

    private void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path && File.Exists(path))
        {
            var res = MessageBox.Show($"Are you sure you want to delete {Path.GetFileName(path)}?", "Confirm Deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                try
                {
                    File.Delete(path);
                    RefreshLibrary();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to delete file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _overlayWindow?.Close();

        if (_currentSession != null)
        {
            // Cancel the close event — we'll close once the session is done saving
            e.Cancel = true;
            IsEnabled = false;
            Title = "Saving recording — please wait...";

            var session = _currentSession;
            _currentSession = null;
            _uiTimer.Stop();

            Task.Run(() =>
            {
                try
                {
                    session.Stop();
                    session.Dispose();
                }
                catch { }

                Dispatcher.BeginInvoke(() => Close());
            });
        }
    }
}