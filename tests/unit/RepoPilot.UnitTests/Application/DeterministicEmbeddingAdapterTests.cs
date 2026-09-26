using RepoPilot.Infrastructure.Providers;
using Xunit;

namespace RepoPilot.UnitTests.Application;

/// <summary>
/// SC-007 requires repeated evaluations over unchanged fixtures to reproduce
/// identical retrieval metrics. That holds trivially when an evaluation does not
/// re-index; these tests make it hold even when it does.
/// </summary>
public sealed class DeterministicEmbeddingAdapterTests
{
    private static readonly DeterministicEmbeddingAdapter Adapter = new(384);

    private static async Task<float[]> EmbedAsync(string text)
    {
        var result = await Adapter.EmbedAsync([text]);
        return result[0];
    }

    [Fact]
    public async Task TheSameTextAlwaysProducesTheSameVector()
    {
        const string text = "public sealed class OrderLookupService { }";

        Assert.Equal(await EmbedAsync(text), await EmbedAsync(text));
    }

    [Fact]
    public async Task DeterminismHoldsAcrossAdapterInstances()
    {
        // A fresh instance must agree with an existing one, or an index written
        // by one process would not be searchable by the next.
        const string text = "order lookup service";

        var first = (await new DeterministicEmbeddingAdapter(384).EmbedAsync([text]))[0];
        var second = (await new DeterministicEmbeddingAdapter(384).EmbedAsync([text]))[0];

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task EveryVectorHasTheDeclaredDimensions()
    {
        var vectors = await Adapter.EmbedAsync(["one", "two", "three"]);

        Assert.All(vectors, v => Assert.Equal(Adapter.Dimensions, v.Length));
    }

    [Fact]
    public async Task VectorsAreUnitLength()
    {
        // Cosine distance assumes normalised vectors; an unnormalised index
        // ranks by magnitude, which here would mean by chunk length.
        var vector = await EmbedAsync("some repository content to embed");

        var magnitude = Math.Sqrt(vector.Sum(v => (double)v * v));

        Assert.Equal(1.0, magnitude, precision: 5);
    }

    [Fact]
    public async Task BatchOrderIsPreserved()
    {
        var texts = new[] { "alpha", "beta", "gamma" };

        var batch = await Adapter.EmbedAsync(texts);

        for (var i = 0; i < texts.Length; i++)
        {
            Assert.Equal(await EmbedAsync(texts[i]), batch[i]);
        }
    }

    [Fact]
    public async Task EmptyTextProducesAUsableVector()
    {
        // A zero vector has no direction, so cosine distance against it is
        // undefined and would produce NaN rankings rather than bad ones.
        var vector = await EmbedAsync("   ");

        Assert.Equal(Adapter.Dimensions, vector.Length);
        Assert.Contains(vector, v => v != 0f);
        Assert.DoesNotContain(vector, float.IsNaN);
    }

    [Fact]
    public async Task SharedVocabularyRanksNearerThanUnrelatedText()
    {
        // The honest claim for this adapter: lexical overlap, not meaning. This
        // asserts exactly that much and no more.
        var query = await EmbedAsync("order lookup service shipping address");
        var related = await EmbedAsync("OrderLookupService returns the shipping address");
        var unrelated = await EmbedAsync("matrix decomposition eigenvalue solver");

        Assert.True(
            Cosine(query, related) > Cosine(query, unrelated),
            "Text sharing vocabulary should rank nearer than unrelated text.");
    }

    [Fact]
    public async Task IdentifierSubWordsContribute()
    {
        // OrderLookupService should share signal with "order lookup service",
        // because naming carries most of the meaning in source code.
        var identifier = await EmbedAsync("OrderLookupService");
        var words = await EmbedAsync("order lookup service");
        var unrelated = await EmbedAsync("quantum chromodynamics");

        Assert.True(Cosine(identifier, words) > Cosine(identifier, unrelated));
    }

    [Fact]
    public void TheModelIdEncodesTheDimensions()
    {
        // The id is what tells an existing index its vectors are no longer
        // comparable. Two configurations that differ must not share an id.
        Assert.NotEqual(
            new DeterministicEmbeddingAdapter(384).ModelId,
            new DeterministicEmbeddingAdapter(256).ModelId);
    }

    [Fact]
    public void TheAdapterDeclaresItselfDeterministic()
    {
        Assert.True(Adapter.IsDeterministic);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
        }

        return dot; // Both operands are unit length, so the dot product is cosine.
    }
}
