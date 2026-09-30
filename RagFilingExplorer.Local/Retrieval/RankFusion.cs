namespace RagFilingExplorer.Local.Retrieval;

/// <summary>
/// Reciprocal rank fusion (Cormack, Clarke and Buettcher, 2009): each list adds 1 / (k + rank) to every key it
/// ranks, and keys are ordered by the sum. It needs no score calibration - bm25 and cosine distance aren't on
/// one scale - and it favours what several lists agree on. k = 60 is the paper's value and the common default.
/// </summary>
internal static class RankFusion
{
    public const int K = 60;

    /// <summary>Every key any list ranks, best first; ties keep the order keys were first seen in.</summary>
    public static List<(int Key, double Score)> Fuse(IEnumerable<IReadOnlyList<int>> rankings)
    {
        Dictionary<int, double> scores = new();
        List<int> firstSeen = new();
        foreach (IReadOnlyList<int> ranking in rankings)
        {
            for (int i = 0; i < ranking.Count; i++)
            {
                if (!scores.ContainsKey(ranking[i]))
                {
                    scores[ranking[i]] = 0;
                    firstSeen.Add(ranking[i]);
                }

                scores[ranking[i]] += 1.0 / (K + i + 1);
            }
        }

        // OrderBy is stable, so equal scores stay in first-seen order.
        return firstSeen.OrderByDescending(key => scores[key]).Select(key => (key, scores[key])).ToList();
    }
}
