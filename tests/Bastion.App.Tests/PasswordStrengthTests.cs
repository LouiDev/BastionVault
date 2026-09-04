using Bastion.App.Services;
using Bastion.Core;

namespace Bastion.App.Tests;

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
    public void CrackTimeFollowsTheKdfCost()
    {
        PasswordStrengthResult result = PasswordStrength.Estimate("7Kq!vX2m@Ld9Zt#4");

        double fast = PasswordStrength.CrackSeconds(result.Entropy, KdfParameters.FromPreset(KdfPreset.Fast));
        double strong = PasswordStrength.CrackSeconds(result.Entropy, KdfParameters.FromPreset(KdfPreset.Strong));

        // Strong costs 1 GiB x 4 passes against Fast's 64 MiB x 3, so it must buy real time.
        Assert.True(strong > fast * 20, $"fast {fast:E2} s, strong {strong:E2} s");
    }

    [Fact]
    public void TheSentenceNamesThePresetAndTheGpus()
    {
        PasswordStrengthResult result = PasswordStrength.Estimate("7Kq!vX2m@Ld9Zt#4");
        string sentence = PasswordStrength.Sentence(result, KdfParameters.Default, "Standard");

        Assert.StartsWith("At Standard, eight high-end GPUs would need ", sentence, StringComparison.Ordinal);
        Assert.EndsWith(" to guess this password.", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void AWeakPasswordFallsInsideASecond()
    {
        PasswordStrengthResult result = PasswordStrength.Estimate("123456");
        string sentence = PasswordStrength.Sentence(result, KdfParameters.Default, "Standard");

        Assert.Contains("less than a second", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void DurationsAreRoundedIntoWords()
    {
        Assert.Equal("less than a second", PasswordStrength.FormatDuration(0.4));
        Assert.Equal("1 second", PasswordStrength.FormatDuration(1));
        Assert.Contains("minute", PasswordStrength.FormatDuration(120), StringComparison.Ordinal);
        Assert.Contains("year", PasswordStrength.FormatDuration(60 * 60 * 24 * 400), StringComparison.Ordinal);
        Assert.Contains("thousand years", PasswordStrength.FormatDuration(60d * 60 * 24 * 365 * 5000), StringComparison.Ordinal);
        Assert.Equal("longer than the age of the universe", PasswordStrength.FormatDuration(double.PositiveInfinity));
    }
}
