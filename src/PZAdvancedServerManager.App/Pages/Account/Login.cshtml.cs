using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PZAdvancedServerManager.App.Authentication;

namespace PZAdvancedServerManager.App.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel(
    SignInManager<ManagerUser> signInManager,
    UserManager<ManagerUser> userManager) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public bool SetupAvailable { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(ResolveReturnUrl());
        SetupAvailable = !await userManager.Users.AnyAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetupAvailable = !await userManager.Users.AnyAsync();
        if (!ModelState.IsValid) return Page();

        var user = await userManager.FindByNameAsync(Input.Username.Trim());
        if (user is null || !user.IsEnabled)
        {
            await Task.Delay(250);
            ErrorMessage = "Nom d’utilisateur ou mot de passe incorrect.";
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(user, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            user.LastLoginAtUtc = DateTimeOffset.UtcNow;
            await userManager.UpdateAsync(user);
            return LocalRedirect(ResolveReturnUrl());
        }

        ErrorMessage = result.IsLockedOut
            ? "Ce compte est temporairement verrouillé après plusieurs tentatives. Réessayez plus tard."
            : "Nom d’utilisateur ou mot de passe incorrect.";
        return Page();
    }

    private string ResolveReturnUrl() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : Url.Page("/Index")!;

    public sealed class LoginInput
    {
        [Required]
        public string Username { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
    }
}
