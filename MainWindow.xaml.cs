using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GearGlass.Services;
using GearGlass.ViewModels;

namespace GearGlass;

public partial class MainWindow : Window
{
    private const int GearToothCount = 12;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int WmHotkey = 0x0312;
    private const int MaNoActivate = 3;
    private const int InputModeHotKeyId = 0x4747;
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int ModNoRepeat = 0x4000;
    private const int VkF12 = 0x7B;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly nint HwndTopmost = new(-1);

    private readonly HudViewModel _viewModel = new();
    private readonly SystemMetricsService _systemMetrics = new();
    private readonly MediaSessionService _mediaSessions = new();
    private readonly AppVolumeService _appVolume = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task _pollTask = Task.CompletedTask;
    private readonly DispatcherTimer _overlayTimer = new(DispatcherPriority.Background);
    private HwndSource? _windowSource;
    private nint _windowHandle;
    private bool _isClickThrough;
    private bool _fullscreenWindowActive;
    private bool? _inputModeOverride = false;
    private double _gearAngle;
    private bool _drawerExpanded = true;
    private bool _mediaCommandInProgress;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        BuildGear();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Left = 12;
        Top = 12;
        Width = 500;
        Height = 190;
        SetDrawerExpanded(true);
        _overlayTimer.Interval = TimeSpan.FromMilliseconds(250);
        _overlayTimer.Tick += OverlayTimer_Tick;
        _overlayTimer.Start();

        try
        {
            _pollTask = PollAsync(_shutdown.Token);
            await _pollTask;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowMessageHook);
        if (!OverlayNative.RegisterHotKey(
                _windowHandle,
                InputModeHotKeyId,
                ModControl | ModAlt | ModNoRepeat,
                VkF12))
        {
            MessageBox.Show(
                "Ctrl+Alt+F12 kısayolu kaydedilemedi. Fare geçiş modunu bu oturumda değiştiremeyebilirsiniz.",
                "GearGlass",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        ApplyOverlayStyles();
        KeepWindowTopmost();
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == InputModeHotKeyId)
        {
            ToggleInputMode();
            handled = true;
            return nint.Zero;
        }

        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }

        return nint.Zero;
    }

    private void OverlayTimer_Tick(object? sender, EventArgs e)
    {
        if (_windowHandle == nint.Zero || !IsVisible)
        {
            return;
        }

        KeepWindowTopmost();
        _fullscreenWindowActive = OverlayNative.IsFullscreenWindowForeground(_windowHandle);
        var clickThrough = _inputModeOverride ?? _fullscreenWindowActive;
        if (_isClickThrough != clickThrough)
        {
            _isClickThrough = clickThrough;
            ApplyOverlayStyles();
        }

        if (OverlayNative.TryGetCursorPosition(out var cursor) &&
            OverlayNative.GetWindowRect(_windowHandle, out var windowBounds))
        {
            var cursorInside = cursor.X >= windowBounds.Left && cursor.X < windowBounds.Right &&
                               cursor.Y >= windowBounds.Top && cursor.Y < windowBounds.Bottom;
            SetDrawerExpanded(cursorInside);
        }
    }

    private void ToggleInputMode()
    {
        var currentlyClickThrough = _inputModeOverride ?? _fullscreenWindowActive;
        _inputModeOverride = !currentlyClickThrough;
        _isClickThrough = _inputModeOverride.Value;
        ApplyOverlayStyles();
    }

    private void ApplyOverlayStyles()
    {
        var extendedStyle = OverlayNative.GetWindowLongPtr(_windowHandle, GwlExStyle).ToInt64();
        extendedStyle |= WsExNoActivate;
        if (_isClickThrough)
        {
            extendedStyle |= WsExTransparent;
        }
        else
        {
            extendedStyle &= ~((long)WsExTransparent);
        }

        OverlayNative.SetWindowLongPtr(_windowHandle, GwlExStyle, new nint(extendedStyle));
        OverlayNative.SetWindowPos(
            _windowHandle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpFrameChanged);
    }

    private void KeepWindowTopmost()
    {
        if (_windowHandle != nint.Zero)
        {
            OverlayNative.SetWindowPos(
                _windowHandle,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoActivate);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var nextMediaRefresh = DateTime.MinValue;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshot = await Task.Run(_systemMetrics.Read, cancellationToken);
                _viewModel.ApplySystemSnapshot(snapshot);
            }
            catch (Win32Exception error)
            {
                _viewModel.ReportSystemError(error.Message);
            }

            if (DateTime.UtcNow >= nextMediaRefresh)
            {
                _viewModel.ApplyMediaResult(await _mediaSessions.ReadCurrentAsync());
                nextMediaRefresh = DateTime.UtcNow.AddSeconds(2);
            }

            var volume = await Task.Run(() => _appVolume.Read(_viewModel.MediaSourceHint), cancellationToken);
            _viewModel.ApplyAppVolume(volume);

            await timer.WaitForNextTickAsync(cancellationToken);
        }
    }

    private void BuildGear()
    {
        var profile = new (double Offset, double Radius)[]
        {
            (-0.5, 24), (-0.34, 24), (-0.29, 30), (-0.16, 30),
            (0.16, 30), (0.29, 30), (0.34, 24), (0.5, 24)
        };
        var points = new PointCollection();

        for (var tooth = 0; tooth < GearToothCount; tooth++)
        {
            foreach (var (offset, radius) in profile)
            {
                var angle = ((tooth + offset) / GearToothCount) * Math.PI * 2 - Math.PI / 2;
                points.Add(new Point(31 + Math.Cos(angle) * radius, 31 + Math.Sin(angle) * radius));
            }
        }

        GearShape.Points = points;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The drawer is locked to the upper-left corner.
    }

    private void Drawer_MouseEnter(object sender, MouseEventArgs e) => SetDrawerExpanded(true);

    private void Drawer_MouseLeave(object sender, MouseEventArgs e) => SetDrawerExpanded(false);

    private void SetDrawerExpanded(bool expanded)
    {
        if (_drawerExpanded == expanded)
        {
            return;
        }

        _drawerExpanded = expanded;

        BeginAnimation(
            WidthProperty,
            new DoubleAnimation(expanded ? 500 : 60, TimeSpan.FromMilliseconds(expanded ? 260 : 220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        BeginAnimation(
            HeightProperty,
            new DoubleAnimation(expanded ? 190 : 60, TimeSpan.FromMilliseconds(expanded ? 260 : 220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        DrawerContent.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(expanded ? 1 : 0, TimeSpan.FromMilliseconds(expanded ? 180 : 120)));
        DrawerContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CollapsedTab.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        Left = 12;
        Top = 12;
    }

    private void ToggleSystemPanel(object sender, RoutedEventArgs e)
    {
        var isExpanded = !_viewModel.IsSystemPanelExpanded;
        _viewModel.IsSystemPanelExpanded = isExpanded;

        SystemPanelContent.BeginAnimation(
            FrameworkElement.HeightProperty,
            new DoubleAnimation(isExpanded ? 72 : 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        SystemPanelSlide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(isExpanded ? 0 : -14, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            },
            HandoffBehavior.SnapshotAndReplace);

        BeginAnimation(
            HeightProperty,
            new DoubleAnimation(isExpanded ? 190 : 118, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
        Top = 12;
        Left = 12;
    }

    private void SystemPanel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var direction = e.Delta > 0 ? -1 : 1;
        _viewModel.CycleFocus(direction);
        _gearAngle += direction * 120;
        GearRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(_gearAngle, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
        e.Handled = true;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void AppVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || !_viewModel.AppVolumeAvailable)
        {
            return;
        }

        _ = Task.Run(() => _appVolume.SetVolume(_viewModel.MediaSourceHint, e.NewValue));
    }

    private async void PreviousTrack_Click(object sender, RoutedEventArgs e) =>
        await SendMediaCommandAsync(MediaControlCommand.Previous);

    private async void PlayPause_Click(object sender, RoutedEventArgs e) =>
        await SendMediaCommandAsync(MediaControlCommand.PlayPause);

    private async void NextTrack_Click(object sender, RoutedEventArgs e) =>
        await SendMediaCommandAsync(MediaControlCommand.Next);

    private async Task SendMediaCommandAsync(MediaControlCommand command)
    {
        if (_mediaCommandInProgress)
        {
            return;
        }

        _mediaCommandInProgress = true;
        try
        {
            var before = await _mediaSessions.ReadCurrentAsync();
            var result = await _mediaSessions.SendCommandAsync(command);
            if (!result.Accepted)
            {
                _viewModel.ReportMediaControlStatus(result.Status ?? "Medya komutu kabul edilmedi.");
                return;
            }

            for (var attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(250);
                var mediaResult = await _mediaSessions.ReadCurrentAsync();
                _viewModel.ApplyMediaResult(mediaResult);
                if (HasMediaStateChanged(before.Session, mediaResult.Session, command))
                {
                    break;
                }
            }
        }
        finally
        {
            _mediaCommandInProgress = false;
        }
    }

    private static bool HasMediaStateChanged(
        NowPlayingSnapshot? before,
        NowPlayingSnapshot? after,
        MediaControlCommand command)
    {
        if (after is null)
        {
            return before is not null;
        }

        return command switch
        {
            MediaControlCommand.PlayPause => before?.PlaybackStatus != after.PlaybackStatus,
            MediaControlCommand.Previous or MediaControlCommand.Next => before?.Title != after.Title,
            _ => false
        };
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _overlayTimer.Stop();
        _overlayTimer.Tick -= OverlayTimer_Tick;
        _windowSource?.RemoveHook(WindowMessageHook);
        if (_windowHandle != nint.Zero)
        {
            OverlayNative.UnregisterHotKey(_windowHandle, InputModeHotKeyId);
        }

        _shutdown.Cancel();
        try
        {
            await _pollTask;
        }
        catch (OperationCanceledException)
        {
        }

        _systemMetrics.Dispose();
        _appVolume.Dispose();
        _shutdown.Dispose();
    }
}
