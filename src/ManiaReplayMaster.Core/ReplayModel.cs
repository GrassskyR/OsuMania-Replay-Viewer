using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Model;

/// <summary>
/// Port of <c>me.replaymaster.model.ReplayModel</c>.
/// </summary>
public sealed class ReplayModel
{
    public List<Note> ReplayData;
    public double Rate;
    public bool Mirror;
    public double[] Judgement;
    public bool IsScoreV2;

    public double Acc;

    public ReplayModel(List<Note> replayData, double rate, bool mirror, double[] judgement, bool isScoreV2 = false)
    {
        ReplayData = replayData;
        Rate = rate;
        Mirror = mirror;
        Judgement = judgement;
        IsScoreV2 = isScoreV2;
    }
}
