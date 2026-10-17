namespace ManiaReplayMaster.Core.Model;

/// <summary>
/// Port of <c>me.replaymaster.model.Config</c> (27 fields, identical defaults).
///
/// YAML I/O is hand-rolled: the config schema is fixed and shallow (scalars + one string
/// list), so this avoids a third-party YAML dependency in a build that must stay
/// reproducible without a reachable NuGet feed.
/// </summary>
public sealed class Config
{
    public int Speed = 20;
    public int Width = 540;
    public int Height = 960;
    public int Stroke = 3;
    public int ActionHeight = 7;
    public int BlockHeight = 40;
    public int FramePerSecond = 60;
    public bool IsMalodyPE = true;

    /// <summary>Alias so render/port code can read the field under its Kotlin name.</summary>
    public bool MalodyPE => IsMalodyPE;

    public bool OmScoreV2Adjust = true;

    public List<string> JudgementColor = new()
    {
        "FFFFFF", // max, best, taiko_300
        "FFD237", // 300, cool, taiko_100
        "79D020", // 200, good
        "1E68C5", // 100
        "E1349B" // 50
    };

    public string TaikoBackgroundRed = "30ED402F";
    public string TaikoBackgroundBlue = "30488FAD";
    public bool TaikoSingleColumn;
    public string TaikoForegroundRed = "88ED402F";
    public string TaikoForegroundBlue = "88488FAD";
    public string MissColor = "FF0000";
    public string LongNoteColor = "646464";

    public bool Debug;
    public string Codec = "libx264";
    public string CodecPreset = "ultrafast";
    public string CodecProfile = "unknown";
    public string OutputDir = "out";
    public bool IsServer;
    public bool IsDesktop;
    public int MaxStepSize = 3000;
    public bool ShowHolding;
    public bool ShowTimestamp;
    public int FfmpegMaxProcessingSize = 20;
    public long MaxEnabledDuration = -1;

    public bool ExportJudgementResults;
    public bool ViewJudgementStatistics;
    public string ViewJudgementStatisticsPath = "judgementStatistics.exe";

    /// <summary>Port of <c>Config.INSTANCE</c>.</summary>
    public static Config Instance { get; set; } = new();

    public static readonly string DefaultSetting = """
        # Falling down speed, in pixel / frame
        speed: 20

        # FPS: frame / second
        framePerSecond: 60

        # Only valid when using Malody. true: PE, false: PC
        malodyPE: true

        # Only valid when using taiko. true: single column, false: multi column
        taikoSingleColumn: false

        # UI settings
        width: 540
        height: 960
        actionHeight: 7 # hit bar height
        blockHeight: 40 # note height
        stroke: 3

        # Colors
        judgementColor:
          - FFFFFF
          - FFD237
          - 79D020
          - 1E68C5
          - E1349B
        longNoteColor: '646464'
        missColor: FF0000
        taikoBackgroundRed: 30ED402F
        taikoBackgroundBlue: 30488FAD

        # Advanced
        outputDir: 'out'
        exportJudgementResults: false
        viewJudgementStatistics: false
        # if you not familiar with ffmpeg, please don't change it
        codec: libx264
        codecPreset: ultrafast
        codecProfile: unknown
        debug: false
        """;

    public static void Init(string yamlPath)
    {
        if (File.Exists(yamlPath))
        {
            Refresh(yamlPath);
        }
        else
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(yamlPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(yamlPath, DefaultSetting);
        }
    }

    public static void Refresh(string yamlPath)
    {
        if (!File.Exists(yamlPath))
        {
            return;
        }

        var config = new Config();
        string? pendingListKey = null;
        var pendingList = new List<string>();

        void ApplyList(string? key, List<string> items)
        {
            if (key is null)
            {
                return;
            }

            if (string.Equals(key, "judgementColor", StringComparison.OrdinalIgnoreCase))
            {
                config.JudgementColor = new List<string>(items);
            }
        }

        foreach (var rawLine in File.ReadAllLines(yamlPath))
        {
            // File.ReadAllLines splits on \n, so a file written with Windows line endings
            // still carries a trailing \r that would otherwise break list-item parsing.
            var line = rawLine.TrimEnd('\r');
            var hash = FindComment(line);
            if (hash >= 0)
            {
                line = line[..hash];
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Block-sequence items are indented under their "key:" line, so the indent must be
            // stripped before testing for the dash.
            var trimmed = line.Trim();

            // Block-sequence item belonging to the most recent "key:" line.
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
            {
                pendingList.Add(Unquote(trimmed.Length > 1 ? trimmed[2..].Trim() : string.Empty));
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            // A new key ends any block sequence that was being collected.
            ApplyList(pendingListKey, pendingList);
            pendingListKey = null;
            pendingList.Clear();

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();
            if (value.Length == 0)
            {
                // "key:" with no inline value starts a block sequence; items follow.
                pendingListKey = key;
                continue;
            }

            config.SetScalar(key, Unquote(value));
        }

        ApplyList(pendingListKey, pendingList);
        Instance = config;
    }

    /// <summary>Finds the index of a '#' comment start (ignoring '#' inside quotes).</summary>
    private static int FindComment(string line)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '#' && !inSingle && !inDouble)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1];
        }

        return value;
    }

    private void SetScalar(string key, string value)
    {
        switch (key)
        {
            case "speed": Speed = ParseInt(value, Speed); break;
            case "width": Width = ParseInt(value, Width); break;
            case "height": Height = ParseInt(value, Height); break;
            case "stroke": Stroke = ParseInt(value, Stroke); break;
            case "actionHeight": ActionHeight = ParseInt(value, ActionHeight); break;
            case "blockHeight": BlockHeight = ParseInt(value, BlockHeight); break;
            case "framePerSecond": FramePerSecond = ParseInt(value, FramePerSecond); break;
            case "malodyPE": IsMalodyPE = ParseBool(value, IsMalodyPE); break;
            case "omScoreV2Adjust": OmScoreV2Adjust = ParseBool(value, OmScoreV2Adjust); break;
            case "taikoBackgroundRed": TaikoBackgroundRed = value; break;
            case "taikoBackgroundBlue": TaikoBackgroundBlue = value; break;
            case "taikoSingleColumn": TaikoSingleColumn = ParseBool(value, TaikoSingleColumn); break;
            case "taikoForegroundRed": TaikoForegroundRed = value; break;
            case "taikoForegroundBlue": TaikoForegroundBlue = value; break;
            case "missColor": MissColor = value; break;
            case "longNoteColor": LongNoteColor = value; break;
            case "debug": Debug = ParseBool(value, Debug); break;
            case "codec": Codec = value; break;
            case "codecPreset": CodecPreset = value; break;
            case "codecProfile": CodecProfile = value; break;
            case "outputDir": OutputDir = value; break;
            case "isServer": IsServer = ParseBool(value, IsServer); break;
            case "isDesktop": IsDesktop = ParseBool(value, IsDesktop); break;
            case "maxStepSize": MaxStepSize = ParseInt(value, MaxStepSize); break;
            case "showHolding": ShowHolding = ParseBool(value, ShowHolding); break;
            case "showTimestamp": ShowTimestamp = ParseBool(value, ShowTimestamp); break;
            case "ffmpegMaxProcessingSize": FfmpegMaxProcessingSize = ParseInt(value, FfmpegMaxProcessingSize); break;
            case "maxEnabledDuration": MaxEnabledDuration = ParseLong(value, MaxEnabledDuration); break;
            case "exportJudgementResults": ExportJudgementResults = ParseBool(value, ExportJudgementResults); break;
            case "viewJudgementStatistics": ViewJudgementStatistics = ParseBool(value, ViewJudgementStatistics); break;
            case "viewJudgementStatisticsPath": ViewJudgementStatisticsPath = value; break;

            // Accept both spellings; the shipped YAML uses "malodyPE".
            case "isMalodyPE": IsMalodyPE = ParseBool(value, IsMalodyPE); break;
        }
    }

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : fallback;

    private static long ParseLong(string value, long fallback)
        => long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : fallback;

    private static bool ParseBool(string value, bool fallback)
        => bool.TryParse(value, out var r) ? r
            : value == "1" ? true
            : value == "0" ? false
            : fallback;

    public Config Clone()
    {
        var clone = (Config)MemberwiseClone();
        clone.JudgementColor = new List<string>(JudgementColor);
        return clone;
    }
}
