using System.Text.Json;
using System.Text.Json.Serialization;

namespace RagFilingExplorer.Local.Evaluation.Grading;

/// <summary>An alternative figure that counts only when the answer names its line (Q2's "Oracle Corporation stockholders").</summary>
internal sealed record AcceptedAlternative(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("if_label")] string IfLabel);

/// <summary>
/// One question's expected answer, as tools/expected-answers.json states it (docs/Manual-Test-Questions.md has the
/// sources). <see cref="Kind"/> is "figure", "fact", "negative" (the filing doesn't say) or "routing" (the keyword route
/// excludes the answer, so a decline passes). <see cref="ChunkExpect"/> is what marks the answer in a retrieved chunk, as
/// the chunk prints it ("(601)", every input of an arithmetic question) - empty for a negative.
/// </summary>
internal sealed record ExpectedAnswer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("expect")] IReadOnlyList<string>? Expect = null,
    [property: JsonPropertyName("unit")] string? Unit = null,
    [property: JsonPropertyName("conflicts")] IReadOnlyList<string>? Conflicts = null,
    [property: JsonPropertyName("traps")] IReadOnlyList<string>? Traps = null,
    [property: JsonPropertyName("accept")] IReadOnlyList<AcceptedAlternative>? Accept = null,
    [property: JsonPropertyName("exact")] bool Exact = false,
    [property: JsonPropertyName("chunk_expect")] IReadOnlyList<string>? ChunkExpect = null)
{
    private sealed record File([property: JsonPropertyName("questions")] IReadOnlyList<ExpectedAnswer> Questions);

    /// <summary>Every expected answer in tools/expected-answers.json, by id.</summary>
    public static IReadOnlyDictionary<string, ExpectedAnswer> LoadAll(DirectoryInfo repoRoot)
    {
        string path = Path.Combine(repoRoot.FullName, "tools", "expected-answers.json");
        File file = JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(path))
            ?? throw new InvalidOperationException($"{path} is empty.");
        return file.Questions.ToDictionary(q => q.Id);
    }
}
