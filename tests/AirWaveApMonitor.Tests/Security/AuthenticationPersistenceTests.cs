using System.Net;
using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Tests.AirWave;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.Tests.Security;

public sealed class AuthenticationPersistenceTests
{
    [Fact]
    public async Task FailedCredentialReplacement_DoesNotClearExistingRejection()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        var handler = Rejected();
        using var session = new AirWaveSession(_ => handler);
        var credentials = new ReplaceableCredentials { FailSave = true };
        var client = new AirWaveClient(session, credentials, TimeProvider.System);
        await client.ReadAsync(profiles.Current!, default);

        var registration = new CredentialRegistration(profiles, session, credentials);
        await Assert.ThrowsAsync<IOException>(() => registration.RegisterAsync(new("new", "new-secret"), default));
        var after = await client.ReadAsync(profiles.Current!, default);

        Assert.True(after.Report.AuthenticationBlocked);
        Assert.Equal(0, after.Report.LoginRequests);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, profiles.Current!.CredentialsVersion);
    }

    [Fact]
    public async Task AuthRejected_RemainsPausedAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        using (var session = new AirWaveSession(_ => Rejected(), store))
        {
            var first = await new AirWaveClient(session, new ReplaceableCredentials(), TimeProvider.System)
                .ReadAsync(profiles.Current!, default);
            Assert.Equal(1, first.Report.LoginRequests);
            Assert.True(first.Report.AuthenticationBlocked);
        }

        var reopenedStore = new SqliteAuthenticationStateStore(StoragePaths.Open(AppMode.Live, directory.Path), gate);
        var handler = Rejected();
        using var reopened = new AirWaveSession(_ => handler, reopenedStore);
        var result = await new AirWaveClient(reopened, new ReplaceableCredentials(), TimeProvider.System)
            .ReadAsync((await Profiles(directory.Path)).Current!, default);

        Assert.Equal(0, result.Report.LoginRequests);
        Assert.Equal(0, handler.Calls);
        Assert.Contains(result.Batch.Errors, error => error.Category == CollectionErrorCategory.AuthenticationBlocked);
        var state = await reopenedStore.ReadAsync(AuthenticationBinding.From(profiles.Current!), default);
        Assert.NotNull(state.Stop);
        Assert.Equal(AuthenticationStopReason.LoginRejected, state.Stop.Reason);
        Assert.Equal(TimeSpan.Zero, state.Stop.OccurredAtUtc.Offset);
    }

    [Fact]
    public async Task RestartWithProxyChange_PreservesRejection()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(StoragePaths.Open(AppMode.Live, directory.Path), gate);
        var at = new DateTimeOffset(2026, 11, 16, 1, 2, 3, TimeSpan.Zero);
        await store.SaveStopAsync(new(AuthenticationBinding.From(profiles.Current!), AuthenticationStopReason.LoginRejected, at), default);
        var handler = Rejected();
        using var session = new AirWaveSession(_ => handler, store);

        var result = await new AirWaveClient(session, new ReplaceableCredentials(), TimeProvider.System)
            .ReadAsync(profiles.Current! with { Proxy = new(ProxyMode.Direct) }, default);

        Assert.Equal(0, handler.Calls);
        Assert.True(result.Report.AuthenticationBlocked);
        Assert.Equal(at, (await store.ReadAsync(AuthenticationBinding.From(profiles.Current!), default)).Stop!.OccurredAtUtc);
    }

    [Fact]
    public async Task OldVersionStop_DoesNotBlockNewVersion()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(StoragePaths.Open(AppMode.Live, directory.Path), gate);
        var newer = profiles.Current! with { CredentialsVersion = 2 };
        var handler = new AirWaveClientTests.ScriptedHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Post ? Login() : Ap()));
        using var session = new AirWaveSession(_ => handler, store);
        var client = new AirWaveClient(session, new ReplaceableCredentials(), TimeProvider.System);
        Assert.Equal(RunOutcome.Succeeded, (await client.ReadAsync(newer, default)).Batch.Outcome);
        await store.SaveStopAsync(new(AuthenticationBinding.From(profiles.Current!), AuthenticationStopReason.LoginRejected, DateTimeOffset.UtcNow), default);

        var result = await client.ReadAsync(newer, default);
        Assert.Equal(RunOutcome.Succeeded, result.Batch.Outcome);
        Assert.False(result.Report.AuthenticationBlocked);
        Assert.Null((await store.ReadAsync(AuthenticationBinding.From(newer), default)).Stop);
    }

    [Fact]
    public async Task ExplicitRetry_ClearsDurablyBeforeReopenedSessionMakesOneAttempt()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var binding = AuthenticationBinding.From(profiles.Current!);
        await store.SaveStopAsync(new(binding, AuthenticationStopReason.LoginRejected, DateTimeOffset.UtcNow), default);
        using (var retrySession = new AirWaveSession(_ => Rejected(), store))
            await retrySession.RetryAuthenticationAsync(profiles.Current!);

        var reopenedStore = new SqliteAuthenticationStateStore(location, gate);
        Assert.Null((await reopenedStore.ReadAsync(binding, default)).Stop);
        var handler = Rejected();
        using var reopened = new AirWaveSession(_ => handler, reopenedStore);
        var client = new AirWaveClient(reopened, new ReplaceableCredentials(), TimeProvider.System);
        var attempted = await client.ReadAsync(profiles.Current!, default);
        var blockedAgain = await client.ReadAsync(profiles.Current!, default);
        Assert.Equal(1, attempted.Report.LoginRequests);
        Assert.Equal(0, blockedAgain.Report.LoginRequests);
        Assert.Equal(1, handler.Calls);
        Assert.NotNull((await reopenedStore.ReadAsync(binding, default)).Stop);
    }

    [Fact]
    public async Task SqliteClearFailure_LeavesMemoryAndReopenedSessionBlocked()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var binding = AuthenticationBinding.From(profiles.Current!);
        await store.SaveStopAsync(new(binding, AuthenticationStopReason.LoginRejected, DateTimeOffset.UtcNow), default);
        var handler = Rejected();
        using var session = new AirWaveSession(_ => handler, store);
        await session.RestoreAuthenticationStateAsync(profiles.Current!);
        ExecuteSql(directory.Path, "CREATE TRIGGER refuse_auth_clear BEFORE UPDATE OF blocked_reason ON authentication_state BEGIN SELECT RAISE(ABORT, 'synthetic clear failure'); END;");

        var error = await Assert.ThrowsAsync<AuthenticationStateStoreException>(() => session.RetryAuthenticationAsync());
        Assert.DoesNotContain("synthetic clear failure", error.ToString());
        var result = await new AirWaveClient(session, new ReplaceableCredentials(), TimeProvider.System).ReadAsync(profiles.Current!, default);
        Assert.True(result.Report.AuthenticationBlocked);
        Assert.Equal(0, handler.Calls);
        using var reopened = new AirWaveSession(_ => handler, new SqliteAuthenticationStateStore(location, gate));
        var afterRestart = await new AirWaveClient(reopened, new ReplaceableCredentials(), TimeProvider.System).ReadAsync(profiles.Current!, default);
        Assert.Equal(0, afterRestart.Report.LoginRequests);
        Assert.True(afterRestart.Report.AuthenticationBlocked);
    }

    [Fact]
    public async Task RepeatedExpiry_PersistsItsReasonAndOriginalUtcTime()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(StoragePaths.Open(AppMode.Live, directory.Path), gate);
        var clock = new ManualTimeProvider();
        clock.Advance(TimeSpan.FromHours(27));
        var handler = new AirWaveClientTests.ScriptedHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Post ? Login() : new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using (var session = new AirWaveSession(_ => handler, store))
        {
            var result = await new AirWaveClient(session, new ReplaceableCredentials(), clock).ReadAsync(profiles.Current!, default);
            Assert.Equal(2, result.Report.LoginRequests);
            Assert.Equal(2, result.Report.ApRequests);
        }
        var state = await new SqliteAuthenticationStateStore(StoragePaths.Open(AppMode.Live, directory.Path), gate)
            .ReadAsync(AuthenticationBinding.From(profiles.Current!), default);
        Assert.Equal(AuthenticationStopReason.RepeatedSessionExpiry, state.Stop!.Reason);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero), state.Stop.OccurredAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientOrReadForbidden_DoesNotPersistAnAuthenticationStop(bool readForbidden)
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var handler = new AirWaveClientTests.ScriptedHandler((_, number) => Task.FromResult(readForbidden && number == 1
            ? Login() : new HttpResponseMessage(readForbidden ? HttpStatusCode.Forbidden : HttpStatusCode.ServiceUnavailable)));
        using (var session = new AirWaveSession(_ => handler, store))
        {
            var result = await new AirWaveClient(session, new ReplaceableCredentials(), TimeProvider.System).ReadAsync(profiles.Current!, default);
            Assert.False(result.Report.AuthenticationBlocked);
        }
        var reopened = new SqliteAuthenticationStateStore(location, gate);
        Assert.Null((await reopened.ReadAsync(AuthenticationBinding.From(profiles.Current!), default)).Stop);
        var retryHandler = Rejected();
        using var next = new AirWaveSession(_ => retryHandler, reopened);
        Assert.Equal(1, (await new AirWaveClient(next, new ReplaceableCredentials(), TimeProvider.System).ReadAsync(profiles.Current!, default)).Report.LoginRequests);
    }

    [Fact]
    public async Task UnreadableDatabase_FailsClosedWithoutHttpOrRawError()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "monitor.db"), "not a database; synthetic-secret");
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(StoragePaths.Open(AppMode.Live, directory.Path), gate);
        var handler = Rejected();
        using var session = new AirWaveSession(_ => handler, store);
        var result = await new AirWaveClient(session, new ReplaceableCredentials(), TimeProvider.System).ReadAsync(profiles.Current!, default);
        Assert.Contains(result.Batch.Errors, error => error.Category == CollectionErrorCategory.Storage);
        Assert.Equal(0, handler.Calls);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("synthetic-secret", json);
        Assert.DoesNotContain("SQLite", json);
        Assert.DoesNotContain(directory.Path, json);
    }

    [Fact]
    public async Task CredentialWriteThenProfileFailure_SurvivesRestartAndRequiresRegistration()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var credentials = new ReplaceableCredentials();
        var originalProfile = File.ReadAllBytes(Path.Combine(directory.Path, "profile.json"));
        File.Delete(Path.Combine(directory.Path, "profile.json"));
        Directory.CreateDirectory(Path.Combine(directory.Path, "profile.json"));
        using (var session = new AirWaveSession(_ => Rejected(), store))
        {
            var registration = new CredentialRegistration(profiles, session, credentials);
            var failure = await Record.ExceptionAsync(() => registration.RegisterAsync(new("new-user", "new-secret"), default));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal("new-user", credentials.Current.GetUsername());
            Assert.Equal(1, profiles.Current!.CredentialsVersion);
        }
        Directory.Delete(Path.Combine(directory.Path, "profile.json"));
        File.WriteAllBytes(Path.Combine(directory.Path, "profile.json"), originalProfile);
        var reopenedProfiles = await Profiles(directory.Path);
        var handler = Rejected();
        using var reopened = new AirWaveSession(_ => handler, new SqliteAuthenticationStateStore(location, gate));
        var client = new AirWaveClient(reopened, credentials, TimeProvider.System);
        var result = await client.ReadAsync(reopenedProfiles.Current!, default);
        Assert.Contains(result.Batch.Errors, error => error.Category == CollectionErrorCategory.CredentialUnavailable);
        Assert.False(result.Report.AuthenticationBlocked);
        Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<CredentialUnavailableException>(() => reopened.RetryAuthenticationAsync());
        await new CredentialRegistration(reopenedProfiles, reopened, credentials).RegisterAsync(new("fixed-user", "fixed-secret"), default);
        Assert.Equal(2, reopenedProfiles.Current!.CredentialsVersion);
        Assert.Equal(1, (await client.ReadAsync(reopenedProfiles.Current!, default)).Report.LoginRequests);
    }

    [Fact]
    public async Task NewRegistration_ActivatesNewVersionWithoutErasingOldStop()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        var old = profiles.Current!;
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(StoragePaths.Open(AppMode.Live, directory.Path), gate);
        await store.SaveStopAsync(new(AuthenticationBinding.From(old), AuthenticationStopReason.LoginRejected, DateTimeOffset.UtcNow), default);
        var handler = new AirWaveClientTests.ScriptedHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Post ? Login() : Ap()));
        var credentials = new ReplaceableCredentials();
        using var session = new AirWaveSession(_ => handler, store);
        await new CredentialRegistration(profiles, session, credentials).RegisterAsync(new("new-user", "new-secret"), default);
        var client = new AirWaveClient(session, credentials, TimeProvider.System);
        var result = await client.ReadAsync(profiles.Current!, default);
        Assert.Equal(RunOutcome.Succeeded, result.Batch.Outcome);
        Assert.Equal(1, result.Report.LoginRequests);
        Assert.NotNull((await store.ReadAsync(AuthenticationBinding.From(old), default)).Stop);
        Assert.Null((await store.ReadAsync(AuthenticationBinding.From(profiles.Current!), default)).Stop);
        var stale = await client.ReadAsync(old, default);
        Assert.Contains(stale.Batch.Errors, error => error.Category == CollectionErrorCategory.CredentialUnavailable);
        Assert.Equal(0, stale.Report.LoginRequests);
        Assert.False(session.AuthenticationBlocked);
    }

    [Fact]
    public async Task CancelledClear_LeavesDurableRejection()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(StoragePaths.Open(AppMode.Live, directory.Path), gate);
        var binding = AuthenticationBinding.From(profiles.Current!);
        await store.SaveStopAsync(new(binding, AuthenticationStopReason.LoginRejected, DateTimeOffset.UtcNow), default);
        using var session = new AirWaveSession(_ => Rejected(), store);
        await session.RestoreAuthenticationStateAsync(profiles.Current!);
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        var clear = session.RetryAuthenticationAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clear);
        gate.Release();
        Assert.True(session.AuthenticationBlocked);
        Assert.NotNull((await store.ReadAsync(binding, default)).Stop);
    }

    [Fact]
    public async Task Store_UsesWalAndNormalizesOriginWithoutClaimingSchemaVersion()
    {
        using var directory = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var binding = new AuthenticationBinding(location.DatasetId, new Uri("https://AIRWAVE.example.invalid:443/"), 7);
        await store.SaveStopAsync(new(binding, AuthenticationStopReason.LoginRejected, DateTimeOffset.UtcNow), default);
        var reopened = new SqliteAuthenticationStateStore(location, gate);
        var canonical = new AuthenticationBinding(location.DatasetId, new Uri("https://airwave.example.invalid/"), 7);
        Assert.NotNull((await reopened.ReadAsync(canonical, default)).Stop);
        using var connection = OpenSqlite(directory.Path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar());
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(0L, command.ExecuteScalar());
        SqliteAuthenticationStateStore.EnsureSchema(connection);
        Assert.NotNull((await reopened.ReadAsync(canonical, default)).Stop);
    }

    [Fact]
    public async Task LateOldLoginRejection_CannotBlockNewIdentity()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var oldSession = new AirWaveSession(_ => new AirWaveClientTests.ScriptedHandler((_, _) =>
        {
            requested.SetResult();
            return response.Task;
        }), store);
        var oldRead = new AirWaveClient(oldSession, new ReplaceableCredentials(), TimeProvider.System).ReadAsync(profiles.Current!, default);
        await requested.Task;
        var newProfile = profiles.Current! with { CredentialsVersion = 2 };
        var handler = new AirWaveClientTests.ScriptedHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Post ? Login() : Ap()));
        using var newSession = new AirWaveSession(_ => handler, store);
        var newClient = new AirWaveClient(newSession, new ReplaceableCredentials(), TimeProvider.System);
        Assert.Equal(RunOutcome.Succeeded, (await newClient.ReadAsync(newProfile, default)).Batch.Outcome);
        response.SetResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.True((await oldRead).Report.AuthenticationBlocked);
        Assert.NotNull((await store.ReadAsync(AuthenticationBinding.From(profiles.Current!), default)).Stop);
        Assert.Null((await store.ReadAsync(AuthenticationBinding.From(newProfile), default)).Stop);
        var after = await newClient.ReadAsync(newProfile, default);
        Assert.Equal(RunOutcome.Succeeded, after.Batch.Outcome);
        Assert.False(after.Report.AuthenticationBlocked);
        Assert.Equal(0, after.Report.LoginRequests);
    }

    [Fact]
    public async Task CancelledRegistrationBeforeCredentialWrite_PersistsGuardAndPreservesStop()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var binding = AuthenticationBinding.From(profiles.Current!);
        await store.SaveStopAsync(new(binding, AuthenticationStopReason.LoginRejected, DateTimeOffset.UtcNow), default);
        using var cancellation = new CancellationTokenSource();
        var cancelling = new CancelAfterIntentStore(store, cancellation);
        var credentials = new ReplaceableCredentials();
        using (var session = new AirWaveSession(_ => Rejected(), cancelling))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CredentialRegistration(profiles, session, credentials)
                .RegisterAsync(new("new-user", "new-secret"), cancellation.Token));
        Assert.Equal("old", credentials.Current.GetUsername());
        Assert.Equal(1, profiles.Current!.CredentialsVersion);
        var reopenedStore = new SqliteAuthenticationStateStore(location, gate);
        var state = await reopenedStore.ReadAsync(binding, default);
        Assert.NotNull(state.Stop);
        Assert.True(state.CredentialRegistrationPending);
        var handler = Rejected();
        using var reopened = new AirWaveSession(_ => handler, reopenedStore);
        await reopened.RestoreAuthenticationStateAsync(profiles.Current!);
        await Assert.ThrowsAsync<CredentialUnavailableException>(() => reopened.RetryAuthenticationAsync());
        Assert.Equal(0, (await new AirWaveClient(reopened, credentials, TimeProvider.System).ReadAsync(profiles.Current!, default)).Report.LoginRequests);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RegistrationIntentWriteFailure_DoesNotReplaceCredentialsOrReuseOldCookies()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var loginForms = new List<string>();
        var handler = new AirWaveClientTests.ScriptedHandler(async (request, _) =>
        {
            if (request.Method != HttpMethod.Post) return Ap();
            loginForms.Add(await request.Content!.ReadAsStringAsync());
            return Login();
        });
        var credentials = new ReplaceableCredentials();
        using var session = new AirWaveSession(_ => handler, store);
        var client = new AirWaveClient(session, credentials, TimeProvider.System);
        Assert.Equal(RunOutcome.Succeeded, (await client.ReadAsync(profiles.Current!, default)).Batch.Outcome);
        ExecuteSql(directory.Path, "CREATE TRIGGER refuse_auth_intent BEFORE INSERT ON authentication_state BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
        await Assert.ThrowsAsync<AuthenticationStateStoreException>(() => new CredentialRegistration(profiles, session, credentials)
            .RegisterAsync(new("new-user", "new-secret"), default));
        Assert.Equal("old", credentials.Current.GetUsername());
        Assert.Equal(1, profiles.Current!.CredentialsVersion);
        var result = await client.ReadAsync(profiles.Current!, default);
        Assert.Equal(RunOutcome.Succeeded, result.Batch.Outcome);
        Assert.Equal(1, result.Report.LoginRequests);
        Assert.Equal(2, loginForms.Count);
        Assert.All(loginForms, form => Assert.Contains("credential_0=old", form));
    }

    [Fact]
    public async Task RejectionWriteFailure_ReturnsStorageFailureAndKeepsProcessBlocked()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(StoragePaths.Open(AppMode.Live, directory.Path), gate);
        await store.ReadAsync(AuthenticationBinding.From(profiles.Current!), default);
        ExecuteSql(directory.Path, "CREATE TRIGGER refuse_auth_stop BEFORE INSERT ON authentication_state BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
        var handler = Rejected();
        using var session = new AirWaveSession(_ => handler, store);
        var client = new AirWaveClient(session, new ReplaceableCredentials(), TimeProvider.System);
        var result = await client.ReadAsync(profiles.Current!, default);
        Assert.Contains(result.Batch.Errors, error => error.Category == CollectionErrorCategory.Storage);
        Assert.True(result.Report.AuthenticationBlocked);
        Assert.Equal(0, (await client.ReadAsync(profiles.Current!, default)).Report.LoginRequests);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FailedStopWrite_RetriesPersistenceWithoutHttpWhenStorageRecovers()
    {
        using var directory = new TemporaryDirectory();
        var profiles = await Profiles(directory.Path);
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var binding = AuthenticationBinding.From(profiles.Current!);
        await store.ReadAsync(binding, default);
        ExecuteSql(directory.Path, "CREATE TRIGGER refuse_auth_stop BEFORE INSERT ON authentication_state BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
        var handler = Rejected();
        using (var session = new AirWaveSession(_ => handler, store))
        {
            var client = new AirWaveClient(session, new ReplaceableCredentials(), TimeProvider.System);
            Assert.Contains((await client.ReadAsync(profiles.Current!, default)).Batch.Errors, error => error.Category == CollectionErrorCategory.Storage);
            var originalTime = session.AuthenticationStop!.OccurredAtUtc;
            ExecuteSql(directory.Path, "DROP TRIGGER refuse_auth_stop;");
            var stillBlocked = await client.ReadAsync(profiles.Current!, default);
            Assert.Equal(0, stillBlocked.Report.LoginRequests);
            Assert.True(stillBlocked.Report.AuthenticationBlocked);
            var durable = await store.ReadAsync(binding, default);
            Assert.NotNull(durable.Stop);
            Assert.Equal(originalTime, durable.Stop.OccurredAtUtc);
        }
        using var reopened = new AirWaveSession(_ => handler, new SqliteAuthenticationStateStore(location, gate));
        Assert.Equal(0, (await new AirWaveClient(reopened, new ReplaceableCredentials(), TimeProvider.System).ReadAsync(profiles.Current!, default)).Report.LoginRequests);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class CancelAfterIntentStore(IAuthenticationStateStore inner, CancellationTokenSource caller) : IAuthenticationStateStore
    {
        public Task<AuthenticationState> ReadAsync(AuthenticationBinding binding, CancellationToken ct) => inner.ReadAsync(binding, ct);
        public Task SaveStopAsync(AuthenticationStop stop, CancellationToken ct) => inner.SaveStopAsync(stop, ct);
        public Task ClearStopAsync(AuthenticationBinding binding, CancellationToken ct) => inner.ClearStopAsync(binding, ct);
        public async Task MarkCredentialRegistrationPendingAsync(AuthenticationBinding binding, CancellationToken ct)
        {
            await inner.MarkCredentialRegistrationPendingAsync(binding, ct);
            caller.Cancel();
        }
    }

    private static SqliteConnection OpenSqlite(string directory)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "monitor.db"), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void ExecuteSql(string directory, string sql)
    {
        using var connection = OpenSqlite(directory);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static async Task<ProfileStore> Profiles(string path)
    {
        var profiles = new ProfileStore(StoragePaths.Open(AppMode.Live, path));
        if (profiles.Current is null)
            await profiles.UpdateAsync(new Uri("https://airwave.example.invalid"), 5, new(), 0, default);
        return profiles;
    }

    internal static AirWaveClientTests.ScriptedHandler Rejected() => new((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

    internal static HttpResponseMessage Login()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("synthetic") };
        response.Headers.Add("X-BISCOTTI", "synthetic-token");
        response.Headers.Add("Set-Cookie", "session=synthetic-cookie; Path=/; Secure");
        return response;
    }

    internal static HttpResponseMessage Ap() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(ApListParserTests.Xml(ApListParserTests.Ap()), Encoding.UTF8, "application/xml")
    };

    internal sealed class ReplaceableCredentials : ICredentialStore
    {
        public bool FailSave { get; set; }
        public AirWaveCredentials Current { get; private set; } = new("old", "old-secret");
        public Task SaveAsync(Guid datasetId, Uri origin, AirWaveCredentials credentials, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailSave) throw new IOException("synthetic save failure");
            Current = credentials;
            return Task.CompletedTask;
        }
        public Task<AirWaveCredentials?> ReadAsync(Guid datasetId, Uri origin, CancellationToken ct)
            => Task.FromResult<AirWaveCredentials?>(Current);
        public Task ClearAsync(Guid datasetId, Uri origin, CancellationToken ct) => Task.CompletedTask;
    }
}
