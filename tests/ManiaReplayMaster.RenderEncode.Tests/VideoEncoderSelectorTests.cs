using ManiaReplayMaster.Encode;

namespace ManiaReplayMaster.RenderEncode.Tests;

public class VideoEncoderSelectorTests
{
    [Fact]
    public void CompiledEncodersExcludeUnsupportedHardwareBeforeProbing()
    {
        var encoders = FfmpegCapabilities.ParseEncoders("Encoders:\n V..... = Video\n V....D libx264 H.264\n A....D aac AAC\n A....D libmp3lame MP3\n");
        var available = VideoEncoderSelector.Available(_ => true, encoders);
        Assert.Equal(new[] { "libx264" }, available);
        Assert.Contains("aac", encoders);
        Assert.DoesNotContain("=", encoders);
    }

    [Fact]
    public void AvailableChecksAllEncodersAndKeepsMultipleWorkingGpus()
    {
        var available = VideoEncoderSelector.Available(candidate => candidate.Codec != "h264_qsv");
        Assert.Equal(new[] { "libx264", "h264_nvenc", "h264_amf" }, available);
    }

    [Theory]
    [InlineData("h264_amf", "speed")]
    public void AutoSkipsUnusableEncodersAndStopsAtFirstWorkingOne(string workingCodec, string preset)
    {
        var selected = VideoEncoderSelector.Select("auto", "ultrafast", candidate => candidate.Codec == workingCodec);

        Assert.Equal(workingCodec, selected.Codec);
        Assert.Equal(preset, selected.Preset);
        Assert.True(selected.IsHardwareAccelerated);
        Assert.False(selected.IsFallback);
    }

    [Fact]
    public void NoUsableHardwareFallsBackToSoftwareWithSoftwarePreset()
    {
        var selected = VideoEncoderSelector.Select("auto", "p7", _ => false);
        Assert.Equal(new EncoderSelection("libx264", "ultrafast", IsFallback: true), selected);
        Assert.False(selected.IsHardwareAccelerated);
    }

    [Theory]
    [InlineData("libx264", "slow", "slow")]
    [InlineData("h264_nvenc", "auto", "p1")]
    public void ExplicitEncoderPreservesUserChoiceWithoutAutomaticProbes(string codec, string preset, string expectedPreset)
    {
        var selected = VideoEncoderSelector.Select(codec, preset,
            _ => throw new InvalidOperationException("Explicit encoders should not trigger auto detection."));
        Assert.Equal(codec, selected.Codec);
        Assert.Equal(expectedPreset, selected.Preset);
    }
}
