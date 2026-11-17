using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Infrastructure.Security;

/// <summary>Explicitly ephemeral state for Demo and adapter-only tests. Live uses SQLite.</summary>
public sealed class MemoryAuthenticationStateStore : IAuthenticationStateStore
{
    private readonly Dictionary<AuthenticationBinding, AuthenticationState> states = [];
    private readonly object gate = new();

    public Task<AuthenticationState> ReadAsync(AuthenticationBinding binding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(Get(binding));
    }

    public Task SaveStopAsync(AuthenticationStop stop, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) states[stop.Binding] = Get(stop.Binding) with { Stop = stop };
        return Task.CompletedTask;
    }

    public Task ClearStopAsync(AuthenticationBinding binding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) states[binding] = Get(binding) with { Stop = null };
        return Task.CompletedTask;
    }

    public Task MarkCredentialRegistrationPendingAsync(AuthenticationBinding binding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) states[binding] = Get(binding) with { CredentialRegistrationPending = true };
        return Task.CompletedTask;
    }

    private AuthenticationState Get(AuthenticationBinding binding) => states.GetValueOrDefault(binding) ?? new(null, false);
}
