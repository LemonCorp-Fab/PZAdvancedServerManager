using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace PZAdvancedServerManager.App.Authentication;

public sealed class ManagerIdentityBootstrapper(
    ManagerIdentityDbContext database,
    RoleManager<IdentityRole> roleManager,
    UserManager<ManagerUser> userManager,
    IConfiguration configuration,
    ILogger<ManagerIdentityBootstrapper> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await database.Database.EnsureCreatedAsync(cancellationToken);

        foreach (var role in ManagerRoles.All)
        {
            if (await roleManager.RoleExistsAsync(role)) continue;
            var result = await roleManager.CreateAsync(new IdentityRole(role));
            EnsureSucceeded(result, $"create the {role} role");
        }

        if (await userManager.Users.AnyAsync(cancellationToken)) return;

        var username = configuration["PZASM_ADMIN_USERNAME"]?.Trim();
        var password = ReadDeploymentSecret("PZASM_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("No manager account exists. Complete the one-time setup page or provide PZASM_ADMIN_USERNAME and an administrator password secret.");
            return;
        }

        var user = new ManagerUser
        {
            UserName = username,
            DisplayName = configuration["PZASM_ADMIN_DISPLAY_NAME"]?.Trim() ?? username,
            IsEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var created = await userManager.CreateAsync(user, password);
        EnsureSucceeded(created, "create the bootstrap administrator");
        EnsureSucceeded(await userManager.AddToRoleAsync(user, ManagerRoles.Administrator), "assign the administrator role");
        logger.LogInformation("The initial manager administrator account was created from deployment secrets.");
    }

    private string? ReadDeploymentSecret(string name)
    {
        var secretPath = configuration[$"{name}_FILE"]?.Trim();
        if (string.IsNullOrWhiteSpace(secretPath)) return configuration[name];

        if (!File.Exists(secretPath))
        {
            throw new InvalidOperationException($"The deployment secret file configured by {name}_FILE does not exist.");
        }

        return File.ReadAllText(secretPath).TrimEnd('\r', '\n');
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (result.Succeeded) return;
        throw new InvalidOperationException($"Unable to {action}: {string.Join("; ", result.Errors.Select(error => error.Description))}");
    }
}
