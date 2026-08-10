using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PZAdvancedServerManager.App.Pages.Account;

[AllowAnonymous]
public sealed class AccessDeniedModel : PageModel;
