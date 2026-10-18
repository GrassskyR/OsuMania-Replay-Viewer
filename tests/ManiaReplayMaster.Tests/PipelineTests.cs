using System.IO.Compression;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Core.Pipeline;
using Xunit;

namespace ManiaReplayMaster.Tests;

public sealed class PipelineTests
{
    private const string BeatmapFile = "ck - Carnation (ck remix) (Gekido-) [LN Master].osu";
    private const string ReplayFile = "- ck - Carnation (ck remix) [LN Master] (2021-12-06) OsuMania.osr";

    [Fact]
    public void Archive_selects_the_map_matching_the_replay()
    {
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            var maps = Path.Combine(temp, "maps");
            Directory.CreateDirectory(maps);
            File.Copy(RepoPaths.Fixture(BeatmapFile), Path.Combine(maps, "matching.osu"));
            File.WriteAllText(Path.Combine(maps, "other.osu"), "unrelated map");
            var input = Path.Combine(temp, "maps.osz");
            ZipFile.CreateFromDirectory(maps, input);

            var result = Pipeline.Prepare(input, RepoPaths.Fixture(ReplayFile),
                tempDirectory: Path.Combine(temp, "unpacked"), config: new Config());

            Assert.Equal("matching.osu", Path.GetFileName(result.BeatmapPath));
            Assert.NotNull(result.BeatmapMd5);
            Assert.Equal(1798, result.BeatMap.Notes.Count);
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }
}
