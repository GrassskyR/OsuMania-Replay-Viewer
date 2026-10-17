using System.Text.Json;

namespace ManiaReplayMaster.Tests;

/// <summary>
/// Locates repository content (fixtures, golden dumps) from inside a test run by walking up
/// from the test assembly location until a marker file is found. Avoids brittle relative paths.
/// </summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> RootLazy = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WINDOWS_NATIVE_REWRITE_PLAN.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    });

    public static string Root => RootLazy.Value;

    public static string Fixtures => Path.Combine(Root, "fixtures");

    public static string Golden => Path.Combine(Root, "tests", "golden");

    public static string Fixture(string fileName) => Path.Combine(Fixtures, fileName);

    public static string GoldenFile(string name) => Path.Combine(Golden, name + ".json");
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static T LoadJson<T>(string path) where T : new()
    {
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
    }
}
