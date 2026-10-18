// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.Encode.Compat;
using ManiaReplayMaster.Encode.Diagnostics;

namespace ManiaReplayMaster.Render;

/// <summary>
/// Port of <c>me.replaymaster.Render</c> (Kotlin/AWT -&gt; C#/GDI+).
///
/// The original does NOT render video frames: for every "window" it draws one tall static strip
/// containing the notes of that time window, and <c>ffmpeg</c> later crops the scrolling viewport
/// from it (rewrite plan §1.1.1, route E1). This port keeps that
/// model exactly: the canvas is <c>gameWidth x (stepSize + height)</c>, it is cleared to black on
/// every iteration, and the slices are written as <c>name_0.png</c>, <c>name_1.png</c>, ...
///
/// Geometry uses the original speed scale at 60 FPS, independent of the output frame rate.
/// Slice boundaries align to both whole pixels and whole frames so fractional frame movement
/// does not introduce jumps when the encoder switches strips.
/// <code>
/// pixelsPerSecond = Config.speed * 60
/// timeHeightRatio = pixelsPerSecond / 1000.0
/// timeToHeight(t) = t * timeHeightRatio
/// heightToTime(h) = h / timeHeightRatio
/// gameWidth       = (isTaiko &amp;&amp; taikoSingleColumn) ? width / 4 : width
/// columnWidth     = Config.width / key          // deliberately Config.width, not gameWidth
/// pixelAlignment  = pixelsPerSecond / gcd(pixelsPerSecond, framePerSecond)
/// totalHeight     = ensureMultiple(timeToHeight(duration), pixelAlignment)
/// stepSize        = ensureMultiple(maxStepSize, pixelAlignment)
/// windowSize      = stepSize + Config.height
/// </code>
///
/// Parity target is "judgement-identical + visually equivalent", not pixel-identical: Java2D and
/// GDI+ antialias differently. Two deliberate GDI+ mappings are therefore made explicit:
/// <list type="bullet">
/// <item>the reference's <c>fillRoundRect(x, y, w, h, 1, 1)</c> / <c>drawRoundRect(x, y, w, h, 1, 1)</c>
/// (1-pixel corner arcs, i.e. a plain rectangle to the eye) become <c>FillRectangle</c> /
/// <c>DrawRectangle</c>;</item>
/// <item>the reference's antialiasing hint is OFF for rectangles and explicitly turned ON by
/// <c>drawRound()</c>, where it then persists for the rest of that window's drawing (the hint is
/// only reset by <c>clear()</c>). <see cref="CreateGraphics"/> starts each window with
/// <c>SmoothingMode.None</c> and the taiko oval primitive flips it to <c>AntiAlias</c>, which
/// reproduces that persistence.</item>
/// </list>
///
/// Added over the reference (which had neither): cooperative cancellation between windows and an
/// <see cref="IProgress{T}"/> sink carrying the render stage's share of the global bar (0..30,
/// matching <c>ReplayMaster.printProgress("render", 0, 30, ...)</c>).
/// </summary>
public sealed class Renderer : IDisposable
{
    private readonly BeatMap _beatMap;
    private readonly ReplayModel _replayModel;
    private readonly string _outputImage;
    private readonly Config _config;
    private readonly PortLogger _log;

    private readonly int _windowFrameCount;
    private readonly double _timeHeightRatio;
    private readonly int _gameHeight;
    private readonly int _gameWidth;
    private readonly int _columnWidth;
    private readonly int _totalHeight;
    private readonly int _stepSize;
    private readonly int _windowSize;

    private readonly Bitmap _bufferedImage;
    private readonly RgbPngWriter _pngWriter;
    private readonly Dictionary<int, SolidBrush> _brushes = new();
    private readonly Dictionary<(int Color, int Stroke), Pen> _pens = new();
    private Font? _timestampFont;
    private bool _disposed;

    public Renderer(BeatMap beatMap, ReplayModel replayModel, string outputImage,
        Config? config = null, PortLogger? log = null)
    {
        _beatMap = beatMap ?? throw new ArgumentNullException(nameof(beatMap));
        _replayModel = replayModel ?? throw new ArgumentNullException(nameof(replayModel));
        _outputImage = outputImage ?? throw new ArgumentNullException(nameof(outputImage));
        _config = config ?? Config.Instance;
        _log = log ?? PortLogger.FromConfig(_config);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_config.Speed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_config.FramePerSecond);
        var pixelsPerSecond = checked(_config.Speed * Config.ScrollReferenceFrameRate);
        var divisor = GreatestCommonDivisor(pixelsPerSecond, _config.FramePerSecond);
        var pixelAlignment = pixelsPerSecond / divisor;
        _timeHeightRatio = pixelsPerSecond / 1000.0;
        _gameHeight = _config.Height;
        _gameWidth = _beatMap.IsTaiko && _config.TaikoSingleColumn ? _config.Width / 4 : _config.Width;
        _columnWidth = _config.Width / _beatMap.Key;
        _totalHeight = EnsureMultiple(TimeToHeight(_beatMap.Duration), pixelAlignment);
        _stepSize = EnsureMultiple(_config.MaxStepSize, pixelAlignment);
        _windowFrameCount = checked(_stepSize / pixelAlignment * (_config.FramePerSecond / divisor));
        _windowSize = _stepSize + _gameHeight;

        // Every slice is cleared to opaque black; an alpha channel only adds PNG work.
        _bufferedImage = new Bitmap(_gameWidth, _windowSize, PixelFormat.Format24bppRgb);
        _pngWriter = new RgbPngWriter(_gameWidth);
    }

    /// <summary>Scroll step between two windows, i.e. one window's worth of pixels.</summary>
    public int StepSize => _stepSize;

    /// <summary>Height of one long-strip PNG, i.e. <c>stepSize + Config.height</c>.</summary>
    public int WindowSize => _windowSize;

    /// <summary>Duration expressed in pixels, rounded up to a whole-frame boundary.</summary>
    public int TotalHeight => _totalHeight;

    /// <summary>Canvas width.</summary>
    public int GameWidth => _gameWidth;

    /// <summary>Visible game height (<c>Config.height</c>).</summary>
    public int GameHeight => _gameHeight;

    /// <summary><c>Config.width / key</c>.</summary>
    public int ColumnWidth => _columnWidth;

    /// <summary>Exact number of output frames covering one scroll step.</summary>
    public int WindowFrameCount => _windowFrameCount;

    /// <summary>
    /// Port of <c>Render.start()</c>: writes the PNG slices and returns the values the encoder needs.
    /// </summary>
    /// <param name="progress">
    /// Receives the render stage's share of the global progress bar (0..30), the same value the
    /// reference passes to <c>ReplayMaster.printProgress("render", 0, 30, ...)</c>.
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before every window. Cancelling leaves the already-written slices on disk.
    /// </param>
    public RenderResult Start(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var index = 0;
        var currentStart = 0;

        // Used only as the progress denominator, exactly like the reference.
        var windowCount = (int)Math.Ceiling((_totalHeight - _gameHeight) / (double)_stepSize);
        var imagePaths = new List<string>();

        while (currentStart < _totalHeight)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (var graphics = CreateGraphics())
            {
                Clear(graphics);
                if (_beatMap.IsTaiko && !_config.TaikoSingleColumn)
                {
                    DrawTaikoBackground(graphics);
                }

                var time = HeightToTime(currentStart + _windowSize);
                RenderNotes(graphics, _beatMap.Notes, time, isBase: true);
                RenderNotes(graphics, _replayModel.ReplayData, time, isBase: false);
            }

            var imagePath = SlicePath(_outputImage, index);
            WritePng(imagePath);
            imagePaths.Add(imagePath);

            currentStart += _stepSize;
            index += 1;

            // Floating-point division on purpose: when totalHeight == gameHeight the reference
            // divides by zero here and reports Infinity, which the progress clamp turns into 30%.
            var interProgress = index * 100.0 / windowCount;
            progress?.Report(ProgressModel.Combine(0, ProgressModel.StageRender, interProgress));
        }

        var result = new RenderResult
        {
            WindowFrameCount = WindowFrameCount,
            GameWidth = _gameWidth,
            GameHeight = _gameHeight,
            WindowSize = _windowSize,
            StepSize = _stepSize,
            TotalHeight = _totalHeight,
            ColumnWidth = _columnWidth,
            WindowCount = windowCount,
            ImageCount = index,
            ImagePaths = imagePaths
        };

        _log.Debug($"render done: images={result.ImageCount} windowCount={windowCount} " +
                   $"windowFrameCount={result.WindowFrameCount} gameWidth={result.GameWidth} " +
                   $"canvas={result.GameWidth}x{result.WindowSize}");

        return result;
    }

    /// <summary>Convenience wrapper: renders one beatmap/replay pair to PNG slices in one call.</summary>
    public static RenderResult RenderToImages(BeatMap beatMap, ReplayModel replayModel,
        string outputImagePath, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default, Config? config = null, PortLogger? log = null)
    {
        using var renderer = new Renderer(beatMap, replayModel, outputImagePath, config, log);
        return renderer.Start(progress, cancellationToken);
    }

    // ---------------------------------------------------------------- geometry helpers

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    /// <summary>Kotlin: <c>ceil(x / base).toInt() * base</c>.</summary>
    private static int EnsureMultiple(double x, int baseValue)
    {
        var result = ToInt(Math.Ceiling(x / baseValue)) * baseValue;
        return result;
    }

    private double TimeToHeight(double time) => time * _timeHeightRatio;

    private double HeightToTime(int height) => height / _timeHeightRatio;

    /// <summary>
    /// Java's <c>double.toInt()</c> (a checked narrowing conversion: NaN -&gt; 0, out of range
    /// saturates). C#'s <c>(int)</c> cast is unspecified for those inputs, so make it explicit.
    /// </summary>
    private static int ToInt(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        if (value <= int.MinValue)
        {
            return int.MinValue;
        }

        return (int)value;
    }

    // ---------------------------------------------------------------- canvas plumbing

    private Graphics CreateGraphics()
    {
        var graphics = Graphics.FromImage(_bufferedImage);
        // AWT's default antialiasing hint for a fresh Graphics2D is OFF; drawRound() turns it on and
        // it then stays on until clear() disposes the context. GDI+ is made to behave identically.
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.PixelOffsetMode = PixelOffsetMode.Default;
        // AWT only antialiases text when KEY_TEXT_ANTIALIASING is set, which the reference never does.
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        return graphics;
    }

    /// <summary>Kotlin <c>clear()</c>: the whole canvas becomes opaque black.</summary>
    private void Clear(Graphics graphics)
    {
        graphics.FillRectangle(GetBrush(Color.Black), 0, 0, _bufferedImage.Width, _bufferedImage.Height);
    }

    /// <summary>Taiko multi-column background stripes (blue, blue, red -- in that order).</summary>
    private void DrawTaikoBackground(Graphics graphics)
    {
        var w = _bufferedImage.Width / _beatMap.Key;
        var padding = _bufferedImage.Width - w * 4;

        var blue = GetBrush(ReadColor(_config.TaikoBackgroundBlue));
        graphics.FillRectangle(blue, padding, 0, w, _bufferedImage.Height);
        graphics.FillRectangle(blue, padding + w * 3, 0, w, _bufferedImage.Height);
        graphics.FillRectangle(GetBrush(ReadColor(_config.TaikoBackgroundRed)), padding + w, 0, w * 2, _bufferedImage.Height);
    }

    /// <summary>
    /// The reference's <c>outputImage.path.replace(".png", "_$i.png")</c> -- a literal replacement of
    /// every occurrence, kept as-is.
    /// </summary>
    private static string SlicePath(string outputImage, int index)
        => outputImage.Replace(".png", $"_{index}.png");

    private void WritePng(string path)
    {
        // ImageIO.write silently returns false when the directory is missing; creating it is a
        // deliberate robustness addition so a render can never "succeed" without producing files.
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _pngWriter.Write(_bufferedImage, path);
    }

    // ---------------------------------------------------------------- drawing primitives

    private SolidBrush GetBrush(Color color)
    {
        var key = color.ToArgb();
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(color);
            _brushes.Add(key, brush);
        }
        return brush;
    }

    private Pen GetPen(Color color, int stroke)
    {
        var key = (color.ToArgb(), stroke);
        if (!_pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(color, stroke * 2 - 1)
            {
                StartCap = LineCap.Square,
                EndCap = LineCap.Square,
                LineJoin = LineJoin.Miter,
                Alignment = PenAlignment.Center
            };
            _pens.Add(key, pen);
        }
        return pen;
    }

    /// <summary>
    /// Port of the reference's <c>rectangle(p1, p2, color, strokeWidth, filledColor)</c>: the two
    /// corners are normalised with <c>abs</c>/<c>min</c>, and <c>strokeWidth == -1</c> means
    /// "fill only" (that is how actions are drawn, with no outline at all). Otherwise the outline
    /// uses <c>BasicStroke(strokeWidth * 2 - 1)</c> -- square caps, miter joins, centred on the path.
    /// </summary>
    private void RoundedRect(Graphics graphics, int x1, int y1, int x2, int y2, Color color,
        int strokeWidth, Color? filledColor = null)
    {
        var w = Math.Abs(x2 - x1);
        var h = Math.Abs(y2 - y1);
        var x = Math.Min(x1, x2);
        var y = Math.Min(y1, y2);
        var margin = Math.Max(0, strokeWidth) + 1;
        if (x + w < -margin || y + h < -margin || x > _gameWidth + margin || y > _windowSize + margin)
        {
            return;
        }

        if (strokeWidth != -1)
        {
            if (filledColor is not null)
            {
                graphics.FillRectangle(GetBrush(filledColor.Value), x, y, w, h);
            }

            graphics.DrawRectangle(GetPen(color, strokeWidth), x, y, w, h);
        }
        else
        {
            graphics.FillRectangle(GetBrush(color), x, y, w, h);
        }
    }

    /// <summary>
    /// Port of the reference's <c>drawRound(p, r, color, strokeWidth, filledColor)</c>: an oval of
    /// radius <paramref name="r"/> centred on <paramref name="p"/>. Setting
    /// <c>SmoothingMode.AntiAlias</c> here is what the reference does with
    /// <c>KEY_ANTIALIASING</c>, and like there it persists for the remainder of the window.
    /// </summary>
    private void Round(Graphics graphics, int px, int py, int r, Color color, int strokeWidth,
        Color? filledColor = null)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var margin = r + Math.Max(0, strokeWidth) + 1;
        if (px < -margin || py < -margin || px > _gameWidth + margin || py > _windowSize + margin)
        {
            return;
        }

        if (strokeWidth != -1)
        {
            if (filledColor is not null)
            {
                graphics.FillEllipse(GetBrush(filledColor.Value), px - r, py - r, 2 * r, 2 * r);
            }

            graphics.DrawEllipse(GetPen(color, strokeWidth), px - r, py - r, 2 * r, 2 * r);
        }
        else
        {
            graphics.FillEllipse(GetBrush(color), px - r, py - r, 2 * r, 2 * r);
        }
    }

    // ---------------------------------------------------------------- note rendering

    /// <summary>Taiko single/duel note: two concentric ovals (the reference really does both).</summary>
    private void RenderTaikoNote(Graphics graphics, Note note, double time)
    {
        var x = ToInt((note.Column + 0.5) * _columnWidth);
        var y = ToInt(TimeToHeight(time));
        var r = ToInt(_columnWidth * 0.4);

        Round(graphics, x, y, r, JudgementToColor(note.JudgementStart), _config.Stroke);
        Round(graphics, x, y, _config.Stroke, JudgementToColor(note.JudgementStart), _config.Stroke);
    }

    /// <summary>
    /// Port of the reference's <c>renderNote()</c>. Actions (the replay's key presses, drawn on top
    /// of the judged notes) use <c>actionHeight</c>, are inset by a fifth of the column on each side
    /// and have no outline; base notes use <c>max(blockHeight, timeToHeight(duration))</c> and the
    /// configured stroke.
    /// </summary>
    private void RenderNote(Graphics graphics, Note note, double time, bool isBase, int judgement,
        string? debugText = null)
    {
        if (isBase && _beatMap.IsTaiko)
        {
            RenderTaikoNote(graphics, note, time);
            return;
        }

        // Integer arithmetic first, then widening -- the reference writes (note.column * columnWidth).toDouble().
        var x = (double)(note.Column * _columnWidth);
        var y = TimeToHeight(time);
        var h = Math.Max((double)_config.BlockHeight, TimeToHeight(note.Duration));
        var width = (double)_columnWidth;

        if (!isBase)
        {
            h = _config.ActionHeight;
            x += width / 5;
            width -= 2 * width / 5;
        }

        Color? filledColor = null;
        if (_beatMap.IsTaiko && _config.TaikoSingleColumn)
        {
            filledColor = ReadColor(note.TaikoIsRed ? _config.TaikoForegroundRed : _config.TaikoForegroundBlue);
        }

        if (y - h < 0)
        {
            h = y;
        }

        var color = JudgementToColor(judgement);
        x += _columnWidth * 0.1;
        width -= 2 * _columnWidth * 0.1;

        var stroke = isBase ? _config.Stroke : -1;
        RoundedRect(graphics, ToInt(x), ToInt(y), ToInt(x + width), ToInt(y - h), color, stroke, filledColor);

        if (debugText is not null)
        {
            DrawTimestamp(graphics, debugText, color, x, y);
        }
    }

    /// <summary>
    /// <c>showTimestamp</c> debug text. The value is Kotlin's
    /// <c>((note.timeStamp - 1000) * replayModel.rate)</c> formatted the way Kotlin formats a
    /// <c>Double</c>, and it is drawn with the AWT <c>drawString</c> baseline convention.
    /// </summary>
    private void DrawTimestamp(Graphics graphics, string text, Color color, double x, double y)
    {
        _timestampFont ??= CreateTimestampFont();
        var font = _timestampFont;
        var ascent = GetAscent(font);
        graphics.DrawString(text, font, GetBrush(color),
            ToInt(x + _config.Stroke), ToInt(y - _config.Stroke) - ascent,
            StringFormat.GenericTypographic);
    }

    /// <summary>AWT's default is <c>Font("Dialog", PLAIN, 12)</c> evaluated at 72 dpi, so 12 pixels.</summary>
    private static Font CreateTimestampFont()
        => new(FontFamily.GenericSansSerif, 12f, FontStyle.Regular, GraphicsUnit.Pixel);

    private static float GetAscent(Font font)
    {
        var family = font.FontFamily;
        var emHeight = family.GetEmHeight(font.Style);
        if (emHeight <= 0)
        {
            return 0f;
        }

        return font.Size * family.GetCellAscent(font.Style) / emHeight;
    }

    private Color JudgementToColor(int judgement)
    {
        var colorText = judgement != -1 ? _config.JudgementColor[judgement] : _config.MissColor;
        return ReadColor(colorText);
    }

    /// <summary>
    /// Dashed long-note tail: the reference walks up the LN body in <c>2 * actionHeight</c> steps and
    /// paints a dash per step.
    /// </summary>
    private void RenderActionLn(Graphics graphics, Note note, double currentTime)
    {
        var actionHeight = _config.ActionHeight;
        var width = actionHeight / 2;
        var x = note.Column * _columnWidth + _columnWidth / 2;
        var y = ToInt(TimeToHeight(currentTime - note.TimeStamp));
        var h = ToInt(TimeToHeight(note.Duration));
        if (y < h)
        {
            h = y;
        }

        if (y - h > _windowSize || y < 0) return;
        // Keep the original dash phase, but jump over dashes below the canvas.
        var dashStep = actionHeight * 2;
        var currentH = Math.Max(dashStep, (y - _windowSize - actionHeight) / dashStep * dashStep);
        while (currentH + actionHeight <= h)
        {
            RoundedRect(graphics,
                x - width, y - currentH,
                x + width, y - currentH - actionHeight,
                ReadColor(_config.LongNoteColor), -1);
            currentH += actionHeight * 2;
        }
    }

    /// <summary>Taiko big note: connects the two duel notes with a filled bar.</summary>
    private void RenderConnectionLine(Graphics graphics, double time, Note leftNote, Note rightNote)
    {
        var start = (leftNote.Column + 1) * _columnWidth - _columnWidth * 0.1;
        var end = rightNote.Column * _columnWidth + _columnWidth * 0.1;
        var y = TimeToHeight(time);
        var width = _config.ActionHeight / 2;

        RoundedRect(graphics,
            ToInt(start), ToInt(y - width),
            ToInt(end), ToInt(y + width),
            ReadColor(_config.LongNoteColor), -1);
    }

    /// <summary>
    /// Port of the reference's <c>renderActionConnectionLine()</c>. It is dead code upstream (its only
    /// call site is commented out) but is kept so the port is complete.
    /// </summary>
    private void RenderActionConnectionLine(Graphics graphics, double time, Note note, Note action)
    {
        var width = _config.ActionHeight / 2;
        var x = note.Column * _columnWidth + _columnWidth / 2;
        var y1 = ToInt(TimeToHeight(time - note.TimeStamp));
        var y2 = ToInt(TimeToHeight(time - action.TimeStamp));
        var y = Math.Max(y1, y2);
        var h = y - Math.Min(y1, y2);
        if (y < h)
        {
            h = y;
        }

        RoundedRect(graphics, x - width, y1, x + width, y2,
            JudgementToColor(action.JudgementStart), -1);
    }

    /// <summary>Port of the reference's <c>render(data, time, isBase)</c>.</summary>
    private void RenderNotes(Graphics graphics, IReadOnlyList<Note> data, double time, bool isBase)
    {
        if (_beatMap.IsTaiko && isBase && !_config.TaikoSingleColumn)
        {
            foreach (var note in data)
            {
                if (note.DuelNote is not null && note.Column <= 1)
                {
                    RenderConnectionLine(graphics, time - note.TimeStamp, note, note.DuelNote);
                }
            }
        }

        for (var i = 0; i < data.Count; i++)
        {
            var note = data[i];
            string? debugText = null;
            if (isBase && _config.ShowTimestamp)
            {
                debugText = JavaDouble.ToString((note.TimeStamp - 1000) * _replayModel.Rate);
            }

            RenderNote(graphics, note, time - note.TimeStamp, isBase, note.JudgementStart, debugText);

            if (!isBase && (note.ShowAsLN || _config.ShowHolding))
            {
                RenderNote(graphics, note, time - note.EndTime, isBase: false, judgement: note.JudgementEnd);
                RenderActionLn(graphics, note, time);
            }
        }

        // The reference has a third block here for `isTaiko && isBase && taikoSingleColumn` whose
        // entire body is commented out (a filter over relatedActionOrNote with an empty forEach), so
        // it intentionally has no port.
    }

    // ---------------------------------------------------------------- colours

    /// <summary>
    /// Port of the reference's <c>readColor()</c>: 6 hex digits are <c>RRGGBB</c> and opaque;
    /// 8 hex digits are <c>AARRGGBB</c>. Anything else is an error.
    /// </summary>
    public static Color ReadColor(string colorHex)
    {
        switch (colorHex.Length)
        {
            case 6:
            {
                var rgb = Convert.ToInt32(colorHex, 16);
                return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            }

            case 8:
            {
                // Java's Integer.parseUnsignedInt: the top bit is not a sign.
                var argb = Convert.ToUInt32(colorHex, 16);
                return Color.FromArgb(
                    (int)((argb >> 24) & 0xFF),
                    (int)((argb >> 16) & 0xFF),
                    (int)((argb >> 8) & 0xFF),
                    (int)(argb & 0xFF));
            }

            default:
                throw new InvalidOperationException($"Unknown colorHex: {colorHex}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var pen in _pens.Values) pen.Dispose();
        foreach (var brush in _brushes.Values) brush.Dispose();
        _timestampFont?.Dispose();
        _pngWriter.Dispose();
        _bufferedImage.Dispose();
    }
}
