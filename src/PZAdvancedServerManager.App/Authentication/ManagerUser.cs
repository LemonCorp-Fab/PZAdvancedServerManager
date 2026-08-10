using Microsoft.AspNetCore.Identity;

namespace PZAdvancedServerManager.App.Authentication;

public sealed class ManagerUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAtUtc { get; set; }
}

public static class ManagerRoles
{
    public const string Administrator = "Administrator";
    public const string Operator = "Operator";

    public static readonly string[] All = [Administrator, Operator];
}
