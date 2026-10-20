using System.Net;
using System.Net.Http.Json;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Api.Tests.Staff;

/// <summary>The configured test account (ADR-0030): password only, every staff capability, and never anyone else.</summary>
public sealed class TestAccountTests
{
    private static readonly StaffAccount TestAdmin = AuthorizationMatrixTests.TestAdmin;

    [Fact]
    public async Task TestAccount_SignsInWithItsPasswordOnly_AndTheSessionSaysSo()
    {
        await using var factory = WithTestAccount(new ApiFactory(), TestAdmin);
        using var client = StaffClient.Anonymous(factory);

        using var response = await client.SignInAsync(TestAdmin.Email, TestAdmin.Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.StaffSessionDto, Http.Cancellation);
        session.ShouldNotBeNull();
        session.TestAccount.ShouldBeTrue();
        session.TwoFactorVerified.ShouldBeFalse("no second factor was used, and the session must not pretend otherwise");
        session.Roles.ShouldBe([StaffRole.Admin]);
        using var current = await client.GetAsync(ApiRoutes.Session);
        (await current.Content.ReadFromJsonAsync(ContractsJsonContext.Default.StaffSessionDto, Http.Cancellation))!.TestAccount.ShouldBeTrue();
        using var occupancy = await client.GetAsync(ApiRoutes.Occupancy);
        occupancy.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task AnExistingAccount_NamedAsTheTestAccount_IsNeverExempted()
    {
        // The setting names the real admin: that account was not created as the test account, so it keeps its rules.
        await using var factory = WithTestAccount(new ApiFactory(), StaffAccounts.Admin);
        using var client = StaffClient.Anonymous(factory);

        using var response = await client.SignInAsync(StaffAccounts.Admin.Email, StaffAccounts.Admin.Password);

        (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.StaffSessionDto, Http.Cancellation))!.TestAccount.ShouldBeFalse();
        using var occupancy = await client.GetAsync(ApiRoutes.Occupancy);
        occupancy.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OtherAccounts_StillNeedTheSecondFactor_WhileATestAccountIsEnabled()
    {
        await using var factory = WithTestAccount(new ApiFactory(), TestAdmin);
        using var client = await StaffClient.SignedInWithPasswordAsync(factory, StaffAccounts.Admin);

        using var occupancy = await client.GetAsync(ApiRoutes.Occupancy);

        occupancy.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TestAccount_WithAWrongPassword_IsRefused()
    {
        await using var factory = WithTestAccount(new ApiFactory(), TestAdmin);
        using var client = StaffClient.Anonymous(factory);

        using var response = await client.SignInAsync(TestAdmin.Email, "not the test passphrase");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EnablingATestAccount_IsLoggedAsAWarning_WithoutItsEmail()
    {
        await using var factory = WithTestAccount(new ApiFactory(), TestAdmin);
        using var client = await StaffClient.SignedInWithPasswordAsync(factory, TestAdmin);

        var warning = factory.Services.GetFakeLogCollector().GetSnapshot().Where(record => record.Id.Id == 3015).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldNotContain(TestAdmin.Email);
    }

    private static ApiFactory WithTestAccount(ApiFactory factory, StaffAccount account) => factory
        .WithSetting("Staff:TestAccount:Email", account.Email)
        .WithSetting("Staff:TestAccount:Password", account.Password);
}
