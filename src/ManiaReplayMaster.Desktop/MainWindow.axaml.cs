using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ManiaReplayMaster.App;

using Avalonia.Threading;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.App.Localization;
using static ManiaReplayMaster.App.Localization.Localizer;
using PipelineRunner = ManiaReplayMaster.Core.Pipeline.Pipeline;

namespace ManiaReplayMaster.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _detectTimer;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _workFinished;
    private bool _closing;
    private bool _allowClose;
    private readonly CancellationTokenSource _encoderDetectionCancellation = new();
    private Task? _encoderDetection;
    internal MainViewModel ViewModel => _vm;
    private IReadOnlyList<CandidateItem>? _lastDetectedCandidates;
    private bool _updatingCandidates;

    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        InitializeComponent();
        DataContext = _vm;
        Closing += OnClosing;
        Opened += (_, _) => _encoderDetection = DetectEncodersAsync();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        LanguageBox.SelectedIndex = _vm.L.Preference == "zh-Hans" ? 1 : _vm.L.Preference == "en" ? 2 : 0;
        LanguageBox.SelectionChanged += OnLanguageSelected;
        Localizer.Current.PropertyChanged += OnLanguageChanged;

        _vm.InitialiseGameLink();
        _vm.Log(Text("Log.Ready"));

        // ~500 ms is slow enough to be free and fast enough to feel instant when a score panel
        // opens; the plan's §5.2 range is 100-200 ms for the memory poll, which happens inside
        // the snapshot next to nothing.
        _detectTimer = new DispatcherTimer()
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _detectTimer.Tick += OnDetectTick;
        _detectTimer.Start();
    }

    private async void OnDetectTick(object? sender, EventArgs e)
    {
        try
        {
            var changed = _vm.PollAutoDetect();
            if (_vm.DetectedCandidates is { } candidates && !ReferenceEquals(candidates, _lastDetectedCandidates))
            {
                _lastDetectedCandidates = candidates;
                UpdateCandidateList(candidates);
            }
            if (changed)
            {
                if (_vm.AutoStartRender && _vm.CanRender)
                {
                    _vm.Log(Text("Log.AutoStart"));
                    await StartRenderAsync(judgeOnly: false);
                }
            }
        }
        catch (Exception ex)
        {
            _vm.Log(Text("Log.AutoDetectError", ex.Message));
        }
    }

    // ---------------------------------------------------------------- file selection

    private async void OnBrowseReplay(object sender, RoutedEventArgs e)
    {
        var path = await PickOpenFile(
            Text("Dialog.ReplayFilter"), Text("Dialog.ChooseReplay"));
        if (path is null)
        {
            return;
        }

        _vm.ReplayPath = path;
        RefreshCandidateList();
        AutoLocateIfPossible();
    }

    private async void OnBrowseBeatmap(object sender, RoutedEventArgs e)
    {
        var path = await PickOpenFile(
            Text("Dialog.BeatmapFilter"), Text("Dialog.ChooseBeatmap"));
        if (path is not null)
        {
            _vm.BeatmapPath = path;
        }
    }

    private async Task<string?> PickOpenFile(string filter, string title)
    {
        var patterns = filter.Contains("*.osr") ? new[] { "*.osr", "*.mr" } : new[] { "*.osu", "*.osz", "*.mc", "*.mcz", "*.zip" };
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (files is null || files.Length == 0) return;

        var replay = files.FirstOrDefault(f => IsExtension(f, ".osr", ".mr"));
        var beatmap = files.FirstOrDefault(f => IsExtension(f, ".osu", ".osz", ".mc", ".mcz", ".zip"));

        if (replay is not null)
        {
            _vm.ReplayPath = replay;
        }

        if (beatmap is not null)
        {
            _vm.BeatmapPath = beatmap;
        }

        if (replay is null && beatmap is null)
        {
            _vm.Log(Text("Log.InvalidDrop"));
            return;
        }

        _vm.Log(Text("Log.Dropped", replay is null ? "-" : Path.GetFileName(replay),
            beatmap is null ? "-" : Path.GetFileName(beatmap)));

        RefreshCandidateList();
        if (replay is not null && beatmap is null)
        {
            AutoLocateIfPossible();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static bool IsExtension(string path, params string[] extensions)
    {
        var extension = Path.GetExtension(path);
        return extensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase));
    }

    private void OnSwapPaths(object sender, RoutedEventArgs e)
    {
        (_vm.ReplayPath, _vm.BeatmapPath) = (_vm.BeatmapPath, _vm.ReplayPath);
    }

    private void AutoLocateIfPossible()
    {
        if (string.IsNullOrWhiteSpace(_vm.ReplayPath) || !File.Exists(_vm.ReplayPath))
        {
            return;
        }

        if (IsExtension(_vm.ReplayPath, ".osr"))
        {
            _vm.LocateFromReplay(_vm.ReplayPath);
        }
    }

    private void OnLocateFromReplay(object sender, RoutedEventArgs e) => AutoLocateIfPossible();

    // ---------------------------------------------------------------- candidates

    private void RefreshCandidateList()
    {
        if (string.IsNullOrWhiteSpace(_vm.ReplayPath) || !File.Exists(_vm.ReplayPath))
        {
            UpdateCandidateList(Array.Empty<CandidateItem>());
            return;
        }

        try
        {
            var candidates = _vm.CandidatesFromReplay(_vm.ReplayPath);
            UpdateCandidateList(candidates);
        }
        catch (Exception ex)
        {
            _vm.Log(Text("Log.CandidateFailed", ex.Message));
        }
    }

    private void UpdateCandidateList(IReadOnlyList<CandidateItem> candidates)
    {
        // Polling must not reset an open menu or select an unverified replay on the user's behalf.
        if (ScoreCandidateBox.ItemsSource is IReadOnlyList<CandidateItem> current
            && current.Select(c => (c.ReplayPath, c.Display)).SequenceEqual(candidates.Select(c => (c.ReplayPath, c.Display))))
            return;

        _updatingCandidates = true;
        try
        {
            ScoreCandidateBox.ItemsSource = candidates;
            ScoreCandidateBox.SelectedItem = candidates.FirstOrDefault(c =>
                string.Equals(c.ReplayPath, _vm.ReplayPath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _updatingCandidates = false;
        }
    }

    private void OnCandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCandidates) return;
        if (ScoreCandidateBox.SelectedItem is CandidateItem item)
        {
            _vm.ReplayPath = item.ReplayPath;
            _vm.Log(Text("Log.CandidateSelected", item.Display));
            AutoLocateIfPossible();
        }
    }

    // ---------------------------------------------------------------- parameters

    private async Task DetectEncodersAsync()
    {
        await _vm.ChangeFfmpegAsync(null, _encoderDetectionCancellation.Token);
    }

    private async void OnBrowseFfmpeg(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanChangeFfmpeg || _closing) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Text("Ffmpeg.Browse"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("FFmpeg") { Patterns = ["*.exe"] }]
        });
        if (_closing || files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        _encoderDetection = _vm.ChangeFfmpegAsync(path, _encoderDetectionCancellation.Token);
        await _encoderDetection;
    }

    private async void OnResetFfmpeg(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanChangeFfmpeg || _closing) return;
        _encoderDetection = _vm.ChangeFfmpegAsync(string.Empty, _encoderDetectionCancellation.Token);
        await _encoderDetection;
    }

    private void OnCodecDropDown(object? sender, RoutedEventArgs e)
    {
        var open = !CodecBox.IsDropDownOpen;
        CodecBox.Focus();
        CodecBox.IsDropDownOpen = open;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]")
            RefreshCandidateList();
    }

    private void OnLanguageSelected(object? sender, SelectionChangedEventArgs e)
    {
        _vm.L.Preference = LanguageBox.SelectedIndex switch { 1 => "zh-Hans", 2 => "en", _ => "auto" };
    }

    private void OnOpenOutputDir(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetFullPath(_vm.OutputDir);
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _vm.Log(Text("Log.OpenOutputFailed", ex.Message));
        }
    }

    // ---------------------------------------------------------------- actions

    private async void OnRender(object sender, RoutedEventArgs e) => await StartRenderAsync(judgeOnly: false);

    private async void OnJudgeOnly(object sender, RoutedEventArgs e) => await StartRenderAsync(judgeOnly: true);

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts is null)
        {
            return;
        }

        _vm.Log(Text("Log.Cancelling"));
        _cts.Cancel();
    }

    internal async Task StartRenderAsync(bool judgeOnly)
    {
        if (_closing || !_vm.CanRender)
        {
            _vm.Log(Text("Log.PickInputs"));
            return;
        }

        var replay = _vm.ReplayPath;
        var beatmap = _vm.BeatmapPath;
        var config = Config.Instance.Clone();
        var ffmpegPath = _vm.ResolveFfmpeg();
        using var cancellation = new CancellationTokenSource();
        _cts = cancellation;
        _workFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = cancellation.Token;
        _vm.IsBusy = true;
        _vm.Progress = 0;
        _vm.SetResultText(() => string.Empty);
        var startedAt = DateTime.Now;
        _vm.SetProgressText(() => Text("Progress.Starting"));
        _vm.Log(Text(judgeOnly ? "Log.Judging" : "Log.Rendering", Path.GetFileName(replay)));

        var progress = new Progress<EncodeProgress>(p =>
        {
            if (_cts != cancellation) return;
            _vm.Progress = Math.Clamp(p.Percentage, 0, 100);
            _vm.SetProgressText(() => Describe(p.Percentage, startedAt));
        });
        IProgress<EncoderSelection> encoderProgress = new Progress<EncoderSelection>(selection =>
        {
            if (_cts != cancellation) return;
            if (selection.IsFallback) _vm.Log(Text("Log.EncoderFallback"));
            _vm.Log(Text("Log.EncoderSelected", selection.Codec, selection.Preset));
        });

        try
        {
            var summary = await Task.Run(() =>
            {
                if (judgeOnly)
                {
                    return RunJudgeOnly(beatmap, replay, config);
                }

                var result = ManiaReplayMaster.Render.RenderExporter.Run(
                    beatmap, replay, config: config, progress: progress,
                    ffmpegPath: ffmpegPath, cancellationToken: token, encoderSelected: encoderProgress.Report);
                return BuildVideoSummary(result.VideoPath, result.BeatMap, result.ReplayModel);
            }, token);
            token.ThrowIfCancellationRequested();
            Report(summary, completed: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Report(() => Text("Log.Cancelled"), completed: false);
        }
        catch (Exception ex)
        {
            Report(() => Text("Log.Failed", ex.Message), completed: false, error: ex);
        }
        finally
        {
            _cts = null;
            _vm.IsBusy = false;
            _workFinished?.TrySetResult();
        }
    }

    private static Func<string> RunJudgeOnly(string beatmapPath, string replayPath, Config config)
    {
        var preparation = PipelineRunner.Prepare(beatmapPath, replayPath, config: config);
        var counts = preparation.Judger is Core.Judger.BaseJudger baseJudger
            ? baseJudger.JudgementFromJudging
            : new List<int>();
        return () => BuildSummary(preparation.BeatMap, preparation.ReplayModel, counts);
    }

    private static string BuildSummary(BeatMap beatMap, ReplayModel replay, IReadOnlyList<int> counts)
    {
        var lines = new List<string>
        {
            Text("Summary.Header", beatMap.GameMode, beatMap.Key, beatMap.Od, replay.Rate),
            Text("Summary.Notes", beatMap.Notes.Count, replay.ReplayData.Count, beatMap.Duration / 1000.0),
            Text("Summary.Accuracy", replay.Acc * 100)
        };

        if (counts.Count > 0)
        {
            lines.Add(Text("Summary.Counts", string.Join(", ", counts)));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static Func<string> BuildVideoSummary(string videoPath, BeatMap beatMap, ReplayModel replay)
    {
        var counts = beatMap.Notes
            .GroupBy(n => n.JudgementStart)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:{g.Count()}");
        var judgementCounts = string.Join(" ", counts);
        var accuracy = replay.Acc * 100;
        var noteCount = beatMap.Notes.Count;
        return () => Text("Summary.Video", Path.GetFileName(videoPath), accuracy, noteCount, judgementCounts);
    }

    private void Report(Func<string> describe, bool completed, Exception? error = null)
    {
        _vm.SetResultText(() => describe().Split('\n')[0]);
        if (completed)
        {
            _vm.Progress = 100;
            _vm.SetProgressText(() => Text("Progress.Done"));
        }
        else
        {
            _vm.SetProgressText(() => Text("Progress.Stopped"));
        }

        _vm.Log(describe());
        if (error is not null) _vm.Log(error.ToString());
    }

    private static string Describe(double percent, DateTime startedAt)
    {
        var elapsed = DateTime.Now - startedAt;
        if (percent <= 0.5 || elapsed.TotalSeconds < 1)
        {
            return Text("Progress.Starting");
        }

        var total = elapsed.TotalSeconds / (percent / 100.0);
        var remaining = TimeSpan.FromSeconds(Math.Max(0, total - elapsed.TotalSeconds));
        return Text("Progress.Details", percent, Format(elapsed), Format(remaining));
    }

    private static string Format(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _detectTimer.Stop();
        _encoderDetectionCancellation.Cancel();
        _cts?.Cancel();
        if (_workFinished is { } completion) await completion.Task;
        if (_encoderDetection is { } detection) await detection;
        _encoderDetectionCancellation.Dispose();
        Localizer.Current.PropertyChanged -= OnLanguageChanged;
        _vm.Dispose();
        _allowClose = true;
        Dispatcher.UIThread.Post(Close);
    }
}
