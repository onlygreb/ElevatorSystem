using System.Net;
using System.Text;
using ElevatorSystem.Api.Tests.Staff;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Logging.Testing;

namespace ElevatorSystem.Api.Tests.Security;

/// <summary>
/// ADR-0024: client addresses and request bodies never reach the logs. Every category logs at Trace
/// here, so the test covers the framework's own messages as well as ours.
/// </summary>
public sealed class LogPrivacyTests
{
    private const string Ipv4 = "203.0.113.77";
    private const string Ipv6 = "2001:db8:77::77";
    private const string BodyMarker = "body-marker-5f1c";

    [Fact]
    public async Task Logs_NeverContainClientAddressesOrRequestBodies()
    {
        await using var factory = new ApiFactory()
            .WithSetting("Logging:LogLevel:Default", "Trace")
            .WithSetting("Logging:LogLevel:Microsoft.AspNetCore", "Trace")
            .WithSetting("RateLimiting:Passenger:TokenLimit", "3")
            .WithSetting("RateLimiting:Passenger:TokensPerPeriod", "1")
            .WithSetting("RateLimiting:Passenger:ReplenishmentPeriod", "01:00:00");
        using var client = factory.CreateClient();

        foreach (var address in new[] { Ipv4, Ipv6 })
        {
            (await SendAsync(client, address, ApiRoutes.HallCalls, $$"""{"floor":3,"direction":"Up","{{BodyMarker}}":1}""")).ShouldBe(HttpStatusCode.Accepted);
            (await SendAsync(client, address, ApiRoutes.HallCalls, $$"""{"floor":"{{BodyMarker}}","direction":"Up"}""")).ShouldBe(HttpStatusCode.BadRequest);
            (await SendAsync(client, address, ApiRoutes.CarCalls, $$"""{"floor":42,"note":"{{BodyMarker}}"}""")).ShouldBe(HttpStatusCode.BadRequest);
            (await SendAsync(client, address, ApiRoutes.CarCalls, """{"floor":2}""")).ShouldBe(HttpStatusCode.TooManyRequests);
        }

        var records = factory.Services.GetFakeLogCollector().GetSnapshot();
        records.ShouldNotBeEmpty("the test is only meaningful if the host actually logged something");
        foreach (var record in records)
        {
            var text = Describe(record);
            text.ShouldNotContain(Ipv4, Case.Insensitive, "client addresses are personal data");
            text.ShouldNotContain(Ipv6, Case.Insensitive, "client addresses are personal data");
            text.ShouldNotContain(BodyMarker, Case.Insensitive, "request bodies are never logged");
        }
    }

    [Fact]
    public async Task Logs_NeverContainStaffEmailsPasswordsOrCodes()
    {
        await using var factory = new ApiFactory()
            .WithSetting("Logging:LogLevel:Default", "Trace")
            .WithSetting("Logging:LogLevel:Microsoft.AspNetCore", "Trace");
        const string Guessed = "guessed-password-5f1c";
        using var intruder = StaffClient.Anonymous(factory);
        intruder.Http.DefaultRequestHeaders.Add(ApiFactory.ClientAddressHeader, Ipv4);
        using (await intruder.SignInAsync("intruder@elevator.test", Guessed))
        using (await intruder.SignInAsync(StaffAccounts.Admin.Email, Guessed))
        {
        }

        using var admin = await StaffClient.SignedInWithTwoFactorsAsync(factory, StaffAccounts.Admin);
        using (await admin.PostAsync(ApiRoutes.SignOut))
        using (await admin.SignInAsync(StaffAccounts.Admin.Email, StaffAccounts.Admin.Password, Totp.Code(admin.AuthenticatorKey!)))
        {
        }

        var secrets = new[] { "intruder@elevator.test", StaffAccounts.Admin.Email, Guessed, StaffAccounts.Admin.Password, Ipv4, admin.AuthenticatorKey!.Replace(" ", "", StringComparison.Ordinal) };
        foreach (var record in factory.Services.GetFakeLogCollector().GetSnapshot())
        {
            var text = Describe(record).Replace(" ", "", StringComparison.Ordinal);
            foreach (var secret in secrets)
            {
                text.ShouldNotContain(secret.Replace(" ", "", StringComparison.Ordinal), Case.Insensitive, "staff e-mails, passwords, keys and addresses never reach the logs");
            }
        }
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string address, string route, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(route, UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ApiFactory.ClientAddressHeader, address);
        using var response = await client.SendAsync(request, Http.Cancellation);
        return response.StatusCode;
    }

    /// <summary>Everything a log sink could write: message, structured state, scopes and the exception.</summary>
    private static string Describe(FakeLogRecord record)
    {
        var text = new StringBuilder()
            .Append(record.Category).Append(' ').Append(record.Message).Append(' ').Append(record.Exception);
        foreach (var (key, value) in record.StructuredState ?? [])
        {
            text.Append(' ').Append(key).Append('=').Append(value);
        }

        foreach (var scope in record.Scopes)
        {
            text.Append(' ').Append(scope);
            if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    text.Append(' ').Append(key).Append('=').Append(value);
                }
            }
        }

        return text.ToString();
    }
}
