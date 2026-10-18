// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Encode.Compat;

namespace ManiaReplayMaster.Encode;

/// <summary>
/// Inputs of one <c>generateVideo()</c> pass in <c>Encoder.kt</c>: either the single/final pass
/// (all windows at once, audio mixed in) or one slice of the <c>ffmpegMaxProcessingSize</c>
/// two-stage behaviour (no audio).
/// </summary>
public sealed record FfmpegVideoRequest
{
    /// <summary><c>Config.width</c>, or <c>width / 4</c> for taiko single column.</summary>
    public required int GameWidth { get; init; }

    /// <summary><c>Config.height</c> (the <c>H</c> of the overlay expression).</summary>
    public required int GameHeight { get; init; }

    public required int FramePerSecond { get; init; }

    public required int Speed { get; init; }

    /// <summary><c>windowFrameCount / framePerSecond</c>.</summary>
    public required double SliceDuration { get; init; }

    /// <summary>The long-strip PNGs of this pass, in window order.</summary>
    public required IReadOnlyList<string> ImagePaths { get; init; }

    public required bool IsTaiko { get; init; }

    /// <summary>True for the single all-in-one pass, false for a middle slice pass.</summary>
    public required bool IsFinal { get; init; }

    /// <summary>Only used when <see cref="IsFinal"/> is true (it becomes input 0).</summary>
    public required string BgmPath { get; init; }

    public required double Rate { get; init; }

    /// <summary><c>delay + beatMap.bgmOffset</c>.</summary>
    public required int AudioDelay { get; init; }

    public required string OutPath { get; init; }

    public required string Codec { get; init; }

    public required string CodecPreset { get; init; }

    public required string CodecProfile { get; init; }
}

/// <summary>Inputs of <c>mergeSlices()</c> in <c>Encoder.kt</c>.</summary>
public sealed record FfmpegMergeRequest
{
    public required string ConcatListPath { get; init; }

    public required string BgmPath { get; init; }

    public required double Rate { get; init; }

    public required int AudioDelay { get; init; }

    public required string OutPath { get; init; }
}

/// <summary>
/// Builds the ffmpeg argv of the reference implementation (rewrite plan route E1), with
/// documented compatibility and pixel-alignment fixes.
///
/// The returned argument lists do not include the FFmpeg executable itself.
///
/// Single passes concatenate raw frames; multi-pass exports stream-copy the encoded slices
/// through the concat demuxer and apply audio tempo/delay only once at the final merge.
/// </summary>
public static class FfmpegCommandBuilder
{
    /// <summary>
    /// <c>-f lavfi -t 1 -i anullsrc &lt;path&gt; -y</c> -- silence placeholder written next to the
    /// rendered images when the beatmap has no playable BGM.
    /// </summary>
    public static List<string> BuildMuteSound(string bgmPath)
        => new() { "-f", "lavfi", "-t", "1", "-i", "anullsrc", bgmPath, "-y" };

    /// <summary>Port of <c>Encoder.generateVideo(IntRange, File, Boolean, String, Int, Int)</c>.</summary>
    public static List<string> BuildVideo(FfmpegVideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var filterGraph = new List<string>();
        var subcommands = new List<string>();
        var outVideos = new List<string>();
        var count = request.ImagePaths.Count;
        var isTaikoSuffix = request.IsTaiko ? ",transpose=1" : string.Empty;
        // Crop's frame index is an integer; time-based expressions can lose a pixel to rounding.
        var scroll = FormattableString.Invariant($"n*{request.Speed}");

        for (var i = 0; i < count; i++)
        {
            // In the final pass input 0 is the BGM, so the window images start at input index 1.
            var inputFileIndex = request.IsFinal ? i + 1 : i;

            // Everything numeric is formatted with the invariant culture: the filter graph must not
            // depend on the machine's decimal separator or digit shapes.
            // Rendered strips are opaque and cover the entire viewport. Moving a crop over
            // them produces the same frames without generating and blending a black canvas.
            // Keep 4:4:4 until after cropping so odd scroll speeds retain exact chroma positions.
            // Decode and convert each static PNG once, then repeat the cached frame. The input
            // time base is 1/FPS, so integer PTS avoids fractional rounding at unusual frame rates.
            filterGraph.Add(FormattableString.Invariant(
                $"[{inputFileIndex}]format=yuv444p,loop=loop=-1:size=1:start=0,setpts=N,crop=w={request.GameWidth}:h={request.GameHeight}:x=0:y=ih-oh-{scroll}:exact=1[video{i}l]"));
            filterGraph.Add(
                $"[video{i}l]trim=end={JavaDouble.ToString(request.SliceDuration)}{isTaikoSuffix}[video{i}]");

            outVideos.Add($"[video{i}]");
            subcommands.Add("-framerate");
            subcommands.Add(request.FramePerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
            subcommands.Add("-i");
            subcommands.Add(request.ImagePaths[i]);
        }

        filterGraph.Add(FormattableString.Invariant(
            $"{string.Concat(outVideos)}concat=n={count}:v=1[video]"));

        var command = new List<string>();
        if (request.IsFinal)
        {
            command.Add("-i");
            command.Add(request.BgmPath);
            filterGraph.Add($"[0:a]atempo={JavaDouble.ToString(request.Rate)}[a1]");
            filterGraph.Add(FormattableString.Invariant(
                $"[a1]adelay={request.AudioDelay}|{request.AudioDelay}[audio]"));
        }

        command.AddRange(subcommands);
        command.Add("-filter_complex");
        command.Add(string.Join(";", filterGraph));
        command.Add("-map");
        command.Add("[video]");
        if (request.IsFinal)
        {
            command.Add("-map");
            command.Add("[audio]");
        }

        AppendEncoderOptions(command, request.OutPath, request.Codec, request.CodecPreset, request.CodecProfile);
        return command;
    }

    /// <summary>Concatenates encoded slices without decoding and encoding the video a second time.</summary>
    public static List<string> BuildMerge(FfmpegMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var filterGraph = new List<string>
        {
            $"[0:a]atempo={JavaDouble.ToString(request.Rate)}[a1]",
            FormattableString.Invariant($"[a1]adelay={request.AudioDelay}|{request.AudioDelay}[audio]")
        };

        var command = new List<string>
        {
            "-i", request.BgmPath,
            "-f", "concat", "-safe", "0", "-i", request.ConcatListPath
        };

        command.Add("-filter_complex");
        command.Add(string.Join(";", filterGraph));
        command.Add("-map");
        command.Add("1:v:0");
        command.Add("-map");
        command.Add("[audio]");
        command.AddRange(new[] { "-c:v", "copy", "-benchmark", request.OutPath });
        return command;
    }

    public static IEnumerable<string> BuildConcatList(IEnumerable<string> slicePaths)
    {
        foreach (var path in slicePaths)
        {
            // ffconcat uses its own quoting rules, independently of ProcessStartInfo.ArgumentList.
            var absolute = Path.GetFullPath(path);
            if (absolute.Contains('\r') || absolute.Contains('\n'))
                throw new ArgumentException("A concat input path cannot contain a line break.", nameof(slicePaths));
            yield return "file '" + absolute.Replace('\\', '/').Replace("'", "'\\''") + "'";
        }
    }

    /// <summary>
    /// The set of H.264 profile names that libx264 actually accepts for <c>-profile:v</c>.
    /// Anything else must be omitted rather than passed through.
    /// </summary>
    private static readonly HashSet<string> KnownH264Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "baseline", "main", "high", "high10", "high422", "high444"
    };

    /// <summary>
    /// <c>-c:v {codec} -preset:v {codecPreset} -pix_fmt yuv420p [-profile:v {codecProfile}] -benchmark {out}</c>.
    ///
    /// DELIBERATE DIVERGENCE FROM THE REFERENCE. <c>Encoder.kt</c> always emits
    /// <c>-profile:v {codecProfile}</c>, and the shipped <c>Config</c> default is the literal
    /// string <c>"unknown"</c>. That is ffmpeg's integer <c>AV_PROFILE_UNKNOWN</c> sentinel, but as
    /// a *string argument* libx264 rejects it ("invalid profile: unknown"), so the reference fails
    /// to encode with its own default config -- verified on this machine against the shipped
    /// v2.4.0 jar, which aborts at the first pass with "Conversion failed!". The port therefore
    /// forwards <c>-profile:v</c> only when the value names a real profile, which keeps the
    /// reference's intended behaviour (no profile constraint by default) while actually working.
    /// </summary>
    private static void AppendEncoderOptions(List<string> command, string outPath, string codec,
        string codecPreset, string codecProfile)
    {
        AppendVideoEncoderOptions(command, codec, codecPreset, codecProfile);
        command.Add("-benchmark");
        command.Add(outPath);
    }

    internal static void AppendVideoEncoderOptions(List<string> command, string codec,
        string codecPreset, string codecProfile)
    {
        if (string.IsNullOrWhiteSpace(codecPreset) || VideoEncoderSelector.IsAutomatic(codecPreset))
            codecPreset = VideoEncoderSelector.DefaultPreset(codec);

        command.Add("-c:v");
        command.Add(codec);
        command.Add(codec == "h264_amf" ? "-quality:v" : "-preset:v");
        command.Add(codecPreset);
        switch (codec)
        {
            case "h264_nvenc":
                command.AddRange(new[] { "-rc:v", "vbr", "-cq:v", "23", "-b:v", "0" });
                break;
            case "h264_qsv":
                command.AddRange(new[] { "-global_quality:v", "23" });
                break;
            case "h264_amf":
                command.AddRange(new[] { "-rc:v", "cqp", "-qp_i:v", "23", "-qp_p:v", "23", "-qp_b:v", "23" });
                break;
        }
        command.Add("-pix_fmt");
        command.Add("yuv420p");

        if (!string.IsNullOrWhiteSpace(codecProfile) && KnownH264Profiles.Contains(codecProfile.Trim()))
        {
            command.Add("-profile:v");
            command.Add(codecProfile.Trim());
        }
    }
}
