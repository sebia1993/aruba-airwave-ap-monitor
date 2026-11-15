using System.Net;
using System.Text.Json;
using AirWaveApMonitor.App.Endpoints;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AirWaveApMonitor.Tests.Web;

internal sealed class ReadApiHost : IAsyncDisposable
{
    private readonly WebApplication app;
    public HttpClient Client { get; }
    private ReadApiHost(WebApplication app, HttpClient client) { this.app = app; Client = client; }
    public static async Task<ReadApiHost> Start(ReadFixture fixture, long currentScope = 1)
    {
        await fixture.Store.InitializeAsync(default);
        await File.WriteAllTextAsync(Path.Combine(fixture.Location.DataDirectory, "profile.json"), JsonSerializer.Serialize(fixture.Profile(currentScope)));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(fixture.Location);
        builder.Services.AddSingleton<ICollectionStore>(fixture.Store);
        builder.Services.AddSingleton<IMonitoringReadStore>(fixture.Store);
        builder.Services.AddSingleton(new ProfileStore(fixture.Location));
        var app = builder.Build(); app.MapMonitoring(); app.MapExport(); await app.StartAsync();
        return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
    }
    public async ValueTask DisposeAsync() { Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
}
