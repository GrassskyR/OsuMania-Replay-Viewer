using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.App.Localization;
using static ManiaReplayMaster.App.Localization.Localizer;
using PipelineRunner = ManiaReplayMaster.Core.Pipeline.Pipeline;

namespace ManiaReplayMaster.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _detectTimer;
    private CancellationTokenSource? _cts;
    private DateTime _startedAt;
    private IReadOnlyList<CandidateItem>? _lastDetectedCandidates;
    private bool _updatingCandidates;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Localizer.Current.PropertyChanged += OnLanguageChanged;

        _vm.InitialiseGameLink();
        _vm.Log(Text("Log.Ready"));

        // ~500 ms is slow enough to be free and fast enough to feel instant when a score panel
        // opens; the plan's §5.2 range is 100-200 ms for the memory poll, which happens inside
        // the snapshot next to nothing.
        _detectTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _detectTimer.Tick += OnDetectTick;
        _detectTimer.Start();
    }

    private void OnDetectTick(object? sender, EventArgs e)
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
                    StartRender(judgeOnly: false);
                }
            }
        }
        catch (Exception ex)
        {
            _vm.Log(Text("Log.AutoDetectError", ex.Message));
        }
    }

    // ---------------------------------------------------------------- file selection

    private void OnBrowseReplay(object sender, RoutedEventArgs e)
    {
        var path = PickOpenFile(
            Text("Dialog.ReplayFilter"), Text("Dialog.ChooseReplay"));
        if (path is null)
        {
            return;
        }

        _vm.ReplayPath = path;
        RefreshCandidateList();
        AutoLocateIfPossible();
    }

    private void OnBrowseBeatmap(object sender, RoutedEventArgs e)
    {
        var path = PickOpenFile(
            Text("Dialog.BeatmapFilter"), Text("Dialog.ChooseBeatmap"));
        if (path is not null)
        {
            _vm.BeatmapPath = path;
        }
    }

    private string? PickOpenFile(string filter, string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = filter,
            Title = title,
            CheckFileExists = true
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

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

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
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

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == System.Windows.Data.Binding.IndexerName)
            ScoreCandidateBox.Items.Refresh();
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        // The slider is the hot control; keep the label in sync immediately.
        _vm.Speed = (int)Math.Round(e.NewValue);
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

    private void OnRender(object sender, RoutedEventArgs e) => StartRender(judgeOnly: false);

    private void OnJudgeOnly(object sender, RoutedEventArgs e) => StartRender(judgeOnly: true);

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts is null)
        {
            return;
        }

        _vm.Log(Text("Log.Cancelling"));
        _cts.Cancel();
    }

    private void StartRender(bool judgeOnly)
    {
        if (!_vm.CanRender)
        {
            _vm.Log(Text("Log.PickInputs"));
            return;
        }

        var replay = _vm.ReplayPath;
        var beatmap = _vm.BeatmapPath;
        var config = Config.Instance.Clone();
        var outputDir = Path.GetFullPath(config.OutputDir);
        Directory.CreateDirectory(outputDir);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _vm.IsBusy = true;
        _vm.Progress = 0;
        _vm.SetResultText(() => string.Empty);
        _startedAt = DateTime.Now;
        _vm.SetProgressText(() => Text("Progress.Starting"));
        _vm.Log(Text(judgeOnly ? "Log.Judging" : "Log.Rendering", Path.GetFileName(replay)));

        var progress = new Progress<EncodeProgress>(p =>
        {
            _vm.Progress = Math.Clamp(p.Percentage, 0, 100);
            _vm.SetProgressText(() => Describe(p.Percentage, _startedAt));
        });

        Task.Run(() =>
        {
            try
            {
                if (judgeOnly)
                {
                    RunJudgeOnly(beatmap, replay, config);
                }
                else
                {
                    var result = ManiaReplayMaster.Render.RenderExporter.Run(
                        beatmap, replay,
                        tempDirectory: null,
                        config: config,
                        progress: progress,
                        ffmpegPath: null,
                        log: null,
                        cancellationToken: token);

                    ReportSuccess(result.VideoPath, result.BeatMap, result.ReplayModel);
                }
            }
            catch (OperationCanceledException)
            {
                Report(() => Text("Log.Cancelled"), null);
            }
            catch (Exception ex)
            {
                Report(() => Text("Log.Failed", ex.Message), ex);
            }
        }, token);
    }

    private void RunJudgeOnly(string beatmapPath, string replayPath, Config config)
    {
        var previous = Config.Instance;
        Config.Instance = config;
        try
        {
            var preparation = PipelineRunner.Prepare(beatmapPath, replayPath);
            var beatMap = preparation.BeatMap;
            var replay = preparation.ReplayModel;
            var counts = preparation.Judger is Core.Judger.BaseJudger baseJudger
                ? baseJudger.JudgementFromJudging
                : new List<int>();

            Report(() => BuildSummary(beatMap, replay, counts), null);
        }
        finally
        {
            Config.Instance = previous;
        }
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

    private void ReportSuccess(string videoPath, BeatMap beatMap, ReplayModel replay)
    {
        var counts = beatMap.Notes
            .GroupBy(n => n.JudgementStart)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:{g.Count()}");
        var judgementCounts = string.Join(" ", counts);
        var accuracy = replay.Acc * 100;
        var noteCount = beatMap.Notes.Count;
        Report(() => Text("Summary.Video", Path.GetFileName(videoPath), accuracy, noteCount, judgementCounts), null);
    }

    private void Report(Func<string> describe, Exception? error)
    {
        Dispatcher.Invoke(() =>
        {
            _vm.IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            _vm.SetResultText(() => describe().Split('\n')[0]);
            if (error is null)
            {
                _vm.Progress = 100;
                _vm.SetProgressText(() => Text("Progress.Done"));
            }
            else
            {
                _vm.SetProgressText(() => Text("Progress.Stopped"));
            }

            _vm.Log(describe());
            if (error is not null)
            {
                _vm.Log(error.ToString());
            }
        });
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

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            _detectTimer.Stop();
            Localizer.Current.PropertyChanged -= OnLanguageChanged;
            _cts?.Cancel();
            _vm.Dispose();
        }
        catch
        {
            // Shutting down must not throw.
        }
    }
}
