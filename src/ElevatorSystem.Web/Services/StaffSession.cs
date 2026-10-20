using ElevatorSystem.Contracts;

namespace ElevatorSystem.Web.Services;

/// <summary>
/// Who is signed in to the admin view. The server decides everything; this only remembers the last answer so the
/// view knows which step to show, and reconnects the hub when the session changes so the server re-evaluates whether
/// the connection may receive staff-only messages (ADR-0022).
/// </summary>
public sealed class StaffSession(IStaffApiClient api, IElevatorHubClient hub, ElevatorStateStore store)
{
    public event EventHandler? Changed;

    /// <summary>The session, or <see langword="null"/> when nobody is signed in.</summary>
    public StaffSessionDto? Current { get; private set; }

    /// <summary>The server has been asked at least once.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Signed in with the second factor: the staff features are available.</summary>
    public bool IsVerified => Current is { TwoFactorVerified: true };

    public bool IsAdmin => IsVerified && Current!.Roles.Contains(StaffRole.Admin);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var result = await api.GetSessionAsync(cancellationToken);
        Current = result.Value;
        IsLoaded = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records a new session (or none, after signing out) and reconnects the hub under it.</summary>
    public async Task ChangeAsync(StaffSessionDto? session, CancellationToken cancellationToken = default)
    {
        Current = session;
        IsLoaded = true;
        if (!IsVerified)
        {
            store.ClearOccupancy();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await hub.RestartAsync(cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await api.SignOutAsync(cancellationToken);
        await ChangeAsync(null, cancellationToken);
    }
}
