using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipStitch;

namespace tests;

public class TimelineRefreshTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

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
        finally { Directory.Delete(directory, true); }
    }

    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void Invoke(MainWindow window, string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for thumbnail refresh.");
            await Task.Delay(50);
        }
    }
}
