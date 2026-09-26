using RepoPilot.Infrastructure.Retrieval;
using Xunit;

namespace RepoPilot.UnitTests.Infrastructure;

/// <summary>
/// Which tokens of a query reach the substring comparison in hybrid retrieval
/// (FR-005).
/// <para>
/// The distinction this draws is the reason the lexical arm can take a whole task
/// description without flooding itself with candidates: identifier-shaped tokens
/// go to the substring comparison, and ordinary words go to text search, which
/// ranks them. A rule that let English words through would make every chunk
/// mentioning an order a candidate for every task that mentions orders.
/// </para>
/// </summary>
public sealed class IdentifierTokensTests
{
    [Fact]
    public void ACamelCaseTokenIsAnIdentifier()
    {
        Assert.Equal(
            ["OrderLookupService"],
            HybridRetriever.IdentifierTokens("where is OrderLookupService defined"));
    }

    [Fact]
    public void ASentenceInitialCapitalIsNotAnInternalOne()
    {
        // The case that matters most. "Orders are arriving with lines for zero
        // units" is prose, and every one of its words belongs to text search.
        Assert.Empty(
            HybridRetriever.IdentifierTokens(
                "Orders are arriving with lines for zero units, which the warehouse cannot pick."));
    }

    [Fact]
    public void UnderscoreAndDottedTokensAreIdentifiers()
    {
        Assert.Equal(
            ["line_item", "Orders.Total"],
            HybridRetriever.IdentifierTokens("the line_item and Orders.Total fields"));
    }

    [Fact]
    public void TrailingSentencePunctuationIsNotPartOfTheToken()
    {
        Assert.Equal(
            ["OrderSorter"],
            HybridRetriever.IdentifierTokens("look at OrderSorter."));
    }

    [Fact]
    public void ATokenInterruptedOnlyAtItsEndByADotIsNotDotted()
    {
        // "OrderSorter." has a dot at the end, which is punctuation. A dotted
        // identifier has one between word characters.
        Assert.Empty(HybridRetriever.IdentifierTokens("consider orders."));
    }

    [Fact]
    public void ShortTokensAreSkipped()
    {
        // Two characters cannot usefully narrow a substring search, and "aB"
        // technically carries an internal capital.
        Assert.Empty(HybridRetriever.IdentifierTokens("aB is too short"));
    }

    [Fact]
    public void ARepeatedIdentifierIsListedOnce()
    {
        Assert.Equal(
            ["LineItemValidator"],
            HybridRetriever.IdentifierTokens("LineItemValidator calls LineItemValidator"));
    }

    [Fact]
    public void PunctuationAndQuotingDoNotHideAnIdentifier()
    {
        Assert.Equal(
            ["LineItemValidator"],
            HybridRetriever.IdentifierTokens("in `LineItemValidator`, the bound"));
    }
}
