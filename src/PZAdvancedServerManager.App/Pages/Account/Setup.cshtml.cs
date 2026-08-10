using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PZAdvancedServerManager.App.Authentication;

namespace PZAdvancedServerManager.App.Pages.Account;

[AllowAnonymous]
public sealed class SetupModel(
    UserManager<ManagerUser> userManager,
    SignInManager<ManagerUser> signInManager) : PageModel
{
    [BindProperty]
    public SetupInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (await userManager.Users.AnyAsync()) return RedirectToPage("/Account/Login");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await userManager.Users.AnyAsync()) return RedirectToPage("/Account/Login");
        if (!ModelState.IsValid) return Page();

        var user = new ManagerUser
        {
            UserName = Input.Username.Trim(),
            DisplayName = Input.DisplayName.Trim(),
            IsEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var result = await userManager.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        var roleResult = await userManager.AddToRoleAsync(user, ManagerRoles.Administrator);
        if (!roleResult.Succeeded)
        {
            await userManager.DeleteAsync(user);
            foreach (var error in roleResult.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return RedirectToPage("/Index");
    }

    public sealed class SetupInput
    {
        [Required, StringLength(64, MinimumLength = 3)]
        public string Username { get; set; } = string.Empty;

        [Required, StringLength(100, MinimumLength = 2)]
        public string DisplayName { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Compare(nameof(Password))]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
