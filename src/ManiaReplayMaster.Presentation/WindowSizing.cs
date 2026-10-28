namespace ManiaReplayMaster.Presentation;

/// <summary>Window dimensions in device-independent pixels, including the native frame.</summary>
public readonly record struct WindowSizing(double Width, double Height, double MinWidth, double MinHeight,
    double AvailableWidth, double AvailableHeight)
{
    public static WindowSizing ForWorkArea(double pixelWidth, double pixelHeight, double scaling)
    {
        if (!double.IsFinite(scaling) || scaling <= 0) throw new ArgumentOutOfRangeException(nameof(scaling));
        if (!double.IsFinite(pixelWidth) || pixelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (!double.IsFinite(pixelHeight) || pixelHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pixelHeight));

        var width = pixelWidth / scaling;
        var height = pixelHeight / scaling;
        var availableWidth = Math.Max(1, width - 24);
        var availableHeight = Math.Max(1, height - 24);
        var minWidth = Math.Min(480, availableWidth);
        var minHeight = Math.Min(320, availableHeight);
        return new WindowSizing(Math.Max(minWidth, Math.Min(900, Math.Min(width * .85, availableWidth))),
            Math.Max(minHeight, Math.Min(620, Math.Min(height * .88, availableHeight))),
            minWidth, minHeight, availableWidth, availableHeight);
    }
}
