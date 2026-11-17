using System.Diagnostics;
using System.IO;
using ClipStitch;

namespace tests;

public class MediaTests
{
    [Fact]
    public void TimelineClickContinuesFromClickedPositionThroughFollowingFiles()
    {
        var first = Video("one.mp4");
        var second = Video("two.mp4");
        var third = Video("three.mp4");
        Assert.Equal(new[] { new Segment(first, 6, 10), new Segment(second, 0, 10), new Segment(third, 0, 10) },
            TimelineSections.Build([first, second, third], [new KeepRange(6, 30)]));
        Assert.Equal(new[] { new Segment(second, 0, 10), new Segment(third, 0, 10) },
            TimelineSections.Build([first, second, third], [new KeepRange(10, 30)]));
    }

    [Fact]
    public void BoundaryPreviewStaysInsideSectionAndCrossesVideoBoundaries()
    {
        var first = Video("one.mp4");
        var second = Video("two.mp4");
        var section = new KeepRange(3, 12);
        Assert.Equal(section, TimelineSections.BoundaryPreview(section, true));
        var endPreview = TimelineSections.BoundaryPreview(section, false);
        Assert.Equal(new KeepRange(7, 12), endPreview);
        Assert.Equal(new[] { new Segment(first, 7, 10), new Segment(second, 0, 2) },
            TimelineSections.Build([first, second], [endPreview]));
        Assert.Equal(new KeepRange(9, 12), TimelineSections.BoundaryPreview(new KeepRange(9, 12), false));
        first.Included = false;
        Assert.Equal(new Segment(second, 0, 2),
            Assert.Single(TimelineSections.Build([first, second], [endPreview])));
    }

    [Fact]
    public void PlayFromInsideSectionContinuesWithFollowingSections()
    {
        KeepRange[] sections = [new(10, 20), new(2, 5), new(30, 40)];
        Assert.Equal(new KeepRange[] { new(12, 20), new(30, 40) }, TimelineSections.PlayFrom(sections, 12, 50));
        Assert.Equal(new KeepRange[] { new(25, 40) }, TimelineSections.PlayFrom(sections, 25, 50));
        Assert.Equal(new KeepRange[] { new(45, 50) }, TimelineSections.PlayFrom(sections, 45, 50));
    }

    [Fact]
    public void SectionContextUsesHalfOpenBoundariesAndGaps()
    {
        KeepRange[] sections = [new(2, 5), new(5, 10), new(15, 20)];
        Assert.Equal(-1, TimelineSections.IndexAt(sections, 0));
        Assert.Equal(0, TimelineSections.IndexAt(sections, 2));
        Assert.Equal(1, TimelineSections.IndexAt(sections, 5));
        Assert.Equal(-1, TimelineSections.IndexAt(sections, 10));
        Assert.Equal(2, TimelineSections.IndexAt(sections, 19.999));
        Assert.Equal(-1, TimelineSections.IndexAt(sections, 20));
        Assert.Equal(new KeepRange[] { new(0, 2), new(10, 15), new(20, 25) }, TimelineSections.Gaps(sections, 25));
        Assert.Empty(TimelineSections.Gaps([new(0, 25)], 25));
    }

    [Fact]
    public void ZoomKeepsTimeUnderPointerAndRespectsBounds()
    {
        var zoomed = TimelineZoom.AtPointer(2, 300, 1000, 400, 120);
        Assert.Equal(2.7, zoomed.Zoom, 6);
        Assert.Equal((300d + 400) / 2000, (zoomed.Offset + 400) / (1000 * zoomed.Zoom), 6);
        var reset = TimelineZoom.AtPointer(1, 0, 1000, 400, -120);
        Assert.Equal(1, reset.Zoom);
        Assert.Equal(0, reset.Offset);
        var max = TimelineZoom.AtPointer(256, 2000, 1000, 400, 120);
        Assert.Equal(256, max.Zoom);
    }

    [Fact]
    public void GlobalSectionsCrossFilesAndPreserveSplits()
    {
        var one = Video("one.mp4");
        var two = Video("two.mp4");
        var split = TimelineSections.Split(new KeepRange(0, 20), 7);
        var segments = TimelineSections.Build([one, two], [split.Left, split.Right]);
        Assert.Equal(new[] { new Segment(one, 0, 7), new Segment(one, 7, 10), new Segment(two, 0, 10) }, segments);
        Assert.Equal(new KeepRange(0, 20), TimelineSections.Join(split.Left, split.Right));
        var trimmed = TimelineSections.Build([one, two], [new KeepRange(3, 7), new KeepRange(12, 15)]);
        Assert.Equal(new[] { new Segment(one, 3, 7), new Segment(two, 2, 5) }, trimmed);
    }

    [Fact]
    public void JoiningRestoresGapAndExcludedFilesDoNotShiftTimeline()
    {
        Assert.Equal(new KeepRange(1, 9), TimelineSections.Join(new KeepRange(1, 3), new KeepRange(7, 9)));
        var one = Video("one.mp4");
        one.Included = false;
        var two = Video("two.mp4");
        var result = TimelineSections.Build([one, two], [new KeepRange(12, 15)]);
        Assert.Equal(new Segment(two, 2, 5), Assert.Single(result));
    }

    [Fact]
    public void RejectsOverlappingSectionsAndBoundarySplits()
    {
        Assert.Throws<InvalidOperationException>(() => TimelineSections.Build([Video("one.mp4")],
            [new KeepRange(0, 5), new KeepRange(4, 7)]));
        Assert.Throws<InvalidOperationException>(() => TimelineSections.Split(new KeepRange(0, 10), 0));
        Assert.Throws<InvalidOperationException>(() => TimelineSections.Split(new KeepRange(0, 10), 10));
    }

    private static VideoItem Video(string name, double duration = 10) =>
        new() { Path = name, Duration = duration, Width = 320, Height = 240, FrameRate = 25, HasAudio = false };

    [Fact]
    public void RangesAreMergedAndOrderedAcrossIncludedVideos()
    {
        var first = Video("one.mp4");
        first.Ranges.Add(new KeepRange(5, 6));
        first.Ranges.Add(new KeepRange(1, 3));
        first.Ranges.Add(new KeepRange(2, 4));
        first.Ranges.Add(new KeepRange(4, 5));
        var excluded = Video("excluded.mp4");
        excluded.Ranges.Add(new KeepRange(0, 10));
        excluded.Included = false;
        var second = Video("two.mp4");
        second.Ranges.Add(new KeepRange(0, 1));
        var result = SegmentBuilder.Build([first, excluded, second]);
        Assert.Equal(2, result.Count);
        Assert.Equal(new Segment(first, 1, 6), result[0]);
        Assert.Equal(new Segment(second, 0, 1), result[1]);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, 2)]
    [InlineData(0, 11)]
    [InlineData(double.NaN, 1)]
    public void InvalidRangesAreRejected(double start, double end)
    {
        var video = Video("invalid.mp4");
        video.Ranges.Add(new KeepRange(start, end));
        Assert.Throws<InvalidOperationException>(() => SegmentBuilder.Build([video]));
    }

    [Fact]
    public void NaturalNamesAreSortedNumerically()
    {
        var names = new[] { @"C:\videos\clip10.mp4", @"C:\videos\clip2.mp4", @"C:\videos\clip1.mp4" };
        Array.Sort(names, new NaturalPathComparer());
        Assert.EndsWith("clip1.mp4", names[0]);
        Assert.EndsWith("clip2.mp4", names[1]);
        Assert.EndsWith("clip10.mp4", names[2]);
    }

    [Fact]
    public async Task DatCandidatesAreProbedForVideoContent()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClipStitch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = System.IO.Path.Combine(directory, "video.mp4");
            string video = System.IO.Path.Combine(directory, "video.DAT");
            string audioSource = System.IO.Path.Combine(directory, "audio.wav");
            string audio = System.IO.Path.Combine(directory, "audio.dat");
            string text = System.IO.Path.Combine(directory, "text.dat");
            string empty = System.IO.Path.Combine(directory, "empty.dat");
            await Ffmpeg("-f", "lavfi", "-i", "color=c=red:s=64x64:r=25:d=1", "-c:v", "libx264", source);
            await Ffmpeg("-f", "lavfi", "-i", "sine=frequency=440:duration=1", audioSource);
            File.Copy(source, video);
            File.Copy(audioSource, audio);
            await File.WriteAllTextAsync(text, "This is not video.");
            await File.WriteAllTextAsync(empty, "");

            Assert.True(MediaTools.IsVideoCandidate(video));
            Assert.True(MediaTools.IsVideoCandidate(source));
            Assert.False(MediaTools.IsVideoCandidate(audioSource));
            var item = await MediaTools.ProbeIfVideoAsync(video, CancellationToken.None);
            Assert.NotNull(item);
            Assert.Equal(video, item.Path);
            Assert.InRange(item.Duration, 0.9, 1.1);
            Assert.Null(await MediaTools.ProbeIfVideoAsync(audio, CancellationToken.None));
            Assert.Null(await MediaTools.ProbeIfVideoAsync(text, CancellationToken.None));
            Assert.Null(await MediaTools.ProbeIfVideoAsync(empty, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => MediaTools.ProbeAsync(text, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                MediaTools.ProbeIfVideoAsync(System.IO.Path.Combine(directory, "missing.dat"), CancellationToken.None));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaTools.ProbeIfVideoAsync(video, cancel.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExportsMultipleSegmentsAndAddsSilentAudio()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClipStitch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string red = System.IO.Path.Combine(directory, "clip 1 ü.mp4");
            string blue = System.IO.Path.Combine(directory, "clip 2.mp4");
            string output = System.IO.Path.Combine(directory, "result.mp4");
            await Ffmpeg("-f", "lavfi", "-i", "color=c=red:s=320x240:r=25:d=0.5",
                "-f", "lavfi", "-i", "color=c=green:s=320x240:r=25:d=0.5",
                "-f", "lavfi", "-i", "color=c=yellow:s=320x240:r=25:d=1",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]", "-map", "[v]", "-map", "3:a",
                "-c:v", "libx264", "-c:a", "aac", "-shortest", red);
            await Ffmpeg("-f", "lavfi", "-i", "color=c=blue:s=640x360:r=30:d=1",
                "-c:v", "libx264", blue);
            var one = await MediaTools.ProbeAsync(red, CancellationToken.None);
            var two = await MediaTools.ProbeAsync(blue, CancellationToken.None);
            one.Ranges.Clear();
            one.Ranges.Add(new KeepRange(0, 0.5));
            one.Ranges.Add(new KeepRange(1, 1.5));
            two.Ranges.Clear();
            two.Ranges.Add(new KeepRange(0, 1));
            var segments = TimelineSections.Build([one, two],
                [new KeepRange(0, 0.5), new KeepRange(1, 1.5), new KeepRange(one.Duration, one.Duration + 1)]);
            await MediaTools.ExportAsync(segments, output, 21, "veryfast", false, new Progress<double>(), CancellationToken.None);
            var result = await MediaTools.ProbeAsync(output, CancellationToken.None);
            Assert.InRange(result.Duration, 1.9, 2.12);
            Assert.Equal(320, result.Width);
            Assert.Equal(240, result.Height);
            Assert.True(result.HasAudio);
            var firstPixel = await SamplePixel(output, 0.25);
            var secondPixel = await SamplePixel(output, 0.75);
            var thirdPixel = await SamplePixel(output, 1.5);
            Assert.True(firstPixel[0] > firstPixel[2] + 100, "First retained segment should be red.");
            Assert.True(secondPixel[0] > 150 && secondPixel[1] > 150 && secondPixel[2] < 100,
                "The second retained segment should be yellow; the green section should be skipped.");
            Assert.True(thirdPixel[2] > thirdPixel[0] + 100, "Last retained segment should be blue.");
            await Assert.ThrowsAsync<IOException>(() => MediaTools.ExportAsync(segments, output, 21, "veryfast", false, new Progress<double>(), CancellationToken.None));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            string cancelled = System.IO.Path.Combine(directory, "cancelled.mp4");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaTools.ExportAsync(segments, cancelled, 21, "veryfast", false, new Progress<double>(), cancel.Token));
            Assert.False(File.Exists(cancelled));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(3, 2.5)]
    [InlineData(30, 29.9)]
    public async Task ThumbnailIsGeneratedWhenRequestedPositionIsPastVideoStream(double duration, double requested)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClipStitch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = System.IO.Path.Combine(directory, "audio-tail.mp4");
            await Ffmpeg("-f", "lavfi", "-i", "color=c=red:s=64x64:r=25:d=1",
                "-f", "lavfi", "-i", $"sine=frequency=440:duration={TimeText.Number(duration)}",
                "-c:v", "libx264", "-c:a", "aac", path);
            var video = await MediaTools.ProbeAsync(path, CancellationToken.None);
            Assert.InRange(video.Duration, duration - 0.1, duration + 0.1);
            var image = await MediaTools.ThumbnailAsync(video, requested, CancellationToken.None);
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            converted.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.True(pixel[2] > pixel[0] + 100, "A frame from the red video should be produced even after its video stream ends.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task Ffmpeg(params string[] args)
    {
        var result = await MediaTools.RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", .. args], CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    private static async Task<byte[]> SamplePixel(string file, double position)
    {
        string ffmpeg = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-ss", TimeText.Number(position), "-i", file, "-frames:v", "1",
            "-vf", "format=rgb24,crop=1:1:160:120", "-f", "rawvideo", "pipe:1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        string error = await process.StandardError.ReadToEndAsync();
        await copy;
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output.ToArray();
    }
}
