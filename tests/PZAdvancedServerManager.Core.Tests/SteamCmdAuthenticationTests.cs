using PZAdvancedServerManager.Core.Publishing;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class SteamCmdAuthenticationTests
{
    [Fact]
    public void DetectsPasswordPromptWithoutLineBreak()
    {
        Assert.True(SteamCmdPromptClassifier.RequestsPassword("Cached credentials not found.\n\npassword:"));
    }

    [Theory]
    [InlineData("This account is protected by Steam Guard. Please enter the current code")]
    [InlineData("Login Failure: Account Login Denied Failed")]
    [InlineData("FAILED (Invalid Login Auth Code)")]
    [InlineData("Enter the two-factor code")]
    public void DetectsSteamGuardChallenges(string output)
    {
        Assert.True(SteamCmdPromptClassifier.RequiresSteamGuard(output));
    }

    [Theory]
    [InlineData("Login Failure: Account Login Denied Failed")]
    [InlineData("FAILED (Invalid Login Auth Code)")]
    [InlineData("Invalid Steam Guard code")]
    public void DetectsRejectedSteamGuardCodes(string output)
    {
        Assert.True(SteamCmdPromptClassifier.RejectsSteamGuardCode(output));
    }

    [Fact]
    public void DistinguishesInteractiveGuardPromptFromRejectedCode()
    {
        const string output = "This account is protected by Steam Guard. Please enter the current code.\nTwo-factor code:";

        Assert.True(SteamCmdPromptClassifier.RequestsSteamGuardCode(output));
        Assert.False(SteamCmdPromptClassifier.RejectsSteamGuardCode(output));
    }

    [Fact]
    public void DetectsPortableSessionLoginSuccess()
    {
        const string output = "Logging in user 'publisher' to Steam Public...OK\nWaiting for client config...OK\nWaiting for user info...OK";

        Assert.True(SteamCmdPromptClassifier.LoginSucceeded(output));
        Assert.False(SteamCmdPromptClassifier.LoginFailed(output));
    }

    [Fact]
    public void DetectsInvalidPasswordSeparatelyFromSteamGuard()
    {
        const string output = "Logging in user 'publisher' to Steam Public...FAILED (Invalid Password)";

        Assert.True(SteamCmdPromptClassifier.LoginFailed(output));
        Assert.False(SteamCmdPromptClassifier.RequiresSteamGuard(output));
    }
}
