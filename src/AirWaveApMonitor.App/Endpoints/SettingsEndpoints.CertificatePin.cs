using System.Text.Json;
using System.Text.Json.Serialization;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.Settings;

namespace AirWaveApMonitor.App.Endpoints;

public static partial class SettingsEndpoints
{
    private static void MapCertificatePinDiscovery(WebApplication app)
    {
        app.MapPost("/api/settings/certificate-pin/probe", (HttpContext http, ProfileStore profiles, RuntimeOptions runtime, TimeProvider clock) =>
            GuardPinRequest(profiles, http, async () =>
            {
                var input = await http.Request.ReadFromJsonAsync<ProbePinInput>(http.RequestAborted) ?? throw new JsonException();
                var profile = RequirePinTarget(runtime, profiles, input.ExpectedConfigurationVersion);
                var sha256 = await CertificatePinProbe.ProbeAsync(profile, http.RequestAborted);
                var status = profile.TemporaryCertificatePin switch
                {
                    null => "unregistered",
                    var pin when !pin.MatchesSha256(sha256) => "changed",
                    { ExpiresAtUtc: null } => "persistent",
                    var pin when pin.IsExpiredAt(clock.GetUtcNow()) => "expired",
                    _ => "matched"
                };
                return Results.Ok(new { profile.Origin, sha256, status, profile.ConfigurationVersion });
            }));

        app.MapPost("/api/settings/certificate-pin/persistent",
            (HttpContext http, ProfileStore profiles, RuntimeOptions runtime, TimeProvider clock, ICredentialStore credentials, AirWaveSession session) =>
            GuardPinRequest(profiles, http, async () =>
            {
                var input = await http.Request.ReadFromJsonAsync<PersistentPinInput>(http.RequestAborted) ?? throw new JsonException();
                var profile = RequirePinTarget(runtime, profiles, input.ExpectedConfigurationVersion);
                if (input.ObservedSha256 is not { Length: 64 } || !input.ObservedSha256.All(Uri.IsHexDigit))
                    return Results.BadRequest(new { message = "인증서를 자동 조회한 뒤 다시 시도하세요." });

                // Inspect a fresh connection under the same gate that protects the profile and its proxy settings.
                var current = await CertificatePinProbe.ProbeAsync(profile, http.RequestAborted);
                if (!current.Equals(input.ObservedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new ProfileConflictException("조회 이후 서버 인증서가 변경됐습니다. 인증서를 다시 조회하세요.");
                await profiles.EnablePersistentCertificatePinAsync(current, input.IndependentlyVerifiedSha256,
                    input.ExpectedConfigurationVersion, clock.GetUtcNow(), http.RequestAborted);
                return Results.Ok(await View(profiles, credentials, runtime, session, http.RequestAborted));
            }));
    }

    private static ProfileSnapshot RequirePinTarget(RuntimeOptions runtime, ProfileStore profiles, long expectedVersion)
    {
        if (runtime.Mode != AppMode.Live || profiles.Current is not { } profile ||
            profile.Origin.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new ArgumentException("Live 모드에서 AirWave HTTPS IP·포트를 먼저 저장하세요.");
        if (profile.ConfigurationVersion != expectedVersion)
            throw new ProfileConflictException("설정이 변경됐습니다. 현재 설정을 다시 읽고 인증서를 조회하세요.");
        return profile;
    }

    private static Task<IResult> GuardPinRequest(ProfileStore profiles, HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Guard(profiles, http, async () =>
        {
            try { return await action(); }
            catch (JsonException) { return Results.BadRequest(new { message = "설정 버전과 인증서 조회 입력값을 확인하세요." }); }
            catch (ArgumentException error) { return Results.BadRequest(new { message = error.Message }); }
            catch (CertificatePinProbeException error) { return Results.Json(new { message = error.Message }, statusCode: StatusCodes.Status502BadGateway); }
        }, waitForGate: true);
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ProbePinInput([property: JsonRequired] long ExpectedConfigurationVersion);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PersistentPinInput([property: JsonRequired] long ExpectedConfigurationVersion,
        [property: JsonRequired] string? ObservedSha256, string? IndependentlyVerifiedSha256);
}
