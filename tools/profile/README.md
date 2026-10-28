# Export profiling

This harness runs the same preparation, renderer and encoder used by the Avalonia desktop export. It leaves
images, videos, FFmpeg commands/benchmark output and `timings.json` in the output directory.
Use a fresh output directory for each comparison. Defaults are 540×960, 60 FPS, speed 20 and
CPU encoding so GPU availability does not change between runs.

```powershell
dotnet run --project tools/profile -c Release -- map.osu replay.osr out/profile/run C:/ffmpeg/ffmpeg.exe libx264 3
```

The last optional argument `render-only` skips FFmpeg. `appCpuMs` and `allocatedMb` describe the
.NET process; FFmpeg CPU time appears in `ffmpeg.log` as `bench: utime=… stime=… rtime=…`.
Compare medians of several runs with the same input, settings, FFmpeg version and power mode.

For managed call stacks, build first and launch the resulting DLL under `dotnet-trace`:

```powershell
dotnet build tools/profile -c Release
dotnet-trace collect --profile cpu-sampling --format Speedscope --output out/profile/export.nettrace -- dotnet tools/profile/bin/Release/net8.0-windows/ManiaReplayMaster.Profile.dll map.osu replay.osr out/profile/traced C:/ffmpeg/ffmpeg.exe
dotnet-trace report out/profile/export.nettrace topN --inclusive
```

Open the generated `.speedscope.json` in a Speedscope viewer to inspect stacks. Blocking pipe
reads and process waits are elapsed time, not CPU work. EventPipe does not sample FFmpeg's
native stacks; its log and controlled filter comparisons measure that part separately.
