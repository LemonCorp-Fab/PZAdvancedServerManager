using PZAdvancedServerManager.Core.Publishing;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class SteamCmdAuthenticationTests
{
    [Fact]
    public void StartsAuthenticationWithLoginCommandAndNoSecretArguments()
    {
        var arguments = SteamCmdService.CreateAuthenticationArguments("publisher_account");

        Assert.Equal(["+login", "publisher_account", "+quit"], arguments);
        Assert.DoesNotContain(arguments, value => value.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(arguments, value => value.Contains("guard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifiesCachedSessionWithoutSecretArguments()
    {
        var arguments = SteamCmdService.CreateCachedSessionVerificationArguments("publisher_account");

        Assert.Equal(["+login", "publisher_account", "+info", "+quit"], arguments);
        Assert.DoesNotContain(arguments, value => value.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(arguments, value => value.Contains("guard", StringComparison.OrdinalIgnoreCase));
    }

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

    [Theory]
    [InlineData("Waiting for confirmation")]
    [InlineData("PollAuthSessionStatus succeeded, no refresh token yet")]
    public void DetectsPendingMobileApproval(string output)
    {
        Assert.True(SteamCmdPromptClassifier.AwaitsMobileApproval(output));
        Assert.False(SteamCmdPromptClassifier.MobileApprovalExpired(output));
    }

    [Theory]
    [InlineData("Timed out waiting for confirmation")]
    [InlineData("Account logon denied, need two-factor code")]
    public void DetectsExpiredMobileApproval(string output)
    {
        Assert.True(SteamCmdPromptClassifier.MobileApprovalExpired(output));
    }

    [Fact]
    public void KeepsSteamCmdAliveWhileTheMobileRequestIsPolling()
    {
        const string polling = "cannot call UpdateAuthSessionWithSteamGuardCode because we do not have a code available\nWaiting for confirmation\nPollAuthSessionStatus succeeded, no refresh token yet";
        const string expired = polling + "\nAccount logon denied, need two-factor code\nTimed out waiting for confirmation";

        Assert.True(SteamCmdPromptClassifier.AwaitsMobileApproval(polling));
        Assert.False(SteamCmdPromptClassifier.RequiresSteamGuard(polling));
        Assert.False(SteamCmdPromptClassifier.MobileApprovalExpired(polling));
        Assert.True(SteamCmdPromptClassifier.RequiresSteamGuard(expired));
        Assert.True(SteamCmdPromptClassifier.MobileApprovalExpired(expired));
    }

    [Fact]
    public void DoesNotRequireGuardForAccountsWithoutAChallenge()
    {
        const string output = "Logging in user 'publisher' to Steam Public...OK\nWaiting for user info...OK";

        Assert.False(SteamCmdPromptClassifier.AwaitsMobileApproval(output));
        Assert.False(SteamCmdPromptClassifier.RequiresSteamGuard(output));
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
