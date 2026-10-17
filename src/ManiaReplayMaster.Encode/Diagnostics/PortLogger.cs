using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Encode.Diagnostics;

/// <summary>
/// Port of the Kotlin top-level <c>debug()</c> helper plus the <c>[FFMPEG]</c> line trace.
///
/// The reference implementation prints <c>[DEBUG] &lt;message&gt;</c> to stdout only when
/// <c>Config.debug</c> is set. A GUI cannot consume stdout, so the logger accepts an optional
/// sink: when a sink is supplied every message is forwarded to it (the sink decides what to
/// show) and the console fallback is skipped.
///
/// The ffmpeg stdout/stderr pumps call this from two pool threads concurrently, hence the lock.
/// </summary>
public sealed class PortLogger
{
    private readonly object _gate = new();
    private readonly Config? _config;
    private readonly Action<string>? _sink;

    public PortLogger(Config? config = null, Action<string>? sink = null)
    {
        _config = config;
        _sink = sink;
    }

    /// <summary>Creates a logger that follows the reference's <c>Config.debug</c> console behaviour.</summary>
    public static PortLogger FromConfig(Config config, Action<string>? sink = null) => new(config, sink);

    public void Debug(string message)
    {
        lock (_gate)
        {
            if (_sink is not null)
            {
                _sink(message);
                return;
            }

            if (_config?.Debug == true)
            {
                Console.WriteLine("[DEBUG] " + message);
            }
        }
    }

    public void Debug(string key, params object?[] args)
    {
        Debug(args.Length == 0 ? key : key + " " + string.Join(", ", args));
    }
}
