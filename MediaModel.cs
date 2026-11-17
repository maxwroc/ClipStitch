using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClipStitch;

public sealed class VideoItem : INotifyPropertyChanged
{
    private bool included = true;
    public required string Path { get; init; }
    public string ContainerFormat { get; init; } = "";
    public string? PreviewPath { get; internal set; }
    public string Name => System.IO.Path.GetFileName(Path);
    public required double Duration { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double FrameRate { get; init; }
    public required bool HasAudio { get; init; }
    public int VideoStreamIndex { get; init; }
    public int AudioStreamIndex { get; init; }
    public ObservableCollection<KeepRange> Ranges { get; } = [];
    public bool Included
    {
        get => included;
        set { included = value; OnPropertyChanged(); }
    }
    public string Description => $"{Name}  ({TimeText.Format(Duration)})";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record KeepRange(double Start, double End);

public sealed record Segment(VideoItem Video, double Start, double End)
{
    public double Duration => End - Start;
}

public static class TimelineZoom
{
    public static (double Zoom, double Offset) AtPointer(double zoom, double offset, double viewport, double pointer, int wheelDelta)
    {
        if (!double.IsFinite(zoom) || !double.IsFinite(offset) || !double.IsFinite(viewport) ||
            !double.IsFinite(pointer) || zoom < 1 || viewport <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewport), "A finite, positive timeline viewport is required.");
        pointer = Math.Clamp(pointer, 0, viewport);
        double anchor = Math.Clamp((offset + pointer) / (viewport * zoom), 0, 1);
        double next = Math.Clamp(zoom * Math.Pow(1.35, wheelDelta / 120d), 1, 256);
        double nextOffset = Math.Clamp(anchor * viewport * next - pointer, 0, viewport * (next - 1));
        return (next, nextOffset);
    }
}

public static class TimelineSections
{
    public static KeepRange BoundaryPreview(KeepRange section, bool startMarker) =>
        new(startMarker ? section.Start : Math.Max(section.Start, section.End - 5), section.End);

    /// <summary>Ranges to play after a timeline click: the remainder of the clicked section, or the gap up to the next section, followed by the later sections.</summary>
    public static List<KeepRange> PlayFrom(IEnumerable<KeepRange> sections, double time, double total)
    {
        var ordered = sections.OrderBy(r => r.Start).ToList();
        int index = ordered.FindIndex(r => time < r.End);
        if (index < 0) return [new KeepRange(time, total)];
        return [new KeepRange(time, ordered[index].End), .. ordered.Skip(index + 1)];
    }

    public static int IndexAt(IReadOnlyList<KeepRange> sections, double time)
    {
        for (int i = 0; i < sections.Count; i++)
            if (time >= sections[i].Start && time < sections[i].End) return i;
        return -1;
    }

    public static List<KeepRange> Gaps(IEnumerable<KeepRange> sections, double total)
    {
        var result = new List<KeepRange>();
        double end = 0;
        foreach (var section in sections.OrderBy(s => s.Start))
        {
            if (section.Start > end) result.Add(new KeepRange(end, section.Start));
            end = Math.Max(end, section.End);
        }
        if (end < total) result.Add(new KeepRange(end, total));
        return result;
    }

    public static List<Segment> Build(IEnumerable<VideoItem> source, IEnumerable<KeepRange> sections)
    {
        var videos = source.ToArray();
        var ordered = sections.OrderBy(r => r.Start).ToArray();
        double total = videos.Sum(v => v.Duration);
        double previousEnd = 0;
        foreach (var section in ordered)
        {
            if (!double.IsFinite(section.Start) || !double.IsFinite(section.End) ||
                section.Start < previousEnd || section.Start < 0 || section.End > total || section.Start >= section.End)
                throw new InvalidOperationException("Sections must be within the timeline and must not overlap.");
            previousEnd = section.End;
        }
        var result = new List<Segment>();
        foreach (var section in ordered)
        {
            double offset = 0;
            foreach (var video in videos)
            {
                double start = Math.Max(section.Start, offset);
                double end = Math.Min(section.End, offset + video.Duration);
                if (video.Included && end > start)
                    result.Add(new Segment(video, start - offset, end - offset));
                offset += video.Duration;
            }
        }
        return result;
    }

    public static (KeepRange Left, KeepRange Right) Split(KeepRange section, double position)
    {
        if (!double.IsFinite(position) || position <= section.Start || position >= section.End)
            throw new InvalidOperationException("Place the playhead inside the selected section to split it.");
        return (new KeepRange(section.Start, position), new KeepRange(position, section.End));
    }

    public static KeepRange Join(KeepRange first, KeepRange second) =>
        new(Math.Min(first.Start, second.Start), Math.Max(first.End, second.End));
}

public static class SegmentBuilder
{
    public static List<Segment> Build(IEnumerable<VideoItem> videos)
    {
        var result = new List<Segment>();
        foreach (var video in videos.Where(v => v.Included))
        {
            double? start = null;
            double end = 0;
            foreach (var range in video.Ranges.OrderBy(r => r.Start).ThenBy(r => r.End))
            {
                if (!double.IsFinite(range.Start) || !double.IsFinite(range.End) ||
                    range.Start < 0 || range.End > video.Duration || range.Start >= range.End)
                    throw new InvalidOperationException($"Invalid keep range in {video.Name}.");
                if (start is null) { start = range.Start; end = range.End; }
                else if (range.Start <= end) end = Math.Max(end, range.End);
                else { result.Add(new Segment(video, start.Value, end)); start = range.Start; end = range.End; }
            }
            if (start is not null) result.Add(new Segment(video, start.Value, end));
        }
        return result;
    }
}

public static class TimeText
{
    public static string Format(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    public static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class NaturalPathComparer : IComparer<string>
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return StringComparer.Ordinal.Compare(x, y);
        var natural = StrCmpLogicalW(System.IO.Path.GetFileName(x), System.IO.Path.GetFileName(y));
        return natural != 0 ? natural : StringComparer.Ordinal.Compare(x, y);
    }
}
