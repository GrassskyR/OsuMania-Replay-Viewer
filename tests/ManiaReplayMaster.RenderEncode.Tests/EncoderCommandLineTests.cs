using ManiaReplayMaster.Encode;
using ManiaReplayMaster.Encode.Compat;

namespace ManiaReplayMaster.RenderEncode.Tests;

/// <summary>
/// Pins the exact ffmpeg argv produced by the port against the reference implementation
/// (<c>Encoder.kt</c>). These tests never launch ffmpeg: the whole command line, filter graph
/// included, is the highest-value regression surface of the encode layer.
/// </summary>
public class EncoderCommandLineTests
{
    private const string ImageDir = @"C:\work\im";
    private const string OutDir = @"C:\work\out";

    private static FfmpegVideoRequest VideoRequest(int imageCount, bool isFinal, bool taiko = false)
        => new()
        {
            GameWidth = 540,
            GameHeight = 960,
            FramePerSecond = 60,
            Speed = 20,
            SliceDuration = 150 / 60.0,
            ImagePaths = Enumerable.Range(0, imageCount).Select(i => $@"{ImageDir}\v_{i}.png").ToList(),
            IsTaiko = taiko,
            IsFinal = isFinal,
            BgmPath = $@"{ImageDir}\mute_sound.mp3",
            Rate = 1.0,
            AudioDelay = 1000,
            OutPath = $@"{OutDir}\v.mp4",
            Codec = "libx264",
            CodecPreset = "ultrafast",
            CodecProfile = "unknown"
        };

    private static FfmpegMergeRequest MergeRequest(double rate = 1.0)
        => new()
        {
            SlicePaths = new[]
            {
                $@"{OutDir}\v_0.mp4",
                $@"{OutDir}\v_1.mp4",
                $@"{OutDir}\v_2.mp4"
            },
            BgmPath = $@"{ImageDir}\mute_sound.mp3",
            Rate = rate,
            AudioDelay = 1000,
            OutPath = $@"{OutDir}\v.mp4",
            Codec = "libx264",
            CodecPreset = "ultrafast",
            CodecProfile = "unknown"
        };

    [Fact]
    public void FinalSinglePass_MatchesReferenceCommandLineExactly()
    {
        var expected = new List<string>
        {
            "-i", $@"{ImageDir}\mute_sound.mp3",
            "-r", "1", "-loop", "1", "-i", $@"{ImageDir}\v_0.png",
            "-r", "1", "-loop", "1", "-i", $@"{ImageDir}\v_1.png",
            "-filter_complex",
            "color=s=540*960,fps=60[bg0];" +
            "[bg0][1]overlay=y=-h+H+t*1200:shortest=1[video0l];" +
            "[video0l]trim=end=2.5[video0];" +
            "color=s=540*960,fps=60[bg1];" +
            "[bg1][2]overlay=y=-h+H+t*1200:shortest=1[video1l];" +
            "[video1l]trim=end=2.5[video1];" +
            "[video0][video1]concat=n=2:v=1[video];" +
            "[0:a]atempo=1.0[a1];" +
            "[a1]adelay=1000|1000[audio]",
            "-map", "[video]",
            "-map", "[audio]",
            "-c:v", "libx264",
            "-preset:v", "ultrafast",
            // NOTE: no "-profile:v" here. The reference always emits "-profile:v <profile>", but its
            // shipped default is the literal string "unknown", which libx264 rejects outright
            // ("x264 [error]: invalid profile: unknown"). The port omits the flag unless the value
            // names a real H.264 profile -- see FfmpegCommandBuilder.AppendEncoderOptions.
            "-benchmark",
            $@"{OutDir}\v.mp4"
        };

        var actual = FfmpegCommandBuilder.BuildVideo(VideoRequest(imageCount: 2, isFinal: true));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SlicePass_HasNoAudioInputAndImageInputsStartAtZero()
    {
        var expected = new List<string>
        {
            "-r", "1", "-loop", "1", "-i", $@"{ImageDir}\v_0.png",
            "-r", "1", "-loop", "1", "-i", $@"{ImageDir}\v_1.png",
            "-filter_complex",
            "color=s=540*960,fps=60[bg0];" +
            "[bg0][0]overlay=y=-h+H+t*1200:shortest=1[video0l];" +
            "[video0l]trim=end=2.5[video0];" +
            "color=s=540*960,fps=60[bg1];" +
            "[bg1][1]overlay=y=-h+H+t*1200:shortest=1[video1l];" +
            "[video1l]trim=end=2.5[video1];" +
            "[video0][video1]concat=n=2:v=1[video]",
            "-map", "[video]",
            "-c:v", "libx264",
            "-preset:v", "ultrafast",
            "-benchmark",
            $@"{OutDir}\v.mp4"
        };

        var actual = FfmpegCommandBuilder.BuildVideo(VideoRequest(imageCount: 2, isFinal: false));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Taiko_AppendsTransposeAfterTrim()
    {
        var actual = FfmpegCommandBuilder.BuildVideo(VideoRequest(imageCount: 1, isFinal: false, taiko: true));

        var graph = FilterGraph(actual);
        Assert.Contains("[video0l]trim=end=2.5,transpose=1[video0]", graph);
        Assert.Contains("[bg0][0]overlay=y=-h+H+t*1200:shortest=1[video0l];", graph);
    }

    [Fact]
    public void NonTaiko_HasNoTranspose()
    {
        var actual = FfmpegCommandBuilder.BuildVideo(VideoRequest(imageCount: 1, isFinal: false));

        Assert.DoesNotContain(actual, argument => argument.Contains("transpose", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_MatchesReferenceOrdering_AudioFiltersBeforeConcat()
    {
        var expected = new List<string>
        {
            "-i", $@"{ImageDir}\mute_sound.mp3",
            "-i", $@"{OutDir}\v_0.mp4",
            "-i", $@"{OutDir}\v_1.mp4",
            "-i", $@"{OutDir}\v_2.mp4",
            "-filter_complex",
            "[0:a]atempo=1.5[a1];[a1]adelay=1000|1000[audio];[1:v][2:v][3:v]concat=n=3:v=1:a=0[video]",
            "-map", "[video]",
            "-map", "[audio]",
            "-c:v", "libx264",
            "-preset:v", "ultrafast",
            "-benchmark",
            $@"{OutDir}\v.mp4"
        };

        var actual = FfmpegCommandBuilder.BuildMerge(MergeRequest(rate: 1.5));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Merge_ScalesAudioByRateAndDelayByAudioDelay()
    {
        var actual = FfmpegCommandBuilder.BuildMerge(MergeRequest(rate: 0.75));
        var graph = actual[actual.IndexOf("-filter_complex") + 1];

        Assert.StartsWith("[0:a]atempo=0.75[a1];[a1]adelay=1000|1000[audio];", graph);
    }

    private static string FilterGraph(IReadOnlyList<string> command)
    {
        var index = command.ToList().IndexOf("-filter_complex");
        Assert.True(index >= 0, "no -filter_complex in the command line");
        return command[index + 1];
    }

    [Fact]
    public void MuteSound_MatchesReferenceArgumentOrder()
    {
        var expected = new List<string>
        {
            "-f", "lavfi", "-t", "1", "-i", "anullsrc", $@"{ImageDir}\mute_sound.mp3", "-y"
        };

        Assert.Equal(expected, FfmpegCommandBuilder.BuildMuteSound($@"{ImageDir}\mute_sound.mp3"));
    }

    [Fact]
    public void OverlayScrollIsFramePerSecondTimesSpeed()
    {
        var request = VideoRequest(imageCount: 1, isFinal: false) with
        {
            FramePerSecond = 30,
            Speed = 25
        };

        var graph = FilterGraph(FfmpegCommandBuilder.BuildVideo(request));

        Assert.Contains("overlay=y=-h+H+t*750:shortest=1", graph);
    }

    /// <summary>
    /// Guards the deliberate divergence: "-profile:v unknown" (the reference's default) must never
    /// reach ffmpeg, because libx264 aborts with "invalid profile: unknown". A real profile must
    /// still be forwarded unchanged.
    /// </summary>
    [Theory]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("baseline", true)]
    [InlineData("main", true)]
    [InlineData("high", true)]
    [InlineData("High10", true)]
    [InlineData("high444", true)]
    [InlineData("not-a-profile", false)]
    public void ProfileIsForwardedOnlyWhenLibx264AcceptsIt(string profile, bool expectedPresent)
    {
        var command = FfmpegCommandBuilder.BuildVideo(
            VideoRequest(imageCount: 1, isFinal: false) with { CodecProfile = profile });

        var index = command.IndexOf("-profile:v");
        Assert.Equal(expectedPresent, index >= 0);
        if (expectedPresent)
        {
            Assert.Equal(profile.Trim(), command[index + 1]);
        }
    }

    [Fact]
    public void ProfileValueIsForwardedForMergePassToo()
    {
        var command = FfmpegCommandBuilder.BuildMerge(MergeRequest() with { CodecProfile = "high" });

        var index = command.IndexOf("-profile:v");
        Assert.True(index >= 0);
        Assert.Equal("high", command[index + 1]);
    }

    [Fact]
    public void SliceDurationAndRateUseJavaDoubleFormatting()
    {
        // windowFrameCount 120 / fps 30 = 4.0 -> Java prints "4.0", not "4"
        var request = VideoRequest(imageCount: 1, isFinal: true) with
        {
            SliceDuration = 120 / 30.0,
            Rate = 1.0
        };

        var graph = FilterGraph(FfmpegCommandBuilder.BuildVideo(request));
        Assert.Contains("trim=end=4.0[video0]", graph);
        Assert.Contains("atempo=1.0[a1]", graph);
    }
}
