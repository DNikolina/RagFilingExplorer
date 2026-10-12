namespace RagFilingExplorer.Core.VectorStore;

/// <summary>
/// The query and document text an embedding model was trained to see. A wrong template never errors - retrieval just
/// gets worse - so each model's comes from its own model card, here in code, and an unlisted model is refused at
/// startup instead of embedded with a guess. Changing a template changes every stored vector: rebuild with --rebuild
/// (the manifest records the model, not its template). Each card's templates: docs/Decision-Log.md, "embedding-model
/// comparison" and "Batch 3 - embeddings, free then paid".
/// </summary>
internal sealed class EmbeddingTemplates
{
    // nomic-embed-text keeps the original index name, rag.<strategy>.db, so its index is never rebuilt for a rename.
    private const string NomicEmbedText = "nomic-embed-text";

    private readonly Func<string, string> query;
    private readonly Func<string?, string, string> document;

    private EmbeddingTemplates(string model, Func<string, string> query, Func<string?, string, string> document)
    {
        Model = model;
        this.query = query;
        this.document = document;
    }

    /// <summary>Ollama's model name, as configured (Ollama:EmbeddingModel).</summary>
    public string Model { get; }

    /// <summary>
    /// The templates for <paramref name="model"/> - matched on its name without the tag, so "nomic-embed-text:latest" is
    /// nomic-embed-text; an unlisted model is a <see cref="StartupException"/>.
    /// </summary>
    public static EmbeddingTemplates For(string model) => model.Split(':')[0] switch
    {
        // Nomic's card: the prefix is mandatory for v1.5 ("the text prompt *must* include a task instruction prefix").
        NomicEmbedText => new(model,
            q => $"search_query: {q}",
            (context, text) => $"search_document: {(context is null ? "" : context + "\n")}{text}"),

        // Google's cards (google/embeddinggemma-300m, google/embeddinggemma-2 - the same templates): the document's title
        // slot holds the company line, "none" without one.
        "embeddinggemma" or "embeddinggemma-2" => new(model,
            q => $"task: search result | query: {q}",
            (context, text) => $"title: {context ?? "none"} | text: {text}"),

        _ => throw new StartupException(
            $"Ollama:EmbeddingModel '{model}' has no query/document templates - add its model card's to EmbeddingTemplates "
            + $"(known: {NomicEmbedText}, embeddinggemma, embeddinggemma-2)."),
    };

    /// <summary>The text embedded for a question.</summary>
    public string Query(string question) => query(question);

    /// <summary>The text embedded for a chunk: its embedding context (Structured's company line), if any, and its text.</summary>
    public string Document(string? context, string text) => document(context, text);

    /// <summary>The index's name part for this model: none for nomic-embed-text (rag.&lt;strategy&gt;.db), else the model
    /// with its tag (rag.&lt;strategy&gt;.embeddinggemma-2-270m.db), so each model keeps its own index.</summary>
    public string? IndexSuffix => Model.Split(':')[0] == NomicEmbedText ? null : Model.Replace(':', '-');
}
