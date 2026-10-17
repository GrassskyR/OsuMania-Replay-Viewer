// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Core.Replay;

namespace ManiaReplayMaster.Core.Beatmap;

/// <summary>Port of <c>me.replaymaster.beatmap.OsuMapReader</c>.</summary>
public sealed class OsuMapReader : IMapReader
{
    private const int StatusUnknown = 0;
    private const int StatusTime = 1;
    private const int StatusObjects = 2;

    public string MapExtension => "osu";

    public string ZipExtension => "osz";

    public BeatMap ReadMap(string path)
    {
        var beatMap = new BeatMap();
        var notes = new List<Note>();
        beatMap.Notes = notes;

        var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var status = StatusUnknown;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line == "[TimingPoints]")
            {
                status = StatusTime;
            }
            else if (line == "[HitObjects]")
            {
                status = StatusObjects;
            }
            else if (line.StartsWith('['))
            {
                status = StatusUnknown;
            }
            else
            {
                switch (status)
                {
                    case StatusUnknown:
                        if (line.Contains(':'))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length == 2)
                            {
                                ParseAttribute(parts[0].Trim(), parts[1].Trim(), beatMap, parent);
                            }
                        }

                        break;

                    case StatusTime:
                        break;

                    case StatusObjects:
                        if (line.Length > 0)
                        {
                            ParseHitObject(line, notes, beatMap);
                        }

                        break;
                }
            }
        }

        return beatMap;
    }

    private static void ParseAttribute(string key, string value, BeatMap beatMap, string parentDirectory)
    {
        switch (key)
        {
            case "OverallDifficulty":
                // NOTE: the original uses Double.parseDouble, which throws on a malformed value;
                // this port keeps the same behaviour by not swallowing the exception.
                beatMap.Od = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                break;

            case "AudioFilename":
                beatMap.BgmPath = Path.GetFullPath(Path.Combine(parentDirectory, value.Trim()));
                break;

            case "Mode":
                switch (value)
                {
                    case "0":
                        beatMap.Type = BeatMap.TypeOsuStd;
                        beatMap.GameMode = "osu!std";
                        break;
                    case "1":
                        beatMap.Type = BeatMap.TypeTaiko;
                        beatMap.GameMode = "osu!taiko";
                        beatMap.Key = 4;
                        break;
                    case "3":
                        beatMap.Type = BeatMap.TypeMania;
                        beatMap.GameMode = "osu!mania";
                        break;
                    default:
                        throw new ModeNotSupportedException(value);
                }

                break;

            case "SliderMultiplier":
                beatMap.SliderMultiplier = double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var slider)
                    ? slider
                    : null;
                break;

            case "CircleSize":
                if (beatMap.IsMania)
                {
                    beatMap.Key = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                }

                break;
        }
    }

    private static void ParseHitObject(string line, List<Note> notes, BeatMap beatMap)
    {
        var elements = line.Split(',');
        if (elements.Length < 5)
        {
            return;
        }

        var timeStamp = long.Parse(elements[2], System.Globalization.CultureInfo.InvariantCulture);
        var type = int.Parse(elements[3], System.Globalization.CultureInfo.InvariantCulture);

        switch (beatMap.Type)
        {
            case BeatMap.TypeMania:
            {
                var column = (int)Math.Floor(
                    int.Parse(elements[0], System.Globalization.CultureInfo.InvariantCulture)
                    * beatMap.Key / 512.0);

                var endTime = 0L;
                if ((type & 128) != 0 && elements.Length > 5)
                {
                    var endPart = elements[5].Split(':')[0];
                    long.TryParse(endPart, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out endTime);
                }

                var duration = (type & 128) != 0 ? endTime - timeStamp : 0L;
                notes.Add(new Note(timeStamp, column, duration));
                break;
            }

            case BeatMap.TypeTaiko:
            {
                if ((type & 1) != 0)
                {
                    // hit circle
                    var hitSound = int.Parse(elements[4], System.Globalization.CultureInfo.InvariantCulture);
                    var isRim = (hitSound & 2) != 0 || (hitSound & 8) != 0;

                    // The original constructs `Note(timeStamp, column, taikoIsRed = !isRim)` where
                    // column is 0 for a rim (blue) note and 1 otherwise (red). Keep the same mapping.
                    var note = new Note(timeStamp, isRim ? 0 : 1, taikoIsRed: !isRim);
                    notes.Add(note);

                    if ((hitSound & 4) != 0)
                    {
                        // big note
                        var duelNote = new Note(timeStamp, 3 - note.Column, duelNote: note, taikoIsRed: note.TaikoIsRed);
                        note.DuelNote = duelNote;
                        notes.Add(duelNote);
                    }
                }
                else if ((type & (1 << 1)) != 0)
                {
                    // drum rolls: ignored, matching the original
                }
                else if ((type & (1 << 3)) != 0)
                {
                    // denden notes: ignored, matching the original
                }

                break;
            }
        }
    }

    /// <summary>Convenience: read a map, translating failures the way Main.kt does.</summary>
    public BeatMap ReadMapChecked(string path)
    {
        try
        {
            return ReadMap(path);
        }
        catch (Exception ex) when (ex is IBaseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidBeatmapException(path, ex);
        }
    }
}
