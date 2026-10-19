using System.Xml.Linq;

namespace ElevatorSystem.Architecture.Tests;

/// <summary>
/// Reads the solution's MSBuild files straight from disk, so the rules see exactly what the build sees.
/// </summary>
internal static class Solution
{
    public static DirectoryInfo Root { get; } = FindRoot();

    public static IReadOnlyList<ProjectFile> SourceProjects { get; } = LoadProjects("src");

    /// <summary>Every project, props and targets file in the repository, excluding build output.</summary>
    public static IReadOnlyList<FileInfo> MSBuildFiles { get; } =
        [.. Root.EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(file => file.Extension is ".csproj" or ".props" or ".targets")
            .Where(file => !IsBuildOutput(file))];

    private static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElevatorSystem.slnx")))
        {
            directory = directory.Parent;
        }

        return directory
            ?? throw new InvalidOperationException($"ElevatorSystem.slnx not found above {AppContext.BaseDirectory}.");
    }

    private static List<ProjectFile> LoadProjects(string folder) =>
        [.. Root.GetDirectories(folder).Single()
            .EnumerateFiles("*.csproj", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Select(ProjectFile.Load)
            .OrderBy(project => project.Name, StringComparer.Ordinal)];

    private static bool IsBuildOutput(FileInfo file)
    {
        var segments = file.FullName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }
}

internal sealed record ProjectFile(string Name, IReadOnlySet<string> ProjectReferences, IReadOnlySet<string> PackageReferences)
{
    public static ProjectFile Load(FileInfo file)
    {
        var xml = XDocument.Load(file.FullName);

        var projectReferences = xml.Descendants("ProjectReference")
            .Select(element => ProjectNameFromPath(element.Attribute("Include")?.Value ?? string.Empty))
            .ToHashSet(StringComparer.Ordinal);

        var packageReferences = xml.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ProjectFile(Path.GetFileNameWithoutExtension(file.Name), projectReferences, packageReferences);
    }

    // Project files use Windows separators; normalise so the name resolves on Linux CI as well.
    private static string ProjectNameFromPath(string includePath) =>
        Path.GetFileNameWithoutExtension(includePath.Replace('\\', '/'));
}
