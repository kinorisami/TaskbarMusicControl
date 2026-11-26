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
using TaskbarMusicControls.Services;

namespace TaskbarMusicControls.Overlay;

internal enum WidgetDisplayMode
{
    FullDetails,
    CoverAndControls,
    CoverOnly
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
    private WidgetDisplayMode _displayMode = WidgetDisplayMode.FullDetails;
    private LyricsSourcePreference _lyricsSourcePreference = LyricsSourcePreference.Automatic;
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
    internal event Action<object?, LyricsSourcePreference>? LyricsSourceChanged;
    internal event Action<object?, TimeSpan>? LyricsOffsetChanged;
    internal event Action<object?, string>? TaskbarSelectionChanged;

    public bool HasValidNativeHandle =>
        _nativeHandle != 0 && IsWindow(_nativeHandle);

    public MusicOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += OnIsVisibleChanged;
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
        bool canGoNext)
    {
        ToggleButton.Content = isPlaying ? "Ⅱ" : "▶";
        ToggleButton.IsEnabled = canPlayPause;
        HoverPlaybackButton.IsEnabled = canPlayPause;
        PreviousButton.IsEnabled = canGoPrevious;
        NextButton.IsEnabled = canGoNext;
        ToggleButton.SetValue(
            AutomationProperties.NameProperty,
            isPlaying ? "Pause" : "Play");
    }

    internal void UpdateLyrics(LyricsDocument? lyrics, TimeSpan position)
    {
        _lyrics = lyrics;
        _lastLyricsPosition = position;
        var lyricPosition = position - _lyricsOffset;
        if (lyricPosition < TimeSpan.Zero)
            lyricPosition = TimeSpan.Zero;
        var current = lyrics?.At(lyricPosition);
        var originalFrame = current?.Original;
        var translationFrame = current?.Translation;
        var original = originalFrame?.Text;
        var translation = translationFrame?.Text;
        var hasTranslation = !string.IsNullOrWhiteSpace(translation);
        var hasLine = _lyricsMode && !string.IsNullOrWhiteSpace(original);
        LyricsPanel.Visibility = hasLine ? Visibility.Visible : Visibility.Collapsed;
        MetadataTextPanel.Visibility = hasLine ? Visibility.Collapsed : Visibility.Visible;
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
        UpdateDisplayModeMenuChecks();
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

        if (effectiveMode == WidgetDisplayMode.CoverOnly)
        {
            Artwork.Visibility = Visibility.Visible;
            ArtworkColumn.Width = new GridLength(1, GridUnitType.Star);
            Artwork.HorizontalAlignment = HorizontalAlignment.Center;
            MetadataPanel.Visibility = Visibility.Collapsed;
            MetadataColumn.Width = new GridLength(0);
            ControlsColumn.Width = new GridLength(0);
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
            ControlsColumn.Width = new GridLength(74);
            return;
        }

        LyricsButton.Visibility = Visibility.Visible;
        LyricsButton.Width = 24;
        MetadataPanel.Visibility = Visibility.Visible;
        MetadataColumn.Width = new GridLength(1, GridUnitType.Star);
        ControlsColumn.Width = new GridLength(112);
        ScheduleMarqueeRefresh();
    }

    private WidgetDisplayMode GetEffectiveMode(double availableWidth)
    {
        var mode = _displayMode;
        // If the available width cannot fit the full layout at its natural size,
        // drop straight to cover-and-controls instead of squeezing the full layout.
        if (mode == WidgetDisplayMode.FullDetails && availableWidth < DefaultWidth)
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

    private void UpdateDisplayModeMenuChecks()
    {
        foreach (var item in RootBorder.ContextMenu?.Items.OfType<MenuItem>() ?? [])
        {
            if (Enum.TryParse<WidgetDisplayMode>(item.Tag?.ToString(), out var mode))
                item.IsChecked = mode == _displayMode;
        }
    }

    private void OnDisplayModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item ||
            !Enum.TryParse<WidgetDisplayMode>(item.Tag?.ToString(), out var mode))
            return;

        SetDisplayMode(mode);
        DisplayModeChanged?.Invoke(this, mode);
    }

    internal void SetLyricsSource(LyricsSourcePreference preference)
    {
        _lyricsSourcePreference = preference;
        var sourceMenu = RootBorder.ContextMenu?.Items.OfType<MenuItem>()
            .FirstOrDefault(menu => menu.Header?.ToString() == "Lyric source");
        foreach (var item in sourceMenu?.Items.OfType<MenuItem>() ?? [])
        {
            item.IsCheckable = true;
            item.IsChecked = Enum.TryParse<LyricsSourcePreference>(item.Tag?.ToString(), out var value) &&
                value == preference;
        }
    }

    private void OnLyricsSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item ||
            !Enum.TryParse<LyricsSourcePreference>(item.Tag?.ToString(), out var preference))
            return;

        SetLyricsSource(preference);
        LyricsSourceChanged?.Invoke(this, preference);
    }

    internal void SetLyricsOffset(TimeSpan offset)
    {
        _lyricsOffset = offset;
        var offsetMenu = RootBorder.ContextMenu?.Items.OfType<MenuItem>()
            .FirstOrDefault(menu => menu.Header?.ToString() == "Lyric timing (this song)");
        foreach (var item in offsetMenu?.Items.OfType<MenuItem>() ?? [])
        {
            item.IsCheckable = true;
            item.IsChecked = int.TryParse(item.Tag?.ToString(), out var milliseconds) &&
                milliseconds == (int)offset.TotalMilliseconds;
        }
        if (_lyrics is not null)
            UpdateLyrics(_lyrics, _lastLyricsPosition);
    }

    private void OnLyricsOffsetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item ||
            !int.TryParse(item.Tag?.ToString(), out var milliseconds))
            return;

        var offset = TimeSpan.FromMilliseconds(milliseconds);
        SetLyricsOffset(offset);
        LyricsOffsetChanged?.Invoke(this, offset);
    }

    internal void SetTaskbarOptions(
        IReadOnlyList<TaskbarOption> options,
        string selectedDeviceId)
    {
        var optionsChanged = !TaskbarOptionsEqual(_taskbarOptions, options);
        _taskbarOptions = options;
        _selectedTaskbarDeviceId = selectedDeviceId;
        if (optionsChanged)
            RebuildTaskbarMenu();
        else
            UpdateTaskbarMenuChecks();
    }

    internal void SetTaskbarSelection(string selectedDeviceId)
    {
        _selectedTaskbarDeviceId = selectedDeviceId;
        UpdateTaskbarMenuChecks();
    }

    private void RebuildTaskbarMenu()
    {
        if (TaskbarMenu is null)
            return;

        TaskbarMenu.Visibility = _taskbarOptions.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        TaskbarMenu.Items.Clear();
        foreach (var option in _taskbarOptions)
        {
            var item = new MenuItem
            {
                Header = option.IsPrimary
                    ? $"{option.DisplayName} (primary)"
                    : option.DisplayName,
                Tag = option.DeviceId,
                IsCheckable = true,
                IsChecked = string.Equals(option.DeviceId, _selectedTaskbarDeviceId,
                    StringComparison.Ordinal)
            };
            item.Click += OnTaskbarClick;
            TaskbarMenu.Items.Add(item);
        }
    }

    private void OnTaskbarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string deviceId })
            return;

        _selectedTaskbarDeviceId = deviceId;
        UpdateTaskbarMenuChecks();
        TaskbarSelectionChanged?.Invoke(this, deviceId);
    }

    private void UpdateTaskbarMenuChecks()
    {
        foreach (var item in TaskbarMenu?.Items.OfType<MenuItem>() ?? [])
        {
            item.IsChecked = string.Equals(item.Tag?.ToString(), _selectedTaskbarDeviceId,
                StringComparison.Ordinal);
        }
    }

    private static bool TaskbarOptionsEqual(
        IReadOnlyList<TaskbarOption> left,
        IReadOnlyList<TaskbarOption> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].DeviceId, right[i].DeviceId, StringComparison.Ordinal) ||
                !string.Equals(left[i].DisplayName, right[i].DisplayName, StringComparison.Ordinal) ||
                left[i].IsPrimary != right[i].IsPrimary)
                return false;
        }
        return true;
    }

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

    private void OnArtworkMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Opening the context menu steals mouse capture and raises MouseLeave even
        // though the cursor is still over the artwork. Keep the hover button stable
        // while the menu is open instead of toggling it for one frame.
        if (!IsContextMenuOpen)
            HoverPlaybackButton.Visibility = Visibility.Collapsed;
    }

    private void OnContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (_hoverPlaybackOnlyMode)
            HoverPlaybackButton.Visibility = Artwork.IsMouseOver
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private bool IsContextMenuOpen => RootBorder.ContextMenu?.IsOpen == true;

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

        UpdateMarquee(TitleCanvas, TitlePrimary, TitleSpacer, TitleSecondary,
            TitleTranslation, _currentTitle);
        UpdateMarquee(ArtistCanvas, ArtistPrimary, ArtistSpacer, ArtistSecondary,
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
                _renderedTranslation is null, _originalTimeRemaining);
        if (refreshTranslation && _renderedTranslation is not null)
            UpdateLyricsMarquee(LyricsTranslationCanvas, LyricsTranslationText,
                _renderedTranslation, false, _translationTimeRemaining);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            ScheduleMarqueeRefresh();
        else
            StopMarqueeAnimations();
    }

    private void StopMarqueeAnimations()
    {
        StopTextMarquee(TitleTranslation);
        StopTextMarquee(ArtistTranslation);
        StopTextMarquee(LyricsOriginalText.RenderTransform as TranslateTransform);
        StopTextMarquee(LyricsTranslationText.RenderTransform as TranslateTransform);
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
        if (viewportWidth <= 0 || textWidth <= viewportWidth + 1)
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

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        // Close the context menu before closing the window; otherwise the popup
        // can stay visible for a few seconds while WPF tears down the app.
        if (RootBorder.ContextMenu is { IsOpen: true } menu)
            menu.IsOpen = false;

        Close();
    }

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
