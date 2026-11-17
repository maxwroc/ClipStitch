using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace ClipStitch;

public partial class MainWindow : Window
{
    private enum PlaybackMode { Stopped, Playing, Paused }

    private readonly Geometry playGlyph = Geometry.Parse("M 4,2 L 18,10 L 4,18 Z");
    private readonly Geometry pauseGlyph = Geometry.Parse("M 3,2 L 8,2 L 8,18 L 3,18 Z M 12,2 L 17,2 L 17,18 L 12,18 Z");
    private readonly ObservableCollection<VideoItem> videos = [];
    private readonly ObservableCollection<KeepRange> sections = [];
    private readonly List<(double Start, double End, System.Windows.Media.Imaging.BitmapImage Image)> thumbnailImages = [];
    private readonly LibVLC libVlc;
    private readonly LibVLCSharp.Shared.MediaPlayer player;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer thumbnailRefreshTimer;
    private CancellationTokenSource? folderCancellation;
    private CancellationTokenSource? thumbnailCancellation;
    private CancellationTokenSource? exportCancellation;
    private string? sourceFolder;
    private List<Segment>? preview;
    private int previewIndex;
    private PlaybackMode playbackMode;
    private double position;
    private int selectedSectionIndex = -1;
    private long playbackRevision;
    private bool pendingSeek;
    private bool seekIssued;
    private long seekIssuedAt;
    private bool selectingForPreview;
    private long seekTarget;
    private bool draggingMarker;
    private bool draggingStartMarker;
    private int coverHideTicks = -1;
    private bool draggingScrollbar;
    private bool inSizeMove;
    private bool resizeRefreshPending;
    private DateTime lastManualScroll = DateTime.MinValue;
    private double autoScrollTarget = -1;
    private double timelineZoom = 1;
    private Border? selectedRangeBorder;
    private VideoItem? loadedVideo;
    private VideoItem? Current => VideoList.SelectedItem as VideoItem;
    private KeepRange? SelectedSection => selectedSectionIndex >= 0 && selectedSectionIndex < sections.Count
        ? sections[selectedSectionIndex] : null;
    private double TotalDuration => videos.Sum(v => v.Duration);

    // Fixed margin at both timeline ends so a selection handle placed outside a section edge stays visible.
    private const double HandleWidth = 14;
    private const double PlayheadHeaderHeight = 12;
    private double TimelineContentHeight => Math.Max(0, Timeline.ActualHeight - PlayheadHeaderHeight);
    private double ContentWidth => Math.Max(0, Timeline.ActualWidth - 2 * HandleWidth);
    private double TimeToX(double time) => HandleWidth + (TotalDuration <= 0 ? 0 : time / TotalDuration * ContentWidth);
    private double DurationToWidth(double duration) => TotalDuration <= 0 ? 0 : Math.Max(0, duration / TotalDuration * ContentWidth);
    private double XToTime(double x) => ContentWidth <= 0 ? 0 : Math.Clamp((x - HandleWidth) / ContentWidth * TotalDuration, 0, TotalDuration);
    private double OffsetOf(VideoItem video)
    {
        double offset = 0;
        foreach (var item in videos) { if (item == video) break; offset += item.Duration; }
        return offset;
    }

    public MainWindow()
    {
        InitializeComponent();
        Core.Initialize();
        libVlc = new LibVLC("--no-video-title-show");
        player = new LibVLCSharp.Shared.MediaPlayer(libVlc);
        VideoView.MediaPlayer = player;
        VideoList.ItemsSource = videos;
        VolumePopup.Opened += (_, _) => VolumeSlider.Focus();
        Deactivated += (_, _) => VolumePopup.IsOpen = false;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += Timer_Tick;
        timer.Start();
        thumbnailRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        thumbnailRefreshTimer.Tick += (_, _) =>
        {
            thumbnailRefreshTimer.Stop();
            if (inSizeMove)
            {
                resizeRefreshPending = true;
                return;
            }
            if (folderCancellation is not null && videos.Count > 0)
                _ = LoadTimelineThumbnailsAsync(folderCancellation.Token);
        };
        SourceInitialized += (_, _) =>
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WindowMessageHook);
        player.EncounteredError += Player_EncounteredError;
        player.EndReached += Player_EndReached;
        Closed += (_, _) =>
        {
            folderCancellation?.Cancel();
            thumbnailCancellation?.Cancel();
            exportCancellation?.Cancel();
            timer.Stop();
            thumbnailRefreshTimer.Stop();
            player.Stop();
            VideoView.MediaPlayer = null;
            player.Dispose();
            libVlc.Dispose();
        };
        UpdateControls();
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder of videos" };
        if (dialog.ShowDialog(this) != true) return;
        folderCancellation?.Cancel();
        thumbnailCancellation?.Cancel();
        thumbnailRefreshTimer.Stop();
        resizeRefreshPending = false;
        ThumbnailLoadingBadge.Visibility = Visibility.Collapsed;
        StopPlayback();
        var cancellation = new CancellationTokenSource();
        folderCancellation = cancellation;
        videos.Clear();
        sections.Clear();
        selectedSectionIndex = -1;
        SetPosition(0);
        thumbnailImages.Clear();
        timelineZoom = 1;
        UpdateTimelineWidth();
        TimelineScroll.ScrollToHorizontalOffset(0);
        Thumbnails.Children.Clear();
        DrawRanges();
        FolderLabel.Text = dialog.FolderName;
        sourceFolder = dialog.FolderName;
        StatusLabel.Text = "Reading video information…";
        try
        {
            var files = Directory.EnumerateFiles(dialog.FolderName)
                .Where(MediaTools.IsVideoCandidate)
                .OrderBy(f => f, new NaturalPathComparer()).ToArray();
            var failures = new List<string>();
            foreach (var file in files)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var video = string.Equals(System.IO.Path.GetExtension(file), ".dat", StringComparison.OrdinalIgnoreCase)
                        ? await MediaTools.ProbeIfVideoAsync(file, cancellation.Token)
                        : await MediaTools.ProbeAsync(file, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (video is not null) videos.Add(video);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add($"{System.IO.Path.GetFileName(file)}: {ex.Message}"); }
            }
            if (videos.Count > 0)
            {
                sections.Add(new KeepRange(0, TotalDuration));
                VideoList.SelectedIndex = 0;
                ScheduleThumbnailRefresh();
            }
            StatusLabel.Text = $"Loaded {videos.Count} video(s)." + (failures.Count > 0 ? $" {failures.Count} could not be opened." : "");
            if (failures.Count > 0) MessageBox.Show(this, string.Join("\n\n", failures), "Some videos could not be opened", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("Could not open folder", ex); }
        finally { UpdateControls(); }
    }

    private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (preview is not null && !selectingForPreview) InvalidatePreview();
        if (!selectingForPreview && Current is { } video)
        {
            PreviewMarker(OffsetOf(video), true);
        }
    }

    private async Task LoadTimelineThumbnailsAsync(CancellationToken folderToken)
    {
        thumbnailCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(folderToken);
        thumbnailCancellation = cancellation;
        try
        {
            double width = ContentWidth;
            if (width <= 0 || TotalDuration <= 0) return;
            double startTime = XToTime(TimelineScroll.HorizontalOffset);
            double endTime = XToTime(TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth);
            if (endTime <= startTime) return;
            int count = Math.Clamp((int)Math.Ceiling(TimelineScroll.ViewportWidth / 100), 1, 24);
            ShowThumbnailLoading(0, count);
            var fresh = new List<(double Start, double End, System.Windows.Media.Imaging.BitmapImage Image)>();
            for (int i = 0; i < count; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                double start = startTime + (endTime - startTime) * i / count;
                double end = startTime + (endTime - startTime) * (i + 1) / count;
                var (video, local) = Locate((start + end) / 2);
                var image = await MediaTools.ThumbnailAsync(video, local, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                fresh.Add((start, end, image));
                ShowThumbnailLoading(i + 1, count);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            thumbnailImages.Clear();
            thumbnailImages.AddRange(fresh);
            DrawThumbnails();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusLabel.Text = $"Thumbnail generation failed: {ex.Message}"; }
        finally
        {
            // Keep the badge while a newer refresh is pending or running.
            if (thumbnailCancellation == cancellation && !thumbnailRefreshTimer.IsEnabled && !resizeRefreshPending)
                ThumbnailLoadingBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void Include_Click(object sender, RoutedEventArgs e)
    {
        InvalidatePreview();
        UpdateControls();
    }

    private void SetPosition(double time)
    {
        position = Math.Clamp(time, 0, TotalDuration);
        int index = draggingMarker ? selectedSectionIndex : TimelineSections.IndexAt(sections, position);
        if (index != selectedSectionIndex)
        {
            selectedSectionIndex = index;
            DrawRanges();
        }
        DrawPlayhead();
        PlaybackLabel.Text = $"{TimeText.Format(position)} / {TimeText.Format(TotalDuration)}";
        UpdateControls();
    }

    private void UpdateControls()
    {
        bool playable = sections.Count > 0 && videos.Any(v => v.Included);
        PlayPauseButton.IsEnabled = playable || playbackMode == PlaybackMode.Paused;
        StopButton.IsEnabled = playable;
        ExportButton.IsEnabled = playable && exportCancellation is null;
        bool playing = playbackMode == PlaybackMode.Playing;
        PlayPauseIcon.Data = playing ? pauseGlyph : playGlyph;
        PlayPauseButton.ToolTip = playing ? "Pause playback" : playbackMode == PlaybackMode.Paused
            ? "Resume playback from the paused position" : "Play from the first selection";
        System.Windows.Automation.AutomationProperties.SetName(PlayPauseButton, playing ? "Pause" : "Play");
        var section = SelectedSection;
        bool active = section is not null && !draggingMarker;
        PlaySectionButton.IsEnabled = active;
        PreviousSectionButton.IsEnabled = JoinPreviousButton.IsEnabled = active && selectedSectionIndex > 0;
        NextSectionButton.IsEnabled = JoinNextButton.IsEnabled = active && selectedSectionIndex + 1 < sections.Count;
        SplitButton.IsEnabled = active && position > section!.Start && position < section.End;
        RemoveSectionButton.IsEnabled = active && sections.Count > 1;
    }

    private void AddRange_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSection is not { } range || position <= range.Start || position >= range.End) return;
        try
        {
            var split = TimelineSections.Split(range, position);
            InvalidatePreview();
            int index = selectedSectionIndex;
            sections[index] = split.Left;
            sections.Insert(index + 1, split.Right);
            SetPosition(position);
            DrawRanges();
        }
        catch (InvalidOperationException ex) { ShowError("Cannot split section", ex); }
    }

    private void RemoveRange_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSection is null || sections.Count <= 1) return;
        sections.RemoveAt(selectedSectionIndex);
        if (preview is not null) RebuildPreviewAtPosition();
        SetPosition(position);
        DrawRanges();
    }

    private void JoinPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSection is not null) JoinSections(selectedSectionIndex - 1);
    }
    private void JoinNext_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSection is not null) JoinSections(selectedSectionIndex);
    }
    private void JoinSections(int first)
    {
        if (first < 0 || first + 1 >= sections.Count) return;
        InvalidatePreview();
        var joined = TimelineSections.Join(sections[first], sections[first + 1]);
        sections.RemoveAt(first + 1);
        sections[first] = joined;
        SetPosition(position);
        DrawRanges();
    }

    private void DrawThumbnails()
    {
        Thumbnails.Children.Clear();
        if (TotalDuration <= 0) return;
        var content = new Rectangle { Width = ContentWidth, Height = TimelineContentHeight, Fill = new SolidColorBrush(Color.FromRgb(24, 45, 67)) };
        Canvas.SetLeft(content, HandleWidth);
        Thumbnails.Children.Add(content);
        foreach (var item in thumbnailImages)
        {
            var image = new Image { Source = item.Image, Stretch = Stretch.UniformToFill,
                Width = DurationToWidth(item.End - item.Start), Height = TimelineContentHeight };
            Canvas.SetLeft(image, TimeToX(item.Start));
            Thumbnails.Children.Add(image);
        }
    }

    private void DrawRanges()
    {
        if (RangeOverlay is null || draggingMarker) return;
        DrawGaps();
        RangeOverlay.Children.Clear();
        selectedRangeBorder = null;
        if (TotalDuration <= 0) return;
        foreach (var range in sections)
        {
            bool selected = ReferenceEquals(range, SelectedSection);
            var rect = new Border { Width = DurationToWidth(range.End - range.Start),
                Height = TimelineContentHeight, Background = new SolidColorBrush(selected ? Color.FromArgb(48, 37, 220, 143) : Color.FromArgb(30, 99, 157, 177)),
                BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(161, 255, 115) : Color.FromRgb(143, 244, 191)),
                // The selected section's sides are formed by its handles, which sit just outside the section edges.
                BorderThickness = selected ? new Thickness(0, 3, 0, 3) : new Thickness(3),
                CornerRadius = new CornerRadius(selected ? 0 : 5), Tag = range,
                IsHitTestVisible = false };
            if (selected) selectedRangeBorder = rect;
            Canvas.SetLeft(rect, TimeToX(range.Start));
            RangeOverlay.Children.Add(rect);
        }
        double offset = 0;
        foreach (var video in videos)
        {
            if (offset > 0)
            {
                var line = new Rectangle { Width = 1, Height = TimelineContentHeight, Fill = Brushes.SteelBlue, IsHitTestVisible = false, Tag = offset };
                Canvas.SetLeft(line, TimeToX(offset));
                RangeOverlay.Children.Add(line);
            }
            offset += video.Duration;
        }
        if (SelectedSection is { } selectedRange)
        {
            AddMarker(selectedRange, true);
            AddMarker(selectedRange, false);
        }
        DrawPlayhead();
    }

    private void DrawGaps()
    {
        GapOverlay.Children.Clear();
        foreach (var gap in TimelineSections.Gaps(sections, TotalDuration))
        {
            var shade = new Rectangle { Width = DurationToWidth(gap.End - gap.Start), Height = TimelineContentHeight,
                Fill = new SolidColorBrush(Color.FromArgb(38, 0, 0, 0)), IsHitTestVisible = false };
            Canvas.SetLeft(shade, TimeToX(gap.Start));
            GapOverlay.Children.Add(shade);
        }
    }

    private void AddMarker(KeepRange range, bool start)
    {
        var thumb = new Thumb { Width = HandleWidth, Height = TimelineContentHeight, Cursor = Cursors.SizeWE,
            Background = new SolidColorBrush(Color.FromRgb(161, 255, 115)), Tag = start,
            ToolTip = $"{(start ? "Start" : "End")}: {TimeText.Format(start ? range.Start : range.End)}" };
        System.Windows.Automation.AutomationProperties.SetName(thumb, start ? "Drag section start" : "Drag section end");
        var template = new ControlTemplate(typeof(Thumb));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, thumb.Background);
        border.SetValue(Border.CornerRadiusProperty, start ? new CornerRadius(5, 0, 0, 5) : new CornerRadius(0, 5, 5, 0));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(TextBlock.TextProperty, start ? "◀" : "▶");
        label.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(12, 48, 45)));
        label.SetValue(TextBlock.FontSizeProperty, 10d);
        label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        border.AppendChild(label);
        template.VisualTree = border;
        thumb.Template = template;
        Canvas.SetLeft(thumb, HandleLeft(start ? range.Start : range.End, start));
        thumb.DragStarted += (_, _) =>
        {
            draggingMarker = true;
            draggingStartMarker = start;
            InvalidatePreview();
            PreviewMarker(start ? sections[selectedSectionIndex].Start : sections[selectedSectionIndex].End, start);
        };
        thumb.DragDelta += (_, args) =>
        {
            int index = selectedSectionIndex;
            if (index < 0 || ContentWidth <= 0) return;
            var current = sections[index];
            double delta = args.HorizontalChange / ContentWidth * TotalDuration;
            double minimum = index == 0 ? 0 : sections[index - 1].End;
            double maximum = index == sections.Count - 1 ? TotalDuration : sections[index + 1].Start;
            double minDuration = Math.Min(0.001, current.End - current.Start);
            var updated = start
                ? new KeepRange(Math.Clamp(current.Start + delta, minimum, current.End - minDuration), current.End)
                : new KeepRange(current.Start, Math.Clamp(current.End + delta, current.Start + minDuration, maximum));
            sections[index] = updated;
            if (selectedRangeBorder is not null)
            {
                selectedRangeBorder.Tag = updated;
                Canvas.SetLeft(selectedRangeBorder, TimeToX(updated.Start));
                selectedRangeBorder.Width = DurationToWidth(updated.End - updated.Start);
            }
            Canvas.SetLeft(thumb, HandleLeft(start ? updated.Start : updated.End, start));
            thumb.ToolTip = $"{(start ? "Start" : "End")}: {TimeText.Format(start ? updated.Start : updated.End)}";
            DrawGaps();
            PreviewMarker(start ? updated.Start : updated.End, start);
        };
        thumb.DragCompleted += (_, args) =>
        {
            var section = SelectedSection;
            draggingMarker = false;
            SetPosition(position);
            DrawRanges();
            if (!args.Canceled && section is not null)
                BeginBoundaryPreview(section, start);
        };
        RangeOverlay.Children.Add(thumb);
    }

    // Start handles sit left of the boundary and end handles right of it, so the inner handle edge is the exact cut point.
    private double HandleLeft(double boundary, bool start) => start ? TimeToX(boundary) - HandleWidth : TimeToX(boundary);

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawThumbnails();
        if (draggingMarker) RepositionOverlay();
        else DrawRanges();
        DrawPlayhead();
        if (e.WidthChanged && thumbnailRefreshTimer is not null && videos.Count > 0 && !draggingScrollbar)
            ScheduleThumbnailRefresh();
    }

    private void RepositionOverlay()
    {
        if (TotalDuration <= 0) return;
        DrawGaps();
        foreach (UIElement element in RangeOverlay.Children)
        {
            if (element is Border { Tag: KeepRange range } sectionBorder)
            {
                Canvas.SetLeft(sectionBorder, TimeToX(range.Start));
                sectionBorder.Width = DurationToWidth(range.End - range.Start);
            }
            else if (element is Rectangle { Tag: double offset } line)
                Canvas.SetLeft(line, TimeToX(offset));
            else if (element is Thumb { Tag: bool start } handle && SelectedSection is { } selected)
                Canvas.SetLeft(handle, HandleLeft(start ? selected.Start : selected.End, start));
        }
    }
    private void TimelineScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTimelineWidth();
        if (e.WidthChanged && thumbnailRefreshTimer is not null && videos.Count > 0 && !draggingScrollbar)
            ScheduleThumbnailRefresh();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmEnterSizeMove = 0x0231, WmExitSizeMove = 0x0232;
        if (message == WmEnterSizeMove) inSizeMove = true;
        else if (message == WmExitSizeMove)
        {
            inSizeMove = false;
            if (resizeRefreshPending)
            {
                resizeRefreshPending = false;
                if (videos.Count > 0 && !draggingScrollbar) ScheduleThumbnailRefresh();
            }
        }
        return IntPtr.Zero;
    }
    private void UpdateTimelineWidth()
    {
        if (TimelineScroll is null || Timeline is null) return;
        double viewport = TimelineScroll.ViewportWidth;
        if (viewport > 2 * HandleWidth && double.IsFinite(viewport))
            Timeline.Width = (viewport - 2 * HandleWidth) * timelineZoom + 2 * HandleWidth;
    }
    private void TimelineScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (thumbnailRefreshTimer is null || videos.Count == 0) return;
        if (e.ViewportWidthChange != 0) UpdateTimelineWidth();
        if (e.HorizontalChange != 0 && Math.Abs(TimelineScroll.HorizontalOffset - autoScrollTarget) > 1)
            lastManualScroll = DateTime.UtcNow;
        // Wait until the scrollbar is released so reloading thumbnails does not interrupt the drag.
        if (draggingScrollbar) return;
        if (e.HorizontalChange != 0 || e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0)
            ScheduleThumbnailRefresh();
    }

    private void TimelineScroll_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (e.OriginalSource is Thumb { TemplatedParent: ScrollBar })
        {
            draggingScrollbar = true;
            thumbnailRefreshTimer.Stop();
            thumbnailCancellation?.Cancel();
            resizeRefreshPending = false;
            ThumbnailLoadingBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void TimelineScroll_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.OriginalSource is Thumb { TemplatedParent: ScrollBar } && draggingScrollbar)
        {
            draggingScrollbar = false;
            lastManualScroll = DateTime.UtcNow;
            if (videos.Count > 0) ScheduleThumbnailRefresh();
        }
    }

    private void ThumbnailLoading_MouseWheel(object sender, MouseWheelEventArgs e) => TimelineScroll_MouseWheel(sender, e);

    private void ScheduleThumbnailRefresh()
    {
        thumbnailRefreshTimer.Stop();
        thumbnailCancellation?.Cancel();
        if (inSizeMove) resizeRefreshPending = true;
        else
        {
            resizeRefreshPending = false;
            thumbnailRefreshTimer.Start();
        }
        ShowThumbnailLoading(null, 0);
    }

    private void ShowThumbnailLoading(int? done, int total)
    {
        ThumbnailLoadingBadge.Visibility = Visibility.Visible;
        ThumbnailLoadingProgress.IsIndeterminate = done is null;
        if (done is { } count)
        {
            ThumbnailLoadingProgress.Maximum = total;
            ThumbnailLoadingProgress.Value = count;
            ThumbnailLoadingText.Text = $"Loading previews {count}/{total}";
        }
        else ThumbnailLoadingText.Text = "Loading previews…";
    }
    private void TimelineScroll_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TotalDuration <= 0 || TimelineScroll.ViewportWidth <= 0) return;
        double anchor = e.GetPosition(TimelineScroll).X;
        if (draggingMarker && SelectedSection is { } range)
            anchor = TimeToX(draggingStartMarker ? range.Start : range.End) - TimelineScroll.HorizontalOffset;
        double contentViewport = TimelineScroll.ViewportWidth - 2 * HandleWidth;
        if (contentViewport <= 0) return;
        // Zoom in content coordinates, excluding the fixed handle margins at both ends.
        var zoom = TimelineZoom.AtPointer(timelineZoom, TimelineScroll.HorizontalOffset,
            contentViewport, anchor - HandleWidth, e.Delta);
        e.Handled = true;
        // Already at the minimum or maximum zoom: nothing changes, so keep the current thumbnails without a reload.
        if (Math.Abs(zoom.Zoom - timelineZoom) < 1e-9) return;
        timelineZoom = zoom.Zoom;
        UpdateTimelineWidth();
        TimelineScroll.UpdateLayout();
        TimelineScroll.ScrollToHorizontalOffset(zoom.Offset);
        ScheduleThumbnailRefresh();
    }
    private void DrawPlayhead()
    {
        if (PlayheadOverlay is null) return;
        PlayheadOverlay.Children.Clear();
        if (TotalDuration <= 0 || Timeline.ActualWidth <= 0) return;
        double x = Math.Clamp(TimeToX(position), 0, Timeline.ActualWidth);
        var line = new Rectangle { Width = 2, Height = Timeline.ActualHeight - 8, Fill = Brushes.White };
        Canvas.SetTop(line, 8);
        Canvas.SetLeft(line, Math.Clamp(x - 1, 0, Math.Max(0, Timeline.ActualWidth - 2)));
        PlayheadOverlay.Children.Add(line);
        var tip = new Polygon { Points = new PointCollection { new(0, 0), new(12, 0), new(6, 8) }, Fill = Brushes.White };
        Canvas.SetLeft(tip, Math.Clamp(x - 6, 0, Math.Max(0, Timeline.ActualWidth - 12)));
        PlayheadOverlay.Children.Add(tip);
    }
    private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            for (DependencyObject? node = source; node is not null && node != Timeline; node = VisualTreeHelper.GetParent(node))
                if (node is Thumb) return;
        }
        if (draggingMarker || TotalDuration <= 0 || ContentWidth <= 0) return;
        SeekTo(XToTime(e.GetPosition(Timeline).X));
    }

    private void SeekTo(double time)
    {
        if (TotalDuration <= 0) return;
        try
        {
            SetPosition(time);
            RebuildPreviewAtPosition();
            var (video, local) = Locate(position);
            SelectVideo(video);
            StartMedia(video, local);
        }
        catch (Exception ex) { ShowError("Cannot preview from this position", ex); }
    }

    private (VideoItem Video, double Local) Locate(double time)
    {
        double offset = 0;
        foreach (var video in videos)
        {
            if (time < offset + video.Duration || video == videos[^1])
                return (video, Math.Clamp(time - offset, 0, Math.Max(0, video.Duration - 0.001)));
            offset += video.Duration;
        }
        throw new InvalidOperationException("No videos are loaded.");
    }
    private void SelectVideo(VideoItem video)
    {
        selectingForPreview = true;
        try { VideoList.SelectedItem = video; }
        finally { selectingForPreview = false; }
    }

    private void RebuildPreviewAtPosition()
    {
        double start = Math.Min(position, Math.Max(0, TotalDuration - 0.001));
        var ranges = TimelineSections.PlayFrom(sections, start, TotalDuration);
        preview = TimelineSections.Build(videos, ranges);
        var (video, local) = Locate(start);
        // An explicit seek still shows the requested source, even if it is excluded from automatic playback.
        if (preview.Count == 0 || preview[0].Video != video)
            preview.Insert(0, new Segment(video, local, Math.Min(video.Duration, ranges[0].End - OffsetOf(video))));
        previewIndex = 0;
    }

    // Moves the playhead and scrolls the zoomed timeline so a newly started section is visible.
    private void JumpTimelineTo(double time)
    {
        SetPosition(time);
        if (draggingScrollbar || TotalDuration <= 0 || Timeline.ActualWidth <= 0) return;
        double x = TimeToX(time);
        if (x < TimelineScroll.HorizontalOffset || x > TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth * 0.8)
        {
            autoScrollTarget = Math.Max(0, x - TimelineScroll.ViewportWidth * 0.2);
            lastManualScroll = DateTime.MinValue;
            TimelineScroll.ScrollToHorizontalOffset(autoScrollTarget);
        }
    }

    private void PreviewMarker(double boundary, bool start)
    {
        if (TotalDuration <= 0) return;
        SetPosition(boundary);
        double sample = start ? boundary : Math.Max(0, boundary - 0.001);
        var (video, local) = Locate(sample);
        SelectVideo(video);
        preview = null;
        playbackMode = PlaybackMode.Stopped;
        StartMedia(video, local);
        UpdateControls();
    }

    private void PlayClip_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSection is not null) BeginPreview(selectedSectionIndex);
    }

    private void PreviousSection_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSection is not null && selectedSectionIndex > 0) BeginPreview(selectedSectionIndex - 1);
    }

    private void NextSection_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSection is not null && selectedSectionIndex + 1 < sections.Count) BeginPreview(selectedSectionIndex + 1);
    }

    private void BeginPreview(int sectionIndex)
    {
        try
        {
            StartPreview(TimelineSections.Build(videos, sections.Skip(sectionIndex)));
        }
        catch (Exception ex) { ShowError("Cannot preview", ex); }
    }

    private void BeginBoundaryPreview(KeepRange section, bool startMarker)
    {
        try
        {
            var range = TimelineSections.BoundaryPreview(section, startMarker);
            StartPreview(TimelineSections.Build(videos, [range, .. sections.Where(s => s.Start >= section.End)]));
        }
        catch (Exception ex) { ShowError("Cannot preview section", ex); }
    }

    private void StartPreview(List<Segment> segments)
    {
        if (segments.Count == 0)
        {
            StopPlayback();
            StatusLabel.Text = "No included video in this preview range.";
            return;
        }
        preview = segments;
        previewIndex = 0;
        playbackMode = PlaybackMode.Playing;
        UpdateControls();
        StartPreviewSegment();
    }

    private void StartPreviewSegment()
    {
        if (preview is null || previewIndex >= preview.Count) { StopPlayback(); return; }
        var segment = preview[previewIndex];
        SelectVideo(segment.Video);
        double globalStart = OffsetOf(segment.Video) + segment.Start;
        JumpTimelineTo(globalStart);
        StartMedia(segment.Video, segment.Start);
    }

    private void StartMedia(VideoItem video, double seconds)
    {
        playbackRevision++;
        player.Mute = playbackMode != PlaybackMode.Playing;
        pendingSeek = true;
        seekIssued = false;
        seekIssuedAt = Environment.TickCount64;
        seekTarget = (long)Math.Round(seconds * 1000);
        if (loadedVideo == video && player.IsSeekable &&
            (player.IsPlaying || player.State == VLCState.Paused))
        {
            // Briefly resume to render seeks made while showing a still frame.
            player.SetPause(false);
            return;
        }
        CoverWithCurrentFrame();
        player.Stop();
        player.Mute = true;
        using var media = new LibVLCSharp.Shared.Media(libVlc, new Uri(video.Path));
        // Initialize the decoder before seeking; some streams only provide video headers at the start.
        if (!player.Play(media))
        {
            StopPlayback();
            StatusLabel.Text = $"Could not start playback of {video.Name}.";
            return;
        }
        loadedVideo = video;
        player.Volume = (int)VolumeSlider.Value;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (coverHideTicks > 0 && !pendingSeek && player.State is VLCState.Playing or VLCState.Paused && --coverHideTicks == 0)
        {
            coverHideTicks = -1;
            FrameCover.Visibility = Visibility.Collapsed;
        }
        if (playbackMode == PlaybackMode.Playing && player.State == VLCState.Paused)
        {
            player.SetPause(false);
            return;
        }
        if (pendingSeek && player.IsPlaying && (seekTarget == 0 || player.IsSeekable))
        {
            player.Volume = (int)VolumeSlider.Value;
            player.Mute = playbackMode != PlaybackMode.Playing || !seekIssued;
            if (!seekIssued)
            {
                if (player.VoutCount == 0 || (seekTarget > 0 && player.Time <= 0)) return;
                seekIssuedAt = Environment.TickCount64;
                player.Time = seekTarget;
                seekIssued = true;
                return;
            }
            long mediaTime = player.Time;
            if (playbackMode != PlaybackMode.Playing)
            {
                // Still frames need the target; running playback must tolerate imprecise native seeks.
                long elapsed = Math.Max(0, Environment.TickCount64 - seekIssuedAt);
                if (player.VoutCount == 0 || mediaTime < seekTarget - 500 || mediaTime > seekTarget + elapsed + 500) return;
            }
            pendingSeek = false;
            if (FrameCover.Visibility == Visibility.Visible) coverHideTicks = 6;
            if (playbackMode != PlaybackMode.Playing)
            {
                if (mediaTime > seekTarget + 500) player.Time = seekTarget;
                player.SetPause(true);
            }
            return;
        }
        if (pendingSeek) return;
        if (playbackMode != PlaybackMode.Playing)
        {
            if (player.IsPlaying) player.SetPause(true);
            return;
        }
        if (!player.IsPlaying) return;
        double local = Math.Max(0, player.Time / 1000d);
        SetPosition(local + (loadedVideo is null ? 0 : OffsetOf(loadedVideo)));
        if (timelineZoom > 1 && TotalDuration > 0 && !draggingMarker)
        {
            double x = TimeToX(position);
            bool userBrowsing = draggingScrollbar || DateTime.UtcNow - lastManualScroll < TimeSpan.FromSeconds(4);
            if (!userBrowsing && (x < TimelineScroll.HorizontalOffset || x > TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth))
            {
                autoScrollTarget = Math.Max(0, x - TimelineScroll.ViewportWidth * 0.2);
                TimelineScroll.ScrollToHorizontalOffset(autoScrollTarget);
            }
        }
        // Advance slightly early so the player never reaches the file end, where LibVLC clears the picture.
        double fileEnd = loadedVideo?.Duration ?? double.MaxValue;
        if (preview is not null && local >= Math.Min(preview[previewIndex].End, Math.Max(preview[previewIndex].Start, fileEnd - 0.12)))
        {
            previewIndex++;
            StartPreviewSegment();
        }
        else if (preview is null && local >= fileEnd - 0.12)
            StopPlayback();
    }

    private void Player_EncounteredError(object? sender, EventArgs e)
    {
        long revision = playbackRevision;
        Dispatcher.BeginInvoke(() =>
        {
            if (revision != playbackRevision) return;
            StopPlayback();
            StatusLabel.Text = "Playback failed. Check that the source file is accessible and supported.";
        });
    }

    private void Player_EndReached(object? sender, EventArgs e)
    {
        long revision = playbackRevision;
        Dispatcher.BeginInvoke(() =>
        {
            if (revision != playbackRevision || player.State != VLCState.Ended) return;
            if (pendingSeek && loadedVideo is { } seekingVideo)
            {
                if (seekTarget / 1000d < seekingVideo.Duration - 0.15 || playbackMode != PlaybackMode.Playing)
                {
                    StartMedia(seekingVideo, Math.Min(seekTarget / 1000d, Math.Max(0, seekingVideo.Duration - 0.15)));
                    return;
                }
            }
            if (playbackMode != PlaybackMode.Playing) return;
            if (preview is not null && previewIndex + 1 < preview.Count)
            {
                previewIndex++;
                StartPreviewSegment();
            }
            else if (loadedVideo is { } video)
            {
                // Reload the finished file and hold its last frame instead of showing a blank picture.
                StopPlayback();
                StartMedia(video, Math.Max(0, video.Duration - 0.15));
            }
        });
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (playbackMode == PlaybackMode.Playing)
        {
            playbackMode = PlaybackMode.Paused;
            player.Mute = true;
            if (!pendingSeek)
            {
                if (loadedVideo is not null) SetPosition(OffsetOf(loadedVideo) + Math.Max(0, player.Time / 1000d));
                player.SetPause(true);
            }
        }
        else if (playbackMode == PlaybackMode.Paused)
        {
            playbackMode = PlaybackMode.Playing;
            player.Mute = pendingSeek && !seekIssued;
            if (!pendingSeek && loadedVideo is not null)
            {
                seekTarget = (long)Math.Round((position - OffsetOf(loadedVideo)) * 1000);
                pendingSeek = true;
                seekIssued = true;
                seekIssuedAt = Environment.TickCount64;
            }
            player.SetPause(false);
        }
        else BeginPreview(0);
        UpdateControls();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        var first = TimelineSections.Build(videos, sections).FirstOrDefault();
        if (first is not null) SeekTo(OffsetOf(first.Video) + first.Start);
    }

    // Stopping keeps the current frame visible; the player is only paused.
    private void StopPlayback()
    {
        playbackRevision++;
        playbackMode = PlaybackMode.Stopped;
        preview = null;
        pendingSeek = false;
        player.Mute = true;
        if (player.IsPlaying) player.SetPause(true);
        UpdateControls();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (player is not null) player.Volume = (int)Math.Round(e.NewValue);
    }

    private void VolumePopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        VolumePopup.IsOpen = false;
        VolumeButton.Focus();
        e.Handled = true;
    }

    private void CoverWithCurrentFrame()
    {
        if (player.State is not (VLCState.Playing or VLCState.Paused)) return;
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ClipStitch-frame-{Guid.NewGuid():N}.png");
        try
        {
            if (!player.TakeSnapshot(0, file, 0, 0) || !File.Exists(file)) return;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            FrameCoverImage.Source = image;
            FrameCover.Visibility = Visibility.Visible;
            coverHideTicks = -1;
        }
        catch (Exception) { }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }
    private void InvalidatePreview()
    {
        if (preview is not null || playbackMode != PlaybackMode.Stopped) StopPlayback();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        ExportProgressDialog? progressDialog = null;
        try
        {
            var segments = TimelineSections.Build(videos, sections);
            if (segments.Count == 0) { MessageBox.Show(this, "Select at least one section to keep."); return; }
            string folder = sourceFolder is not null && Directory.Exists(sourceFolder)
                ? sourceFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            string baseName = sourceFolder is null ? "joined" : new DirectoryInfo(sourceFolder).Name;
            if (string.IsNullOrWhiteSpace(baseName) || baseName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) baseName = "joined";
            var nameDialog = new SaveNameDialog(this, folder, baseName + ".mp4");
            if (nameDialog.ShowDialog() != true) return;
            string output = nameDialog.FilePath;
            if (videos.Any(v => string.Equals(System.IO.Path.GetFullPath(v.Path),
                    System.IO.Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The output must not overwrite an input video.");
            bool overwrite = File.Exists(output);
            if (overwrite && MessageBox.Show(this, $"{System.IO.Path.GetFileName(output)} already exists.\nReplace it after the export completes?", "Confirm replacement",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var quality = int.Parse(((ComboBoxItem)QualityBox.SelectedItem).Content.ToString()!);
            var preset = ((ComboBoxItem)PresetBox.SelectedItem).Content.ToString()!;
            var first = segments[0].Video;
            StopPlayback();
            exportCancellation = new CancellationTokenSource();
            var cancellation = exportCancellation;
            string message = $"Saving {segments.Count} section(s) at {first.Width / 2 * 2}×{first.Height / 2 * 2} to\n{output}";
            StatusLabel.Text = message.Replace('\n', ' ');
            progressDialog = new ExportProgressDialog(this, message);
            progressDialog.CancelRequested += () => cancellation.Cancel();
            var dialog = progressDialog;
            var progress = new Progress<double>(dialog.Report);
            Task? export = null;
            // Start the export once the modal window is shown; ShowDialog blocks input to the main window until it closes.
            dialog.Loaded += (_, _) =>
            {
                export = MediaTools.ExportAsync(segments, output, quality, preset, overwrite, progress, cancellation.Token);
                export.ContinueWith(_ => dialog.Finish(), TaskScheduler.FromCurrentSynchronizationContext());
            };
            dialog.ShowDialog();
            progressDialog = null;
            if (export is not null) await export;
            StatusLabel.Text = $"Export complete: {output}";
            MessageBox.Show(this, $"Video saved to:\n{output}", "Export complete");
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Export cancelled; the destination was not changed."; }
        catch (Exception ex) { ShowError("Export failed", ex); }
        finally
        {
            progressDialog?.Finish();
            exportCancellation?.Dispose();
            exportCancellation = null;
        }
    }
    private void ShowError(string title, Exception ex)
    {
        StatusLabel.Text = $"{title}: {ex.Message}";
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
