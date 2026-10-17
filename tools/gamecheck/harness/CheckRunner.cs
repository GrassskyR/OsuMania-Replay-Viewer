using System.Text;

namespace ManiaReplayMaster.GameCheck;

/// <summary>
/// Minimal assertion recorder. Every check prints PASS/FAIL with the measured value, and the process
/// exit code is the failure count's sign, so the harness can run in CI.
///
/// Output is UTF-8 (never raw bytes to a GBK console) — the harness writes a UTF-8 log file and only
/// prints ASCII-safe lines.
/// </summary>
public sealed class CheckRunner
{
    private readonly List<string> _lines = new();
    private int _passed;
    private int _failed;

    public int Passed => _passed;

    public int Failed => _failed;

    public string Log => string.Join(Environment.NewLine, _lines);

    public void Section(string title)
    {
        Write(string.Empty);
        Write($"=== {title} ===");
    }

    public void Info(string message) => Write($"  · {message}");

    public void Detail(string message) => Write($"      {message}");

    public void Pass(string what, string? measured)
    {
        _passed++;
        Write($"  PASS  {what}: {measured}");
    }

    public void Fail(string what, string? measured)
    {
        _failed++;
        Write($"  FAIL  {what}: {measured}");
    }

    public void Check(bool condition, string what, string? measured)
    {
        if (condition)
        {
            Pass(what, measured);
        }
        else
        {
            Fail(what, measured);
        }
    }

    /// <summary>Compares an integer against an inclusive/exclusive bound and reports the value either way.</summary>
    public void CheckAtLeast(long actual, long minimum, string what)
        => Check(actual >= minimum, what, $"measured {actual:N0}, required >= {minimum:N0}");

    public void CheckEqual<T>(T actual, T expected, string what)
        => Check(EqualityComparer<T>.Default.Equals(actual, expected), what, $"measured {actual}, expected {expected}");

    public void CheckRatio(long numerator, long denominator, double minimum, string what)
    {
        if (denominator == 0)
        {
            Fail(what, "denominator is 0");
            return;
        }

        var ratio = (double)numerator / denominator;
        Check(ratio >= minimum, what, $"{numerator:N0}/{denominator:N0} = {ratio:P4}, required >= {minimum:P4}");
    }

    /// <summary>Records a check that could not be executed (no osu! install, no game session).</summary>
    public void Skip(string what, string reason) => Write($"  SKIP  {what}: {reason}");

    public void Metric(string name, object? value) => Write($"  METRIC {name} = {value}");

    public void WriteRaw(string message) => Write(message);

    private void Write(string message)
    {
        Console.WriteLine(message);
        _lines.Add(message);
    }

    public void SaveLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Log, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
