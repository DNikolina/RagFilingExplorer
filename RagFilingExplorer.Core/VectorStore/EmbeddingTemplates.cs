namespace RagFilingExplorer.Core.VectorStore;

/// <summary>
/// The query and document text an embedding model was trained to see, and the size of its vectors. A wrong template never
/// errors - retrieval just gets worse - so each model's comes from its own model card, here in code, and an unlisted
/// model is refused at startup instead of embedded with a guess. Changing a template changes every stored vector: rebuild
/// with --rebuild (the manifest records the model and its vector size, not its template). Each card's templates: docs/Decision-Log.md, "embedding-model
/// comparison" and "Batch 3 - embeddings, free then paid".
/// </summary>
internal sealed class EmbeddingTemplates
{
    // nomic-embed-text keeps the original index name, rag.<strategy>.db, so its index is never rebuilt for a rename.
    private const string NomicEmbedText = "nomic-embed-text";

    private readonly Func<string, string> query;
    private readonly Func<string?, string, string> document;

    private EmbeddingTemplates(
        string model, EmbeddingProvider provider, int dimensions, Func<string, string> query, Func<string?, string, string> document)
    {
        Model = model;
        Provider = provider;
        Dimensions = dimensions;
        this.query = query;
        this.document = document;
    }

    /// <summary>The model's name, as configured (Embedding:Model).</summary>
    public string Model { get; }

    /// <summary>Who serves the model - Providers:Embedding must name it (<see cref="EnsureServedBy"/>).</summary>
    public EmbeddingProvider Provider { get; }

    /// <summary>The size of the model's vectors - its native size from its card, never truncated; the index's vector column
    /// is created with it (FilingChunkRecord.Definition).</summary>
    public int Dimensions { get; }

    /// <summary>
    /// The templates for <paramref name="model"/> - matched on its name without the tag, so "nomic-embed-text:latest" is
    /// nomic-embed-text; an unlisted model is a <see cref="StartupException"/>.
    /// </summary>
    public static EmbeddingTemplates For(string model) => model.Split(':')[0] switch
    {
        // Nomic's card: the prefix is mandatory for v1.5 ("the text prompt *must* include a task instruction prefix").
        NomicEmbedText => new(model, EmbeddingProvider.Ollama, 768,
            q => $"search_query: {q}",
            (context, text) => $"search_document: {(context is null ? "" : context + "\n")}{text}"),

        // Google's cards (google/embeddinggemma-300m, google/embeddinggemma-2 - the same templates, both 768 natively): the
        // document's title slot holds the company line, "none" without one.
        "embeddinggemma" or "embeddinggemma-2" => new(model, EmbeddingProvider.Ollama, 768,
            q => $"task: search result | query: {q}",
            (context, text) => $"title: {context ?? "none"} | text: {text}"),

        // Voyage's docs (Embeddings): no text template - the request's input_type ("query", "document") has Voyage
        // prepend its own retrieval prompt (VoyageEmbeddingGenerator). The company line opens the document, as nomic's.
        // Both 1024: voyage-finance-2's only size, voyage-4-large's default (2048, 512 and 256 not used).
        "voyage-4-large" or "voyage-finance-2" => new(model, EmbeddingProvider.Voyage, 1024,
            q => q,
            (context, text) => context is null ? text : $"{context}\n{text}"),

        _ => throw new StartupException(
            $"Embedding:Model '{model}' has no query/document templates - add its model card's to EmbeddingTemplates "
            + $"(known: {NomicEmbedText}, embeddinggemma, embeddinggemma-2, voyage-4-large, voyage-finance-2)."),
    };

    /// <summary>Refuses a model that <paramref name="provider"/> doesn't serve - a Voyage model asked of Ollama would fail
    /// only as "not pulled", an Ollama model asked of Voyage only once billed calls begin.</summary>
    public void EnsureServedBy(EmbeddingProvider provider)
    {
        if (provider != Provider)
        {
            throw new StartupException($"Embedding:Model '{Model}' is served by {Provider}, but Providers:Embedding is {provider}.");
        }
    }

    /// <summary>The text embedded for a question.</summary>
    public string Query(string question) => query(question);

    /// <summary>The text embedded for a chunk: its embedding context (Structured's company line), if any, and its text.</summary>
    public string Document(string? context, string text) => document(context, text);

    /// <summary>The index's name part for this model: none for nomic-embed-text (rag.&lt;strategy&gt;.db), else the model
    /// with its tag (rag.&lt;strategy&gt;.embeddinggemma-2-270m.db), so each model keeps its own index.</summary>
    public string? IndexSuffix => Model.Split(':')[0] == NomicEmbedText ? null : Model.Replace(':', '-');
}
