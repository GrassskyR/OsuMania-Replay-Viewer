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
    public required IReadOnlyList<string> SlicePaths { get; init; }

    public required string BgmPath { get; init; }

    public required double Rate { get; init; }

    public required int AudioDelay { get; init; }

    public required string OutPath { get; init; }

    public required string Codec { get; init; }

    public required string CodecPreset { get; init; }

    public required string CodecProfile { get; init; }
}

/// <summary>
/// Builds the ffmpeg argv of the reference implementation (rewrite plan route E1), with
/// documented compatibility and pixel-alignment fixes.
///
/// These methods are pure and deliberately public: the ffmpeg command line -- in particular the
/// whole <c>-filter_complex</c> string -- is the highest-value regression surface of this layer,
/// and it can be pinned by a test without running ffmpeg at all.
///
/// The returned lists do NOT include the ffmpeg executable itself; <see cref="Encoder"/> prepends
/// it exactly like the reference's <c>command.add(0, ffmpegPath)</c>.
///
/// Two orderings are easy to get wrong and are preserved on purpose:
/// <list type="bullet">
/// <item>the single/final pass appends <c>concat</c> to the graph *before* <c>atempo</c>/<c>adelay</c>;</item>
/// <item><c>mergeSlices</c> appends <c>atempo</c>/<c>adelay</c> *before* <c>concat</c>, and that concat
/// uses <c>v=1:a=0</c>.</item>
/// </list>
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
        // Round the time back to a whole frame before applying the pixel step: floating-point
        // t * fps * speed can land just below an integer and intermittently lose one pixel.
        var overlayScroll = FormattableString.Invariant($"round(t*{request.FramePerSecond})*{request.Speed}");

        for (var i = 0; i < count; i++)
        {
            // In the final pass input 0 is the BGM, so the window images start at input index 1.
            var inputFileIndex = request.IsFinal ? i + 1 : i;

            // Everything numeric is formatted with the invariant culture: the filter graph must not
            // depend on the machine's decimal separator or digit shapes.
            filterGraph.Add(FormattableString.Invariant(
                $"color=s={request.GameWidth}*{request.GameHeight},fps={request.FramePerSecond}[bg{i}]"));
            // A 4:2:0 overlay rounds y to even pixels, causing odd scroll speeds (e.g. 29)
            // to alternate between 28 and 30 pixels per frame. Composite in 4:4:4 first;
            // convert to playback-compatible 4:2:0 only when encoding the finished frame.
            filterGraph.Add(FormattableString.Invariant(
                $"[bg{i}][{inputFileIndex}]overlay=y=-h+H+{overlayScroll}:shortest=1:format=yuv444[video{i}l]"));
            filterGraph.Add(
                $"[video{i}l]trim=end={JavaDouble.ToString(request.SliceDuration)}{isTaikoSuffix}[video{i}]");

            outVideos.Add($"[video{i}]");
            subcommands.Add("-r");
            subcommands.Add("1");
            subcommands.Add("-loop");
            subcommands.Add("1");
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

    /// <summary>Port of <c>Encoder.mergeSlices(...)</c>.</summary>
    public static List<string> BuildMerge(FfmpegMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var filterGraph = new List<string>
        {
            $"[0:a]atempo={JavaDouble.ToString(request.Rate)}[a1]",
            FormattableString.Invariant($"[a1]adelay={request.AudioDelay}|{request.AudioDelay}[audio]")
        };

        var command = new List<string> { "-i", request.BgmPath };
        var outVideos = new List<string>();
        for (var i = 0; i < request.SlicePaths.Count; i++)
        {
            command.Add("-i");
            command.Add(request.SlicePaths[i]);
            outVideos.Add($"[{i + 1}:v]");
        }

        filterGraph.Add(FormattableString.Invariant(
            $"{string.Concat(outVideos)}concat=n={request.SlicePaths.Count}:v=1:a=0[video]"));

        command.Add("-filter_complex");
        command.Add(string.Join(";", filterGraph));
        command.Add("-map");
        command.Add("[video]");
        command.Add("-map");
        command.Add("[audio]");
        AppendEncoderOptions(command, request.OutPath, request.Codec, request.CodecPreset, request.CodecProfile);
        return command;
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
        command.Add("-c:v");
        command.Add(codec);
        command.Add("-preset:v");
        command.Add(codecPreset);
        command.Add("-pix_fmt");
        command.Add("yuv420p");

        if (!string.IsNullOrWhiteSpace(codecProfile) && KnownH264Profiles.Contains(codecProfile.Trim()))
        {
            command.Add("-profile:v");
            command.Add(codecProfile.Trim());
        }

        command.Add("-benchmark");
        command.Add(outPath);
    }
}
