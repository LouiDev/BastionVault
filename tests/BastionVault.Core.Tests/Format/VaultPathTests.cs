using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Format;

/// <summary>Covers the in-vault path grammar of FORMAT.md section 6.3.</summary>
public sealed class VaultPathTests
{
    [Fact]
    public void Separator_IsABackslash()
    {
        Assert.Equal('\\', VaultPath.Separator);
    }

    [Fact]
    public void Format_OfNoSegments_IsTheRoot()
    {
        Assert.Equal("\\", VaultPath.Format([]));
    }

    [Theory]
    [InlineData("\\Docs", "Docs")]
    [InlineData("\\Docs\\a.txt", "Docs", "a.txt")]
    [InlineData("\\Documents\\2026\\notes.txt", "Documents", "2026", "notes.txt")]
    public void Format_JoinsWithALeadingSeparator(string expected, params string[] segments)
    {
        Assert.Equal(expected, VaultPath.Format(segments));
    }

    [Fact]
    public void Format_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => VaultPath.Format(null!));
    }

    [Theory]
    [InlineData("\\")]
    [InlineData("")]
    public void TrySplit_OfTheRoot_YieldsNoSegments(string path)
    {
        Assert.True(VaultPath.TrySplit(path, out string[] segments));
        Assert.Empty(segments);
    }

    [Fact]
    public void TrySplit_SplitsARootedPath()
    {
        Assert.True(VaultPath.TrySplit("\\Documents\\2026\\notes.txt", out string[] segments));

        Assert.Equal(["Documents", "2026", "notes.txt"], segments);
    }

    [Fact]
    public void TrySplit_AcceptsAMissingLeadingSeparator()
    {
        Assert.True(VaultPath.TrySplit("Documents\\notes.txt", out string[] segments));

        Assert.Equal(["Documents", "notes.txt"], segments);
    }

    [Theory]
    [InlineData("\\Docs")]
    [InlineData("\\Docs\\")]              // one trailing separator names the same folder
    [InlineData("Docs\\")]
    public void TrySplit_AcceptsOneTrailingSeparator(string path)
    {
        Assert.True(VaultPath.TrySplit(path, out string[] segments), $"\"{path}\" was rejected.");

        Assert.Equal(["Docs"], segments);
    }

    [Theory]
    [InlineData("\\Docs\\\\")]            // a second trailing separator still leaves an empty segment
    [InlineData("\\\\Docs")]              // doubled separator
    [InlineData("\\Docs\\\\a.txt")]
    [InlineData("\\Docs\\ leading")]      // leading whitespace in a segment
    [InlineData("\\Docs\\trailing ")]
    [InlineData("\\Docs\\trailing.")]
    [InlineData("\\Docs\\CON")]
    [InlineData("\\Docs\\a/b")]
    [InlineData("\\Docs\\a:b")]
    [InlineData("\\Docs\\..")]
    [InlineData("\\Docs\\.")]
    [InlineData("\\a\u0001b")]
    [InlineData("\\a\u202Eb")]
    [InlineData("\\a\u2028b")]
    public void TrySplit_RejectsAMalformedPath(string path)
    {
        Assert.False(VaultPath.TrySplit(path, out string[] segments), $"\"{path}\" was accepted.");
        Assert.Empty(segments);
    }

    [Fact]
    public void TrySplit_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => VaultPath.TrySplit(null!, out _));
    }

    [Theory]
    [InlineData("\\")]
    [InlineData("\\Docs")]
    [InlineData("\\Docs\\2026\\notes.txt")]
    [InlineData("\\\u00DCnic\u00F8de\\\uD83D\uDE00.bin")]
    public void FormatAndTrySplit_RoundTrip(string path)
    {
        Assert.True(VaultPath.TrySplit(path, out string[] segments));

        Assert.Equal(path, VaultPath.Format(segments));
    }

    [Fact]
    public void TrySplit_KeepsTheCaseOfEachSegment()
    {
        Assert.True(VaultPath.TrySplit("\\DoCs\\NoTeS.TxT", out string[] segments));

        Assert.Equal(["DoCs", "NoTeS.TxT"], segments);
    }
}
