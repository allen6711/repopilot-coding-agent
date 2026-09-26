using System.Security.Cryptography;
using System.Text;
using RepoPilot.Application.Ports;

namespace RepoPilot.Infrastructure.Providers;

/// <summary>
/// A deterministic, dependency-free embedding adapter for development and
/// evaluation.
/// <para>
/// <b>What this is and is not.</b> It maps text into a stable vector space using
/// hashed token features, so identical input always produces an identical
/// vector and lexically overlapping texts land nearer each other than unrelated
/// ones. It does <b>not</b> capture meaning the way a trained model does:
/// paraphrases with no shared vocabulary will not be neighbours.
/// </para>
/// <para>
/// <b>Why it is the default.</b> Anthropic's API has no embeddings endpoint, so
/// the chat provider cannot supply vectors and a separate model is required
/// regardless. Committing to a specific trained model means a binary artefact
/// and a licensing decision that the specification and research have not
/// settled. Shipping this first keeps indexing offline, free, and reproducible —
/// which is what makes SC-007 hold even when an evaluation does re-index — and
/// leaves the model choice to be made against measured Recall@5 rather than
/// assumed. That ordering is what Principle V asks for.
/// </para>
/// <para>
/// <b>What replaces it.</b> Any implementation of
/// <see cref="IEmbeddingProviderAdapter"/>. Swapping one in changes the
/// repository's pinned model id and dimensions, which forces a full index
/// rebuild by design — vectors from different models are not comparable, and a
/// mixed index returns silently wrong neighbours.
/// </para>
/// </summary>
public sealed class DeterministicEmbeddingAdapter : IEmbeddingProviderAdapter
{
    private readonly int _dimensions;

    /// <param name="dimensions">
    /// Vector length. Must match the database column's declared size.
    /// </param>
    public DeterministicEmbeddingAdapter(int dimensions = 384)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 16);
        _dimensions = dimensions;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Versioned. Changing the algorithm must change this id, because the id is
    /// what tells an existing index that its vectors are no longer comparable.
    /// </remarks>
    public string ModelId => $"repopilot-deterministic-hash-v1-{_dimensions}";

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public bool IsDeterministic => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var results = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(Embed(text));
        }

        return Task.FromResult<IReadOnlyList<float[]>>(results);
    }

    /// <summary>
    /// Builds one vector using the hashing trick: each token is hashed to a
    /// dimension and a sign, contributions are accumulated, then the vector is
    /// L2-normalised so cosine distance behaves.
    /// </summary>
    private float[] Embed(string text)
    {
        var vector = new float[_dimensions];

        if (string.IsNullOrWhiteSpace(text))
        {
            // A zero vector has no direction, so cosine distance against it is
            // undefined. One fixed non-zero dimension keeps empty input from
            // producing NaN distances that would silently poison a ranking.
            vector[0] = 1f;
            return vector;
        }

        foreach (var token in Tokenize(text))
        {
            var hash = Hash(token);

            // Low bits pick the dimension, one further bit picks the sign. The
            // signed contribution is what stops unrelated tokens that collide on
            // a dimension from always reinforcing each other.
            var index = (int)(hash % (uint)_dimensions);
            var sign = (hash & 0x8000_0000u) == 0 ? 1f : -1f;

            vector[index] += sign;
        }

        Normalize(vector);
        return vector;
    }

    /// <summary>
    /// Splits identifier-aware tokens: word characters, plus the sub-words of
    /// camelCase and snake_case, so <c>OrderLookupService</c> also contributes
    /// <c>order</c>, <c>lookup</c>, and <c>service</c>. Exact identifier lookup
    /// is served by the lexical arm of retrieval, but this keeps the vector arm
    /// from being blind to the naming that carries most of the meaning in code.
    /// </summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (var raw in SplitOnNonAlphanumeric(text))
        {
            // The whole token carries the exact-identifier signal.
            yield return raw.ToLowerInvariant();

            // Its sub-words carry the descriptive signal. Both are emitted, so
            // "OrderLookupService" is near both a query naming it exactly and
            // one describing it in words.
            var subWords = SplitIdentifier(raw).ToList();
            if (subWords.Count > 1)
            {
                foreach (var subWord in subWords)
                {
                    yield return subWord.ToLowerInvariant();
                }
            }
        }
    }

    private static IEnumerable<string> SplitOnNonAlphanumeric(string text)
    {
        var current = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>
    /// Splits camelCase, PascalCase, and letter-digit boundaries.
    /// <para>
    /// An acronym run keeps its trailing capital with the following word, so
    /// <c>HTTPServer</c> yields <c>HTTP</c> and <c>Server</c> rather than
    /// <c>HTTPS</c> and <c>erver</c>.
    /// </para>
    /// </summary>
    private static IEnumerable<string> SplitIdentifier(string token)
    {
        var start = 0;

        for (var i = 1; i < token.Length; i++)
        {
            var previous = token[i - 1];
            var current = token[i];

            var lowerToUpper = char.IsLower(previous) && char.IsUpper(current);
            var acronymEnd = char.IsUpper(previous)
                && char.IsUpper(current)
                && i + 1 < token.Length
                && char.IsLower(token[i + 1]);
            var letterDigit = char.IsLetter(previous) != char.IsLetter(current);

            if (lowerToUpper || acronymEnd || letterDigit)
            {
                yield return token[start..i];
                start = i;
            }
        }

        yield return token[start..];
    }

    private static uint Hash(string token)
    {
        // SHA-256 rather than string.GetHashCode: the latter is randomised per
        // process in .NET, which would make vectors differ between runs and
        // break the determinism this whole class exists to provide.
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), digest);
        return BitConverter.ToUInt32(digest[..4]);
    }

    private static void Normalize(float[] vector)
    {
        double sumOfSquares = 0;
        foreach (var value in vector)
        {
            sumOfSquares += value * value;
        }

        if (sumOfSquares <= 0)
        {
            vector[0] = 1f;
            return;
        }

        var magnitude = (float)Math.Sqrt(sumOfSquares);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= magnitude;
        }
    }
}
