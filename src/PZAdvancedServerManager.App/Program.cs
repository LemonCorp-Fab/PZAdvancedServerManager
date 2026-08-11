using PZAdvancedServerManager.App.Services;
using PZAdvancedServerManager.App.Authentication;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;
using PZAdvancedServerManager.Core.Transfer;
using System.Diagnostics;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var openBrowser = args.Contains("--open-browser", StringComparer.OrdinalIgnoreCase);
var dataRootIndex = Array.FindIndex(args, x => x.Equals("--data-root", StringComparison.OrdinalIgnoreCase));
var dataRoot = dataRootIndex >= 0 && dataRootIndex + 1 < args.Length
    ? args[dataRootIndex + 1]
    : Environment.GetEnvironmentVariable("PZASM_DATA_ROOT");
var adjacentWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var sourceWebRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot"));
var resolvedWebRoot = Directory.Exists(adjacentWebRoot)
    ? adjacentWebRoot
    : Directory.Exists(sourceWebRoot) ? sourceWebRoot : null;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = resolvedWebRoot
});
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://127.0.0.1:5160");

var applicationPaths = new ApplicationPaths(dataRoot);
var identityRoot = Path.Combine(applicationPaths.DataRoot, "identity");
Directory.CreateDirectory(identityRoot);
var trustProxyHeaders = builder.Configuration.GetValue("PZASM_TRUST_PROXY_HEADERS", false);
var identityConnectionString = new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(identityRoot, "manager-identity.db")
}.ToString();

if (trustProxyHeaders) builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services
    .AddDataProtection()
    .SetApplicationName("PZAdvancedServerManager")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(identityRoot, "keys")));
builder.Services.AddDbContext<ManagerIdentityDbContext>(options =>
    options.UseSqlite(identityConnectionString));
builder.Services
    .AddIdentity<ManagerUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.User.RequireUniqueEmail = false;
    })
    .AddEntityFrameworkStores<ManagerIdentityDbContext>()
    .AddDefaultTokenProviders();
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "PZASM.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ReturnUrlParameter = "returnUrl";
});
builder.Services.AddScoped<ManagerIdentityBootstrapper>();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Setup");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AllowAnonymousToPage("/Error");
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = PackTransferService.MaximumUniqueArchiveBytes + 1024L * 1024 * 1024);
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueCountLimit = 20000;
    options.MemoryBufferThreshold = 64 * 1024;
    options.MultipartBodyLengthLimit = PackTransferService.MaximumUniqueArchiveBytes + 1024L * 1024 * 1024;
});
builder.Services.AddSingleton(applicationPaths);
builder.Services.AddSingleton<PackageProjectStore>();
builder.Services.AddSingleton<PzDiscoveryService>();
builder.Services.AddSingleton<PzEnvironmentService>();
builder.Services.AddSingleton<PackageValidator>();
builder.Services.AddSingleton<PackageBuildService>();
builder.Services.AddSingleton<SteamCmdService>();
builder.Services.AddSingleton<SteamCmdInstaller>();
builder.Services.AddHostedService<SteamCmdAutoInstallWorker>();
builder.Services.AddSingleton<WorkshopCatalogService>();
builder.Services.AddSingleton<MapPriorityService>();
builder.Services.AddSingleton<ModConflictAnalyzer>();
builder.Services.AddSingleton<TextConflictDiffService>();
builder.Services.AddSingleton<ServerOrchestrationService>();
builder.Services.AddSingleton<StoredSecretProtector>();
builder.Services.AddSingleton<RemoteServerConnectionStore>();
builder.Services.AddSingleton<LocalServerProfileStore>();
builder.Services.AddSingleton<SshRemoteServerService>();
builder.Services.AddSingleton<PineHostingClient>();
builder.Services.AddSingleton<IRemoteServerBackend, SshRconRemoteBackend>();
builder.Services.AddSingleton<IRemoteServerBackend, PineHostingRemoteBackend>();
builder.Services.AddSingleton<RemoteServerBackendRouter>();
builder.Services.AddSingleton<ServerProfileService>();
builder.Services.AddSingleton<ServerWorldDataStore>();
builder.Services.AddSingleton<RconConsoleStore>();
builder.Services.AddSingleton<PackageSourceSnapshotService>();
builder.Services.AddSingleton<PackageProjectService>();
builder.Services.AddSingleton<PackageLifecycleService>();
builder.Services.AddSingleton<PackageAutomationService>();
builder.Services.AddSingleton<WorkshopImportService>();
builder.Services.AddSingleton<PackTransferService>();
builder.Services.AddSingleton<ServerConnectionTransferService>();
builder.Services.AddSingleton<TransferWorkspaceCleaner>();
builder.Services.AddSingleton<StorageMaintenanceService>();
builder.Services.AddHostedService<TransferCleanupWorker>();
builder.Services.AddHostedService<PackageAutomationWorker>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<ManagerIdentityBootstrapper>().InitializeAsync();
}

if (trustProxyHeaders) app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseRouting();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

app.UseAuthentication();
app.Use(async (context, next) =>
{
    await next();
    if (context.User.Identity?.IsAuthenticated == true)
        context.Response.Headers.CacheControl = "no-store, max-age=0";
});
app.UseAuthorization();

app.MapRazorPages();
app.MapGet("/health/live", () => Results.Json(new { status = "alive" }))
    .AllowAnonymous();
app.MapGet("/health/ready", async (ManagerIdentityDbContext database, SteamCmdInstaller installer, CancellationToken cancellationToken) =>
{
    var databaseReady = await database.Database.CanConnectAsync(cancellationToken);
    var steamCmd = installer.GetStatus();
    var payload = new
    {
        status = databaseReady ? "ready" : "unavailable",
        identityDatabase = databaseReady ? "ready" : "unavailable",
        steamCmd = steamCmd.Installed ? "installed" : "not-installed"
    };
    return Results.Json(payload, statusCode: databaseReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

if (openBrowser)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault(x => x.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? "http://127.0.0.1:5160";
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { /* The URL remains visible in the console when no browser handler is available. */ }
    });
}

app.Run();
