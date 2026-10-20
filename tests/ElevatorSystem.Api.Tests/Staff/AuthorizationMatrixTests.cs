using System.Net;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ElevatorSystem.Api.Tests.Staff;

/// <summary>
/// Who may call what (ADR-0022, api-backend skill): every protected endpoint against every kind of caller. A
/// password-only session can register its second factor and nothing more.
/// </summary>
public sealed class AuthorizationMatrixTests : IAsyncLifetime
{
    public enum Caller
    {
        Anonymous,
        AdminWithPasswordOnly,
        AuditorWithTwoFactors,
        AdminWithTwoFactors,
    }

    private const HttpStatusCode Allowed = HttpStatusCode.OK;

    private static readonly StaffAccount SecondAdmin = new("second.admin@elevator.test", "a second admin passphrase", StaffRole.Admin);

    private readonly ApiFactory _factory = new ApiFactory()
        .WithSetting("Staff:Accounts:2:Email", SecondAdmin.Email)
        .WithSetting("Staff:Accounts:2:Password", SecondAdmin.Password)
        .WithSetting("Staff:Accounts:2:Role", "Admin");
    private readonly Dictionary<Caller, StaffClient> _clients = [];

    public static TheoryData<string, string, Caller, HttpStatusCode> Matrix => new()
    {
        { "GET", ApiRoutes.Occupancy, Caller.Anonymous, HttpStatusCode.Unauthorized },
        { "GET", ApiRoutes.Occupancy, Caller.AdminWithPasswordOnly, HttpStatusCode.Forbidden },
        { "GET", ApiRoutes.Occupancy, Caller.AuditorWithTwoFactors, Allowed },
        { "GET", ApiRoutes.Occupancy, Caller.AdminWithTwoFactors, Allowed },

        { "POST", ApiRoutes.ServiceActions, Caller.Anonymous, HttpStatusCode.Unauthorized },
        { "POST", ApiRoutes.ServiceActions, Caller.AdminWithPasswordOnly, HttpStatusCode.Forbidden },
        { "POST", ApiRoutes.ServiceActions, Caller.AuditorWithTwoFactors, HttpStatusCode.Forbidden },
        { "POST", ApiRoutes.ServiceActions, Caller.AdminWithTwoFactors, HttpStatusCode.Accepted },

        { "POST", ApiRoutes.Scenarios, Caller.Anonymous, HttpStatusCode.Unauthorized },
        { "POST", ApiRoutes.Scenarios, Caller.AdminWithPasswordOnly, HttpStatusCode.Forbidden },
        { "POST", ApiRoutes.Scenarios, Caller.AuditorWithTwoFactors, HttpStatusCode.Forbidden },
        { "POST", ApiRoutes.Scenarios, Caller.AdminWithTwoFactors, HttpStatusCode.Accepted },
        { "POST", ApiRoutes.ScenariosStop, Caller.Anonymous, HttpStatusCode.Unauthorized },
        { "POST", ApiRoutes.ScenariosStop, Caller.AuditorWithTwoFactors, HttpStatusCode.Forbidden },
        { "POST", ApiRoutes.ScenariosStop, Caller.AdminWithTwoFactors, HttpStatusCode.Accepted },
        { "POST", ApiRoutes.Passengers, Caller.Anonymous, HttpStatusCode.Accepted },

        { "GET", ApiRoutes.AuditEntries, Caller.Anonymous, HttpStatusCode.Unauthorized },
        { "GET", ApiRoutes.AuditEntries, Caller.AdminWithPasswordOnly, HttpStatusCode.Forbidden },
        { "GET", ApiRoutes.AuditEntries, Caller.AuditorWithTwoFactors, Allowed },
        { "GET", ApiRoutes.AuditEntries, Caller.AdminWithTwoFactors, Allowed },

        { "GET", ApiRoutes.AuditVerification, Caller.Anonymous, HttpStatusCode.Unauthorized },
        { "GET", ApiRoutes.AuditVerification, Caller.AdminWithPasswordOnly, HttpStatusCode.Forbidden },
        { "GET", ApiRoutes.AuditVerification, Caller.AuditorWithTwoFactors, Allowed },
        { "GET", ApiRoutes.AuditVerification, Caller.AdminWithTwoFactors, Allowed },

        { "GET", ApiRoutes.Session, Caller.Anonymous, HttpStatusCode.Unauthorized },
        { "GET", ApiRoutes.Session, Caller.AdminWithPasswordOnly, Allowed },
        { "GET", ApiRoutes.Session, Caller.AuditorWithTwoFactors, Allowed },

        { "POST", ApiRoutes.TwoFactorSetup, Caller.Anonymous, HttpStatusCode.Unauthorized },
        { "POST", ApiRoutes.TwoFactorSetup, Caller.AdminWithPasswordOnly, Allowed },
        { "POST", ApiRoutes.TwoFactorEnable, Caller.Anonymous, HttpStatusCode.Unauthorized },
        { "POST", ApiRoutes.SignOut, Caller.Anonymous, HttpStatusCode.Unauthorized },
    };

    public async ValueTask InitializeAsync()
    {
        _clients[Caller.Anonymous] = StaffClient.Anonymous(_factory);
        _clients[Caller.AdminWithPasswordOnly] = await StaffClient.SignedInWithPasswordAsync(_factory, SecondAdmin);
        _clients[Caller.AuditorWithTwoFactors] = await StaffClient.SignedInWithTwoFactorsAsync(_factory, StaffAccounts.Auditor);
        _clients[Caller.AdminWithTwoFactors] = await StaffClient.SignedInWithTwoFactorsAsync(_factory, StaffAccounts.Admin);
        await _factory.StatusAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        await _factory.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Endpoint_AnswersEachCallerAsAllowed(string method, string route, Caller caller, HttpStatusCode expected)
    {
        var client = _clients[caller];

        using var response = method == "GET"
            ? await client.GetAsync(route)
            : await client.PostAsync(route, route switch
            {
                ApiRoutes.ServiceActions => new ServiceActionRequest(ServiceAction.StartMaintenance),
                ApiRoutes.Scenarios => new StartScenarioRequest(DemoScenario.MorningRush),
                _ => null,
            });

        if (expected == Allowed)
        {
            response.StatusCode.ShouldBeOneOf([HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable], $"{caller} may call {method} {route}");
        }
        else
        {
            response.StatusCode.ShouldBe(expected, $"{caller} calling {method} {route}");
        }
    }

    [Fact]
    public void EveryApiEndpoint_StatesWhoMayCallIt()
    {
        var undeclared = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(ApiRoutes.Base, StringComparison.Ordinal) == true)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null && endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(endpoint => endpoint.DisplayName)
            .ToList();

        undeclared.ShouldBeEmpty("every API endpoint is either AllowAnonymous or protected by a policy");
    }
}
