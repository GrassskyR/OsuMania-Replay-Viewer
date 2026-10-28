using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ManiaReplayMaster.Presentation.Localization;
using static ManiaReplayMaster.Presentation.Localization.Localizer;

namespace ManiaReplayMaster.Presentation;

/// <summary>
/// View-model for the single main window. Binds directly onto the ported <c>Config</c> fields so the
/// parameter panel maps 1:1 onto the reference implementation's config (plan §1.4), and keeps the
/// UI thread free while rendering runs on a worker.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly GameBridge _game = new();
    private readonly UiSettings _settings;
    public Localizer L => Localizer.Current;

    public IReadOnlyList<CandidateItem>? DetectedCandidates { get; private set; }

    public MainViewModel(UiSettings? settings = null)
    {
        _settings = settings ?? UiSettings.Current;
        var config = Core.Model.Config.Instance;
        _settings.ApplyRendering(config);
        _ffmpegPath = _settings.FfmpegPath;
        _speed = config.Speed;
        _width = config.Width;
        _height = config.Height;
        _framePerSecond = config.FramePerSecond;
        _codec = config.Codec;
        if (!CodecChoices.Contains(_codec)) CodecChoices.Add(_codec);
        PresetChoices = GetPresetChoices(_codec, config.CodecPreset);
        _codecPreset = CanSelectPreset && PresetChoices.Contains(config.CodecPreset) ? config.CodecPreset : "auto";
        config.CodecPreset = _codecPreset;
        _outputDir = config.OutputDir;
        _debugLogging = config.Debug;

        LogLines = new ObservableCollection<string>();
        Localizer.Current.PropertyChanged += OnLanguageChanged;
    }

    public ObservableCollection<string> LogLines { get; }

    private string _ffmpegPath;
    public string FfmpegPath => _ffmpegPath;
    public string ResolveFfmpeg() => string.IsNullOrWhiteSpace(_ffmpegPath)
        ? ManiaReplayMaster.Encode.FfmpegLocator.Locate() : _ffmpegPath;
    public bool CanChangeFfmpeg => !IsBusy && !IsCheckingFfmpeg;
    private bool _isCheckingFfmpeg;
    public bool IsCheckingFfmpeg
    {
        get => _isCheckingFfmpeg;
        set
        {
            if (!Set(ref _isCheckingFfmpeg, value)) return;
            OnPropertyChanged(nameof(CanChangeFfmpeg));
            OnPropertyChanged(nameof(CanRender));
        }
    }

    private void UseFfmpeg(string path)
    {
        if (Set(ref _ffmpegPath, path, nameof(FfmpegPath))) _settings.SetFfmpegPath(path);
    }

    public void ReplaceAvailableCodecs(IEnumerable<string> codecs)
    {
        _updatingCodecChoices = true;
        try
        {
            CodecChoices.Clear();
            foreach (var codec in codecs.Distinct()) CodecChoices.Add(codec);
            CodecChoices.Add("auto");
        }
        finally { _updatingCodecChoices = false; }
        OnPropertyChanged(nameof(Codec));
        OnPropertyChanged(nameof(CodecPreset));
        if (!CodecChoices.Contains(Codec) && Codec != "auto")
            Log(Text("Ffmpeg.UnavailableCodec", Codec));
    }

    public async Task ChangeFfmpegAsync(string? newPath, CancellationToken cancellationToken)
    {
        if (IsCheckingFfmpeg || IsBusy) return;
        IsCheckingFfmpeg = true;
        try
        {
            var path = newPath ?? FfmpegPath;
            if (!string.IsNullOrWhiteSpace(path)) path = Path.GetFullPath(path);
            var executable = string.IsNullOrWhiteSpace(path)
                ? ManiaReplayMaster.Encode.FfmpegLocator.Locate() : path;
            var result = await Task.Run(() =>
            {
                var version = newPath is not null
                    ? ManiaReplayMaster.Encode.FfmpegCapabilities.Validate(executable, cancellationToken) : null;
                var codecs = ManiaReplayMaster.Encode.VideoEncoderSelector.DetectAvailable(executable,
                    new ManiaReplayMaster.Encode.Diagnostics.PortLogger(), cancellationToken);
                return (version, codecs);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (newPath is not null) UseFfmpeg(path);
            ReplaceAvailableCodecs(result.codecs);
            Log(Text("Ffmpeg.Active", executable));
            if (result.version is not null) Log(result.version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Log(Text("Ffmpeg.Failed", ex.Message));
        }
        finally { IsCheckingFfmpeg = false; }
    }

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
        set
        {
            if (Set(ref _autoDetectFromGame, value) && !value)
                SetDetectedScore(() => Text("Status.AutoOff"));
        }
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
        set
        {
            if (Set(ref _memoryEnabled, value))
            {
                _game.SetMemoryEnabled(value);
                if (!value) SetDetectedScore(() => Text("Status.MemoryOff"));
            }
        }
    }

    private Func<string> _detectedScoreText = () => Text("Status.NoScore");
    public string DetectedScoreText => _detectedScoreText();
    private void SetDetectedScore(Func<string> describe)
    {
        var previous = DetectedScoreText;
        _detectedScoreText = describe;
        if (previous != DetectedScoreText) OnPropertyChanged(nameof(DetectedScoreText));
    }

    private bool _gameInitialised;
    private bool _osuRunning;
    public string GameStatusText => Text(!_gameInitialised ? "Status.Checking"
        : _osuRunning ? "Status.Running" : "Status.Closed");

    private string _gamePillBrush = "#9AA0AD";
    public string GamePillBrush
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
            _settings.RememberRendering(Core.Model.Config.Instance);
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

    public ObservableCollection<string> CodecChoices { get; } = ["libx264", "auto"];
    private bool _updatingCodecChoices;

    public void AddAvailableCodecs(IEnumerable<string> codecs)
    {
        // Preserve a saved/custom selection and its preset while detection finishes in the background.
        foreach (var codec in codecs)
            if (!CodecChoices.Contains(codec)) CodecChoices.Add(codec);
    }

    public IReadOnlyList<string> PresetChoices { get; private set; } = ["auto"];

    private static IReadOnlyList<string> GetPresetChoices(string codec, string savedPreset = "auto")
    {
        string[] presets = codec switch
        {
            "libx264" => ["auto", "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo"],
            "h264_nvenc" => ["auto", "p1", "p2", "p3", "p4", "p5", "p6", "p7"],
            "h264_qsv" => ["auto", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"],
            "h264_amf" => ["auto", "speed", "balanced", "quality", "high_quality"],
            _ => ["auto"]
        };
        // Retain valid older NVENC aliases and presets belonging to custom encoders.
        var legacyAlias = codec == "h264_nvenc" && savedPreset is "fast" or "medium" or "slow";
        var customCodec = codec is not ("auto" or "libx264" or "h264_nvenc" or "h264_qsv" or "h264_amf");
        return !presets.Contains(savedPreset) && (legacyAlias || customCodec) ? [.. presets, savedPreset] : presets;
    }

    public bool CanSelectPreset => !string.Equals(Codec, "auto", StringComparison.OrdinalIgnoreCase);

    private string _codec = "libx264";
    public string Codec
    {
        get => _codec;
        set
        {
            if (_updatingCodecChoices || string.IsNullOrWhiteSpace(value)) return;
            if (Set(ref _codec, value))
            {
                Core.Model.Config.Instance.Codec = value;
                PresetChoices = GetPresetChoices(value);
                OnPropertyChanged(nameof(PresetChoices));
                OnPropertyChanged(nameof(CanSelectPreset));
                CodecPreset = "auto";
                OnPropertyChanged(nameof(CodecPreset));
                _settings.RememberRendering(Core.Model.Config.Instance);
            }
        }
    }

    private string _codecPreset = "auto";
    public string CodecPreset
    {
        get => _codecPreset;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && Set(ref _codecPreset, value))
            {
                Core.Model.Config.Instance.CodecPreset = value;
                _settings.RememberRendering(Core.Model.Config.Instance);
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

    private Func<string> _progressText = () => Text("Progress.Idle");
    public string ProgressText => _progressText();
    public void SetProgressText(Func<string> describe)
    {
        _progressText = describe;
        OnPropertyChanged(nameof(ProgressText));
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
                OnPropertyChanged(nameof(CanChangeFfmpeg));
            }
        }
    }

    private Func<string> _resultText = () => string.Empty;
    public string ResultText => _resultText();
    public void SetResultText(Func<string> describe)
    {
        _resultText = describe;
        OnPropertyChanged(nameof(ResultText));
    }

    public bool CanRender => !IsBusy && !IsCheckingFfmpeg
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
        _gameInitialised = true;
        RefreshGameStatus();
        if (_game.Available && _game.OsuFound)
        {
            Log(Text("Game.Located", _game.OsuRoot));
        }
        else if (_game.Available)
        {
            Log(Text("Game.NotFound"));
        }
        else
        {
            Log(_game.Detail);
        }
    }

    private void RefreshGameStatus()
    {
        _osuRunning = _game.OsuRunning;
        GamePillBrush = _osuRunning ? "#79D2A6" : "#9AA0AD";
        OnPropertyChanged(nameof(GameStatusText));
    }

    /// <summary>
    /// Polls for a game snapshot and, if auto-detect is enabled, applies any newly resolved
    /// replay/beatmap paths. Returns true when auto-start should fire.
    /// </summary>
    public bool PollAutoDetect()
    {
        // Keep the status live even when score detection/memory reading is disabled or rendering is active.
        RefreshGameStatus();
        if (!AutoDetectFromGame || IsBusy || !_game.Available)
        {
            return false;
        }

        if (!MemoryEnabled)
        {
            SetDetectedScore(() => Text("Status.MemoryOff"));
            return false;
        }

        var snapshot = _game.PollSnapshot();
        var result = _game.FromSnapshot(snapshot);
        // Candidate selection must also work when no replay is confident enough for auto-fill.
        // Outside a score screen, preserve the list loaded from a manually chosen replay.
        if (snapshot.HasBeatmapIdentity && snapshot.State is Game.OsuGameState.Results or Game.OsuGameState.ScorePanelOpen)
            DetectedCandidates = result.Candidates;
        if (!result.HasAny)
        {
            if (!string.IsNullOrWhiteSpace(result.StatusText))
            {
                SetDetectedScore(result.Describe);
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

        SetDetectedScore(result.Describe);

        if (changed)
        {
            Log(Text("Log.Detected", result.StatusText));
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

        SetDetectedScore(result.Describe);
        Log(Text("Log.BeatmapLookup", result.StatusText));
        return result;
    }

    public IReadOnlyList<CandidateItem> CandidatesFromReplay(string replayPath)
    {
        _game.Initialise();
        return _game.FromReplayFile(replayPath).Candidates;
    }

    private static bool PathsEqual(string a, string b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        OnPropertyChanged(nameof(DetectedScoreText));
        OnPropertyChanged(nameof(GameStatusText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ResultText));
    }

    public void Dispose()
    {
        Localizer.Current.PropertyChanged -= OnLanguageChanged;
        _game.Dispose();
    }

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
