using System.Drawing;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Render;

namespace ManiaReplayMaster.RenderEncode.Tests;

/// <summary>
/// Renderer parity checks. AWT and GDI+ antialias differently, so -- as agreed in the rewrite plan
/// (§4.2, §7.4) -- the target is "judgement-identical + visually equivalent": these tests assert
/// canvas geometry, slice count, colour mapping and the structural pixels (note positions, filled
/// vs outlined blocks, background stripes), not a pixel-exact diff against the Java output.
/// </summary>
public class RendererTests
{
    private static BeatMap ManiaMap(long duration = 5000)
        => new() { Key = 4, Duration = duration, Type = BeatMap.TypeMania };

    private static ReplayModel Model(params Note[] actions)
        => new(actions.ToList(), 1.0, false, new double[] { 16, 34, 67, 97, 121, 158 });

    // ------------------------------------------------------------------ mania rendering

    [Fact]
    public void Start_WritesBlackSlicesAndOutlinedBaseNotesWithFilledActions()
    {
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            // Odd width exercises the BGR stride padding in the PNG writer.
            var config = new Config { Width = 541 };
            var beatMap = ManiaMap();
            // judgement 1 == "FFD237"
            beatMap.Notes.Add(new Note(timeStamp: 2000, column: 2, duration: 0, judgementStart: 1));
            var action = new Note(timeStamp: 2000, column: 2, duration: 0, judgementStart: 1)
            {
                // Judging clears this for rice notes (BaseJudger.Judge: target.Duration == 0).
                ShowAsLN = false
            };

            var imagePath = Path.Combine(temp, "v.png");
            using (var renderer = new Renderer(beatMap, Model(action), imagePath, config))
            {
                var result = renderer.Start();

                Assert.Equal(541, result.GameWidth);
                Assert.Equal(3960, result.WindowSize);
                Assert.Equal(2, result.WindowCount); // ceil((6000 - 960) / 3000)
                Assert.Equal(2, result.ImageCount);
                Assert.Equal(150, result.WindowFrameCount);
                Assert.Equal(Path.Combine(temp, "v_0.png"), result.ImagePaths[0]);
                Assert.Equal(Path.Combine(temp, "v_1.png"), result.ImagePaths[1]);
            }

            using var slice = new Bitmap(Path.Combine(temp, "v_0.png"));
            Assert.Equal(541, slice.Width);
            Assert.Equal(3960, slice.Height);

            // Background is opaque black everywhere it is not painted.
            Assert.Equal(Black, slice.GetPixel(5, 5).ToArgb());
            Assert.Equal(Black, slice.GetPixel(540, 3959).ToArgb());

            // Window 0 bottom is heightToTime(3960) = 3300 ms, so the note lands at y = (3300-2000)*1.2 = 1560.
            // Base note: x = 2*135 = 270, inset by columnWidth*0.1 -> 283.5 .. 391.5, outline only.
            Assert.True(ContainsColour(slice, 337, 1513, 337, 1527, IsJudgementYellow),
                "expected the base note outline near y=1520");
            Assert.Equal(Black, slice.GetPixel(337, 1540).ToArgb());

            // Action block: inset by width/5 and then columnWidth*0.1, 7 px tall above y=1560, filled.
            Assert.True(ContainsColour(slice, 312, 1552, 362, 1561, IsJudgementYellow),
                "expected the filled action block near y=1553..1560");
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }

    [Fact]
    public void LongNote_RendersDashedTailInLongNoteColour()
    {
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            var beatMap = ManiaMap();
            beatMap.Notes.Add(new Note(timeStamp: 2000, column: 2, duration: 500, judgementStart: 1));
            var action = new Note(timeStamp: 2000, column: 2, duration: 500, judgementStart: 1, judgementEnd: 1);

            using (var renderer = new Renderer(beatMap, Model(action), Path.Combine(temp, "v.png"), new Config()))
            {
                renderer.Start();
            }

            using var slice = new Bitmap(Path.Combine(temp, "v_0.png"));

            // Dashes are painted in longNoteColor "646464" from y=1560 upwards in 14 px steps.
            Assert.True(ContainsColour(slice, 310, 1200, 364, 1548, IsLongNoteGrey),
                "expected the LN tail dashes in longNoteColor");

            // The LN tail head (action.endTime) is filled with its own judgement colour.
            Assert.True(ContainsColour(slice, 312, 952, 362, 961, IsJudgementYellow),
                "expected the LN end block near y=953..960");
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }

    [Fact]
    public void Start_HonoursCancellation()
    {
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            using var renderer = new Renderer(ManiaMap(), Model(), Path.Combine(temp, "v.png"), new Config());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => renderer.Start(null, cts.Token));
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }

    // ------------------------------------------------------------------ taiko rendering

    [Fact]
    public void TaikoMultiColumn_PaintsBackgroundStripesAndRingOvals()
    {
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            var beatMap = new BeatMap { Key = 4, Duration = 5000, Type = BeatMap.TypeTaiko };
            // Mirrors what OsuMapReader produces for a big note: the duel note is placed in the
            // mirrored column (3 - column) and both reference each other.
            var left = new Note(timeStamp: 2000, column: 0, duration: 0, judgementStart: 0);
            var duel = new Note(timeStamp: 2000, column: 3, duration: 0, judgementStart: 0, duelNote: left);
            left.DuelNote = duel;
            beatMap.Notes.Add(left);
            beatMap.Notes.Add(duel);

            using (var renderer = new Renderer(beatMap, Model(), Path.Combine(temp, "v.png"), new Config()))
            {
                var result = renderer.Start();
                Assert.Equal(540, result.GameWidth); // multi column keeps the full width
            }

            using var slice = new Bitmap(Path.Combine(temp, "v_0.png"));

            // Stripes: blue, red (2 columns), blue. w = 135, padding = 0.
            var blue = slice.GetPixel(50, 10);
            var red = slice.GetPixel(300, 10);
            Assert.True(blue.B > blue.G && blue.G > blue.R && blue.R > 0,
                $"expected a translucent blue stripe, got {blue}");
            Assert.True(red.R > red.G && red.R > red.B && red.R > 0,
                $"expected a translucent red stripe, got {red}");

            // The taiko note is a ring (radius 54 at x=67, y=1560): ring painted, interior untouched.
            // Note the reference never fills taiko ovals -- the judgement colour is only stroked.
            Assert.True(ContainsColour(slice, 115, 1555, 128, 1565, colour => colour.A > 200 && colour.R > 200),
                "expected the taiko note ring in the judgement colour");
            // (92, 1560) is 25 px from the ring centre, i.e. between the ring and the tiny centre
            // circle, so it must still show the untouched background stripe.
            Assert.Equal(slice.GetPixel(92, 10).ToArgb(), slice.GetPixel(92, 1560).ToArgb());

            // Big-note connection bar between the duel notes (longNoteColor): x 121..418, y 1557..1563.
            Assert.True(ContainsColour(slice, 150, 1556, 400, 1564, IsLongNoteGrey),
                "expected the taiko duel connection bar");
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }

    // ------------------------------------------------------------------ helpers

    private const int Black = unchecked((int)0xFF000000);

    private static bool IsJudgementYellow(Color colour)
        => colour.R >= 200 && colour.G >= 180 && colour.B <= 130 && colour.A > 200;

    private static bool IsLongNoteGrey(Color colour)
        => colour.R == 100 && colour.G == 100 && colour.B == 100 && colour.A > 200;

    /// <summary>
    /// Anti-aliasing and GDI+ rounding move edge pixels, so colour probes scan a small rectangle
    /// instead of a single pixel (the plan explicitly allows antialiasing differences).
    /// </summary>
    private static bool ContainsColour(Bitmap bitmap, int x1, int y1, int x2, int y2,
        Func<Color, bool> predicate)
    {
        for (var y = y1; y <= y2; y++)
        {
            for (var x = x1; x <= x2; x++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                {
                    return true;
                }
            }
        }

        return false;
    }

}
