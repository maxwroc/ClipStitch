using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace ClipStitch;

public static class MediaTools
{
    private static string Tool(string name)
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", name + ".exe");
        if (!File.Exists(path)) throw new FileNotFoundException($"Missing {name}.exe. Rebuild with the bundled FFmpeg tools.", path);
        return path;
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string name, IEnumerable<string> arguments, CancellationToken token, Action<string>? output = null)
    {
        var info = new ProcessStartInfo(Tool(name))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {name}.");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = Task.Run(async () =>
        {
            var result = new StringBuilder();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (output is null) result.AppendLine(line);
                else output(line);
            }
            return result.ToString();
        });
        await process.WaitForExitAsync();
        var text = await stderr;
        var outputText = await stdout;
        token.ThrowIfCancellationRequested();
        return (process.ExitCode, outputText, text);
    }

    public static async Task<VideoItem> ProbeAsync(string path, CancellationToken token)
    {
        var result = await RunAsync("ffprobe", ["-v", "error", "-show_format", "-show_streams", "-of", "json", path], token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Stderr.Trim());
        using var doc = JsonDocument.Parse(result.Stdout);
        var root = doc.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => GetString(s, "codec_type") == "video" &&
            (!s.TryGetProperty("disposition", out var d) || !d.TryGetProperty("attached_pic", out var a) || a.GetInt32() == 0));
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("No video stream found.");
        double duration = ParseDouble(GetString(root.GetProperty("format"), "duration"));
        if (duration <= 0) duration = ParseDouble(GetString(video, "duration"));
        if (duration <= 0 || !double.IsFinite(duration)) throw new InvalidOperationException("Video duration is unavailable.");
        int width = video.GetProperty("width").GetInt32(), height = video.GetProperty("height").GetInt32();
        int rotation = 0;
        if (video.TryGetProperty("tags", out var tags)) int.TryParse(GetString(tags, "rotate"), out rotation);
        if (video.TryGetProperty("side_data_list", out var sideData))
            foreach (var item in sideData.EnumerateArray())
                if (item.TryGetProperty("rotation", out var r)) rotation = r.GetInt32();
        if (Math.Abs(rotation % 180) == 90) (width, height) = (height, width);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Video dimensions are unavailable.");
        var rate = GetString(video, "avg_frame_rate").Split('/');
        double fps = rate.Length == 2 && ParseDouble(rate[1]) != 0 ? ParseDouble(rate[0]) / ParseDouble(rate[1]) : 0;
        if (!double.IsFinite(fps) || fps <= 0 || fps > 240) fps = 30;
        var audio = streams.FirstOrDefault(s => GetString(s, "codec_type") == "audio");
        var itemResult = new VideoItem { Path = path, Duration = duration, Width = width, Height = height,
            FrameRate = fps, HasAudio = audio.ValueKind != JsonValueKind.Undefined,
            VideoStreamIndex = video.GetProperty("index").GetInt32(),
            AudioStreamIndex = audio.ValueKind == JsonValueKind.Undefined ? 0 : audio.GetProperty("index").GetInt32() };
        itemResult.Ranges.Add(new KeepRange(0, duration));
        return itemResult;
    }

    private static string GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    public static async Task<BitmapImage?> ThumbnailAsync(VideoItem video, double time, CancellationToken token)
    {
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clipstitch-{Guid.NewGuid():N}.jpg");
        try
        {
            var r = await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-ss", TimeText.Number(time),
                "-i", video.Path, "-frames:v", "1", "-vf", "scale=160:90:force_original_aspect_ratio=decrease", "-y", file], token);
            if (r.ExitCode != 0) throw new InvalidOperationException(r.Stderr.Trim());
            if (!File.Exists(file)) throw new InvalidOperationException("No thumbnail frame was produced.");
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    public static async Task ExportAsync(IReadOnlyList<Segment> segments, string destination, int crf, string preset,
        bool overwriteConfirmed, IProgress<double> progress, CancellationToken token)
    {
        if (segments.Count == 0) throw new InvalidOperationException("Select at least one section to keep.");
        string target = System.IO.Path.GetFullPath(destination);
        if (segments.Any(s => string.Equals(System.IO.Path.GetFullPath(s.Video.Path), target, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The output must not overwrite an input video.");
        if (File.Exists(target) && !overwriteConfirmed) throw new IOException("The output file already exists.");
        string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, $".clipstitch-{Guid.NewGuid():N}.mp4");
        int width = Math.Max(2, segments[0].Video.Width / 2 * 2);
        int height = Math.Max(2, segments[0].Video.Height / 2 * 2);
        string fps = TimeText.Number(segments[0].Video.FrameRate);
        double total = segments.Sum(s => s.Duration);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        foreach (var s in segments) { args.Add("-i"); args.Add(s.Video.Path); }
        var filters = new StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            string start = TimeText.Number(s.Start), end = TimeText.Number(s.End), duration = TimeText.Number(s.Duration);
            filters.Append($"[{i}:{s.Video.VideoStreamIndex}]trim=start={start}:end={end},setpts=PTS-STARTPTS,scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},format=yuv420p[v{i}];");
            if (s.Video.HasAudio)
                filters.Append($"[{i}:{s.Video.AudioStreamIndex}]atrim=start={start}:end={end},asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,apad,atrim=duration={duration}[a{i}];");
            else
                filters.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={duration},asetpts=PTS-STARTPTS[a{i}];");
        }
        for (int i = 0; i < segments.Count; i++) filters.Append($"[v{i}][a{i}]");
        filters.Append($"concat=n={segments.Count}:v=1:a=1[outv][outa]");
        args.AddRange(["-filter_complex", filters.ToString(), "-map", "[outv]", "-map", "[outa]",
            "-c:v", "libx264", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-preset", preset,
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-progress", "pipe:1", "-y", temp]);
        try
        {
            var result = await RunAsync("ffmpeg", args, token, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
                    progress.Report(Math.Clamp(us / 1_000_000d / total, 0, 1));
            });
            if (result.ExitCode != 0) throw new InvalidOperationException("FFmpeg export failed:\n" + result.Stderr.Trim());
            token.ThrowIfCancellationRequested();
            if (File.Exists(target) && !overwriteConfirmed) throw new IOException("The output file appeared during export; it was not replaced.");
            File.Move(temp, target, overwriteConfirmed);
            progress.Report(1);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
