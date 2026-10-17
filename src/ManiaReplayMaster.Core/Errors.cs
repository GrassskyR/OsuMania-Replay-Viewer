// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
namespace ManiaReplayMaster.Core;

/// <summary>Port of the <c>BaseException</c> interface: every failure carries a machine-readable code.</summary>
public interface IBaseException
{
    string ErrorCode { get; }
}

public sealed class ModeNotSupportedException : IOException, IBaseException
{
    public string ErrorCode => "mode_not_support";

    public ModeNotSupportedException(string mode) : base($"Not support this kind of map: {mode}")
    {
    }
}

public sealed class ReplayModeNotMatchedException : IOException, IBaseException
{
    public string ErrorCode => "convert_not_support";

    public ReplayModeNotMatchedException(string mode, string beatmapMode)
        : base($"Replay mode ({mode}) doesn't match the beatmap's mode ({beatmapMode}).")
    {
    }
}

public sealed class VideoGenerationException : Exception, IBaseException
{
    public string ErrorCode => "render_failed";

    public VideoGenerationException(Exception? rootCause) : base("Generate file failed!", rootCause)
    {
    }

    public VideoGenerationException(string message) : base("Generate file failed! " + message)
    {
    }
}

public sealed class BeatmapNotFoundException : IOException, IBaseException
{
    public string ErrorCode => "beatmap_not_found";

    public BeatmapNotFoundException(string md5) : base($"Cannot find the beatmap with the given MD5: {md5}")
    {
    }
}

public sealed class InvalidBeatmapException : IOException, IBaseException
{
    public string ErrorCode => "beatmap_invalid";

    public InvalidBeatmapException(string beatmapPath, Exception? rootCause)
        : base($"Invalid beatmap file: {beatmapPath}", rootCause)
    {
    }
}

public sealed class InvalidReplayException : IOException, IBaseException
{
    public string ErrorCode => "replay_invalid";

    public InvalidReplayException(string replayPath, Exception? rootCause)
        : base($"Invalid replay file: {replayPath}", rootCause)
    {
    }
}

public sealed class DurationTooLongException : IOException, IBaseException
{
    public string ErrorCode => "duration_too_long";

    public DurationTooLongException() : base("Duration of the beatmap is too long!")
    {
    }
}

public static class ErrorCodes
{
    public static string Parse(Exception throwable)
    {
        return throwable is IBaseException baseException ? baseException.ErrorCode : throwable.ToString();
    }
}
