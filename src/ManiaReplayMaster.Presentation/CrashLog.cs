namespace ManiaReplayMaster.Presentation;

public static class CrashLog
{
    public static string LastPath { get; private set; } = string.Empty;

    public static void Write(Exception ex)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ManiaReplayViewer");
            Directory.CreateDirectory(directory);
            LastPath = Path.Combine(directory, "crash.log");
            File.AppendAllText(LastPath,
                $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
