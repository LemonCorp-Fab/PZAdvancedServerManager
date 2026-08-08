using PZAdvancedServerManager.App.Services;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;
using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

var openBrowser = args.Contains("--open-browser", StringComparer.OrdinalIgnoreCase);
var dataRootIndex = Array.FindIndex(args, x => x.Equals("--data-root", StringComparison.OrdinalIgnoreCase));
var dataRoot = dataRootIndex >= 0 && dataRootIndex + 1 < args.Length ? args[dataRootIndex + 1] : null;
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
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5160");

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.Configure<FormOptions>(options => options.ValueCountLimit = 20000);
builder.Services.AddSingleton(new ApplicationPaths(dataRoot));
builder.Services.AddSingleton<PackageProjectStore>();
builder.Services.AddSingleton<PzDiscoveryService>();
builder.Services.AddSingleton<PzEnvironmentService>();
builder.Services.AddSingleton<PackageValidator>();
builder.Services.AddSingleton<PackageBuildService>();
builder.Services.AddSingleton<SteamCmdService>();
builder.Services.AddSingleton<SteamCmdInstaller>();
builder.Services.AddSingleton<WorkshopCatalogService>();
builder.Services.AddSingleton<MapPriorityService>();
builder.Services.AddSingleton<ServerOrchestrationService>();
builder.Services.AddSingleton<RemoteServerConnectionStore>();
builder.Services.AddSingleton<SshRemoteServerService>();
builder.Services.AddSingleton<ServerProfileService>();
builder.Services.AddSingleton<ServerWorldDataStore>();
builder.Services.AddSingleton<PackageSourceSnapshotService>();
builder.Services.AddSingleton<PackageProjectService>();
builder.Services.AddSingleton<PackageLifecycleService>();
builder.Services.AddSingleton<PackageAutomationService>();
builder.Services.AddSingleton<WorkshopImportService>();
builder.Services.AddHostedService<PackageAutomationWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseAuthorization();

app.MapRazorPages();

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
