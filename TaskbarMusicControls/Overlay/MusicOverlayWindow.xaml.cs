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

public partial class MusicOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = unchecked((long)0x80000000);
    private const long WsClipSiblings = 0x04000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTop = 0;
    private const double DefaultWidth = 350;
    private const double MinimumWidth = 108;

    private nint _taskbarHandle;
    private nint _parentTaskbarHandle;
    private nint _nativeHandle;
    private string _currentTitle = "No media playing";
    private string _currentArtist = "Start playback in a media app";
    private bool _lyricsMode;
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

    public bool HasValidNativeHandle =>
        _nativeHandle != 0 && IsWindow(_nativeHandle);

    public MusicOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
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

        if (artworkBytes is not { Length: > 0 })
        {
            ArtworkImage.Source = null;
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtworkGlyph.Visibility = Visibility.Visible;
            ScheduleMarqueeRefresh();
            return;
        }

        try
        {
            using var stream = new MemoryStream(artworkBytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            ArtworkImage.Source = image;
            ArtworkImage.Visibility = Visibility.Visible;
            ArtworkGlyph.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            ArtworkImage.Source = null;
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtworkGlyph.Visibility = Visibility.Visible;
        }

        ScheduleMarqueeRefresh();
    }

    public void UpdatePlaybackState(
        bool isPlaying,
        bool canPlayPause,
        bool canGoPrevious,
        bool canGoNext)
    {
        ToggleButton.Content = isPlaying ? "Ⅱ" : "▶";
        ToggleButton.IsEnabled = canPlayPause;
        PreviousButton.IsEnabled = canGoPrevious;
        NextButton.IsEnabled = canGoNext;
        ToggleButton.SetValue(
            AutomationProperties.NameProperty,
            isPlaying ? "Pause" : "Play");
    }

    internal void UpdateLyrics(LyricsDocument? lyrics, TimeSpan position)
    {
        _lyrics = lyrics;
        var lyricPosition = position + TimeSpan.FromSeconds(0);
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

    public bool PositionInAvailableArea(
        nint taskbarHandle,
        Rect taskbarBounds,
        Rect availableBounds)
    {
        if (availableBounds.IsEmpty || availableBounds.Width <= 0 ||
            taskbarBounds.Height <= 0)
            return false;

        var dpi = GetDpiScale(taskbarHandle);
        var availableWidth = availableBounds.Width;
        var width = Math.Min(DefaultWidth, availableWidth / dpi.DpiScaleX);
        if (width < MinimumWidth)
            return false;

        if (Math.Abs(Width - width) > 0.5)
        {
            Width = width;
            ApplyResponsiveLayout(width);
        }
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
                return false;

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

        return true;
    }

    public bool IsWithinAvailableArea(
        nint taskbarHandle,
        Rect taskbarBounds,
        Rect availableBounds)
    {
        _taskbarHandle = taskbarHandle;
        if (!IsVisible || _parentTaskbarHandle != taskbarHandle ||
            availableBounds.IsEmpty || taskbarBounds.IsEmpty)
            return false;

        var dpi = GetDpiScale(taskbarHandle);
        var left = Left * dpi.DpiScaleX;
        var top = Top * dpi.DpiScaleY;
        var right = left + Width * dpi.DpiScaleX;
        var bottom = top + Height * dpi.DpiScaleY;
        const double tolerance = 2;

        return left >= availableBounds.Left - tolerance &&
               right <= availableBounds.Right + tolerance &&
               top >= taskbarBounds.Top - tolerance &&
               bottom <= taskbarBounds.Bottom + tolerance;
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width < 220)
        {
            Artwork.Visibility = Visibility.Collapsed;
            ArtworkColumn.Width = new GridLength(0);
            MetadataPanel.Visibility = Visibility.Collapsed;
            MetadataColumn.Width = new GridLength(0);
            ControlsColumn.Width = new GridLength(1, GridUnitType.Star);

            var buttonWidth = Math.Max(24, (width - 8) / 4);
            LyricsButton.Width = buttonWidth;
            PreviousButton.Width = buttonWidth;
            ToggleButton.Width = buttonWidth;
            NextButton.Width = buttonWidth;
            return;
        }

        LyricsButton.Width = 24;
        PreviousButton.Width = 22;
        ToggleButton.Width = 30;
        NextButton.Width = 22;
        ControlsColumn.Width = new GridLength(112);
        MetadataPanel.Visibility = Visibility.Visible;
        MetadataColumn.Width = new GridLength(1, GridUnitType.Star);

        var showArtwork = width >= 300;
        Artwork.Visibility = showArtwork ? Visibility.Visible : Visibility.Collapsed;
        ArtworkColumn.Width = showArtwork ? new GridLength(50) : new GridLength(0);
        ScheduleMarqueeRefresh();
    }

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

    private void OnRootMouseRightButtonUp(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is { } menu)
        {
            menu.PlacementTarget = element;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

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
