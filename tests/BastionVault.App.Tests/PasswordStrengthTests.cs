using BastionVault.App.Services;

namespace BastionVault.App.Tests;

/// <summary>
/// The password estimator. The exact bit counts are a judgement call, so these tests pin the
/// behaviour a user would notice: known-bad passwords score as bad, structure is recognised, and
/// a genuinely random passphrase scores well.
/// </summary>
public sealed class PasswordStrengthTests
{
    [Theory]
    [InlineData("123456")]
    [InlineData("password")]
    [InlineData("qwerty")]
    [InlineData("letmein")]
    [InlineData("monkey")]
    [InlineData("Password1")]
    [InlineData("p@ssw0rd")]
    [InlineData("trustno1")]
    public void CommonPasswordsAreRejected(string password)
    {
        PasswordStrengthResult result = PasswordStrength.Estimate(password);

        Assert.True(
            result.Level <= PasswordStrengthLevel.Weak,
            $"'{password}' scored {result.Level} at {result.Entropy:N1} bits.");
        Assert.Contains(result.Patterns, p => p.Kind == PatternKind.Dictionary);
    }

    [Theory]
    [InlineData("aaaaaaaaaa", PatternKind.Repeat)]
    [InlineData("abcdefghij", PatternKind.Sequence)]
    [InlineData("qwertyuiop", PatternKind.Dictionary)]
    [InlineData("zaq12wsxcde", PatternKind.KeyboardWalk)]
    public void StructureIsRecognised(string password, PatternKind expected)
    {
        PasswordStrengthResult result = PasswordStrength.Estimate(password);

        Assert.Contains(result.Patterns, p => p.Kind == expected);
        Assert.True(result.Level <= PasswordStrengthLevel.Fair);
    }

    [Fact]
    public void AYearIsRecognisedAsADate()
    {
        PasswordStrengthResult result = PasswordStrength.Estimate("orchard1987");
        Assert.Contains(result.Patterns, p => p.Kind == PatternKind.Date);
    }

    [Theory]
    [InlineData("7Kq!vX2m@Ld9Zt#4")]
    [InlineData("gravel-oyster-mandolin-42")]
    [InlineData("Nx8$wPq3Lv6Rt0Yb2Hs")]
    public void RandomLookingPasswordsScoreWell(string password)
    {
        PasswordStrengthResult result = PasswordStrength.Estimate(password);

        Assert.True(
            result.Level >= PasswordStrengthLevel.Strong,
            $"'{password}' scored {result.Level} at {result.Entropy:N1} bits.");
    }

    [Fact]
    public void AnEmptyPasswordIsEmpty()
    {
        PasswordStrengthResult result = PasswordStrength.Estimate(string.Empty);

        Assert.Equal(0, result.Length);
        Assert.Equal(0, result.Entropy);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public void TooShortIsCalledOut()
    {
        PasswordStrengthResult result = PasswordStrength.Estimate("Kx9#");
        Assert.Equal("Too short: use at least eight characters.", result.Weakness);
    }

    [Fact]
    public void TheDictionaryIsBigEnoughToBeUseful()
    {
        Assert.True(CommonPasswords.Count >= 2000, $"Only {CommonPasswords.Count} entries.");
        Assert.Equal(1, CommonPasswords.Rank("123456"));
        Assert.Null(CommonPasswords.Rank("gravel-oyster-mandolin"));
    }

    [Fact]
    public void TheSentenceNeverQuotesACrackTime()
    {
        string[] passwords = ["123456", "sunshine1", "7Kq!vX2m", "7Kq!vX2m@Ld9Zt#4", "correct horse battery staple mountain lantern 42!"];
        foreach (string password in passwords)
        {
            string sentence = PasswordStrength.Sentence(PasswordStrength.Estimate(password));

            Assert.False(string.IsNullOrWhiteSpace(sentence), password);
            Assert.DoesNotMatch("GPU|would need|seconds|minutes|hours|days|months|years|universe", sentence);
        }
    }

    [Fact]
    public void EveryBandHasItsOwnSentence()
    {
        var sentences = new HashSet<string>(StringComparer.Ordinal);
        foreach (PasswordStrengthLevel level in Enum.GetValues<PasswordStrengthLevel>())
        {
            string sentence = PasswordStrength.Sentence(new PasswordStrengthResult(12, 50, level, [], null));

            Assert.False(string.IsNullOrWhiteSpace(sentence), level.ToString());
            Assert.True(sentences.Add(sentence), $"{level} shares its sentence with another band.");
        }
    }

    [Fact]
    public void AnEmptyPasswordAsksForOne()
    {
        Assert.Equal("Type a password to see its strength.", PasswordStrength.Sentence(PasswordStrengthResult.Empty));
    }
}
