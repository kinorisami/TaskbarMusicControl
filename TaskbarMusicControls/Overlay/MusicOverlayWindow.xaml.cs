using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TaskbarMusicControls.Interop;
using TaskbarMusicControls.Services;

namespace TaskbarMusicControls.Overlay;

internal enum WidgetDisplayMode
{
    FullDetails,
    CoverAndControls,
    CoverOnly,
    /// <summary>
    /// The lyrics fill the widget; the playback controls stay out of the way
    /// until the pointer is over the widget, then slide down from the top edge.
    /// </summary>
    LyricsWithHoverControls
}

/// <summary>
/// Why the overlay could not be placed inside the taskbar.
/// </summary>
public enum TaskbarPositionResult
{
    Positioned,
    /// <summary>The available area is empty or too narrow to host the widget.</summary>
    NoSpace,
    /// <summary>
    /// The window could not be attached to the taskbar window. This normally
    /// means Explorer runs with different privileges than this process, which
    /// makes Windows block the cross process re-parenting.
    /// </summary>
    NotAttached
}

public partial class MusicOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = unchecked((long)0x80000000);
    private const long WsClipSiblings = 0x04000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTop = 0;
    private const double DefaultWidth = 350;
    private const double MinimumWidth = 50;
    private const double TransportLayoutWidth = 130;

    /// <summary>
    /// How far the control drawer drops down. It covers the whole text area so
    /// the line underneath never peeks out under the drawer.
    /// </summary>
    private double DrawerRevealHeight => Height > 0 ? Height : 60;

    private static readonly Brush AccentBrush = CreateFrozenBrush(0xFF, 0x16, 0x7C, 0x69);
    private static readonly Brush InactiveBrush = CreateFrozenBrush(0xFF, 0x30, 0x34, 0x3A);
    private const string CopyGlyph = "\uE8C8";
    private const string CopiedGlyph = "\uE73E";

    private nint _taskbarHandle;
    private nint _parentTaskbarHandle;
    private nint _nativeHandle;
    private string _currentTitle = "No media playing";
    private string _currentArtist = "Start playback in a media app";
    private byte[]? _currentArtworkBytes;
    private int _artworkDecodeWidth;
    private bool _artworkRenderFailed;
    private bool _lyricsMode;
    private TimeSpan _lyricsOffset;
    private TimeSpan _lastLyricsPosition;
    private bool _hoverPlaybackOnlyMode;
    private bool _hoverControlsMode;
    private bool _drawerOpen;
    private readonly DispatcherTimer _drawerHideTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };
    private readonly DispatcherTimer _progressTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };
    private TimeSpan _progressPosition;
    private TimeSpan _progressDuration;
    private bool _progressPlaying;
    private long _progressAnchorTimestamp;
    private bool _canSeek;
    private bool _seeking;
    private double _seekFraction;
    private bool _showTranslation = true;
    private readonly DispatcherTimer _copyFeedbackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(700)
    };
    private WidgetDisplayMode _displayMode = WidgetDisplayMode.FullDetails;
    private LyricsSourceSettings _lyricsSourceSettings = LyricsSourceSettings.Default;
    private LyricsSource? _lyricsSource;
    private bool _lyricsLocked;
    private bool _startWithWindows;
    private IReadOnlyList<TaskbarOption> _taskbarOptions = Array.Empty<TaskbarOption>();
    private string _selectedTaskbarDeviceId = "";
    private double _lastAvailableWidth = DefaultWidth;
    private double _responsiveLayoutWidth = -1;
    private LyricsDocument? _lyrics;
    private string _renderedOriginal = "";
    private string? _renderedTranslation;
    private TimeSpan? _renderedOriginalTime;
    private TimeSpan? _renderedTranslationTime;
    private TimeSpan? _originalTimeRemaining;
    private TimeSpan? _translationTimeRemaining;

    public event EventHandler? PreviousRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? NextRequested;
    public event Action<object?, bool>? LyricsModeChanged;
    internal event Action<object?, WidgetDisplayMode>? DisplayModeChanged;
    internal event Action<object?, LyricsSourceSettings>? LyricsSourceSettingsChanged;
    internal event EventHandler? LyricsRefetchRequested;
    internal event Action<object?, bool>? LyricsLockChanged;
    internal event Action<object?, bool>? TranslationChanged;
    internal event Action<object?, bool>? StartupChanged;
    internal event Action<object?, TimeSpan>? LyricsOffsetChanged;
    internal event Action<object?, string>? TaskbarSelectionChanged;
    internal event Action<object?, TimeSpan>? SeekRequested;
    internal event EventHandler? PlayerActivateRequested;

    public bool HasValidNativeHandle =>
        _nativeHandle != 0 && IsWindow(_nativeHandle);

    public MusicOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += OnIsVisibleChanged;
        _drawerHideTimer.Tick += OnDrawerHideTimerTick;
        _progressTimer.Tick += (_, _) => RenderProgress();
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            CopyButton.Content = CopyGlyph;
        };
    }

    private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public void UpdateMedia(string title, string artist, byte[]? artworkBytes)
    {
        _currentTitle = string.IsNullOrWhiteSpace(title)
            ? "No media playing"
            : title;
        _currentArtist = string.IsNullOrWhiteSpace(artist)
            ? "Start playback in a media app"
            : artist;
        TitlePrimary.Text = TitleSecondary.Text = _currentTitle;
        ArtistPrimary.Text = ArtistSecondary.Text = _currentArtist;
        TitleCanvas.ToolTip = $"{_currentTitle}{Environment.NewLine}{_currentArtist}";
        ArtistCanvas.ToolTip = _currentArtist;

        // The same byte array means the same image: decoding it again would
        // only throw away the bitmap that is already on screen. A failed decode
        // and a changed display scale are the exceptions.
        var decodeWidth = GetArtworkDecodeWidth();
        if (!ReferenceEquals(artworkBytes, _currentArtworkBytes) ||
            decodeWidth != _artworkDecodeWidth ||
            _artworkRenderFailed)
        {
            _currentArtworkBytes = artworkBytes;
            _artworkDecodeWidth = decodeWidth;
            _artworkRenderFailed = !ApplyArtwork(artworkBytes, decodeWidth);
        }

        ScheduleMarqueeRefresh();
    }

    /// <summary>
    /// The width the cover is decoded at. The cover is rendered 48x48 DIP, so
    /// decoding it larger would keep a full resolution bitmap alive for a
    /// thumbnail.
    /// </summary>
    private int GetArtworkDecodeWidth()
    {
        var dpi = GetDpiScale(_taskbarHandle);
        return (int)Math.Max(
            48,
            Math.Ceiling(48 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
    }

    private bool ApplyArtwork(byte[]? artworkBytes, int decodeWidth)
    {
        if (artworkBytes is not { Length: > 0 })
        {
            ArtworkImage.Source = null;
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtworkGlyph.Visibility = Visibility.Visible;
            return true;
        }

        try
        {
            using var stream = new MemoryStream(artworkBytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            // Drop the previous bitmap before handing over the new one so old and
            // new artwork never both stay referenced.
            ArtworkImage.Source = null;
            ArtworkImage.Source = image;
            ArtworkImage.Visibility = Visibility.Visible;
            ArtworkGlyph.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception)
        {
            ArtworkImage.Source = null;
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtworkGlyph.Visibility = Visibility.Visible;
            return false;
        }
    }

    public void UpdatePlaybackState(
        bool isPlaying,
        bool canPlayPause,
        bool canGoPrevious,
        bool canGoNext,
        bool canSeek)
    {
        ToggleButton.Content = isPlaying ? "Ⅱ" : "▶";
        ToggleButton.IsEnabled = canPlayPause;
        HoverPlaybackButton.IsEnabled = canPlayPause;
        PreviousButton.IsEnabled = canGoPrevious;
        NextButton.IsEnabled = canGoNext;
        ToggleButton.SetValue(
            AutomationProperties.NameProperty,
            isPlaying ? "Pause" : "Play");
        _canSeek = canSeek;
        UpdateProgressCursor();
    }

    /// <summary>
    /// The bar is only clickable when the player accepts a seek *and* a total
    /// length is known - without one there is nothing to map the click onto.
    /// </summary>
    private bool ProgressInteractive => _canSeek && _progressDuration > TimeSpan.Zero;

    private void UpdateProgressCursor() =>
        ProgressHit.Cursor = ProgressInteractive ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;

    internal void UpdateLyrics(LyricsDocument? lyrics, TimeSpan position)
    {
        _lyrics = lyrics;
        _lastLyricsPosition = position;
        var lyricPosition = position - _lyricsOffset;
        if (lyricPosition < TimeSpan.Zero)
            lyricPosition = TimeSpan.Zero;
        var current = lyrics?.At(lyricPosition);
        var originalFrame = current?.Original;
        var translationFrame = _showTranslation ? current?.Translation : null;
        var original = originalFrame?.Text;
        var translation = translationFrame?.Text;
        var hasTranslation = !string.IsNullOrWhiteSpace(translation);
        var hasLine = _lyricsMode && !string.IsNullOrWhiteSpace(original);
        LyricsPanel.Visibility = hasLine ? Visibility.Visible : Visibility.Collapsed;
        MetadataTextPanel.Visibility = hasLine ? Visibility.Collapsed : Visibility.Visible;
        // Nothing to copy before the first line of the song, and nothing to
        // toggle when the source delivered no translation at all.
        CopyButton.IsEnabled = hasLine;
        TranslationButton.IsEnabled = lyrics is { TranslationLines.Count: > 0 };
        if (!hasLine) return;

        var originalChanged = original != _renderedOriginal || originalFrame!.Time != _renderedOriginalTime;
        var translationChanged = translation != _renderedTranslation || translationFrame?.Time != _renderedTranslationTime;
        var translationVisibilityChanged = hasTranslation != (_renderedTranslation is not null);
        if (!originalChanged && !translationChanged) return;
        _renderedOriginal = original!;
        _renderedTranslation = hasTranslation ? translation : null;
        _renderedOriginalTime = originalFrame!.Time;
        _renderedTranslationTime = translationFrame?.Time;
        _originalTimeRemaining = originalFrame.NextTime is { } originalNext
            ? originalNext - lyricPosition
            : null;
        _translationTimeRemaining = translationFrame?.NextTime is { } translationNext
            ? translationNext - lyricPosition
            : null;
        LyricsOriginalText.Text = _renderedOriginal;
        LyricsTranslationText.Text = _renderedTranslation ?? "";
        if (originalChanged)
            AnimateLineEnter(LyricsOriginalText);
        if (translationChanged)
            AnimateLineEnter(LyricsTranslationText);
        LyricsOriginalCanvas.VerticalAlignment = hasTranslation
            ? VerticalAlignment.Top
            : VerticalAlignment.Center;
        LyricsTranslationCanvas.Visibility = _renderedTranslation is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        ScheduleLyricsMarqueeRefresh(
            originalChanged || translationVisibilityChanged,
            translationChanged || translationVisibilityChanged);
    }

    internal void SetLyricsMode(bool enabled)
    {
        _lyricsMode = enabled;
        LyricsButton.Foreground = enabled
            ? new SolidColorBrush(Color.FromRgb(22, 124, 105))
            : new SolidColorBrush(Color.FromRgb(48, 52, 58));
    }

    internal void SetDisplayMode(WidgetDisplayMode mode)
    {
        _displayMode = mode;
        var width = GetEffectiveWidth(_lastAvailableWidth);
        if (Math.Abs(Width - width) > 0.5)
            Width = width;
        ApplyResponsiveLayout(width);
    }

    public TaskbarPositionResult PositionInAvailableArea(
        nint taskbarHandle,
        Rect taskbarBounds,
        Rect availableBounds)
    {
        if (availableBounds.IsEmpty || availableBounds.Width <= 0 ||
            taskbarBounds.Height <= 0)
            return TaskbarPositionResult.NoSpace;

        var dpi = GetDpiScale(taskbarHandle);
        var availableWidth = availableBounds.Width / dpi.DpiScaleX;
        _lastAvailableWidth = availableWidth;
        var width = GetEffectiveWidth(availableWidth);
        if (width < MinimumWidth)
            return TaskbarPositionResult.NoSpace;

        if (Math.Abs(Width - width) > 0.5)
            Width = width;
        if (Math.Abs(_responsiveLayoutWidth - width) > 0.5)
            ApplyResponsiveLayout(width);
        _taskbarHandle = taskbarHandle;

        var physicalWidth = width * dpi.DpiScaleX;
        var physicalHeight = Height * dpi.DpiScaleY;
        var physicalLeft = availableBounds.Right - physicalWidth;
        var physicalTop = taskbarBounds.Top +
            (taskbarBounds.Height - physicalHeight) / 2;

        Left = physicalLeft / dpi.DpiScaleX;
        Top = physicalTop / dpi.DpiScaleY;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            if (!AttachToTaskbar(handle, taskbarHandle))
                return TaskbarPositionResult.NotAttached;

            var rect = new NativeRect(
                (int)Math.Round(physicalLeft),
                (int)Math.Round(physicalTop),
                (int)Math.Round(physicalLeft + physicalWidth),
                (int)Math.Round(physicalTop + physicalHeight));
            MapWindowPoints(0, taskbarHandle, ref rect, 2);
            SetWindowPos(
                handle,
                HwndTop,
                rect.Left,
                rect.Top,
                (int)Math.Round(physicalWidth),
                (int)Math.Round(physicalHeight),
                SwpNoActivate);
        }

        return TaskbarPositionResult.Positioned;
    }

    private void ApplyResponsiveLayout(double width)
    {
        _responsiveLayoutWidth = width;
        var effectiveMode = GetEffectiveMode(width);
        _hoverControlsMode = effectiveMode == WidgetDisplayMode.LyricsWithHoverControls;

        if (effectiveMode == WidgetDisplayMode.CoverOnly)
        {
            Artwork.Visibility = Visibility.Visible;
            ArtworkColumn.Width = new GridLength(1, GridUnitType.Star);
            Artwork.HorizontalAlignment = HorizontalAlignment.Center;
            MetadataPanel.Visibility = Visibility.Collapsed;
            MetadataColumn.Width = new GridLength(0);
            DockControlsInColumn(0);
            PlaybackControls.Visibility = Visibility.Collapsed;
            SetHoverPlaybackOnlyMode(true);
            return;
        }

        Artwork.Visibility = Visibility.Visible;
        Artwork.HorizontalAlignment = HorizontalAlignment.Left;
        ArtworkColumn.Width = new GridLength(50);
        PlaybackControls.Visibility = Visibility.Visible;
        SetHoverPlaybackOnlyMode(false);
        PreviousButton.Width = 22;
        ToggleButton.Width = 30;
        NextButton.Width = 22;

        if (effectiveMode == WidgetDisplayMode.CoverAndControls)
        {
            LyricsButton.Visibility = Visibility.Collapsed;
            LyricsButton.Width = 0;
            MetadataPanel.Visibility = Visibility.Collapsed;
            MetadataColumn.Width = new GridLength(1, GridUnitType.Star);
            DockControlsInColumn(74);
            return;
        }

        LyricsButton.Visibility = Visibility.Visible;
        LyricsButton.Width = 24;
        MetadataPanel.Visibility = Visibility.Visible;

        if (effectiveMode == WidgetDisplayMode.LyricsWithHoverControls)
        {
            // No separate controls column: the lyrics use the whole width and the
            // strip becomes a drawer over the text area.
            MetadataColumn.Width = new GridLength(1, GridUnitType.Star);
            ControlsColumn.Width = new GridLength(0);
            PlaceControlsAsDrawer();
        }
        else
        {
            MetadataColumn.Width = new GridLength(1, GridUnitType.Star);
            DockControlsInColumn(112);
        }

        ScheduleMarqueeRefresh();
    }

    /// <summary>
    /// Puts the control strip back into its own column on the right, where every
    /// layout except the lyrics one shows it.
    /// </summary>
    private void DockControlsInColumn(double columnWidth)
    {
        ControlsColumn.Width = new GridLength(columnWidth);
        Grid.SetColumn(ControlsReveal, 2);
        ControlsReveal.BeginAnimation(FrameworkElement.HeightProperty, null);
        // The drawer fades itself out when it closes; without this the docked
        // strip would inherit that zero opacity and stay invisible.
        ControlsReveal.BeginAnimation(UIElement.OpacityProperty, null);
        ControlsReveal.Opacity = 1;
        ControlsReveal.Height = double.NaN;
        ControlsReveal.Background = null;
        ControlsReveal.BorderThickness = new Thickness(0);
        ControlsReveal.VerticalAlignment = VerticalAlignment.Stretch;
        DrawerLayout.Margin = new Thickness(0);
        PlaybackControls.HorizontalAlignment = HorizontalAlignment.Stretch;
        PlaybackControls.Margin = new Thickness(0);
        ProgressText.Visibility = Visibility.Collapsed;
        ProgressHit.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Collapsed;
        TranslationButton.Visibility = Visibility.Collapsed;
        MetadataPanel.BeginAnimation(UIElement.OpacityProperty, null);
        MetadataPanel.Opacity = 1;
        _seeking = false;
        _drawerOpen = false;
        _drawerHideTimer.Stop();
        _progressTimer.Stop();
    }

    /// <summary>
    /// Moves the control strip over the text area, where it drops down from the
    /// top edge as a drawer with its own background.
    /// </summary>
    private void PlaceControlsAsDrawer()
    {
        Grid.SetColumn(ControlsReveal, 1);
        // No surface of its own: the taskbar shows through, so the drawer always
        // matches the taskbar colour whatever the wallpaper does to it. The
        // lyrics are faded out while it is open, so nothing shows through.
        ControlsReveal.Background = null;
        ControlsReveal.BorderThickness = new Thickness(0);
        ControlsReveal.VerticalAlignment = VerticalAlignment.Top;
        DrawerLayout.Margin = new Thickness(10, 0, 10, 0);
        PlaybackControls.HorizontalAlignment = HorizontalAlignment.Right;
        PlaybackControls.Margin = new Thickness(0);
        ProgressText.Visibility = Visibility.Visible;
        ProgressHit.Visibility = Visibility.Visible;
        CopyButton.Visibility = Visibility.Visible;
        TranslationButton.Visibility = Visibility.Visible;
        UpdateTranslationButton();
        UpdateProgressCursor();
        RenderProgress();
        ApplyDrawerRestState();
    }

    /// <summary>
    /// Puts the drawer back in place without animating: open where the pointer
    /// already is, closed everywhere else.
    /// </summary>
    private void ApplyDrawerRestState()
    {
        _drawerOpen = _hoverControlsMode &&
            (ContentArea.IsMouseOver || ControlsReveal.IsMouseOver || IsContextMenuOpen);
        ControlsReveal.BeginAnimation(FrameworkElement.HeightProperty, null);
        ControlsReveal.BeginAnimation(UIElement.OpacityProperty, null);
        ControlsReveal.Height = _drawerOpen ? DrawerRevealHeight : 0;
        ControlsReveal.Opacity = 1;

        // The drawer has no background, so the lyrics are hidden while it covers
        // them instead of showing through it.
        MetadataPanel.BeginAnimation(UIElement.OpacityProperty, null);
        MetadataPanel.Opacity = _drawerOpen ? 0 : 1;
        if (_drawerOpen)
        {
            _progressTimer.Start();
            RenderProgress();
        }
        else
        {
            _progressTimer.Stop();
        }
    }

    /// <summary>
    /// Feeds the progress bar with the position the widget last received. The
    /// media service only publishes a state when something changes, so the
    /// overlay advances the bar itself between those updates.
    /// </summary>
    internal void UpdateProgress(TimeSpan position, TimeSpan duration, bool isPlaying)
    {
        _progressPosition = position;
        _progressDuration = duration;
        _progressPlaying = isPlaying;
        _progressAnchorTimestamp = Stopwatch.GetTimestamp();
        UpdateProgressCursor();
        RenderProgress();
    }

    private void RenderProgress()
    {
        var position = _seeking ? PreviewPosition() : CurrentProgressPosition();
        if (_progressDuration > TimeSpan.Zero)
        {
            ProgressText.Text =
                $"{FormatPlaybackTime(position)} / {FormatPlaybackTime(_progressDuration)}";
            var fraction = Math.Clamp(
                position.TotalSeconds / _progressDuration.TotalSeconds, 0, 1);
            ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * fraction);
            return;
        }

        // The player reports no length (an empty timeline), so only the elapsed
        // time can be shown - the bar itself would have nothing to measure.
        ProgressText.Text = FormatPlaybackTime(position);
        ProgressFill.Width = 0;
    }

    private TimeSpan CurrentProgressPosition()
    {
        var position = _progressPosition;
        if (_progressPlaying)
            position += Stopwatch.GetElapsedTime(_progressAnchorTimestamp);
        if (_progressDuration > TimeSpan.Zero && position > _progressDuration)
            position = _progressDuration;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    private static string FormatPlaybackTime(TimeSpan value) =>
        $"{(int)value.TotalMinutes}:{value.Seconds:00}";

    private TimeSpan PreviewPosition() => TimeSpan.FromTicks(
        (long)(_progressDuration.Ticks * Math.Clamp(_seekFraction, 0, 1)));

    private void OnProgressMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!ProgressInteractive)
            return;

        _seeking = true;
        // Stop the interpolation so the preview is not overwritten mid-drag.
        _progressTimer.Stop();
        _seekFraction = FractionAt(e.GetPosition(ProgressHit).X);
        RenderProgress();
        ProgressHit.CaptureMouse();
        e.Handled = true;
    }

    private void OnProgressMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_seeking)
            return;

        _seekFraction = FractionAt(e.GetPosition(ProgressHit).X);
        RenderProgress();
    }

    private void OnProgressMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_seeking)
            return;

        _seekFraction = FractionAt(e.GetPosition(ProgressHit).X);
        CommitSeek();
        if (ProgressHit.IsMouseCaptured)
            ProgressHit.ReleaseMouseCapture();
    }

    private void OnProgressLostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_seeking)
            return;

        // Capture can be lost without a mouse-up (the context menu, alt-tab).
        // Keep the preview position: the user still asked to go there.
        CommitSeek();
    }

    private double FractionAt(double x)
    {
        var width = ProgressHit.ActualWidth;
        return width <= 0 ? 0 : Math.Clamp(x / width, 0, 1);
    }

    private void CommitSeek()
    {
        _seeking = false;
        _progressTimer.Start();
        var target = PreviewPosition();
        // Move the widget right away; the player may take a moment to catch up.
        _progressPosition = target;
        _progressAnchorTimestamp = Stopwatch.GetTimestamp();
        RenderProgress();
        SeekRequested?.Invoke(this, target);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var text = CurrentLyricText();
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // The clipboard can be held open by another process; doing nothing
            // is better than crashing the widget.
            return;
        }

        CopyButton.Content = CopiedGlyph;
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    /// <summary>
    /// What the copy button puts on the clipboard: the line on screen, so the
    /// translation is only included while it is displayed.
    /// </summary>
    private string CurrentLyricText()
    {
        if (string.IsNullOrWhiteSpace(_renderedOriginal))
            return "";
        return string.IsNullOrWhiteSpace(_renderedTranslation)
            ? _renderedOriginal
            : _renderedOriginal + Environment.NewLine + _renderedTranslation;
    }

    /// <summary>
    /// Whether the translation line is shown. Kept in settings.json, so the
    /// choice survives a restart.
    /// </summary>
    internal void SetShowTranslation(bool enabled)
    {
        _showTranslation = enabled;
        UpdateTranslationButton();
        if (_lyrics is not null)
            UpdateLyrics(_lyrics, _lastLyricsPosition);
    }

    private void OnTranslationClick(object sender, RoutedEventArgs e)
    {
        SetShowTranslation(!_showTranslation);
        TranslationChanged?.Invoke(this, _showTranslation);
    }

    internal void SetStartWithWindows(bool enabled) => _startWithWindows = enabled;

    private void UpdateTranslationButton() =>
        TranslationButton.Foreground = _showTranslation ? AccentBrush : InactiveBrush;

    private void SetDrawerOpen(bool open)
    {
        if (!_hoverControlsMode || _drawerOpen == open)
            return;

        _drawerOpen = open;
        if (open)
        {
            _progressTimer.Start();
            // The bar is sized from the track width, which is only known after
            // the drawer has been laid out.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RenderProgress));
        }
        else
        {
            _progressTimer.Stop();
        }

        var easing = new CubicEase
        {
            EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn
        };
        ControlsReveal.BeginAnimation(
            FrameworkElement.HeightProperty,
            new DoubleAnimation(open ? DrawerRevealHeight : 0,
                TimeSpan.FromMilliseconds(open ? 180 : 140))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });

        // The height alone makes the panel slide over the lyrics as a hard edge;
        // fading it in at the same time softens that line.
        ControlsReveal.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 160 : 120))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });

        // The lyrics step aside as the drawer arrives, so the drawer itself needs
        // no background to hide them.
        MetadataPanel.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(open ? 0 : 1, TimeSpan.FromMilliseconds(open ? 120 : 140))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });
    }

    private void OnContentMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _drawerHideTimer.Stop();
        SetDrawerOpen(true);
    }

    private void OnContentMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (IsContextMenuOpen)
            return;

        // The text area and the drawer are siblings, so moving onto the open
        // drawer leaves the text area for a moment. The short delay turns that
        // into a hand-over instead of a flicker.
        _drawerHideTimer.Stop();
        _drawerHideTimer.Start();
    }

    private void OnDrawerHideTimerTick(object? sender, EventArgs e)
    {
        _drawerHideTimer.Stop();
        if (ContentArea.IsMouseOver || ControlsReveal.IsMouseOver || IsContextMenuOpen)
            return;
        SetDrawerOpen(false);
    }

    private WidgetDisplayMode GetEffectiveMode(double availableWidth)
    {
        var mode = _displayMode;
        // If the available width cannot fit the full layout at its natural size,
        // drop straight to cover-and-controls instead of squeezing the full layout.
        if ((mode is WidgetDisplayMode.FullDetails or WidgetDisplayMode.LyricsWithHoverControls) &&
            availableWidth < DefaultWidth)
            mode = WidgetDisplayMode.CoverAndControls;
        if (availableWidth < TransportLayoutWidth)
            mode = WidgetDisplayMode.CoverOnly;
        return mode;
    }

    private double GetEffectiveWidth(double availableWidth)
    {
        var preferred = GetEffectiveMode(availableWidth) switch
        {
            WidgetDisplayMode.CoverOnly => MinimumWidth,
            WidgetDisplayMode.CoverAndControls => TransportLayoutWidth,
            _ => DefaultWidth
        };
        return Math.Min(preferred, availableWidth);
    }

    // Command ids for the native context menu.
    private const int CmdDisplayModeBase = 100;
    private const int CmdLyricsLock = 200;
    private const int CmdLyricsRefetch = 201;
    private const int CmdPreferLocal = 202;
    private const int CmdOrderNeteaseFirst = 203;
    private const int CmdOrderLrclibFirst = 204;
    private const int CmdOffsetBase = 300;
    private const int CmdOffsetReset = 310;
    private const int CmdTaskbarBase = 400;
    private const int CmdStartWithWindows = 500;
    private const int CmdExit = 501;

    private static readonly int[] OffsetDeltas = [-200, -1000, 200, 1000];

    private bool _menuOpen;

    private bool IsContextMenuOpen => _menuOpen;

    private void OnRootRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Right)
            return;

        e.Handled = true;
        var position = PointToScreen(e.GetPosition(this));
        var command = ShowContextMenu((int)position.X, (int)position.Y);
        if (command != 0)
            DispatchMenuCommand(command);
    }

    /// <summary>
    /// Reopening a native menu after a command makes it flash, so repeated
    /// adjustment uses the wheel instead: Ctrl + wheel moves the lyric offset by
    /// 0.2 s per notch while the pointer is over the widget.
    /// </summary>
    private void OnRootMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
            return;

        e.Handled = true;
        var step = Math.Sign(e.Delta) * 200;
        ApplyOffset(_lyricsOffset + TimeSpan.FromMilliseconds(step));
    }

    /// <summary>
    /// Builds the menu from the current state and shows it. The menu is rebuilt
    /// every time instead of being kept in sync, which is what makes the native
    /// menu cheaper than the WPF one.
    /// </summary>
    private int ShowContextMenu(int screenX, int screenY)
    {
        using var menu = new NativeMenu();

        var layout = menu.AddSubmenu("Layout");
        AddDisplayMode(layout, "Full layout", WidgetDisplayMode.FullDetails);
        AddDisplayMode(layout, "Cover and controls", WidgetDisplayMode.CoverAndControls);
        AddDisplayMode(layout, "Cover only (hover to play/pause)", WidgetDisplayMode.CoverOnly);
        AddDisplayMode(layout, "Lyrics (controls on hover)", WidgetDisplayMode.LyricsWithHoverControls);

        var source = menu.AddSubmenu("Lyric source");
        source.AddItem(LockText(), CmdLyricsLock, _lyricsLocked, _lyricsSource is not null);
        source.AddSeparator();
        source.AddItem("Refresh lyrics (this song)", CmdLyricsRefetch);
        source.AddSeparator();
        source.AddItem("Local files first", CmdPreferLocal,
            _lyricsSourceSettings.PreferLocal, !_lyricsLocked);
        source.AddItem("NetEase → LRCLIB", CmdOrderNeteaseFirst,
            _lyricsSourceSettings.Order == LyricsOnlineOrder.NeteaseFirst, !_lyricsLocked);
        source.AddItem("LRCLIB → NetEase", CmdOrderLrclibFirst,
            _lyricsSourceSettings.Order == LyricsOnlineOrder.LrclibFirst, !_lyricsLocked);

        var timing = menu.AddSubmenu("Lyric timing (this song)");
        timing.AddItem(OffsetText(), 0, isEnabled: false);
        timing.AddSeparator();
        for (var i = 0; i < OffsetDeltas.Length; i++)
        {
            var delta = OffsetDeltas[i];
            var label = delta < 0 ? $"Earlier {delta / 1000.0:+0.0;-0.0} s"
                : $"Later {delta / 1000.0:+0.0;-0.0} s";
            timing.AddItem(label, CmdOffsetBase + i);
        }

        timing.AddSeparator();
        timing.AddItem("Reset (0.0 s)", CmdOffsetReset, isEnabled: _lyricsOffset != TimeSpan.Zero);

        if (_taskbarOptions.Count > 0)
        {
            var taskbar = menu.AddSubmenu("Taskbar");
            for (var i = 0; i < _taskbarOptions.Count; i++)
            {
                var option = _taskbarOptions[i];
                var label = option.IsPrimary ? $"{option.DisplayName} (primary)" : option.DisplayName;
                taskbar.AddItem(label, CmdTaskbarBase + i,
                    string.Equals(option.DeviceId, _selectedTaskbarDeviceId, StringComparison.Ordinal));
            }
        }

        menu.AddSeparator();
        menu.AddItem("Start with Windows", CmdStartWithWindows, _startWithWindows);
        menu.AddItem("Exit Taskbar Music Widget", CmdExit);

        // The menu must be owned by a window of this thread: TrackPopupMenuEx
        // posts its messages to the owner, and an owner in another process never
        // receives them, which cancels the menu immediately.
        var owner = _nativeHandle != 0 ? _nativeHandle : _taskbarHandle;
        _menuOpen = true;
        try
        {
            return NativeMenu.Show(menu.Handle, owner, screenX, screenY);
        }
        finally
        {
            _menuOpen = false;

            // The pointer may have left while the menu held the mouse, so the
            // hover states are restored from where it is now - the same thing the
            // WPF menu's Closed handler used to do.
            if (_hoverPlaybackOnlyMode)
            {
                HoverPlaybackButton.Visibility = Artwork.IsMouseOver
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (!ContentArea.IsMouseOver && !ControlsReveal.IsMouseOver)
                SetDrawerOpen(false);
        }
    }

    private void AddDisplayMode(NativeMenu menu, string text, WidgetDisplayMode mode) =>
        menu.AddItem(text, CmdDisplayModeBase + (int)mode, _displayMode == mode);

    private void DispatchMenuCommand(int command)
    {
        if (command >= CmdDisplayModeBase && command < CmdDisplayModeBase + 16)
        {
            var mode = (WidgetDisplayMode)(command - CmdDisplayModeBase);
            SetDisplayMode(mode);
            DisplayModeChanged?.Invoke(this, mode);
            return;
        }

        if (command >= CmdOffsetBase && command < CmdOffsetBase + OffsetDeltas.Length)
        {
            ApplyOffset(_lyricsOffset + TimeSpan.FromMilliseconds(OffsetDeltas[command - CmdOffsetBase]));
            return;
        }

        if (command >= CmdTaskbarBase && command < CmdTaskbarBase + _taskbarOptions.Count)
        {
            var deviceId = _taskbarOptions[command - CmdTaskbarBase].DeviceId;
            if (!string.Equals(deviceId, _selectedTaskbarDeviceId, StringComparison.Ordinal))
            {
                _selectedTaskbarDeviceId = deviceId;
                TaskbarSelectionChanged?.Invoke(this, deviceId);
            }

            return;
        }

        switch (command)
        {
            case CmdLyricsLock:
                LyricsLockChanged?.Invoke(this, !_lyricsLocked);
                break;
            case CmdLyricsRefetch:
                LyricsRefetchRequested?.Invoke(this, EventArgs.Empty);
                break;
            case CmdPreferLocal:
                ChangeSourceSettings(_lyricsSourceSettings with
                {
                    PreferLocal = !_lyricsSourceSettings.PreferLocal
                });
                break;
            case CmdOrderNeteaseFirst:
                ChangeSourceSettings(_lyricsSourceSettings with
                {
                    Order = LyricsOnlineOrder.NeteaseFirst
                });
                break;
            case CmdOrderLrclibFirst:
                ChangeSourceSettings(_lyricsSourceSettings with
                {
                    Order = LyricsOnlineOrder.LrclibFirst
                });
                break;
            case CmdOffsetReset:
                ApplyOffset(TimeSpan.Zero);
                break;
            case CmdStartWithWindows:
                _startWithWindows = !_startWithWindows;
                StartupChanged?.Invoke(this, _startWithWindows);
                break;
            case CmdExit:
                ExitWidget();
                break;
        }
    }

    private void ChangeSourceSettings(LyricsSourceSettings settings)
    {
        if (_lyricsSourceSettings == settings)
            return;

        _lyricsSourceSettings = settings;
        LyricsSourceSettingsChanged?.Invoke(this, settings);
    }

    private void ApplyOffset(TimeSpan offset)
    {
        SetLyricsOffset(offset);
        LyricsOffsetChanged?.Invoke(this, _lyricsOffset);
    }

    private string LockText() => _lyricsSource switch
    {
        null => "Lock source",
        var source => _lyricsLocked
            ? $"Locked to {SourceName(source)}"
            : $"Lock to {SourceName(source)}"
    };

    private static string SourceName(LyricsSource? source) => source switch
    {
        LyricsSource.Netease => "NetEase Music",
        LyricsSource.Lrclib => "LRCLIB",
        LyricsSource.Local => "Local file",
        _ => "-"
    };

    private string OffsetText()
    {
        var seconds = _lyricsOffset.TotalSeconds;
        var text = seconds == 0
            ? "0.0 s"
            : seconds.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " s";
        return $"Current: {text}";
    }

    /// <summary>How far the lyric offset can be pushed in either direction.</summary>
    private const int MaxOffsetMs = 15000;

    internal void SetLyricsSourceSettings(LyricsSourceSettings settings) => _lyricsSourceSettings = settings;

    internal void SetLyricsStatus(LyricsSource? source) => _lyricsSource = source;

    internal void SetLyricsLock(bool locked, LyricsSource? source)
    {
        _lyricsLocked = locked;
        if (source is not null)
            _lyricsSource = source;
    }

    internal void SetLyricsOffset(TimeSpan offset)
    {
        _lyricsOffset = TimeSpan.FromMilliseconds(
            Math.Clamp((int)offset.TotalMilliseconds, -MaxOffsetMs, MaxOffsetMs));
        if (_lyrics is not null)
            UpdateLyrics(_lyrics, _lastLyricsPosition);
    }

    internal void SetTaskbarOptions(IReadOnlyList<TaskbarOption> options, string selectedDeviceId)
    {
        _taskbarOptions = options;
        _selectedTaskbarDeviceId = selectedDeviceId;
    }

    internal void SetTaskbarSelection(string selectedDeviceId) => _selectedTaskbarDeviceId = selectedDeviceId;

    private void SetHoverPlaybackOnlyMode(bool enabled)
    {
        _hoverPlaybackOnlyMode = enabled;
        Artwork.Background = enabled
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromRgb(38, 53, 61));
        HoverPlaybackButton.Visibility = enabled && Artwork.IsMouseOver
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnArtworkMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_hoverPlaybackOnlyMode && !IsContextMenuOpen)
            HoverPlaybackButton.Visibility = Visibility.Visible;
    }

    /// <summary>Left click on the cover brings the player's window forward.</summary>
    private void OnArtworkMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
            return;

        PlayerActivateRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Middle click copies "title - artist".</summary>
    private void OnArtworkMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle)
            return;
        if (_currentTitle is "No media playing" || _currentTitle is "Start playback in a media app")
            return;

        var text = string.IsNullOrWhiteSpace(_currentArtist)
            ? _currentTitle
            : $"{_currentTitle} - {_currentArtist}";
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            return;
        }

        // A short dip in opacity is the only feedback that fits in 48 px.
        Artwork.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1, 0.5, TimeSpan.FromMilliseconds(90))
            {
                AutoReverse = true,
                FillBehavior = FillBehavior.Stop
            });
        e.Handled = true;
    }

    private void OnArtworkMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Opening the context menu steals mouse capture and raises MouseLeave even
        // though the cursor is still over the artwork. Keep the hover button stable
        // while the menu is open instead of toggling it for one frame.
        if (!IsContextMenuOpen)
            HoverPlaybackButton.Visibility = Visibility.Collapsed;
    }

    private void OnHoverPlaybackClick(object sender, RoutedEventArgs e) =>
        ToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _nativeHandle = handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(
            handle,
            GwlExStyle,
            new nint(style | WsExToolWindow | WsExNoActivate));
        AttachToTaskbar(handle, _taskbarHandle);
    }

    private bool AttachToTaskbar(nint handle, nint taskbarHandle)
    {
        if (handle == 0 || taskbarHandle == 0)
            return false;
        if (_parentTaskbarHandle == taskbarHandle && GetParent(handle) == taskbarHandle)
            return true;

        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        style = (style & ~WsPopup) | WsChild | WsClipSiblings;
        SetWindowLongPtr(handle, GwlStyle, new nint(style));
        SetParent(handle, taskbarHandle);
        if (GetParent(handle) != taskbarHandle)
            return false;

        _parentTaskbarHandle = taskbarHandle;
        return true;
    }

    internal void RaiseToTop()
    {
        var handle = _nativeHandle != 0 ? _nativeHandle : new WindowInteropHelper(this).Handle;
        if (handle != 0 && IsWindow(handle))
            SetWindowPos(
                handle,
                HwndTop,
                0,
                0,
                0,
                0,
                SwpNoActivate | SwpNoMove | SwpNoSize);
    }

    private void OnMetadataSizeChanged(object sender, SizeChangedEventArgs e) =>
        ScheduleMarqueeRefresh();

    private void ScheduleMarqueeRefresh() => Dispatcher.BeginInvoke(
        System.Windows.Threading.DispatcherPriority.Loaded,
        new Action(RefreshMarquees));

    private void RefreshMarquees()
    {
        if (!IsLoaded || MetadataPanel.Visibility != Visibility.Visible ||
            MetadataPanel.ActualWidth <= 0)
            return;

        UpdateMarquee(TitleCanvas, TitlePanel, TitlePrimary, TitleSpacer, TitleSecondary,
            TitleTranslation, _currentTitle);
        UpdateMarquee(ArtistCanvas, ArtistPanel, ArtistPrimary, ArtistSpacer, ArtistSecondary,
            ArtistTranslation, _currentArtist);
        RefreshLyricsMarquees();
    }

    private void ScheduleLyricsMarqueeRefresh(bool refreshOriginal, bool refreshTranslation) => Dispatcher.BeginInvoke(
        System.Windows.Threading.DispatcherPriority.Loaded,
        new Action(() => RefreshLyricsMarquees(refreshOriginal, refreshTranslation)));

    private void RefreshLyricsMarquees(bool refreshOriginal = true, bool refreshTranslation = true)
    {
        if (!IsLoaded || LyricsPanel.Visibility != Visibility.Visible) return;
        if (refreshOriginal)
            UpdateLyricsMarquee(LyricsOriginalCanvas, LyricsOriginalText, _renderedOriginal,
                true, _originalTimeRemaining);
        if (refreshTranslation && _renderedTranslation is not null)
            UpdateLyricsMarquee(LyricsTranslationCanvas, LyricsTranslationText,
                _renderedTranslation, true, _translationTimeRemaining);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ScheduleMarqueeRefresh();
        }
        else
        {
            StopMarqueeAnimations();
            _progressTimer.Stop();
        }
    }

    private void StopMarqueeAnimations()
    {
        StopTextMarquee(TitleTranslation);
        StopTextMarquee(ArtistTranslation);
        StopTextMarquee(LyricsOriginalText.RenderTransform as TranslateTransform);
        StopTextMarquee(LyricsTranslationText.RenderTransform as TranslateTransform);
    }

    /// <summary>
    /// Slides a new lyric line up into place instead of swapping it in. The
    /// X axis of the same transform belongs to the marquee, so only Y is used.
    /// </summary>
    private static void AnimateLineEnter(TextBlock textBlock)
    {
        if (textBlock.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            textBlock.RenderTransform = transform;
        }

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        textBlock.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private static void StopTextMarquee(TranslateTransform? transform)
    {
        if (transform is null)
            return;

        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
    }

    private static void UpdateLyricsMarquee(
        Canvas canvas,
        TextBlock textBlock,
        string text,
        bool center,
        TimeSpan? timeUntilNext)
    {
        var transform = textBlock.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            textBlock.RenderTransform = transform;
        }
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        textBlock.ClearValue(WidthProperty);
        var textWidth = MeasureTextWidth(textBlock, text);
        var viewportWidth = canvas.ActualWidth;
        if (viewportWidth <= 0) return;
        Canvas.SetLeft(textBlock, center && textWidth < viewportWidth
            ? Math.Max(0, (viewportWidth - textWidth) / 2)
            : 0);
        if (textWidth <= viewportWidth + 1) return;
        textBlock.Width = textWidth;
        var naturalDuration = textWidth / 70.0;
        var delay = timeUntilNext is { } remainingTime
            ? Math.Min(0.12, Math.Max(0, remainingTime.TotalSeconds * 0.1))
            : 0.8;
        var duration = timeUntilNext is { } remaining
            ? Math.Min(naturalDuration, Math.Max(0.12, remaining.TotalSeconds - delay - 0.03))
            : naturalDuration;
        var animation = new DoubleAnimation
        {
            From = 0,
            To = -(textWidth - viewportWidth + 24),
            Duration = TimeSpan.FromSeconds(Math.Max(0.2, duration)),
            BeginTime = TimeSpan.FromSeconds(delay),
            RepeatBehavior = timeUntilNext.HasValue
                ? new RepeatBehavior(1)
                : RepeatBehavior.Forever
        };
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private static void UpdateMarquee(
        Canvas viewport,
        StackPanel panel,
        TextBlock first,
        Border spacer,
        TextBlock second,
        TranslateTransform translation,
        string text)
    {
        translation.BeginAnimation(TranslateTransform.XProperty, null);
        translation.X = 0;
        first.Text = second.Text = text;
        spacer.Visibility = Visibility.Collapsed;
        second.Visibility = Visibility.Collapsed;
        first.ClearValue(WidthProperty);
        second.ClearValue(WidthProperty);

        var textWidth = MeasureTextWidth(first, text);
        var viewportWidth = viewport.ActualWidth;
        if (viewportWidth <= 0)
            return;

        // Centre the text while it fits; a line that has to scroll starts from
        // the left edge instead, or its beginning would never be visible.
        Canvas.SetLeft(panel, textWidth <= viewportWidth + 1
            ? Math.Max(0, (viewportWidth - textWidth) / 2)
            : 0);
        if (textWidth <= viewportWidth + 1)
            return;

        const double gap = 32;
        spacer.Visibility = Visibility.Visible;
        second.Visibility = Visibility.Visible;
        first.UpdateLayout();
        var distance = first.ActualWidth + gap;
        var animation = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = TimeSpan.FromSeconds(Math.Max(4, distance / 30)),
            BeginTime = TimeSpan.FromSeconds(1.5),
            RepeatBehavior = RepeatBehavior.Forever
        };

        translation.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private static double MeasureTextWidth(TextBlock textBlock, string text)
    {
        var typeface = new Typeface(
            textBlock.FontFamily,
            textBlock.FontStyle,
            textBlock.FontWeight,
            textBlock.FontStretch);
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            textBlock.FlowDirection,
            typeface,
            textBlock.FontSize,
            textBlock.Foreground,
            VisualTreeHelper.GetDpi(textBlock).PixelsPerDip);
        return formattedText.WidthIncludingTrailingWhitespace;
    }

    private static DpiScale GetDpiScale(nint hwnd)
    {
        var dpi = hwnd == 0 ? 0 : GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        return new DpiScale(scale, scale);
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) =>
        PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void OnToggleClick(object sender, RoutedEventArgs e) =>
        ToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnNextClick(object sender, RoutedEventArgs e) =>
        NextRequested?.Invoke(this, EventArgs.Empty);

    private void OnLyricsClick(object sender, RoutedEventArgs e)
    {
        SetLyricsMode(!_lyricsMode);
        LyricsModeChanged?.Invoke(this, _lyricsMode);
    }

    internal void ExitWidget() => Close();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint child, nint newParent);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int MapWindowPoints(
        nint from,
        nint to,
        ref NativeRect points,
        uint pointCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
