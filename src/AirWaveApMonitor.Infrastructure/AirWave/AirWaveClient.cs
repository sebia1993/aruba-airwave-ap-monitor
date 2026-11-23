using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Security;
namespace AirWaveApMonitor.Infrastructure.AirWave;

public sealed record AirWaveReadResult(ApCollectionBatch Batch, DiagnosticReport Report);
public sealed class AirWaveClient(AirWaveSession session, ICredentialStore credentials, TimeProvider clock) : IApSource
{
    public async Task<ApCollectionBatch> CollectAsync(CollectionContext context, CancellationToken ct)
    {
        if (context.Kind != CollectionKind.ApStatus)
            return ApListParser.Failure(CollectionErrorCategory.Unsupported, clock.GetUtcNow());
        using var deadline = new CancellationTokenSource(context.DeadlineUtc > clock.GetUtcNow() ? context.DeadlineUtc - clock.GetUtcNow() : TimeSpan.Zero, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        return (await ReadAsync(context.Profile, linked.Token)).Batch;
    }
    public async Task<AirWaveReadResult> ReadAsync(ProfileSnapshot profile, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var elapsed = Stopwatch.StartNew();
        var stages = ImmutableArray.CreateBuilder<DiagnosticStage>();
        var loginRequests = 0;
        var apRequests = 0;
        int? status = null;
        var tls = "not-observed";
        string? tlsFailureCode = null;
        TlsCertificateChecks? certificateChecks = null;
        HttpRequestOptions? requestOptions = null;
        var stage = "credentials";
        ApCollectionBatch batch;
        using var budget = new CancellationTokenSource(profile.Settings.RunTimeout, clock);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        var acquired = false;
        var authenticationBlocked = false;
        try
        {
            await session.Gate.WaitAsync(total.Token);
            acquired = true;
            await session.BindAsync(profile, total.Token);
            await session.PersistPendingStopAsync(total.Token);
            if (session.AuthenticationBlocked)
                throw new ReadFailure(CollectionErrorCategory.AuthenticationBlocked);
            if (session.CredentialRegistrationPending)
                throw new ReadFailure(CollectionErrorCategory.CredentialUnavailable);
            var secret = await credentials.ReadAsync(profile.DatasetId, profile.Origin, total.Token);
            if (secret is null)
                throw new ReadFailure(CollectionErrorCategory.Configuration);
            stages.Add(new(stage, "succeeded", null, elapsed.ElapsedMilliseconds, "실제 실행 계정에서 자격 증명 읽기 완료"));
            for (var attempt = 0; ; attempt++)
            {
                if (session.Token is null)
                {
                    stage = "login";
                    loginRequests++;
                    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(profile.Origin, "/LOGIN"));
                    request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "destination", "/index.html" }, { "credential_0", secret.GetUsername() }, { "credential_1", secret.GetPassword() } });
                    var response = await SendAsync(request, total.Token, isLogin: true);
                    var loginStatus = response.Status;
                    if (loginStatus is 401 or 403)
                        throw new ReadFailure(CollectionErrorCategory.AuthenticationRejected);
                    var safeLocation = response.Location is null || SameOrigin(profile.Origin, response.Location);
                    var acceptedStatus = loginStatus is >= 200 and < 300 || loginStatus is 302 or 303;
                    if (!acceptedStatus || !safeLocation || string.IsNullOrWhiteSpace(response.Token) || session.Cookies.GetCookies(new Uri(profile.Origin, "/ap_list.xml")).Count == 0)
                    {
                        // Public documentation does not establish an arbitrary 200 HTML body as expired authentication.
                        throw new ReadFailure(CollectionErrorCategory.InvalidResponse);
                    }
                    session.Token = response.Token;
                    stages.Add(new(stage, "succeeded", status, elapsed.ElapsedMilliseconds, "쿠키·X-BISCOTTI 수신 확인"));
                }
                stage = "ap-read";
                apRequests++;
                using var get = new HttpRequestMessage(HttpMethod.Get, new Uri(profile.Origin, "/ap_list.xml"));
                get.Headers.TryAddWithoutValidation("X-BISCOTTI", session.Token);
                var ap = await SendAsync(get, total.Token, stopOnExpiry: attempt > 0);
                var expired = IsExpired(ap.Status, ap.Location);
                if (expired)
                {
                    session.Token = null;
                    session.Cookies = new CookieContainer();
                    stages.Add(new(stage, "expired", status, elapsed.ElapsedMilliseconds, "확인된 세션 만료"));
                    if (attempt == 0)
                        continue;
                    throw new ReadFailure(CollectionErrorCategory.SessionExpired);
                }
                if (ap.Status == 403)
                    throw new ReadFailure(CollectionErrorCategory.Permission);
                if (ap.Status is < 200 or >= 300)
                    throw new ReadFailure(CollectionErrorCategory.Transport);
                stages.Add(new(stage, "succeeded", status, elapsed.ElapsedMilliseconds, "AP 응답 본문 읽기 완료"));
                stage = "parse";
                total.Token.ThrowIfCancellationRequested();
                batch = new ApListParser().Parse(new MemoryStream(ap.Body, false), clock.GetUtcNow(), total.Token);
                total.Token.ThrowIfCancellationRequested();
                stages.Add(new(stage, batch.Outcome.ToString().ToLowerInvariant(), status, elapsed.ElapsedMilliseconds, $"유효 AP {batch.Observations.Length} / 제외 {batch.ExcludedRecords.Length}"));
                break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { batch = Fail(CollectionErrorCategory.Timeout); }
        catch (ReadFailure failure) { batch = Fail(failure.Category); }
        catch (CredentialUnavailableException) { batch = Fail(CollectionErrorCategory.CredentialUnavailable); }
        catch (PlatformNotSupportedException) { batch = Fail(CollectionErrorCategory.Unsupported); }
        catch (ResponseLimitException) { batch = Fail(CollectionErrorCategory.ResponseTooLarge); }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.SecureConnectionError ||
            error.InnerException is AuthenticationException || requestOptions?.TryGetValue(AirWaveSession.CertificateFailureKey, out _) == true)
        {
            batch = FailTls();
        }
        catch (HttpRequestException) { batch = Fail(CollectionErrorCategory.Transport); }
        catch (AuthenticationException) { batch = FailTls(); }
        catch (AuthenticationStateStoreException) { batch = Fail(CollectionErrorCategory.Storage); }
        catch (IOException) { batch = Fail(CollectionErrorCategory.Transport); }
        catch (CookieException) { batch = Fail(CollectionErrorCategory.InvalidResponse); }
        finally { if (acquired) { authenticationBlocked = session.AuthenticationBlocked; session.Gate.Release(); } }
        return new(batch, new(Guid.NewGuid(), started, ExecutionIdentity.Current, ConnectionPath(profile.Proxy), tls, status, loginRequests, apRequests, batch.Observations.Length, batch.ExcludedRecords.Length, elapsed.ElapsedMilliseconds, batch.Outcome, authenticationBlocked, stages.ToImmutable(), batch.Errors,
            [$"미검증 필드 {batch.Observations.SelectMany(ap => ap.Fields).Where(field => field.Value.Availability == FieldAvailability.Unverified).Select(field => field.Key).Distinct(StringComparer.Ordinal).Count()}종: 실제 지원·단위 확인 필요.", "문서·fixture 기반 어댑터. 실제 AirWave 버전·계정·필드는 현장 미검증.", "원천 숫자 시각의 단위 미확인: 원문 값만 보존하며 장애 시각으로 사용하지 않음.", "이력 DB·운영 RunId·장애/복구 기록은 아직 제공하지 않음."]) { TlsFailureCode = tlsFailureCode, TlsCertificateChecks = certificateChecks });

        ApCollectionBatch FailTls()
        {
            AirWaveSession.CertificateFailure? certificateFailure = null;
            requestOptions?.TryGetValue(AirWaveSession.CertificateFailureKey, out certificateFailure);
            tls = "failed";
            tlsFailureCode = certificateFailure?.Code ?? "tls-handshake-or-certificate";
            certificateChecks = certificateFailure?.Checks ?? TlsCertificateChecks.Unknown;
            var message = certificateChecks is { ServerName: "mismatch", TrustChain: "failed" }
                ? "TLS 인증서 서버 이름 불일치 및 신뢰 체인 실패. 접속 이름과 인증서 체인을 함께 확인하세요. HTTP_STATUS=미수신"
                : TlsCertificateDiagnostics.SafeMessage(tlsFailureCode);
            return Fail(CollectionErrorCategory.Tls, message);
        }

        ApCollectionBatch Fail(CollectionErrorCategory category, string? message = null)
        {
            var safeMessage = message ?? SafeMessages.For(category);
            stages.Add(new(stage, "failed", status, elapsed.ElapsedMilliseconds, safeMessage));
            var failed = ApListParser.Failure(category, clock.GetUtcNow());
            return message is null ? failed : failed with { Errors = [new(category, 1, safeMessage)] };
        }
        bool IsExpired(int responseStatus, Uri? location) => responseStatus == 401 ||
            responseStatus is 301 or 302 or 303 or 307 or 308 && location is not null &&
            SameOrigin(profile.Origin, location) && location.AbsolutePath == "/LOGIN";

        async Task<WireResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellation,
            bool isLogin = false, bool stopOnExpiry = false)
        {
            status = null;
            tls = "not-observed";
            requestOptions = request.Options;
            certificateChecks = TlsCertificateChecks.Unknown;
            using var requestBudget = new CancellationTokenSource(profile.Settings.RequestTimeout, clock);
            using var requestToken = CancellationTokenSource.CreateLinkedTokenSource(cancellation, requestBudget.Token);
            var cookie = session.Cookies.GetCookieHeader(request.RequestUri!);
            if (cookie.Length > 0)
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await session.Client!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken.Token);
            status = (int)response.StatusCode;
            tls = "validated";
            certificateChecks = TlsCertificateChecks.Validated;
            // A confirmed rejection belongs to the received headers. Body IO, timeout,
            // size failure, or caller cancellation must not erase that durable evidence.
            if (isLogin && status is 401 or 403)
                await session.StopAuthenticationAsync(AuthenticationStopReason.LoginRejected, clock.GetUtcNow());
            var location = response.Headers.Location;
            if (location is not null && !location.IsAbsoluteUri)
                location = new Uri(profile.Origin, location);
            if (stopOnExpiry && IsExpired(status.Value, location))
                await session.StopAuthenticationAsync(AuthenticationStopReason.RepeatedSessionExpiry, clock.GetUtcNow());
            // All bodies, including failed login/error responses, share the decompressed byte and read-time budget.
            await using var body = await response.Content.ReadAsStreamAsync(requestToken.Token);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var n = await body.ReadAsync(buffer, requestToken.Token);
                if (n == 0)
                    break;
                if (output.Length + n > profile.Settings.MaximumDecompressedResponseBytes)
                    throw new ResponseLimitException();
                await output.WriteAsync(buffer.AsMemory(0, n), requestToken.Token);
            }
            requestToken.Token.ThrowIfCancellationRequested();
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var value in cookies)
                    session.Cookies.SetCookies(request.RequestUri!, value);
            string? token = null;
            if (response.Headers.TryGetValues("X-BISCOTTI", out var tokens))
            {
                var values = tokens.ToArray();
                if (values.Length == 1 && values[0].Length <= 8192)
                    token = values[0];
            }
            return new(status.Value, output.ToArray(), token, location);
        }
    }
    private static bool SameOrigin(Uri origin, Uri target) => target.Scheme == origin.Scheme && target.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) && target.Port == origin.Port && target.UserInfo.Length == 0;
    internal static string ConnectionPath(ProxySettings proxy) => proxy.Mode switch { ProxyMode.Direct => "direct", ProxyMode.Fixed => "fixed-proxy", _ => "system-execution-account" };
    private sealed record WireResponse(int Status, byte[] Body, string? Token, Uri? Location);
    private sealed class ReadFailure(CollectionErrorCategory category) : Exception
    {
        public CollectionErrorCategory Category { get; } = category;
    }
}
