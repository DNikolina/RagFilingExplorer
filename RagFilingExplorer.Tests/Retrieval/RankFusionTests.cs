using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Tests.Retrieval;

[TestFixture]
public class RankFusionTests
{
    [Test]
    public void Fuse_KeyBothListsRankHighly_BeatsEachListsOwnFirst()
    {
        List<(int Key, double Score)> fused = RankFusion.Fuse([[1, 3, 4], [2, 3, 5]]);

        Assert.That(fused.Select(f => f.Key), Is.EqualTo(new[] { 3, 1, 2, 4, 5 }));
    }

    [Test]
    public void Fuse_Score_IsTheSumOfReciprocalRanks()
    {
        List<(int Key, double Score)> fused = RankFusion.Fuse([[7], [8, 7]]);

        Assert.That(fused[0].Key, Is.EqualTo(7));
        Assert.That(fused[0].Score, Is.EqualTo(1.0 / (RankFusion.K + 1) + 1.0 / (RankFusion.K + 2)).Within(1e-12));
    }

    // tools/replay_recall.py orders ties the same way (Python's stable sort over first-seen keys), so the replay
    // reproduces the app's ranks exactly.
    [Test]
    public void Fuse_EqualScores_KeepFirstSeenOrder()
    {
        List<(int Key, double Score)> fused = RankFusion.Fuse([[1, 2], [2, 1]]);

        Assert.That(fused.Select(f => f.Key), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void Fuse_EmptyLists_AreIgnored()
    {
        Assert.That(RankFusion.Fuse([[], [4, 5], []]).Select(f => f.Key), Is.EqualTo(new[] { 4, 5 }));
    }
}
