namespace RepoPilot.Application.Ports;

/// <summary>
/// Turns text into vectors for the meaning-based arm of retrieval (FR-005).
/// <para>
/// A second adapter, separate from <see cref="IChatProviderAdapter"/>, because
/// the chat provider does not necessarily offer embeddings — Anthropic's API has
/// no embeddings endpoint at all. Treating them as one port would have made the
/// chat provider's choice silently dictate the retrieval model.
/// </para>
/// <para>
/// <see cref="ModelId"/> and <see cref="Dimensions"/> are pinned onto a
/// repository at first index. Changing either invalidates the index and forces a
/// full rebuild, because vectors produced by different models are not comparable
/// and a mixed index would return silently wrong neighbours.
/// </para>
/// </summary>
public interface IEmbeddingProviderAdapter
{
    /// <summary>Identifies the embedding model, recorded on the repository.</summary>
    string ModelId { get; }

    /// <summary>Vector length. Must match the database column's declared size.</summary>
    int Dimensions { get; }

    /// <summary>
    /// Whether identical input always yields an identical vector.
    /// <para>
    /// SC-007 requires that repeating an evaluation over unchanged fixtures
    /// reproduces identical retrieval metrics. That holds trivially when an
    /// evaluation does not re-index, but a deterministic embedder makes it hold
    /// even when it does — so this is surfaced rather than assumed.
    /// </para>
    /// </summary>
    bool IsDeterministic { get; }

    /// <summary>
    /// Embeds a batch of texts.
    /// </summary>
    /// <returns>
    /// One vector per input, in the same order. Each has <see cref="Dimensions"/>
    /// elements.
    /// </returns>
    /// <exception cref="ProviderUnavailableException">The provider is unusable.</exception>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default);
}
