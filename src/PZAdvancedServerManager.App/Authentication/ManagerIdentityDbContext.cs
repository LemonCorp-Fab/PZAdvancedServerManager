using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace PZAdvancedServerManager.App.Authentication;

public sealed class ManagerIdentityDbContext(DbContextOptions<ManagerIdentityDbContext> options)
    : IdentityDbContext<ManagerUser>(options);
