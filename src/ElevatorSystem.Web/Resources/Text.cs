namespace ElevatorSystem.Web.Resources;

/// <summary>
/// Keys of <see cref="SharedResource"/>. Every user-facing string goes through these constants, and a test
/// checks that each one exists in both the en-US and the pt-BR .resx files.
/// </summary>
internal static class Text
{
    public const string RefusedInvalidFloor = nameof(RefusedInvalidFloor);
    public const string RefusedInvalidDirection = nameof(RefusedInvalidDirection);
    public const string RefusedOutOfService = nameof(RefusedOutOfService);
    public const string RefusedBusy = nameof(RefusedBusy);
    public const string RefusedTooManyRequests = nameof(RefusedTooManyRequests);
    public const string RefusedUnavailable = nameof(RefusedUnavailable);
    public const string RefusedUnexpected = nameof(RefusedUnexpected);
}
