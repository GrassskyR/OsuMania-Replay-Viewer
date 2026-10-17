namespace ManiaReplayMaster.RenderEncode.Tests;

/// <summary>
/// Locates repository content (fixtures) from inside a test run by walking up from the test
/// assembly location until the plan document is found.
/// </summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> RootLazy = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WINDOWS_NATIVE_REWRITE_PLAN.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root from " + AppContext.BaseDirectory);
    });

    public static string Root => RootLazy.Value;

    public static string Fixtures => Path.Combine(Root, "fixtures");

    public static string Fixture(string fileName) => Path.Combine(Fixtures, fileName);

    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mrm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
