# ClipStitch

A Windows application for selecting and joining sections from videos in a folder.

## Run

Install the .NET 10 Windows Desktop Runtime (x64), then run `ClipStitch.exe` from the published folder. Keep the executable, its accompanying files, and the `tools` folder together. FFmpeg, FFprobe, and LibVLC are included; no separate installation of those tools is necessary.

1. Select **Open folder**. Videos directly in the folder appear in natural filename order; uncheck a video to omit it. Files with a `.dat` extension are checked for video content and included only if they contain video.
2. The thumbnail timeline spans the entire length of all loaded videos, including unchecked files; thin blue dividers indicate file boundaries. The white playhead marks the current position. Scroll the mouse wheel over the timeline to zoom around the pointer and use the horizontal scrollbar to navigate; thumbnails refresh for the visible area. While they load (for example after resizing the window, zooming, or scrolling), the timeline is dimmed, a centered progress indicator is shown, and clicks on the timeline are blocked; wheel zoom still works. Initially, one keep section covers the entire timeline. Click the thumbnail timeline to play from that exact position, or click a section in the list to play it from the beginning. Drag the selected section's green arrow handles to trim its start or end and preview the boundary frame. The handles sit just outside the section, so the inner edge of each handle is the exact cut point and the playhead reaches it precisely; a narrow dark margin at both ends of the timeline leaves room for a handle at the very start or end. While dragging, wheel zoom is centered on that boundary. On release, playback starts at the new start, or five seconds before the new end (limited to the section's start), and stops at that section's end. The **Continue** button (a selection bracket followed by a double play triangle) is then enabled: it keeps playing from the current position through the rest of that section and the following kept sections only, skipping removed footage. It is disabled when there is nothing left to play or after any other playback or handle drag starts. Alternatively, enter times (or use **Use playhead**) and click **Update selected**. Times can be entered as `hh:mm:ss.fff` or seconds.
3. Position the playhead inside the selected section and click **New section (split)** to create two sections. Trim or remove sections as needed. **Join back with previous** and **Join back with next** combine neighboring sections, restoring any gap between them. Sections cannot overlap; their boundaries can meet. Unchecked videos are skipped without shifting the original timeline positions.
4. **Play from selection** (a green play triangle between selection brackets) starts at the selected section's beginning and continues through subsequent kept sections and files. The play-from-start icon previews all sections. Hover over the playback icons for their labels. Pause/resume preserves the sequence; Stop ends it and keeps the current frame on screen. The preview always shows a frame: the first frame when a video is selected or loaded, the last displayed frame while the next file loads, and the final frame when playback ends. The preview may pause while loading another file and its cut points are approximate. Transitions within the same playing file use seeking instead of reloading. Export uses the same section order but performs precise processing; compression and scaling are not represented in preview.
5. Choose quality and speed, then click **Save joined video…** (right of the playback buttons). A dialog proposes saving into the opened folder, named after that folder (for example `Holiday.mp4`); the name without `.mp4` is preselected so you can type a new one immediately, and **Change folder…** picks another location. While saving, a progress window blocks the rest of the app until the export finishes, fails, or is cancelled with **Cancel export**. Lower quality numbers mean higher quality and larger files; slower presets generally improve compression at the cost of processing time. Output is H.264 video and AAC audio. Videos of different sizes are fitted into the first retained video's frame with black padding where necessary. Missing audio is replaced with silence.

Selections are not saved when the app closes. Changes to selections stop an active combined preview. A video with no keep sections contributes no footage.

Clicking the thumbnail timeline inside a kept section plays the rest of that section and then jumps to the beginning of each following section; the playhead, selected section, and visible timeline area jump with it. Clicking in a gap plays through to the end of the next section, then continues with the following sections; after the last section it plays to the end of the timeline. Use the play controls to preview only the kept sections.

## Build

Install the .NET 10 SDK and run:

```powershell
dotnet test tests\tests.csproj
dotnet publish ClipStitch.csproj -c Release -r win-x64 --self-contained false -o publish
```

The FFmpeg 8.1.2 essentials binaries are expected in `tools\ffmpeg\bin`; they are copied to the build and publish directories. They are not stored in this repository, so download them before building. The project includes `NuGet.Config` to restore packages from nuget.org. To create the tool folder, download the `ffmpeg-8.1.2-essentials_build.zip` release from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), verify its published SHA-256, and extract the archive's `bin\ffmpeg.exe`, `bin\ffprobe.exe`, `LICENSE`, and `README.txt` into `tools\ffmpeg`. The application requires an FFmpeg build with `libx264` and AAC support.

## Third-party components

The bundled FFmpeg build is distributed by Gyan Doshi under GPLv3 and contains libx264; its license and build details are in `tools\ffmpeg`. The corresponding FFmpeg source and build information are linked from [the build provider](https://www.gyan.dev/ffmpeg/builds/). LibVLC and LibVLCSharp are provided by VideoLAN through NuGet; see their package licenses. Review all applicable licenses and source-distribution obligations before redistributing this application.
