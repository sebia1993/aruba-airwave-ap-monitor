using System.Net;
using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Tests.AirWave;
using AirWaveApMonitor.Tests.Support;

namespace AirWaveApMonitor.Tests.Security;

public sealed class CredentialRegistrationTests
{
    [Fact]
    public async Task CancellationAfterDurableCredentialSave_CannotReuseOldSession()
    {
        using var directory = new TemporaryDirectory();
        var profiles = new ProfileStore(StoragePaths.Open(AppMode.Live, directory.Path));
        await profiles.UpdateAsync(new Uri("https://airwave.example.invalid"), 5, new(), 0, default);
        using var cancellation = new CancellationTokenSource();
        var credentials = new CancellingCredentialStore(cancellation);
        var loginForms = new List<string>();
        var handler = new AirWaveClientTests.ScriptedHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                loginForms.Add(await request.Content!.ReadAsStringAsync());
                var login = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("synthetic") };
                login.Headers.Add("X-BISCOTTI", "token-" + loginForms.Count);
                login.Headers.Add("Set-Cookie", "session=cookie-" + loginForms.Count + "; Path=/; Secure");
                return login;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ApListParserTests.Xml(ApListParserTests.Ap()), Encoding.UTF8, "application/xml")
            };
        });
        using var session = new AirWaveSession(_ => handler);
        var client = new AirWaveClient(session, credentials, TimeProvider.System);
        var before = await client.ReadAsync(profiles.Current!, default);
        Assert.Equal(RunOutcome.Succeeded, before.Batch.Outcome);
        Assert.Contains("credential_0=old-user", Assert.Single(loginForms));

        var registration = new CredentialRegistration(profiles, session, credentials);
        await profiles.OperationGate.WaitAsync();
        Exception? error;
        try { error = await Record.ExceptionAsync(() => registration.RegisterAsync(new("new-user", "new-password"), cancellation.Token)); }
        finally { profiles.OperationGate.Release(); }

        var after = await client.ReadAsync(profiles.Current!, default);
        Assert.Equal(1, after.Report.LoginRequests);
        Assert.Equal(RunOutcome.Succeeded, after.Batch.Outcome);
        Assert.Equal(2, loginForms.Count);
        Assert.Contains("credential_0=new-user", loginForms[1]);
        Assert.Null(error);
        Assert.Equal(2, profiles.Current!.CredentialsVersion);
        Assert.Equal(2, profiles.Current.ScopeVersion);
        var reopened = new ProfileStore(StoragePaths.Open(AppMode.Live, directory.Path));
        Assert.Equal(2, reopened.Current!.CredentialsVersion);
    }

    private sealed class CancellingCredentialStore(CancellationTokenSource caller) : ICredentialStore
    {
        private AirWaveCredentials current = new("old-user", "old-password");
        public Task SaveAsync(Guid datasetId, Uri origin, AirWaveCredentials credentials, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            current = credentials; // The durable replacement has completed at this boundary.
            caller.Cancel();
            return Task.CompletedTask;
        }
        public Task<AirWaveCredentials?> ReadAsync(Guid datasetId, Uri origin, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<AirWaveCredentials?>(current);
        }
        public Task ClearAsync(Guid datasetId, Uri origin, CancellationToken ct) => Task.CompletedTask;
    }
}
