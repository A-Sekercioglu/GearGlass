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
    private const double ExpandedWidth = 500;
    private const double CollapsedSize = 60;
    private const double FullHeight = 230;
    private const double CompactHeight = 158;
    // How long the drawer stays open after launch or a re-launch, so the HUD is easy to spot.
    private static readonly TimeSpan RevealDuration = TimeSpan.FromSeconds(4);
    private static readonly nint HwndTopmost = new(-1);

    private readonly HudViewModel _viewModel = new();
    private readonly SystemMetricsService _systemMetrics = new();
    private readonly MediaSessionService _mediaSessions = new();
    private readonly AppVolumeService _appVolume = new();
    private readonly AudioSpectrumService _audioSpectrum = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HudSettingsStore _settings = new();
    // Kept in a field so the native hook never calls a collected delegate.
    private readonly OverlayNative.WinEventProc _foregroundChanged;
    private readonly DispatcherTimer _topmostBurstTimer = new(DispatcherPriority.Normal);
    private DateTime _topmostBurstUntil;
    private nint _foregroundHook;
    private Task _pollTask = Task.CompletedTask;
    private Task _volumeWriterTask = Task.CompletedTask;
    private double? _pendingVolume;
    private readonly DispatcherTimer _overlayTimer = new(DispatcherPriority.Background);
    private HwndSource? _windowSource;
    private nint _windowHandle;
    private bool _isClickThrough;
    private bool _fullscreenWindowActive;
    // null = automatic: mouse input on, pass-through while a fullscreen app is in the foreground.
    private bool? _inputModeOverride;
    private double _gearAngle;
    private bool _drawerExpanded = true;
    private DateTime _holdOpenUntil;
    private bool _mediaCommandInProgress;
    private bool _cleanupStarted;
    private bool _cleanupCompleted;
    private bool _visualizerRunning;
    private TimeSpan _lastRenderTime;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.VolumeChangeRequested += OnVolumeChangeRequested;
        _foregroundChanged = OnForegroundChanged;
        _topmostBurstTimer.Interval = TimeSpan.FromMilliseconds(100);
        _topmostBurstTimer.Tick += TopmostBurstTimer_Tick;
        BuildGear();

        // Position before the first frame so the HUD never flashes at a default spot.
        var position = PlaceOnScreen(_settings.LoadPosition() ?? HudPlacement.Default);
        Left = position.Left;
        Top = position.Top;
    }

    /// <summary>Raised when the user hides the HUD with its × button; the app keeps running in the tray.</summary>
    public event Action? HiddenToTray;

    public bool IsClickThrough => _isClickThrough;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Width = ExpandedWidth;
        Height = ExpandedHeight;
        _holdOpenUntil = DateTime.UtcNow + RevealDuration;
        _overlayTimer.Interval = TimeSpan.FromMilliseconds(250);
        _overlayTimer.Tick += OverlayTimer_Tick;
        _overlayTimer.Start();
        _audioSpectrum.EnsureRunning();
        StartVisualizer();

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
        _foregroundHook = OverlayNative.HookForegroundChanges(_foregroundChanged);
    }

    /// <summary>
    /// A game or other window just came to the front. Put the HUD back on top right away and
    /// keep doing so for a moment, because games often reorder their window while they start
    /// or switch to fullscreen.
    /// </summary>
    private void OnForegroundChanged(
        nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (_cleanupStarted || !IsVisible)
        {
            return;
        }

        KeepWindowTopmost();
        _topmostBurstUntil = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        _topmostBurstTimer.Start();
    }

    private void TopmostBurstTimer_Tick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow >= _topmostBurstUntil || _cleanupStarted)
        {
            _topmostBurstTimer.Stop();
            return;
        }

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
        var fullscreenActive = OverlayNative.IsFullscreenWindowForeground(_windowHandle);
        if (fullscreenActive != _fullscreenWindowActive)
        {
            // A manual Ctrl+Alt+F12 choice lasts until the fullscreen state changes.
            _fullscreenWindowActive = fullscreenActive;
            _inputModeOverride = null;
        }

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
            // In pass-through mode the HUD cannot be clicked, so do not pop it open under the cursor.
            SetDrawerExpanded((cursorInside && !_isClickThrough) || IsHeldOpen);
        }
    }

    /// <summary>Switches mouse pass-through on or off (Ctrl+Alt+F12 or the tray menu).</summary>
    public void ToggleInputMode()
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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshot = await Task.Run(_systemMetrics.Read, cancellationToken);
                _viewModel.ApplySystemSnapshot(snapshot);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _viewModel.ReportSystemError(error is Win32Exception ? error.Message : "Sistem verisi okunamadı");
            }

            try
            {
                if (!_mediaCommandInProgress)
                {
                    _viewModel.ApplyMediaResult(await _mediaSessions.ReadCurrentAsync());
                }

                var hint = _viewModel.MediaSourceHint;
                var volume = await Task.Run(() => _appVolume.Read(hint), cancellationToken);
                _viewModel.ApplyAppVolume(volume);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _viewModel.ApplyAppVolume(new AppVolumeReading(false, 0, "SES HATA"));
            }

            // Restarts loopback capture after a playback-device change.
            _audioSpectrum.EnsureRunning();

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

    private double ExpandedHeight => _viewModel.IsSystemPanelExpanded ? FullHeight : CompactHeight;

    private void Drawer_MouseEnter(object sender, MouseEventArgs e) => SetDrawerExpanded(!_isClickThrough);

    private void Drawer_MouseLeave(object sender, MouseEventArgs e) => SetDrawerExpanded(IsHeldOpen);

    private bool IsHeldOpen => DateTime.UtcNow < _holdOpenUntil;

    /// <summary>
    /// Opens the drawer for a few seconds and puts the HUD back on top. Called when the user
    /// launches GearGlass again while it is already running, so the launch is never silent.
    /// </summary>
    public void RevealHud()
    {
        if (_cleanupStarted)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        _holdOpenUntil = DateTime.UtcNow + RevealDuration;
        KeepWindowTopmost();
        SetDrawerExpanded(true);
    }

    /// <summary>Hides the HUD; GearGlass keeps running in the notification area.</summary>
    public void HideHud()
    {
        if (!IsVisible || _cleanupStarted)
        {
            return;
        }

        _holdOpenUntil = DateTime.MinValue;
        SetDrawerExpanded(false);
        StopVisualizer();
        Hide();
    }

    /// <summary>Moves the HUD back to the top-left corner and forgets the saved position.</summary>
    public void ResetPosition()
    {
        Left = HudPlacement.Default.Left;
        Top = HudPlacement.Default.Top;
        _settings.SavePosition(HudPlacement.Default);
        RevealHud();
    }

    /// <summary>Lets the user drag the HUD by any part that is not a control.</summary>
    private void Hud_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClickThrough || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was already released; nothing to drag.
            return;
        }

        var position = PlaceOnScreen(new HudPosition(Left, Top));
        Left = position.Left;
        Top = position.Top;
        _settings.SavePosition(position);
    }

    /// <summary>Keeps the fully opened HUD inside the visible desktop.</summary>
    private static HudPosition PlaceOnScreen(HudPosition position) =>
        HudPlacement.Clamp(
            position,
            ExpandedWidth,
            FullHeight,
            new ScreenArea(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight));

    private void SetDrawerExpanded(bool expanded)
    {
        if (_drawerExpanded == expanded)
        {
            return;
        }

        _drawerExpanded = expanded;

        var duration = TimeSpan.FromMilliseconds(expanded ? 260 : 220);
        BeginAnimation(
            WidthProperty,
            new DoubleAnimation(expanded ? ExpandedWidth : CollapsedSize, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(
            HeightProperty,
            new DoubleAnimation(expanded ? ExpandedHeight : CollapsedSize, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);

        if (expanded)
        {
            StartVisualizer();
            DrawerContent.Visibility = Visibility.Visible;
            CollapsedTab.Visibility = Visibility.Collapsed;
            DrawerContent.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        }
        else
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fadeOut.Completed += (_, _) =>
            {
                // Skip if the drawer was re-opened while fading out.
                if (!_drawerExpanded)
                {
                    StopVisualizer();
                    DrawerContent.Visibility = Visibility.Collapsed;
                    CollapsedTab.Visibility = Visibility.Visible;
                }
            };
            DrawerContent.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }

    /// <summary>Animates the visualizer once per rendered frame, only while the drawer is open.</summary>
    private void StartVisualizer()
    {
        if (_visualizerRunning || _cleanupStarted)
        {
            return;
        }

        _visualizerRunning = true;
        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void StopVisualizer()
    {
        if (!_visualizerRunning)
        {
            return;
        }

        _visualizerRunning = false;
        CompositionTarget.Rendering -= OnCompositionRendering;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        var renderTime = e is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
        if (renderTime == _lastRenderTime)
        {
            // Rendering can fire more than once per frame.
            return;
        }

        var elapsed = _lastRenderTime == TimeSpan.Zero ? 0 : (renderTime - _lastRenderTime).TotalSeconds;
        _lastRenderTime = renderTime;
        _viewModel.Visualizer.Advance(elapsed, _audioSpectrum.GetLatestFrame());
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
            new DoubleAnimation(ExpandedHeight, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
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

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        HideHud();
        HiddenToTray?.Invoke();
    }

    private void OnVolumeChangeRequested(double percent)
    {
        if (_cleanupStarted)
        {
            return;
        }

        // Coalesce slider drags: only the latest value is written, one write at a time.
        _pendingVolume = percent;
        if (_volumeWriterTask.IsCompleted)
        {
            _volumeWriterTask = WritePendingVolumeAsync();
        }
    }

    private async Task WritePendingVolumeAsync()
    {
        while (_pendingVolume is double percent)
        {
            _pendingVolume = null;
            var hint = _viewModel.MediaSourceHint;
            try
            {
                await Task.Run(() => _appVolume.SetVolume(hint, percent));
            }
            catch (Exception)
            {
                _viewModel.ApplyAppVolume(new AppVolumeReading(false, 0, "SES HATA"));
            }
        }
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
            var before = _viewModel.CurrentSession;
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
                if (HasMediaStateChanged(before, mediaResult.Session, command))
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

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_cleanupCompleted)
        {
            return;
        }

        // Finish background work before the app shuts down so services are disposed safely.
        e.Cancel = true;
        if (_cleanupStarted)
        {
            return;
        }

        _cleanupStarted = true;
        Hide();
        await CleanupAsync();
        _cleanupCompleted = true;
        Close();
    }

    private async Task CleanupAsync()
    {
        _overlayTimer.Stop();
        _overlayTimer.Tick -= OverlayTimer_Tick;
        _topmostBurstTimer.Stop();
        _topmostBurstTimer.Tick -= TopmostBurstTimer_Tick;
        if (_foregroundHook != nint.Zero)
        {
            OverlayNative.UnhookWinEvent(_foregroundHook);
            _foregroundHook = nint.Zero;
        }

        StopVisualizer();
        _viewModel.VolumeChangeRequested -= OnVolumeChangeRequested;
        _windowSource?.RemoveHook(WindowMessageHook);
        if (_windowHandle != nint.Zero)
        {
            OverlayNative.UnregisterHotKey(_windowHandle, InputModeHotKeyId);
        }

        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_pollTask, _volumeWriterTask).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Cancellation or a timeout: nothing more to wait for.
        }

        _systemMetrics.Dispose();
        _appVolume.Dispose();
        _audioSpectrum.Dispose();
        _shutdown.Dispose();
    }
}
