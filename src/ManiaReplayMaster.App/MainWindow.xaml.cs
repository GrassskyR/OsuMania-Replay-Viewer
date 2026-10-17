using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode;
using PipelineRunner = ManiaReplayMaster.Core.Pipeline.Pipeline;

namespace ManiaReplayMaster.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _detectTimer;
    private CancellationTokenSource? _cts;
    private DateTime _startedAt;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.InitialiseGameLink();
        _vm.Log("Ready. Drop a .osr onto the window, or let auto-detect fill it in.");

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
            if (_vm.PollAutoDetect())
            {
                RefreshCandidateList();
                if (_vm.AutoStartRender && _vm.CanRender)
                {
                    _vm.Log("Auto-start render is enabled — starting.");
                    StartRender(judgeOnly: false);
                }
            }
        }
        catch (Exception ex)
        {
            _vm.Log("auto-detect error: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- file selection

    private void OnBrowseReplay(object sender, RoutedEventArgs e)
    {
        var path = PickOpenFile(
            "Replay files (*.osr;*.mr)|*.osr;*.mr|osu! replays (*.osr)|*.osr|Malody replays (*.mr)|*.mr|All files (*.*)|*.*",
            "Choose a replay");
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
            "Beatmap files (*.osu;*.osz;*.mc;*.mcz;*.zip)|*.osu;*.osz;*.mc;*.mcz;*.zip|All files (*.*)|*.*",
            "Choose a beatmap");
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
            _vm.Log("Dropped files are neither replays nor beatmaps.");
            return;
        }

        _vm.Log($"Dropped: replay={(replay is null ? "-" : Path.GetFileName(replay))}, " +
                $"beatmap={(beatmap is null ? "-" : Path.GetFileName(beatmap))}");

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
        ScoreCandidateBox.ItemsSource = null;
        if (string.IsNullOrWhiteSpace(_vm.ReplayPath) || !File.Exists(_vm.ReplayPath))
        {
            return;
        }

        try
        {
            var candidates = _vm.CandidatesFromReplay(_vm.ReplayPath);
            ScoreCandidateBox.ItemsSource = candidates;
            if (candidates.Count > 1)
            {
                ScoreCandidateBox.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            _vm.Log("candidate lookup failed: " + ex.Message);
        }
    }

    private void OnCandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ScoreCandidateBox.SelectedItem is CandidateItem item)
        {
            _vm.ReplayPath = item.ReplayPath;
            _vm.Log("Switched to candidate: " + item.Display);
        }
    }

    // ---------------------------------------------------------------- parameters

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
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
            _vm.Log("could not open output folder: " + ex.Message);
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

        _vm.Log("Cancelling…");
        _cts.Cancel();
    }

    private void StartRender(bool judgeOnly)
    {
        if (!_vm.CanRender)
        {
            _vm.Log("Pick both a replay and a beatmap first.");
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
        _vm.ResultText = string.Empty;
        _startedAt = DateTime.Now;
        _vm.ProgressText = "starting…";
        _vm.Log(judgeOnly
            ? $"Judging {Path.GetFileName(replay)}…"
            : $"Rendering {Path.GetFileName(replay)}…");

        var progress = new Progress<EncodeProgress>(p =>
        {
            _vm.Progress = Math.Clamp(p.Percentage, 0, 100);
            _vm.ProgressText = Describe(p.Percentage, _startedAt);
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
                Report("Cancelled.", null);
            }
            catch (Exception ex)
            {
                Report("Failed: " + ex.Message, ex);
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

            var summary = BuildSummary(beatMap, replay, counts);
            Report(summary, null);
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
            $"{beatMap.GameMode} · {beatMap.Key}K · OD {beatMap.Od:0.#} · rate x{replay.Rate:0.###}",
            $"notes {beatMap.Notes.Count}, actions {replay.ReplayData.Count}, " +
            $"duration {beatMap.Duration / 1000.0:0.0}s",
            $"accuracy {replay.Acc * 100:0.00}%"
        };

        if (counts.Count > 0)
        {
            lines.Add("judgement counts (ordered): " + string.Join(", ", counts));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void ReportSuccess(string videoPath, BeatMap beatMap, ReplayModel replay)
    {
        var counts = beatMap.Notes
            .GroupBy(n => n.JudgementStart)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:{g.Count()}");
        var summary = $"{Path.GetFileName(videoPath)} · accuracy {replay.Acc * 100:0.00}% · " +
                      $"notes {beatMap.Notes.Count} · " + string.Join(" ", counts);
        Report(summary, null);
    }

    private void Report(string message, Exception? error)
    {
        Dispatcher.Invoke(() =>
        {
            _vm.IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            _vm.ResultText = message.Split('\n')[0];
            if (error is null)
            {
                _vm.Progress = 100;
                _vm.ProgressText = "done";
            }
            else
            {
                _vm.ProgressText = "stopped";
            }

            _vm.Log(message);
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
            return "starting…";
        }

        var total = elapsed.TotalSeconds / (percent / 100.0);
        var remaining = TimeSpan.FromSeconds(Math.Max(0, total - elapsed.TotalSeconds));
        return $"{percent:0.0}%  ·  elapsed {Format(elapsed)}  ·  ~{Format(remaining)} left";
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
            _cts?.Cancel();
            _vm.Dispose();
        }
        catch
        {
            // Shutting down must not throw.
        }
    }
}
