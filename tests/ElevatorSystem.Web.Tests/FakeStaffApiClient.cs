using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests;

/// <summary>A staff API that answers from memory and records every call. Tests set what it returns.</summary>
internal sealed class FakeStaffApiClient : IStaffApiClient
{
    public static readonly StaffSessionDto Admin = new("admin@elevator.test", [StaffRole.Admin], TwoFactorEnabled: true, TwoFactorVerified: true);
    public static readonly StaffSessionDto Auditor = new("auditor@elevator.test", [StaffRole.Auditor], TwoFactorEnabled: true, TwoFactorVerified: true);
    public static readonly StaffSessionDto PasswordOnly = new("admin@elevator.test", [StaffRole.Admin], TwoFactorEnabled: false, TwoFactorVerified: false);

    /// <summary>The session the server reports; <see langword="null"/> when nobody is signed in.</summary>
    public StaffSessionDto? Session { get; set; }

    /// <summary>Answers to sign-in attempts, in order; the last one repeats.</summary>
    public Queue<StaffResult<StaffSessionDto>> SignInAnswers { get; } = new();

    public StaffResult<TwoFactorSetupDto> Setup { get; set; } = StaffResult.Ok(new TwoFactorSetupDto(
        "jbsw y3dp ehpk 3pxp jbsw y3dp ehpk 3pxp", "otpauth://totp/ElevatorSystem:admin%40elevator.test?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=ElevatorSystem&digits=6"));

    public StaffResult<RecoveryCodesDto> Enable { get; set; } = StaffResult.Ok(new RecoveryCodesDto(["AAAAA-11111", "BBBBB-22222"]));

    public Queue<StaffResult<ServiceActionAcceptedResponse>> ServiceAnswers { get; } = new();

    public BuildingOccupancyDto? Occupancy { get; set; }

    public List<AuditPageDto> AuditPages { get; } = [];

    public StaffResult<AuditVerificationDto> Verification { get; set; } =
        StaffResult.Ok(new AuditVerificationDto(true, 4, 4, new string('a', 64), null, null, TestData.Noon));

    public List<SignInRequest> SignIns { get; } = [];

    public List<string> EnableCodes { get; } = [];

    public List<(ServiceAction Action, Guid Key)> ServiceActions { get; } = [];

    public List<long?> AuditPageRequests { get; } = [];

    public int SignOuts { get; private set; }

    public Task<StaffResult<StaffSessionDto>> GetSessionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Session is { } session ? StaffResult.Ok(session) : StaffResult.Fail<StaffSessionDto>(StaffFailure.SessionExpired));

    public Task<StaffResult<StaffSessionDto>> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default)
    {
        SignIns.Add(request);
        var answer = SignInAnswers.Count > 1 ? SignInAnswers.Dequeue() : SignInAnswers.Peek();
        if (answer.Value is { } session)
        {
            Session = session;
        }

        return Task.FromResult(answer);
    }

    public Task<StaffResult<bool>> SignOutAsync(CancellationToken cancellationToken = default)
    {
        SignOuts++;
        Session = null;
        return Task.FromResult(StaffResult.Ok(true));
    }

    public Task<StaffResult<TwoFactorSetupDto>> SetUpTwoFactorAsync(CancellationToken cancellationToken = default) => Task.FromResult(Setup);

    public Task<StaffResult<RecoveryCodesDto>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        EnableCodes.Add(code);
        if (Enable.Succeeded && Session is { } session)
        {
            Session = session with { TwoFactorEnabled = true, TwoFactorVerified = true };
        }

        return Task.FromResult(Enable);
    }

    public Task<StaffResult<ServiceActionAcceptedResponse>> RequestServiceActionAsync(
        ServiceAction action, Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        ServiceActions.Add((action, idempotencyKey));
        return Task.FromResult(ServiceAnswers.Count > 0
            ? ServiceAnswers.Dequeue()
            : StaffResult.Ok(new ServiceActionAcceptedResponse(Guid.NewGuid(), action)));
    }

    public Task<StaffResult<BuildingOccupancyDto>> GetOccupancyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Occupancy is { } occupancy ? StaffResult.Ok(occupancy) : StaffResult.Fail<BuildingOccupancyDto>(StaffFailure.Unavailable));

    public Task<StaffResult<AuditPageDto>> GetAuditPageAsync(long? before, CancellationToken cancellationToken = default)
    {
        AuditPageRequests.Add(before);
        var page = AuditPages.Find(candidate => before is null || (candidate.Entries.Count > 0 && candidate.Entries[0].Sequence < before))
            ?? new AuditPageDto([], null);
        return Task.FromResult(StaffResult.Ok(page));
    }

    public Task<StaffResult<AuditVerificationDto>> VerifyAuditAsync(CancellationToken cancellationToken = default) => Task.FromResult(Verification);

    public static BuildingOccupancyDto OccupancyOf(int inCar, params (int Floor, int Waiting, int Present)[] floors) => new(
        inCar,
        8,
        [.. Enumerable.Range(1, 10).Select(number =>
            floors.FirstOrDefault(floor => floor.Floor == number) is { Floor: > 0 } found
                ? new FloorOccupancyDto(number, found.Waiting, found.Present)
                : new FloorOccupancyDto(number, 0, 0))],
        12,
        TimeSpan.FromSeconds(7.5),
        TestData.Noon);

    public static AuditEntryDto Entry(long sequence, string action = "staff.sign-in", string outcome = "succeeded", string? actor = "admin@elevator.test", bool anonymised = false) =>
        new(sequence, TestData.Noon.AddMinutes(sequence), action, "", outcome, actor, "k1:00", anonymised);
}
