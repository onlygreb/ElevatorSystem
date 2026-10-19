using System.Xml.Linq;

namespace ElevatorSystem.Architecture.Tests;

/// <summary>
/// Enforces ADR-0008: one place for package versions, stable releases only, and no libraries whose
/// licence or behaviour is incompatible with this project.
/// </summary>
public sealed class PackagePolicyTests
{
    // MediatR, AutoMapper, MassTransit (v9+) and FluentAssertions (v8+) moved to commercial licences.
    // Moq shipped SponsorLink, which harvested developer e-mail hashes: unacceptable under LGPD.
    private static readonly string[] DeniedPackages = ["MediatR", "AutoMapper", "MassTransit", "FluentAssertions", "Moq"];

    private static readonly XDocument CentralPackages =
        XDocument.Load(Path.Combine(Solution.Root.FullName, "Directory.Packages.props"));

    [Fact]
    public void NoDeniedPackage_IsReferencedAnywhere()
    {
        var referenced = Solution.MSBuildFiles
            .SelectMany(file => XDocument.Load(file.FullName).Descendants()
                .Where(element => element.Name.LocalName is "PackageReference" or "PackageVersion" or "GlobalPackageReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty));

        var denied = referenced.Where(IsDenied).Distinct(StringComparer.OrdinalIgnoreCase);

        denied.ShouldBeEmpty("see docs/adr/0008 for the approved alternatives");
    }

    [Fact]
    public void ProjectFiles_NeverPinTheirOwnPackageVersions()
    {
        var offenders = Solution.MSBuildFiles
            .Where(file => file.Name != "Directory.Packages.props")
            .SelectMany(file => XDocument.Load(file.FullName).Descendants("PackageReference")
                .Where(element => element.Attribute("Version") is not null || element.Attribute("VersionOverride") is not null)
                .Select(element => $"{file.Name}: {element.Attribute("Include")?.Value}"));

        offenders.ShouldBeEmpty("versions belong in Directory.Packages.props only");
    }

    [Fact]
    public void CentralVersions_AreStableReleases()
    {
        var prereleases = CentralPackages.Descendants()
            .Where(element => element.Name.LocalName is "PackageVersion" or "GlobalPackageReference")
            .Where(element => (element.Attribute("Version")?.Value ?? string.Empty).Contains('-', StringComparison.Ordinal))
            .Select(element => $"{element.Attribute("Include")?.Value} {element.Attribute("Version")?.Value}");

        prereleases.ShouldBeEmpty("prerelease packages are not allowed");
    }

    private static bool IsDenied(string packageId) =>
        DeniedPackages.Any(denied =>
            packageId.Equals(denied, StringComparison.OrdinalIgnoreCase)
            || packageId.StartsWith(denied + ".", StringComparison.OrdinalIgnoreCase));
}
