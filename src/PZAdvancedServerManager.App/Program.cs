using PZAdvancedServerManager.App.Services;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;
using System.Diagnostics;

var openBrowser = args.Contains("--open-browser", StringComparer.OrdinalIgnoreCase);
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddSingleton<ApplicationPaths>();
builder.Services.AddSingleton<PackageProjectStore>();
builder.Services.AddSingleton<PzDiscoveryService>();
builder.Services.AddSingleton<DiscoveryCache>();
builder.Services.AddSingleton<PackageValidator>();
builder.Services.AddSingleton<PackageBuildService>();
builder.Services.AddSingleton<SteamCmdService>();
builder.Services.AddSingleton<ServerOrchestrationService>();
builder.Services.AddHostedService<PackageAutomationWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

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
        catch { /* L'URL reste indiquée dans la console si aucun navigateur n'est associé. */ }
    });
}

app.Run();
