using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ManiaReplayMaster.App;

/// <summary>
/// View-model for the single main window. Binds directly onto the ported <c>Config</c> fields so the
/// parameter panel maps 1:1 onto the reference implementation's config (plan §1.4), and keeps the
/// UI thread free while rendering runs on a worker.
/// </summary>
internal sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly GameBridge _game = new();

    public MainViewModel()
    {
        var config = Core.Model.Config.Instance;
        _speed = config.Speed;
        _width = config.Width;
        _height = config.Height;
        _framePerSecond = config.FramePerSecond;
        _codec = config.Codec;
        _codecPreset = config.CodecPreset;
        _outputDir = config.OutputDir;
        _debugLogging = config.Debug;

        LogLines = new ObservableCollection<string>();
    }

    public ObservableCollection<string> LogLines { get; }

    // ---------------------------------------------------------------- paths

    private string _replayPath = string.Empty;
    public string ReplayPath
    {
        get => _replayPath;
        set
        {
            // CanRender is derived from both paths, so the buttons must be re-evaluated here. Without this
            // notification the Render / Judge only buttons never enable when a path is typed, pasted,
            // browsed or auto-filled — they are bound once at load time and stay disabled forever.
            if (Set(ref _replayPath, value))
            {
                OnPropertyChanged(nameof(CanRender));
            }
        }
    }

    private string _beatmapPath = string.Empty;
    public string BeatmapPath
    {
        get => _beatmapPath;
        set
        {
            if (Set(ref _beatmapPath, value))
            {
                OnPropertyChanged(nameof(CanRender));
            }
        }
    }

    // ---------------------------------------------------------------- detection

    private bool _autoDetectFromGame = true;
    public bool AutoDetectFromGame
    {
        get => _autoDetectFromGame;
        set => Set(ref _autoDetectFromGame, value);
    }

    private bool _autoStartRender;
    public bool AutoStartRender
    {
        get => _autoStartRender;
        set => Set(ref _autoStartRender, value);
    }

    private bool _memoryEnabled = true;
    public bool MemoryEnabled
    {
        get => _memoryEnabled;
        set => Set(ref _memoryEnabled, value);
    }

    private string _detectedScoreText = "No score detected yet.";
    public string DetectedScoreText
    {
        get => _detectedScoreText;
        set => Set(ref _detectedScoreText, value);
    }

    private string _gameStatusText = "osu!: checking…";
    public string GameStatusText
    {
        get => _gameStatusText;
        set => Set(ref _gameStatusText, value);
    }

    private Brush _gamePillBrush = (Brush)System.Windows.Application.Current.Resources["PillOff"];
    public Brush GamePillBrush
    {
        get => _gamePillBrush;
        set => Set(ref _gamePillBrush, value);
    }

    // ---------------------------------------------------------------- config-mapped parameters

    private int _speed;
    public int Speed
    {
        get => _speed;
        set
        {
            if (!Set(ref _speed, value))
            {
                return;
            }

            Core.Model.Config.Instance.Speed = value;
            OnPropertyChanged(nameof(SpeedText));
        }
    }

    public string SpeedText => _speed.ToString();

    private int _width;
    public int Width
    {
        get => _width;
        set
        {
            if (Set(ref _width, value))
            {
                Core.Model.Config.Instance.Width = value;
            }
        }
    }

    private int _height;
    public int Height
    {
        get => _height;
        set
        {
            if (Set(ref _height, value))
            {
                Core.Model.Config.Instance.Height = value;
            }
        }
    }

    private int _framePerSecond;
    public int FramePerSecond
    {
        get => _framePerSecond;
        set
        {
            if (Set(ref _framePerSecond, value))
            {
                Core.Model.Config.Instance.FramePerSecond = value;
            }
        }
    }

    private string _codec = "libx264";
    public string Codec
    {
        get => _codec;
        set
        {
            if (Set(ref _codec, value))
            {
                Core.Model.Config.Instance.Codec = value;
            }
        }
    }

    private string _codecPreset = "ultrafast";
    public string CodecPreset
    {
        get => _codecPreset;
        set
        {
            if (Set(ref _codecPreset, value))
            {
                Core.Model.Config.Instance.CodecPreset = value;
            }
        }
    }

    private string _outputDir = "out";
    public string OutputDir
    {
        get => _outputDir;
        set
        {
            if (Set(ref _outputDir, value))
            {
                Core.Model.Config.Instance.OutputDir = value;
            }
        }
    }

    private bool _debugLogging;
    public bool DebugLogging
    {
        get => _debugLogging;
        set
        {
            if (Set(ref _debugLogging, value))
            {
                Core.Model.Config.Instance.Debug = value;
            }
        }
    }

    // ---------------------------------------------------------------- progress / state

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    private string _progressText = "idle";
    public string ProgressText
    {
        get => _progressText;
        set => Set(ref _progressText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanRender));
            }
        }
    }

    private string _resultText = string.Empty;
    public string ResultText
    {
        get => _resultText;
        set => Set(ref _resultText, value);
    }

    public bool CanRender => !IsBusy
                             && !string.IsNullOrWhiteSpace(ReplayPath)
                             && !string.IsNullOrWhiteSpace(BeatmapPath)
                             && File.Exists(ReplayPath)
                             && (File.Exists(BeatmapPath) || Directory.Exists(BeatmapPath));

    // ---------------------------------------------------------------- logging

    private const int MaxLogLines = 800;

    public void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }
    }

    // ---------------------------------------------------------------- game detection

    public void InitialiseGameLink()
    {
        _game.Initialise();
        if (_game.Available && _game.OsuFound)
        {
            GameStatusText = "osu!: detected ●";
            GamePillBrush = (Brush)System.Windows.Application.Current.Resources["PillOk"];
            Log($"osu! located at {_game.OsuRoot}");
        }
        else if (_game.Available)
        {
            GameStatusText = "osu!: not found";
            GamePillBrush = (Brush)System.Windows.Application.Current.Resources["PillWarn"];
            Log("osu! installation not found — manual mode is fully available.");
        }
        else
        {
            GameStatusText = "osu!: unavailable";
            GamePillBrush = (Brush)System.Windows.Application.Current.Resources["PillOff"];
            Log("Game integration unavailable: " + _game.Detail);
        }
    }

    /// <summary>
    /// Polls for a game snapshot and, if auto-detect is enabled, applies any newly resolved
    /// replay/beatmap paths. Returns true when auto-start should fire.
    /// </summary>
    public bool PollAutoDetect()
    {
        if (!AutoDetectFromGame || IsBusy || !_game.Available)
        {
            return false;
        }

        var result = _game.FromSnapshot(_game.PollSnapshot());
        if (!result.HasAny)
        {
            if (!string.IsNullOrWhiteSpace(result.StatusText))
            {
                DetectedScoreText = result.StatusText;
            }

            return false;
        }

        var changed = false;
        if (result.ReplayPath is not null && !PathsEqual(result.ReplayPath, ReplayPath))
        {
            ReplayPath = result.ReplayPath;
            changed = true;
        }

        if (result.BeatmapPath is not null && !PathsEqual(result.BeatmapPath, BeatmapPath))
        {
            BeatmapPath = result.BeatmapPath;
            changed = true;
        }

        DetectedScoreText = result.StatusText;

        if (changed)
        {
            Log("Detected from osu!: " + result.StatusText);
        }

        return changed;
    }

    /// <summary>Resolves the beatmap for a manually chosen replay using its embedded MD5.</summary>
    public AutoDetectResult LocateFromReplay(string replayPath)
    {
        _game.Initialise();
        var result = _game.FromReplayFile(replayPath);
        if (result.BeatmapPath is not null)
        {
            BeatmapPath = result.BeatmapPath;
        }

        DetectedScoreText = result.StatusText;
        Log("Beatmap lookup: " + result.StatusText);
        return result;
    }

    public IReadOnlyList<CandidateItem> CandidatesFromReplay(string replayPath)
    {
        _game.Initialise();
        return _game.FromReplayFile(replayPath).Candidates;
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _game.Dispose();

    // ---------------------------------------------------------------- INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
