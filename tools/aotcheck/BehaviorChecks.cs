using ManiaReplayMaster.RenderEncode.Tests;

namespace ManiaReplayMaster.AotCheck;

internal static class BehaviorChecks
{
    public static void Run()
    {
        var renderer = new RendererTests();
        Check("Mania 60fps PNG channels/geometry", () => renderer.Start_WritesBlackSlicesAndOutlinedBaseNotesWithFilledActions(60));
        Check("Mania 120fps PNG channels/geometry", () => renderer.Start_WritesBlackSlicesAndOutlinedBaseNotesWithFilledActions(120));
        Check("LN tail", renderer.LongNote_RendersDashedTailInLongNoteColour);
        Check("render cancellation", renderer.Start_HonoursCancellation);
        Check("Taiko rings/background/alpha", renderer.TaikoMultiColumn_PaintsBackgroundStripesAndRingOvals);
        var failure = new EncoderFailureTests();
        Check("hardware failure/software fallback", failure.AutomaticMode_RetriesSoftwareWhenHardwareRejectsAnX264OnlyProfile);
        Check("FFmpeg failure with partial output", failure.FailedProcessIsNotSuccessEvenWhenAnOutputFileExists);
        Check("encoder cancellation", failure.PreCancelledToken_ThrowsOperationCanceledAndDoesNotProduceOutput);
        foreach (var (codec, fps) in new[] { ("libx264", 59), ("auto", 60), ("libx264", 120) })
            Check($"decoded frame cadence + segment merge {codec}/{fps}", () => new ScrollCadenceTests().CopiedSlicesPreserveEveryFrameAndScrollAcrossMergeBoundaries(codec, fps));
        Check("real BGM export/audio duration", () => new RenderExportEndToEndTests().RealBgm_IsMuxedThroughTheFacadeWithAtempoAndAdelay());
        var selector = new VideoEncoderSelectorTests();
        Check("compiled encoder list filters unsupported hardware", selector.CompiledEncodersExcludeUnsupportedHardwareBeforeProbing);
        Check("available encoder discovery includes all working GPUs", selector.AvailableChecksAllEncodersAndKeepsMultipleWorkingGpus);
        Check("NVENC/QSV/AMF selection order", () => selector.AutoSkipsUnusableEncodersAndStopsAtFirstWorkingOne("h264_amf", "speed", 3));
        Check("no GPU -> CPU", selector.NoUsableHardwareFallsBackToSoftwareWithSoftwarePreset);
        Check("explicit software preset", () => selector.ExplicitEncoderPreservesUserChoiceWithoutAutomaticProbes("libx264", "slow", "slow"));
        Check("explicit NVENC preset", () => selector.ExplicitEncoderPreservesUserChoiceWithoutAutomaticProbes("h264_nvenc", "auto", "p1"));
        IntegrationChecks.Run();
    }

    private static void Check(string name, Action action)
    {
        action();
        Console.WriteLine("PASS " + name);
    }
}
