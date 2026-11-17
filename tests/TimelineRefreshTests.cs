using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using ClipStitch;
using LibVLCSharp.Shared;

namespace tests;

public class TimelineRefreshTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref ScreenPoint point);

    [Fact]
    public async Task ResizingWaitsForReleaseAndMaximizingRefreshesTheViewport()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ClipStitch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "video.mp4");
            var result = await MediaTools.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i",
                "color=c=red:s=64x64:r=25:d=1", "-c:v", "libx264", "-y", path], CancellationToken.None);
            Assert.True(result.ExitCode == 0, result.Stderr);
            var video = await MediaTools.ProbeAsync(path, CancellationToken.None);
            await WithWindow(async window =>
            {
                var videos = Field<ObservableCollection<VideoItem>>(window, "videos");
                videos.Add(video);
                typeof(MainWindow).GetField("folderCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, new CancellationTokenSource());
                Invoke(window, "ScheduleThumbnailRefresh");
                var badge = Field<Grid>(window, "ThumbnailLoadingBadge");
                var images = Field<ICollection>(window, "thumbnailImages");
                await WaitUntil(() => images.Count > 0 && badge.Visibility == Visibility.Collapsed);
                var initial = Field<CancellationTokenSource>(window, "thumbnailCancellation");

                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                SendMessage(hwnd, 0x0231, IntPtr.Zero, IntPtr.Zero);
                window.Width -= 100;
                await Task.Delay(800);
                Assert.True(initial.IsCancellationRequested);
                Assert.Same(initial, Field<CancellationTokenSource>(window, "thumbnailCancellation"));
                Assert.Equal(Visibility.Visible, badge.Visibility);
                SendMessage(hwnd, 0x0232, IntPtr.Zero, IntPtr.Zero);
                await WaitUntil(() => !ReferenceEquals(initial, Field<CancellationTokenSource>(window, "thumbnailCancellation"))
                    && badge.Visibility == Visibility.Collapsed);

                var beforeMaximize = Field<CancellationTokenSource>(window, "thumbnailCancellation");
                var scroll = Field<ScrollViewer>(window, "TimelineScroll");
                double previousWidth = scroll.ViewportWidth;
                window.WindowState = WindowState.Maximized;
                await WaitUntil(() => Math.Abs(scroll.ViewportWidth - previousWidth) > 1);
                await WaitUntil(() => !ReferenceEquals(beforeMaximize, Field<CancellationTokenSource>(window, "thumbnailCancellation"))
                    && badge.Visibility == Visibility.Collapsed
                    && images.Count == Math.Clamp((int)Math.Ceiling(scroll.ViewportWidth / 100), 1, 24));

                var beforeRestore = Field<CancellationTokenSource>(window, "thumbnailCancellation");
                double maximizedWidth = scroll.ViewportWidth;
                window.WindowState = WindowState.Normal;
                await WaitUntil(() => Math.Abs(scroll.ViewportWidth - maximizedWidth) > 1);
                await WaitUntil(() => !ReferenceEquals(beforeRestore, Field<CancellationTokenSource>(window, "thumbnailCancellation"))
                    && badge.Visibility == Visibility.Collapsed
                    && images.Count == Math.Clamp((int)Math.Ceiling(scroll.ViewportWidth / 100), 1, 24));
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    [Fact]
    public async Task PlaybackIntentSurvivesSeeksDeletionAndStop()
    {
        await WithVideos(async window =>
        {
            var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "player");
            var sections = Field<ObservableCollection<KeepRange>>(window, "sections");
            Invoke(window, "SeekTo", 7d);
            Assert.Equal("Stopped", Mode(window));
            await AtFrame(window, 7, true);
            Assert.Equal(100, player.Volume);
            Click(window, "PlayPause_Click");
            Assert.Equal(2, Field<double>(window, "position"));
            await AtFrame(window, 2, false);
            Click(window, "PlayPause_Click");
            await WaitUntil(() => player.State == VLCState.Paused);
            double paused = Field<double>(window, "position");
            long revision = Field<long>(window, "playbackRevision");
            Click(window, "PlayPause_Click");
            Assert.Equal(paused, Field<double>(window, "position"));
            Assert.Equal(revision, Field<long>(window, "playbackRevision"));
            Click(window, "PlayPause_Click");

            Field<Slider>(window, "VolumeSlider").Value = 37;
            Invoke(window, "SeekTo", 5d);
            Assert.Equal("Paused", Mode(window));
            Assert.Equal(5, Field<double>(window, "position"));
            AssertSectionControlsDisabled(window);
            await AtFrame(window, 5, true);
            long stillTime = player.Time;
            await Task.Delay(250);
            Assert.InRange(Math.Abs(player.Time - stillTime), 0, 80);
            Click(window, "PlayPause_Click");
            Assert.Equal(5, Field<double>(window, "position"));
            Assert.Equal(9, Field<List<Segment>>(window, "preview")[0].End);

            Invoke(window, "SeekTo", 7d);
            await AtFrame(window, 7, false);
            double beforeRemoval = Field<double>(window, "position");
            revision = Field<long>(window, "playbackRevision");
            Click(window, "RemoveRange_Click");
            Assert.Equal("Playing", Mode(window));
            Assert.Equal(beforeRemoval, Field<double>(window, "position"));
            Assert.Equal(revision, Field<long>(window, "playbackRevision"));
            Assert.Equal(2, sections.Count);
            Assert.Equal(10, Field<List<Segment>>(window, "preview")[0].End);
            AssertSectionControlsDisabled(window);

            Click(window, "PlayPause_Click");
            Invoke(window, "SeekTo", 13d);
            await AtFrame(window, 3, true);
            revision = Field<long>(window, "playbackRevision");
            Click(window, "RemoveRange_Click");
            Assert.Equal("Paused", Mode(window));
            Assert.Equal(13, Field<double>(window, "position"));
            Assert.Equal(revision, Field<long>(window, "playbackRevision"));
            Assert.Equal(10, Assert.Single(Field<List<Segment>>(window, "preview")).End);
            Invoke(window, "SeekTo", 3d);
            Assert.False(Field<Button>(window, "RemoveSectionButton").IsEnabled);
            Click(window, "RemoveRange_Click");
            Assert.Single(sections);

            Click(window, "Stop_Click");
            Assert.Equal("Stopped", Mode(window));
            Assert.Equal(2, Field<double>(window, "position"));
            await AtFrame(window, 2, true);
            Assert.True(player.Mute);
            Assert.Equal(37, player.Volume);
            Invoke(window, "Player_EndReached", null, EventArgs.Empty);
            await Task.Delay(100);
            Assert.Equal("Stopped", Mode(window));
            Click(window, "PlayPause_Click");
            Assert.Equal(2, Field<double>(window, "position"));
            Invoke(window, "SeekTo", 3.8d);
            await WaitUntil(() => Mode(window) == "Stopped");
            Click(window, "PlayPause_Click");
            Assert.Equal(2, Field<double>(window, "position"));
        });
    }

    [Theory]
    [InlineData(6.5, false)]
    [InlineData(13, false)]
    [InlineData(6.5, true)]
    [InlineData(13, true)]
    public async Task MouseClickOnTimelineKeepsTheVideoPlaying(double target, bool mpeg)
    {
        await WithVideos(async window =>
        {
            window.Opacity = 1;
            window.Topmost = true;
            window.Activate();
            Click(window, "PlayPause_Click");
            var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "player");
            await WaitUntil(() => player.IsPlaying && !Field<bool>(window, "pendingSeek"));
            var timeline = Field<Grid>(window, "Timeline");
            var screen = timeline.PointToScreen(new Point(14 + target / 20 * (timeline.ActualWidth - 28), 60));
            var point = new ScreenPoint { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            Assert.True(GetCursorPos(out var previous));
            try
            {
                Assert.True(SetCursorPos(point.X, point.Y));
                await Task.Delay(50);
                Mouse.Synchronize();
                Assert.True(ScreenToClient(hwnd, ref point));
                var location = new IntPtr((point.Y << 16) | (point.X & 0xffff));
                SendMessage(hwnd, 0x0201, new IntPtr(1), location);
                SendMessage(hwnd, 0x0202, IntPtr.Zero, location);
                Assert.InRange(Field<double>(window, "position"), target - 0.1, target + 0.1);
                Assert.Equal("Playing", Mode(window));
                await WaitUntil(() => player.IsPlaying && !Field<bool>(window, "pendingSeek")
                    && Field<Grid>(window, "FrameCover").Visibility == Visibility.Collapsed);
                long time = player.Time;
                await WaitUntil(() => player.Time >= time + 250);
                using var media = player.Media!;
                int displayed = media.Statistics.DisplayedPictures;
                await WaitUntil(() => media.Statistics.DisplayedPictures > displayed + 2);
                Assert.Equal("Playing", Mode(window));
            }
            finally { SetCursorPos(previous.X, previous.Y); }
        }, mpeg);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimelineSeekCompletesAfterTheDispatcherMissesTheTargetFrame(bool pauseDuringSeek)
    {
        await WithVideos(async window =>
        {
            var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "player");
            var timer = Field<DispatcherTimer>(window, "timer");
            Invoke(window, "BeginPreview", 1);
            await WaitUntil(() => !Field<bool>(window, "pendingSeek") && player.IsPlaying && player.Time >= 6000);
            timer.Stop();
            Invoke(window, "SeekTo", 13d);
            if (pauseDuringSeek) Click(window, "PlayPause_Click");
            var cover = Field<Grid>(window, "FrameCover");
            cover.Visibility = Visibility.Visible;
            await WaitUntil(() => player.IsPlaying && player.VoutCount > 0 && player.Time > 0);
            Invoke(window, "Timer_Tick", null, EventArgs.Empty);
            Assert.True(Field<bool>(window, "seekIssued"));
            await WaitUntil(() => player.IsPlaying && player.VoutCount > 0 && player.Time >= 3800);

            if (!pauseDuringSeek)
            {
                Click(window, "PlayPause_Click");
                Click(window, "PlayPause_Click");
            }

            Invoke(window, "Timer_Tick", null, EventArgs.Empty);
            Assert.False(Field<bool>(window, "pendingSeek"));
            Assert.Equal(pauseDuringSeek ? "Paused" : "Playing", Mode(window));
            timer.Start();
            await WaitUntil(() => cover.Visibility == Visibility.Collapsed);
            if (pauseDuringSeek)
            {
                await AtFrame(window, 3, true);
                long paused = player.Time;
                await Task.Delay(250);
                Assert.InRange(Math.Abs(player.Time - paused), 0, 80);
                return;
            }
            long resumed = player.Time;
            await WaitUntil(() => player.Time >= resumed + 250);
            Assert.True(player.IsPlaying);
        });
    }

    [Fact]
    public async Task NewInputInitializesVideoBeforeSeeking()
    {
        await WithVideos(async window =>
        {
            var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "player");
            Invoke(window, "BeginPreview", 1);
            await AtFrame(window, 6, false);
            var timer = Field<DispatcherTimer>(window, "timer");
            timer.Stop();
            Invoke(window, "SeekTo", 13d);
            await WaitUntil(() => player.IsPlaying && player.VoutCount > 0 && player.Time > 0);
            using var media = player.Media!;
            Assert.True(media.Statistics.DecodedVideo > 0);
            Assert.True(player.Time < 3000, "A new input must decode its opening frames before the initial seek.");
            timer.Start();
            await AtFrame(window, 3, false);
            int displayed = media.Statistics.DisplayedPictures;
            await WaitUntil(() => media.Statistics.DisplayedPictures > displayed + 2);
        });
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(5d)]
    public async Task PlayingSeekAcceptsAnImpreciseNativeTimestamp(double nativeSeconds)
    {
        await WithVideos(async window =>
        {
            var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "player");
            Invoke(window, "BeginPreview", 1);
            await AtFrame(window, 6, false);
            var timer = Field<DispatcherTimer>(window, "timer");
            timer.Stop();
            Invoke(window, "SeekTo", 13d);
            await WaitUntil(() => player.IsPlaying && player.VoutCount > 0 && player.Time > 0);
            Invoke(window, "Timer_Tick", null, EventArgs.Empty);
            player.Time = (long)(nativeSeconds * 1000);
            Invoke(window, "Timer_Tick", null, EventArgs.Empty);
            Assert.False(Field<bool>(window, "pendingSeek"));
            timer.Start();
            await WaitUntil(() => Math.Abs(Field<double>(window, "position") - 13) > 0.5);
            Assert.Equal("Playing", Mode(window));
            long time = player.Time;
            await WaitUntil(() => player.Time >= time + 250);
        });
    }

    [Fact]
    public async Task PlayingSeekRecoversFromANativePauseArrivingAfterResume()
    {
        await WithVideos(async window =>
        {
            var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "player");
            var timer = Field<DispatcherTimer>(window, "timer");
            Invoke(window, "BeginPreview", 1);
            await WaitUntil(() => !Field<bool>(window, "pendingSeek") && player.IsPlaying && player.Time >= 6000);
            timer.Stop();
            Invoke(window, "SeekTo", 6.5d);
            Click(window, "PlayPause_Click");
            Click(window, "PlayPause_Click");
            player.SetPause(true);
            await WaitUntil(() => player.State == VLCState.Paused);
            Assert.Equal("Playing", Mode(window));

            Invoke(window, "Timer_Tick", null, EventArgs.Empty);
            await WaitUntil(() => player.IsPlaying);
            timer.Start();
            await WaitUntil(() => !Field<bool>(window, "pendingSeek"));
            long resumed = player.Time;
            await WaitUntil(() => player.Time >= resumed + 250);
            Assert.Equal("Playing", Mode(window));
        });
    }

    [Fact]
    public async Task SectionActionsAndHandleReleaseContinueAcrossSelectionsAndFiles()
    {
        await WithVideos(async window =>
        {
            var sections = Field<ObservableCollection<KeepRange>>(window, "sections");
            var videos = Field<ObservableCollection<VideoItem>>(window, "videos");
            Invoke(window, "SetPosition", 3d);
            Click(window, "NextSection_Click");
            Assert.Equal(6, Field<double>(window, "position"));
            Assert.Equal("Playing", Mode(window));
            Click(window, "PreviousSection_Click");
            Assert.Equal(2, Field<double>(window, "position"));

            var handle = Field<Canvas>(window, "RangeOverlay").Children.OfType<Thumb>().Single(t => t.Tag is false);
            handle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            double width = Field<Grid>(window, "Timeline").ActualWidth - 28;
            handle.RaiseEvent(new DragDeltaEventArgs(width / 20, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            Assert.Equal(5, sections[0].End, 3);
            Assert.Equal(0, Field<int>(window, "selectedSectionIndex"));
            Assert.Equal(2, Field<Canvas>(window, "PlayheadOverlay").Children.Count);
            handle.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Assert.Equal("Playing", Mode(window));
            Assert.Equal(2, Field<double>(window, "position"));
            Assert.Equal(3, Field<List<Segment>>(window, "preview").Count);

            Click(window, "JoinNext_Click");
            Assert.Equal("Stopped", Mode(window));
            Assert.Equal(new KeepRange(2, 9), sections[0]);
            Invoke(window, "SetPosition", 5d);
            Click(window, "AddRange_Click");
            Assert.Equal(new KeepRange(2, 5), sections[0]);
            Assert.Equal(new KeepRange(5, 9), sections[1]);
            Assert.Equal(1, Field<int>(window, "selectedSectionIndex"));
            Assert.False(Field<Button>(window, "SplitButton").IsEnabled);
            Assert.True(Field<Button>(window, "PreviousSectionButton").IsEnabled);
            Click(window, "PlayClip_Click");
            Assert.Equal(5, Field<double>(window, "position"));

            Invoke(window, "SeekTo", 8.8d);
            await WaitUntil(() => ReferenceEquals(videos[1], Field<VideoItem>(window, "loadedVideo"))
                && Field<double>(window, "position") >= 12);
            Assert.InRange(Field<double>(window, "position"), 12, 13);
            Invoke(window, "SeekTo", 17.5d);
            Assert.Equal(17.5, Field<double>(window, "position"));
            var tail = Assert.Single(Field<List<Segment>>(window, "preview"));
            Assert.Equal(7.5, tail.Start);
            Assert.Equal(10, tail.End);
            Invoke(window, "SeekTo", 19.65d);
            await WaitUntil(() => Mode(window) == "Stopped");
        });
    }

    [Fact]
    public async Task PlayheadGapsVolumeAndControlsRemainUsableAtEveryWindowSize()
    {
        await WithVideos(async window =>
        {
            var overlay = Field<Canvas>(window, "PlayheadOverlay");
            var gaps = Field<Canvas>(window, "GapOverlay");
            Assert.False(overlay.IsHitTestVisible);
            Assert.False(gaps.IsHitTestVisible);
            Assert.Equal(4, gaps.Children.Count);
            foreach (var shade in gaps.Children.OfType<Rectangle>())
                Assert.InRange(((SolidColorBrush)shade.Fill).Color.A / 255d, 0.1, 0.2);
            foreach (double boundary in new[] { 0d, 2, 4, 6, 9, 12, 16, 20 })
            {
                Invoke(window, "SetPosition", boundary);
                Assert.Equal(2, overlay.Children.Count);
                Assert.Equal(8, Canvas.GetTop(overlay.Children[0]));
                if (boundary is 0 or 4 or 9 or 16 or 20) AssertSectionControlsDisabled(window);
            }
            Invoke(window, "SetPosition", 7d);
            Click(window, "RemoveRange_Click");
            Assert.Equal(3, gaps.Children.Count);

            foreach (var state in new[] { WindowState.Normal, WindowState.Maximized, WindowState.Normal })
            {
                window.Width = state == WindowState.Normal ? 950 : 1280;
                window.Height = 650;
                window.WindowState = state;
                window.UpdateLayout();
                await Task.Delay(100);
                foreach (string name in new[] { "PlayPauseButton", "StopButton", "VolumeButton", "PreviousSectionButton",
                    "PlaySectionButton", "SplitButton", "JoinPreviousButton", "JoinNextButton", "NextSectionButton", "RemoveSectionButton", "ExportButton" })
                {
                    var button = Field<ButtonBase>(window, name);
                    Assert.NotNull(button.ToolTip);
                    Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)));
                    var location = button.TranslatePoint(new Point(), window);
                    Assert.InRange(location.X, 0, window.ActualWidth - button.ActualWidth);
                    Assert.InRange(location.Y, 0, window.ActualHeight - button.ActualHeight);
                }
                var quality = Field<ComboBox>(window, "QualityBox");
                var save = Field<Button>(window, "ExportButton");
                Assert.InRange(Math.Abs(quality.TranslatePoint(new Point(), window).Y - save.TranslatePoint(new Point(), window).Y), 0, 4);
                Assert.Equal(2, overlay.Children.Count);
            }

            var volume = Field<Slider>(window, "VolumeSlider");
            var popup = Field<Popup>(window, "VolumePopup");
            Field<ToggleButton>(window, "VolumeButton").IsChecked = true;
            await WaitUntil(() => popup.IsOpen && PresentationSource.FromVisual(popup.Child) is not null);
            Assert.NotSame(PresentationSource.FromVisual(window), PresentationSource.FromVisual(popup.Child));
            Assert.False(popup.StaysOpen);
            volume.Value = 42;
            Invoke(window, "SeekTo", 3d);
            await AtFrame(window, 3, true);
            Assert.Equal(42, Field<LibVLCSharp.Shared.MediaPlayer>(window, "player").Volume);
            Invoke(window, "SeekTo", 20d);
            var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "player");
            await AtFrame(window, 9.85, true);
            await Task.Delay(250);
            Assert.Equal(VLCState.Paused, player.State);
            Assert.InRange(player.Time, 9500, 10000);
            Assert.Equal(20, Field<double>(window, "position"));
            Assert.Equal(2, overlay.Children.Count);
            Assert.Equal(42, volume.Value);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            popup.Child.RaiseEvent(key);
            Assert.False(popup.IsOpen);
            Assert.False(Field<ToggleButton>(window, "VolumeButton").IsChecked);
        });
    }

    private static async Task WithVideos(Func<MainWindow, Task> action, bool mpeg = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ClipStitch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, mpeg ? "one.dat" : "one.mp4");
            var result = await MediaTools.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i",
                "testsrc2=s=160x90:r=25:d=10", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-c:v", "libx264", "-g", mpeg ? "25" : "250", "-c:a", mpeg ? "mp2" : "aac",
                "-shortest", "-f", mpeg ? "mpeg" : "mp4", "-y", path], CancellationToken.None);
            Assert.True(result.ExitCode == 0, result.Stderr);
            string second = Path.Combine(directory, mpeg ? "two.dat" : "two.mp4");
            File.Copy(path, second);
            var firstVideo = await MediaTools.ProbeAsync(path, CancellationToken.None);
            var secondVideo = await MediaTools.ProbeAsync(second, CancellationToken.None);
            await WithWindow(async window =>
            {
                var videos = Field<ObservableCollection<VideoItem>>(window, "videos");
                videos.Add(firstVideo);
                videos.Add(secondVideo);
                var sections = Field<ObservableCollection<KeepRange>>(window, "sections");
                sections.Add(new KeepRange(2, 4));
                sections.Add(new KeepRange(6, 9));
                sections.Add(new KeepRange(12, 16));
                Invoke(window, "SetPosition", 2d);
                Invoke(window, "DrawRanges");
                await action(window);
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task WithWindow(Func<MainWindow, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
                    window.Show();
                    await action(window);
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally
                {
                    window?.Close();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(90)); }
        finally { thread.Join(TimeSpan.FromSeconds(5)); }
    }

    private static string Mode(MainWindow window) => Field<object>(window, "playbackMode").ToString()!;

    private static async Task AtFrame(MainWindow window, double seconds, bool paused)
    {
        var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "player");
        try
        {
            await WaitUntil(() => !Field<bool>(window, "pendingSeek") && Math.Abs(player.Time / 1000d - seconds) < 0.4
                && (paused ? player.State == VLCState.Paused : player.IsPlaying));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Expected {(paused ? "paused" : "playing")} at {seconds}s; actual {player.State} at {player.Time}ms, " +
                $"pending seek: {Field<bool>(window, "pendingSeek")}, target: {Field<long>(window, "seekTarget")}ms, intent: {Mode(window)}.", ex);
        }
    }

    private static void AssertSectionControlsDisabled(MainWindow window)
    {
        foreach (string name in new[] { "PreviousSectionButton", "PlaySectionButton", "SplitButton",
            "JoinPreviousButton", "JoinNextButton", "NextSectionButton", "RemoveSectionButton" })
            Assert.False(Field<Button>(window, name).IsEnabled, name);
    }

    private static void Click(MainWindow window, string name) => Invoke(window, name, window, new RoutedEventArgs());

    private static void Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for the UI or media player.");
            await Task.Delay(50);
        }
    }
}
