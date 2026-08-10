using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PZAdvancedServerManager.App.Authentication;

namespace PZAdvancedServerManager.App.Pages.Admin;

[Authorize(Roles = ManagerRoles.Administrator)]
public sealed class UsersModel(UserManager<ManagerUser> userManager) : PageModel
{
    [BindProperty]
    public CreateUserInput Create { get; set; } = new();

    public IReadOnlyList<UserRow> Users { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ManagerRoles.All.Contains(Create.Role, StringComparer.Ordinal)) ModelState.AddModelError(nameof(Create.Role), "Rôle invalide.");
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var user = new ManagerUser
        {
            UserName = Create.Username.Trim(),
            DisplayName = Create.DisplayName.Trim(),
            IsEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var created = await userManager.CreateAsync(user, Create.Password);
        if (!created.Succeeded)
        {
            foreach (var error in created.Errors) ModelState.AddModelError(string.Empty, error.Description);
            await LoadAsync();
            return Page();
        }

        var assigned = await userManager.AddToRoleAsync(user, Create.Role);
        if (!assigned.Succeeded)
        {
            await userManager.DeleteAsync(user);
            foreach (var error in assigned.Errors) ModelState.AddModelError(string.Empty, error.Description);
            await LoadAsync();
            return Page();
        }

        TempData["Toast"] = $"Le compte {user.UserName} a été créé.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(string id)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (userManager.GetUserId(User) == id) return await ErrorPageAsync("Vous ne pouvez pas désactiver votre propre compte.");
        if (user.IsEnabled && await userManager.IsInRoleAsync(user, ManagerRoles.Administrator) && await IsLastEnabledAdministratorAsync(user))
            return await ErrorPageAsync("Le dernier administrateur actif ne peut pas être désactivé.");

        user.IsEnabled = !user.IsEnabled;
        user.LockoutEnd = user.IsEnabled ? null : DateTimeOffset.MaxValue;
        user.LockoutEnabled = true;
        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded) return await ErrorPageAsync(Describe(updated));
        await userManager.UpdateSecurityStampAsync(user);
        TempData["Toast"] = user.IsEnabled ? $"Le compte {user.UserName} est réactivé." : $"Le compte {user.UserName} et ses sessions sont désactivés.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetRoleAsync(string id, string role)
    {
        if (!ManagerRoles.All.Contains(role, StringComparer.Ordinal)) return await ErrorPageAsync("Rôle invalide.");
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        var currentRoles = await userManager.GetRolesAsync(user);
        if (currentRoles.Contains(ManagerRoles.Administrator) && role != ManagerRoles.Administrator && await IsLastEnabledAdministratorAsync(user))
            return await ErrorPageAsync("Le dernier administrateur actif ne peut pas devenir opérateur.");
        if (currentRoles.Contains(role, StringComparer.Ordinal))
        {
            TempData["Toast"] = $"Le rôle de {user.UserName} est déjà à jour.";
            return RedirectToPage();
        }
        var added = await userManager.AddToRoleAsync(user, role);
        if (!added.Succeeded) return await ErrorPageAsync(Describe(added));
        var obsoleteRoles = currentRoles.Where(currentRole => !currentRole.Equals(role, StringComparison.Ordinal)).ToArray();
        if (obsoleteRoles.Length > 0)
        {
            var removed = await userManager.RemoveFromRolesAsync(user, obsoleteRoles);
            if (!removed.Succeeded) return await ErrorPageAsync($"Le nouveau rôle est actif, mais l'ancien rôle n'a pas pu être retiré : {Describe(removed)}");
        }
        await userManager.UpdateSecurityStampAsync(user);
        TempData["Toast"] = $"Le rôle de {user.UserName} a été mis à jour.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(string id, string newPassword)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (string.IsNullOrWhiteSpace(newPassword)) return await ErrorPageAsync("Le nouveau mot de passe est requis.");
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var reset = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!reset.Succeeded) return await ErrorPageAsync(Describe(reset));
        await userManager.UpdateSecurityStampAsync(user);
        TempData["Toast"] = $"Le mot de passe de {user.UserName} a été réinitialisé et ses sessions ont été révoquées.";
        return RedirectToPage();
    }

    private async Task<bool> IsLastEnabledAdministratorAsync(ManagerUser excluded)
    {
        var administrators = await userManager.GetUsersInRoleAsync(ManagerRoles.Administrator);
        return administrators.Count(user => user.IsEnabled && user.Id != excluded.Id) == 0;
    }

    private async Task<IActionResult> ErrorPageAsync(string message)
    {
        ErrorMessage = message;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var currentId = userManager.GetUserId(User);
        var users = await userManager.Users.OrderBy(user => user.DisplayName).ToArrayAsync();
        var rows = new List<UserRow>(users.Length);
        foreach (var user in users)
        {
            var roles = await userManager.GetRolesAsync(user);
            var role = roles.FirstOrDefault() ?? ManagerRoles.Operator;
            rows.Add(new UserRow(user.Id, user.UserName ?? string.Empty, user.DisplayName, user.IsEnabled, role, role == ManagerRoles.Administrator ? "ADMINISTRATEUR" : "OPÉRATEUR", user.CreatedAtUtc, user.LastLoginAtUtc, user.Id == currentId));
        }
        Users = rows;
    }

    private static string Describe(IdentityResult result) => string.Join("; ", result.Errors.Select(error => error.Description));

    public sealed class CreateUserInput
    {
        [Required, StringLength(64, MinimumLength = 3)]
        public string Username { get; set; } = string.Empty;

        [Required, StringLength(100, MinimumLength = 2)]
        public string DisplayName { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string Role { get; set; } = ManagerRoles.Operator;
    }

    public sealed record UserRow(string Id, string Username, string DisplayName, bool IsEnabled, string Role, string RoleLabel, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastLoginAtUtc, bool IsCurrentUser);
}
