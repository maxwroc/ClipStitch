# ClipStitch

A Windows application for selecting and joining sections from videos in a folder.

## Run

Install the .NET 10 Windows Desktop Runtime (x64), then run `ClipStitch.exe` from the published folder. Keep the executable, its accompanying files, and the `tools` folder together. FFmpeg, FFprobe, and LibVLC are included; no separate installation of those tools is necessary.

1. Select **Open folder**. Videos directly in the folder appear in natural filename order; uncheck a video to omit it. Files with a `.dat` extension are checked for video content and included only if they contain video.
2. The thumbnail timeline spans all loaded videos, including unchecked files; thin blue dividers indicate file boundaries. The white pointer and line mark the current position, including at selection edges. Initially, one keep selection covers the timeline. Unselected footage is lightly shaded, leaving the frames visible. Click anywhere on the timeline to seek without changing whether playback is playing or paused. Scroll the mouse wheel to zoom around the pointer and use the horizontal scrollbar to navigate. Thumbnails refresh for the visible area after resizing finishes, including maximizing/restoring. While loading, a centered progress indicator dims the timeline and blocks clicks; wheel zoom still works.
3. Use the green selection controls above the timeline: previous selection, play selection, scissors to split, join previous/next, next selection, and trash to remove. Hover over any button for its tooltip. Controls are enabled only when the playhead is inside a selection; navigation/join also require a neighbor, split requires a position strictly inside, and the last remaining selection cannot be removed. Joining restores any gap between the selections. Splitting/joining stops playback; removing a selection keeps the current position and playing/paused state unchanged.
4. Drag the current selection's green handles to trim and preview a boundary frame. The handles sit outside the selection, with their inner edges marking the exact cuts. Wheel zoom during dragging stays anchored to the boundary. Releasing a handle starts playback at the new start, or five seconds before the new end (limited to the selection's start), then continues through following selections. Previous/next and the bracketed green Play icon also start playback at the target selection's beginning and continue through following selections.
5. The neutral player controls are Play/Pause, Stop, and Volume. Play starts at the first playable frame of the first selection, unless explicitly paused, when it resumes from the paused position (including a position changed by clicking the timeline). Stop returns to that first frame without playing. The speaker opens a floating volume slider that closes on outside click, Escape, or window deactivation. Playback jumps from each selection's end to the next selection, skipping gaps and unchecked videos, and holds the final frame after the last selection. Source transitions keep the last displayed frame visible; same-file transitions seek without reloading. Preview cut points are approximate; export processes them precisely.
6. Choose output quality and encoding speed at the bottom left under the timeline, then click the save icon at the bottom right. A dialog proposes saving into the opened folder, named after that folder (for example `Holiday.mp4`); the name without `.mp4` is preselected, and **Change folder…** picks another location. A progress window blocks the app while saving; **Cancel export** leaves the destination unchanged. Lower quality numbers mean higher quality and larger files; slower presets generally improve compression at the cost of processing time. Output is H.264 video and AAC audio. Videos of different sizes are fitted into the first retained video's frame with black padding where necessary. Missing audio is replaced with silence.

Selections are not saved when the app closes. Export includes only kept portions of checked videos.

Manual seeks into shaded gaps preserve the clicked position and transport state: if playing, footage continues normally until the next selection's end, then jumps to following selections. Seeking after the final selection lets playback reach the timeline's natural end. Removing a selection also removes its automatic end boundary without interrupting playback. These inspection actions do not add unselected footage to export; use the play controls to preview only kept selections.

Timeline seeks keep playback running even if decoding or UI updates are delayed, or a source resumes at an imprecise timestamp. Newly opened sources initialize their video decoder before seeking. An outstanding still-frame pause cannot override a newer Play request.

Thumbnail positions are approximate: sampling avoids the last five seconds of each video (20% of the duration for videos shorter than 25 seconds) and uses fast keyframe-based seeking. If that produces no image, the app retries five seconds earlier, then tries the opening frame if necessary; it no longer decodes the whole video to recover a thumbnail. Playback and export positions are unchanged.

## Build

Install the .NET 10 SDK and run:

```powershell
dotnet test tests\tests.csproj
dotnet publish ClipStitch.csproj -c Release -r win-x64 --self-contained false -o publish
```

The FFmpeg 8.1.2 essentials binaries are expected in `tools\ffmpeg\bin`; they are copied to the build and publish directories. They are not stored in this repository, so download them before building. The project includes `NuGet.Config` to restore packages from nuget.org. To create the tool folder, download the `ffmpeg-8.1.2-essentials_build.zip` release from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), verify its published SHA-256, and extract the archive's `bin\ffmpeg.exe`, `bin\ffprobe.exe`, `LICENSE`, and `README.txt` into `tools\ffmpeg`. The application requires an FFmpeg build with `libx264` and AAC support.

## Third-party components

The bundled FFmpeg build is distributed by Gyan Doshi under GPLv3 and contains libx264; its license and build details are in `tools\ffmpeg`. The corresponding FFmpeg source and build information are linked from [the build provider](https://www.gyan.dev/ffmpeg/builds/). LibVLC and LibVLCSharp are provided by VideoLAN through NuGet; see their package licenses. Review all applicable licenses and source-distribution obligations before redistributing this application.
