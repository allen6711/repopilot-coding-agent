using Orders;
using Xunit;

namespace Orders.Tests;

public sealed class CustomerNameNormalizerTests
{
    [Fact]
    public void Normalize_TrimsSurroundingWhitespace()
    {
        Assert.Equal("Ada Lovelace", CustomerNameNormalizer.Normalize("  Ada Lovelace  "));
    }

    [Fact]
    public void Normalize_CollapsesInternalWhitespace()
    {
        Assert.Equal("Ada Lovelace", CustomerNameNormalizer.Normalize("Ada    Lovelace"));
    }

    [Fact]
    public void Normalize_RejectsAWhitespaceOnlyName()
    {
        Assert.Throws<ArgumentException>(() => CustomerNameNormalizer.Normalize("   "));
    }

    [Fact]
    public void Normalize_RejectsANameLongerThanTheRecordHolds()
    {
        var tooLong = new string('a', CustomerNameNormalizer.MaxLength + 1);

        Assert.Throws<ArgumentException>(() => CustomerNameNormalizer.Normalize(tooLong));
    }
}
