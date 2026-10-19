namespace ElevatorSystem.Architecture.Tests;

/// <summary>
/// Enforces the dependency direction from ADR-0002: Domain at the centre, hosts at the edge,
/// and the browser UI isolated behind the API.
/// </summary>
public sealed class ProjectDependencyTests
{
    private const string Domain = "ElevatorSystem.Domain";
    private const string Contracts = "ElevatorSystem.Contracts";
    private const string Application = "ElevatorSystem.Application";
    private const string Infrastructure = "ElevatorSystem.Infrastructure";
    private const string ServiceDefaults = "ElevatorSystem.ServiceDefaults";
    private const string Engine = "ElevatorSystem.Engine";
    private const string Web = "ElevatorSystem.Web";
    private const string Api = "ElevatorSystem.Api";
    private const string ConsoleDemo = "ElevatorSystem.ConsoleDemo";
    private const string AppHost = "ElevatorSystem.AppHost";

    // Direct project references each project may have. A new project must be added here on purpose,
    // which forces a decision about where it sits in the architecture.
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        [Domain] = [],
        [Contracts] = [],
        [Application] = [Domain, Contracts],
        [Infrastructure] = [Application, Domain, Contracts],
        [ServiceDefaults] = [],
        [Engine] = [Application, Infrastructure, Domain, Contracts, ServiceDefaults],
        [Web] = [Contracts],
        [Api] = [Web, Application, Infrastructure, Contracts, ServiceDefaults],
        [ConsoleDemo] = [Application, Domain, Contracts],
        [AppHost] = [Api, Engine],
    };

    public static TheoryData<string> SourceProjectNames => [.. Solution.SourceProjects.Select(project => project.Name)];

    [Fact]
    public void EverySourceProject_HasAnExplicitDependencyRule()
    {
        var projectsWithoutRule = Solution.SourceProjects
            .Select(project => project.Name)
            .Except(AllowedReferences.Keys);

        projectsWithoutRule.ShouldBeEmpty(
            "every project in src/ needs an entry in AllowedReferences (and an ADR if it changes the architecture)");
    }

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void Project_ReferencesOnlyAllowedProjects(string projectName)
    {
        var project = Solution.SourceProjects.Single(candidate => candidate.Name == projectName);
        var allowed = AllowedReferences.GetValueOrDefault(projectName, []);

        project.ProjectReferences.Except(allowed).ShouldBeEmpty(
            $"{projectName} may only reference: {(allowed.Length == 0 ? "nothing" : string.Join(", ", allowed))}");
    }

    [Theory]
    [InlineData(Domain)]
    [InlineData(Contracts)]
    public void CoreProject_HasNoPackageReferences(string projectName)
    {
        var project = Solution.SourceProjects.Single(candidate => candidate.Name == projectName);

        project.PackageReferences.ShouldBeEmpty($"{projectName} must stay free of third-party dependencies");
    }
}
