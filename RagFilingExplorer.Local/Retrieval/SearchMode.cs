namespace RagFilingExplorer.Local.Retrieval;

/// <summary>How <see cref="RagAnswerService"/> retrieves (appsettings.json's Retrieval:Search).</summary>
internal enum SearchMode
{
    /// <summary>One vector search; a resolved statement type is a hard filter (v1).</summary>
    Vector,

    /// <summary>Vector + keyword search fused by reciprocal rank fusion; a resolved statement type boosts (v2 step 2).</summary>
    Hybrid,
}
