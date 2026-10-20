namespace ManiaReplayMaster.TestSupport;

internal static class RepoPaths
{
    public static string Root { get; set; } = "";
    public static string Fixture(string name) => Path.Combine(Root, "fixtures", name);
    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mrm-aot-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    public static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
