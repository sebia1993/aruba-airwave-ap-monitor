using System.Net;
using System.Text;
using System.Threading.Channels;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Tests.Support;
namespace AirWaveApMonitor.Tests.AirWave;

public sealed class ReadBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FortyFiveSeconds_IncludesBodyAfterHeaders_ForLoginAndAp(bool stallAp)
    {
        var clock = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new AirWaveClientTests.ScriptedHandler((_, n) => Task.FromResult(stallAp && n == 1 ? Login() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PausedBody(entered)) }));
        using var s = new AirWaveSession(_ => h);
        var pending = new AirWaveClient(s, new Credentials(), clock).ReadAsync(AirWaveClientTests.Profile(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(44));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(result.Batch.Errors, e => e.Category == CollectionErrorCategory.Timeout);
        Assert.Equal(stallAp ? 1 : 0, result.Report.ApRequests);
    }
    [Fact]
    public async Task OneHundredTwentySeconds_IsSharedAcrossLoginRetryAndReads()
    {
        var clock = new ManualTimeProvider();
        var entered = Channel.CreateUnbounded<bool>();
        var h = new AirWaveClientTests.ScriptedHandler((_, n) => { var r = n is 1 or 3 ? Login() : new HttpResponseMessage(HttpStatusCode.Unauthorized); r.Content = new StreamContent(new DelayedBody(clock, entered.Writer)); return Task.FromResult(r); });
        using var s = new AirWaveSession(_ => h);
        var pending = new AirWaveClient(s, new Credentials(), clock).ReadAsync(AirWaveClientTests.Profile(), default);
        for (var request = 0; request < 3; request++)
        {
            await entered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(40));
        }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, result.Report.LoginRequests);
        Assert.Equal(1, result.Report.ApRequests);
        Assert.Contains(result.Batch.Errors, e => e.Category == CollectionErrorCategory.Timeout);
        Assert.False(s.AuthenticationBlocked);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedLoginAndApErrorBodies_AreBoundedBeforeParsing(bool ap)
    {
        var h = new AirWaveClientTests.ScriptedHandler((_, n) => Task.FromResult(ap && n == 1 ? Login() : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(new OversizedBody()) }));
        using var s = new AirWaveSession(_ => h);
        var result = await new AirWaveClient(s, new Credentials(), TimeProvider.System).ReadAsync(AirWaveClientTests.Profile(), default);
        Assert.Contains(result.Batch.Errors, e => e.Category == CollectionErrorCategory.ResponseTooLarge);
        Assert.Equal(ap ? 1 : 0, result.Report.ApRequests);
    }
    [Fact]
    public async Task UnknownHtml200_DoesNotTriggerReloginOrAuthLatch()
    {
        var h = new AirWaveClientTests.ScriptedHandler((_, n) => Task.FromResult(n == 1 ? Login() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>unknown failure</html>") }));
        using var s = new AirWaveSession(_ => h);
        var r = await new AirWaveClient(s, new Credentials(), TimeProvider.System).ReadAsync(AirWaveClientTests.Profile(), default);
        Assert.Equal(1, r.Report.LoginRequests);
        Assert.Equal(1, r.Report.ApRequests);
        Assert.False(r.Report.AuthenticationBlocked);
        Assert.Contains(r.Batch.Errors, e => e.Category == CollectionErrorCategory.InvalidResponse);
    }
    [Fact]
    public void Parser_CancellationStopsLargeInputBeforePublishingObservations()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new ApListParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(ApListParserTests.Xml(ApListParserTests.Ap()))), DateTimeOffset.UtcNow, cancel.Token));
    }
    [Fact]
    public void UnknownSourceFields_RemainUnverifiedWithoutInventingValues()
    {
        var result = ApListParserTests.Parse(ApListParserTests.Xml(ApListParserTests.Ap(extra: "<future_field>unconfirmed</future_field>")));
        Assert.Equal(FieldAvailability.Unverified, Assert.Single(result.Observations).Fields["future_field"].Availability);
    }
    private static HttpResponseMessage Login()
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("synthetic") };
        r.Headers.Add("X-BISCOTTI", "token");
        r.Headers.Add("Set-Cookie", "session=cookie; Path=/; Secure");
        return r;
    }
    private sealed class Credentials : ICredentialStore
    {
        public Task<AirWaveCredentials?> ReadAsync(Guid d, Uri o, CancellationToken c) => Task.FromResult<AirWaveCredentials?>(new("test", "test")); public Task SaveAsync(Guid d, Uri o, AirWaveCredentials v, CancellationToken c) => Task.CompletedTask; public Task ClearAsync(Guid d, Uri o, CancellationToken c) => Task.CompletedTask;
    }
    private abstract class Body : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position
        {
            get => 0; set => throw new NotSupportedException();
        }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long l) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
    private sealed class PausedBody(TaskCompletionSource entered) : Body
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }
    private sealed class DelayedBody(TimeProvider clock, ChannelWriter<bool> entered) : Body
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(40), clock, ct);
            entered.TryWrite(true);
            await delay;
            return 0;
        }
    }
    private sealed class OversizedBody : Body
    {
        private long remaining = 64L * 1024 * 1024 + 1; public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var n = (int)Math.Min(remaining, buffer.Length);
            buffer.Span[..n].Fill((byte)'x');
            remaining -= n;
            return ValueTask.FromResult(n);
        }
    }
}
