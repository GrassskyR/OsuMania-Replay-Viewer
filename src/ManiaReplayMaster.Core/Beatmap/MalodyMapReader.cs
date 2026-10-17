using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Beatmap;

/// <summary>Port of <c>me.replaymaster.beatmap.MalodyMapReader</c>.</summary>
public sealed class MalodyMapReader : IMapReader
{
    public string MapExtension => "mc";

    public string ZipExtension => "mcz";

    private sealed record MalodyBpmStamp(double Time, double Bpm, double BeatValue);

    public BeatMap ReadMap(string path)
    {
        var model = McModel.Load(path);

        // meta info: the last note carrying a sound/offset pair is the metadata note
        var metaNote = model.Note.LastOrDefault(it => it.Sound is not null)
                       ?? throw new InvalidBeatmapException(path, new InvalidDataException("Invalid .mc file: sound not found!"));

        var bgmName = metaNote.Sound!;
        var offset = metaNote.Offset ?? 0.0;
        var key = model.Meta.ModeExt.Column;

        // time
        var bpmList = new List<MalodyBpmStamp>();
        var timeList = model.Time.OrderBy(it => it.BeatValue).ToList();
        for (var i = 0; i < timeList.Count; i++)
        {
            var timeObject = timeList[i];
            var curBeatValue = timeObject.BeatValue;
            var curBpm = timeObject.Bpm;
            if (i == 0)
            {
                bpmList.Add(new MalodyBpmStamp(0.0, curBpm, curBeatValue));
            }
            else
            {
                var curTime = BeatToTime(curBeatValue, bpmList[^1]);
                bpmList.Add(new MalodyBpmStamp(curTime, curBpm, curBeatValue));
            }
        }

        // note
        var noteList = model.Note
            .OrderBy(it => it.BeatValue)
            .Where(it => it.Column is not null)
            .Select(it =>
            {
                var startTime = BeatToTimeWithBpm(it.BeatValue, bpmList);
                var endTime = BeatToTimeWithBpm(it.EndBeatValue ?? it.BeatValue, bpmList);
                var column = it.Column!.Value;
                return new Note((long)startTime, column, (long)endTime - (long)startTime);
            })
            .ToList();

        var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        return new BeatMap
        {
            Key = key,
            Notes = noteList,
            BgmPath = Path.GetFullPath(Path.Combine(parent, bgmName)),
            BgmOffset = (int)offset,
            GameMode = "malody:key"
        };
    }

    private static double BeatToTime(double beatValue, MalodyBpmStamp lastStamp)
        => (beatValue - lastStamp.BeatValue) * 60000 / lastStamp.Bpm + lastStamp.Time;

    private static double BeatToTimeWithBpm(double beatValue, List<MalodyBpmStamp> bpmList)
    {
        var position = bpmList.FindIndex(it => it.BeatValue > beatValue);
        if (position == -1)
        {
            position = bpmList.Count;
        }

        position -= 1;
        return BeatToTime(beatValue, bpmList[position]);
    }
}
