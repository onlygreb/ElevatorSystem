using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ElevatorSystem.Api.Tests.OpenApi;

/// <summary>
/// The OpenAPI document is part of the API's contract: any change to it must show up in review as a
/// change to <c>openapi-v1.verified.json</c>.
/// </summary>
/// <remarks>
/// A hand-rolled snapshot rather than Verify: Verify now requires a paid maintenance licence or a claimed
/// exemption, which ADR-0008 does not allow us to assume. On a mismatch the test writes
/// <c>openapi-v1.received.json</c> next to the snapshot; review the difference and, if it is intended,
/// replace the snapshot with it (or run the tests with <c>UPDATE_SNAPSHOTS=1</c>).
/// </remarks>
public sealed class OpenApiDocumentTests
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        // Readable in review: "application/problem+json" rather than "application/problem\u002Bjson".
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public async Task OpenApiDocument_MatchesTheApprovedSnapshot()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync("/openapi/v1.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var actual = Normalise(await response.Content.ReadAsStringAsync(Http.Cancellation));
        await MatchSnapshotAsync(actual);
    }

    /// <summary>Drops the server list (it names the test host) and fixes formatting and line endings.</summary>
    private static string Normalise(string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        document.Remove("servers");
        return document.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>
    /// The snapshot's folder in the source tree. Found from the test binary rather than the compiler's
    /// source path, because CI builds map source paths to <c>/_/</c> (deterministic builds).
    /// </summary>
    private static string SnapshotDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElevatorSystem.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException($"ElevatorSystem.slnx not found above {AppContext.BaseDirectory}.")
            : Path.Combine(directory.FullName, "tests", "ElevatorSystem.Api.Tests", "OpenApi");
    }

    private static async Task MatchSnapshotAsync(string actual)
    {
        // Multi-line XML doc comments reach descriptions with the checkout's line endings (CRLF on Windows,
        // LF on CI); compare them as LF.
        actual = actual.Replace(@"\r\n", @"\n", StringComparison.Ordinal);
        var directory = SnapshotDirectory();
        var verified = Path.Combine(directory, "openapi-v1.verified.json");
        var received = Path.Combine(directory, "openapi-v1.received.json");
        var expected = File.Exists(verified) ? (await File.ReadAllTextAsync(verified, Http.Cancellation)).ReplaceLineEndings("\n") : null;

        if (expected == actual)
        {
            File.Delete(received);
            return;
        }

        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
        {
            await File.WriteAllTextAsync(verified, actual, Http.Cancellation);
            return;
        }

        await File.WriteAllTextAsync(received, actual, Http.Cancellation);
        actual.ShouldBe(expected, $"the OpenAPI document changed; compare {received} with {verified}");
    }
}
