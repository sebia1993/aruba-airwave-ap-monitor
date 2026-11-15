using System.Net;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Tests.AirWave;
using AirWaveApMonitor.Tests.Support;

namespace AirWaveApMonitor.Tests.Security;

public sealed class AuthenticationHeaderFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "io")]
    [InlineData(HttpStatusCode.Forbidden, "io")]
    [InlineData(HttpStatusCode.Unauthorized, "timeout")]
    [InlineData(HttpStatusCode.Forbidden, "timeout")]
    [InlineData(HttpStatusCode.Unauthorized, "size")]
    [InlineData(HttpStatusCode.Forbidden, "size")]
    [InlineData(HttpStatusCode.Unauthorized, "cancel")]
    [InlineData(HttpStatusCode.Forbidden, "cancel")]
    public async Task RejectedLoginHeaders_StopSurvivesBodyFailureAndRestart(HttpStatusCode status, string failure)
    {
        using var directory = new TemporaryDirectory();
        var profile = (await AuthenticationPersistenceTests.Profiles(directory.Path)).Current!;
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var clock = new ManualTimeProvider();
        var rejectedAt = clock.GetUtcNow();
        using var caller = new CancellationTokenSource();
        var bodyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AirWaveClientTests.ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StreamContent(new FailingBody(failure, bodyEntered))
        }));
        using (var session = new AirWaveSession(_ => handler, store))
        {
            var pending = new AirWaveClient(session, new AuthenticationPersistenceTests.ReplaceableCredentials(), clock)
                .ReadAsync(profile, caller.Token);
            if (failure is "timeout" or "cancel")
            {
                await bodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (failure == "timeout") clock.Advance(TimeSpan.FromSeconds(45));
                else caller.Cancel();
            }
            if (failure == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            else
            {
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
                var category = failure switch
                {
                    "io" => CollectionErrorCategory.Transport,
                    "timeout" => CollectionErrorCategory.Timeout,
                    _ => CollectionErrorCategory.ResponseTooLarge
                };
                Assert.Contains(result.Batch.Errors, error => error.Category == category);
                Assert.Equal((int)status, result.Report.HttpStatus);
                Assert.DoesNotContain("synthetic-body-secret", JsonSerializer.Serialize(result));
            }
        }
        var reopenedStore = new SqliteAuthenticationStateStore(location, gate);
        var state = await reopenedStore.ReadAsync(AuthenticationBinding.From(profile), default);
        Assert.NotNull(state.Stop);
        Assert.Equal(AuthenticationStopReason.LoginRejected, state.Stop.Reason);
        Assert.Equal(rejectedAt, state.Stop.OccurredAtUtc);
        var restartedHandler = AuthenticationPersistenceTests.Rejected();
        using var restarted = new AirWaveSession(_ => restartedHandler, reopenedStore);
        var next = await new AirWaveClient(restarted, new AuthenticationPersistenceTests.ReplaceableCredentials(), clock).ReadAsync(profile, default);
        Assert.True(next.Report.AuthenticationBlocked);
        Assert.Equal(0, next.Report.LoginRequests);
        Assert.Equal(0, restartedHandler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedExpiryHeaders_StopSurvivesSecondExpiredBodyFailure(bool redirect)
    {
        using var directory = new TemporaryDirectory();
        var profile = (await AuthenticationPersistenceTests.Profiles(directory.Path)).Current!;
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        var handler = new AirWaveClientTests.ScriptedHandler((_, number) =>
        {
            if (number is 1 or 3) return Task.FromResult(AuthenticationPersistenceTests.Login());
            var response = new HttpResponseMessage(redirect ? HttpStatusCode.Redirect : HttpStatusCode.Unauthorized);
            if (redirect) response.Headers.Location = new Uri("/LOGIN", UriKind.Relative);
            if (number == 4) response.Content = new StreamContent(new FailingBody("io", new TaskCompletionSource()));
            return Task.FromResult(response);
        });
        using (var session = new AirWaveSession(_ => handler, store))
        {
            var result = await new AirWaveClient(session, new AuthenticationPersistenceTests.ReplaceableCredentials(), TimeProvider.System).ReadAsync(profile, default);
            Assert.Equal(2, result.Report.LoginRequests);
            Assert.Equal(2, result.Report.ApRequests);
            Assert.Contains(result.Batch.Errors, error => error.Category == CollectionErrorCategory.Transport);
        }
        var reopened = new SqliteAuthenticationStateStore(location, gate);
        Assert.Equal(AuthenticationStopReason.RepeatedSessionExpiry, (await reopened.ReadAsync(AuthenticationBinding.From(profile), default)).Stop?.Reason);
        var noHttp = AuthenticationPersistenceTests.Rejected();
        using var nextSession = new AirWaveSession(_ => noHttp, reopened);
        Assert.Equal(0, (await new AirWaveClient(nextSession, new AuthenticationPersistenceTests.ReplaceableCredentials(), TimeProvider.System).ReadAsync(profile, default)).Report.LoginRequests);
        Assert.Equal(0, noHttp.Calls);
    }

    private sealed class FailingBody(string failure, TaskCompletionSource entered) : Stream
    {
        private long remaining = 64L * 1024 * 1024 + 1;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            entered.TrySetResult();
            if (failure == "io") throw new IOException("synthetic-body-secret");
            if (failure is "timeout" or "cancel")
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(remaining, buffer.Length);
            buffer.Span[..count].Fill((byte)'x');
            remaining -= count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
