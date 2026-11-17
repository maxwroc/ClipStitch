using System.IO;

namespace ClipStitch;

internal sealed class PreviewCache(CancellationToken folderToken) : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(folderToken);
    private readonly SemaphoreSlim access = new(1, 1);
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"ClipStitch-preview-{Guid.NewGuid():N}");

    public async Task PrepareAsync(VideoItem video)
    {
        if (video.ContainerFormat != "mpeg") return;
        var token = cancellation.Token;
        await access.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{Guid.NewGuid():N}.mkv");
            // VLC's MPEG-PS demuxer seeks by byte fraction, not by an index of timestamps.
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-fflags", "+genpts", "-i", video.Path,
                "-map", $"0:{video.VideoStreamIndex}" };
            if (video.HasAudio) args.AddRange(["-map", $"0:{video.AudioStreamIndex}"]);
            string[] output = ["-avoid_negative_ts", "disabled", "-f", "matroska", "-y", path];
            var result = await MediaTools.RunAsync("ffmpeg", [.. args, "-c", "copy", .. output], token).ConfigureAwait(false);
            if (result.ExitCode != 0 && result.Stderr.Contains("Can't write packet with unknown timestamp", StringComparison.Ordinal))
            {
                result = await MediaTools.RunAsync("ffmpeg", [.. args,
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", "-fps_mode", "passthrough",
                    "-c:a", "pcm_s16le", .. output], token).ConfigureAwait(false);
            }
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Could not prepare indexed preview for {video.Name}:\n{result.Stderr.Trim()}");
            token.ThrowIfCancellationRequested();
            video.PreviewPath = path;
        }
        finally { access.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        await access.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        finally
        {
            access.Release();
            access.Dispose();
            cancellation.Dispose();
        }
    }
}
