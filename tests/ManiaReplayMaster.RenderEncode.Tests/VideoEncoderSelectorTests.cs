using ManiaReplayMaster.Encode;

namespace ManiaReplayMaster.RenderEncode.Tests;

public class VideoEncoderSelectorTests
{
    [Fact]
    public void AvailableChecksAllEncodersAndKeepsMultipleWorkingGpus()
    {
        var attempted = new List<EncoderSelection>();
        var available = VideoEncoderSelector.Available(candidate =>
        {
            attempted.Add(candidate);
            return candidate.Codec != "h264_qsv";
        });
        Assert.Equal(new[] { "libx264", "h264_nvenc", "h264_amf" }, available);
        Assert.Equal(new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf" }, attempted.Select(c => c.Codec));
        Assert.Equal(new[] { "ultrafast", "p1", "veryfast", "speed" }, attempted.Select(c => c.Preset));
    }

    [Theory]
    [InlineData("h264_amf", "speed", 3)]
    public void AutoSkipsUnusableEncodersAndStopsAtFirstWorkingOne(string workingCodec, string preset, int probes)
    {
        var attempted = new List<EncoderSelection>();
        var selected = VideoEncoderSelector.Select("auto", "ultrafast", candidate =>
        {
            attempted.Add(candidate);
            return candidate.Codec == workingCodec;
        });

        Assert.Equal(workingCodec, selected.Codec);
        Assert.Equal(preset, selected.Preset);
        Assert.True(selected.IsHardwareAccelerated);
        Assert.False(selected.IsFallback);
        Assert.Equal(probes, attempted.Count);
        Assert.DoesNotContain(attempted, candidate => candidate.Preset == "ultrafast");
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
