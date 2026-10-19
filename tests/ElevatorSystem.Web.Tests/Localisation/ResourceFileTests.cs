using System.Reflection;
using System.Xml.Linq;
using ElevatorSystem.Web.Resources;

namespace ElevatorSystem.Web.Tests.Localisation;

/// <summary>Every <see cref="Text"/> key has a non-empty string in both cultures, and the files hold nothing else.</summary>
public sealed class ResourceFileTests
{
    private static readonly string[] Keys =
    [
        .. typeof(Text).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!),
    ];

    public static TheoryData<string> ResourceFiles => ["SharedResource.resx", "SharedResource.pt-BR.resx"];

    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void ResourceFile_HasAValueForEveryKey(string fileName)
    {
        var entries = Load(fileName);

        Keys.Where(key => string.IsNullOrWhiteSpace(entries.GetValueOrDefault(key))).ShouldBeEmpty($"{fileName} is missing strings");
    }

    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void ResourceFile_HasNoUnusedKeys(string fileName)
    {
        var entries = Load(fileName);

        entries.Keys.Except(Keys).ShouldBeEmpty($"{fileName} has strings no Text constant refers to");
    }

    [Fact]
    public void Translations_KeepTheSameFormatPlaceholders()
    {
        var english = Load("SharedResource.resx");
        var portuguese = Load("SharedResource.pt-BR.resx");

        var mismatched = Keys.Where(key => !Placeholders(english[key]).SequenceEqual(Placeholders(portuguese[key])));

        mismatched.ShouldBeEmpty("a translation that drops or adds a {n} placeholder shows wrong data or throws");
    }

    private static Dictionary<string, string> Load(string fileName)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "ElevatorSystem.Web", "Resources", fileName);
        return XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(data => (string)data.Attribute("name")!, data => (string?)data.Element("value") ?? string.Empty);
    }

    private static IEnumerable<string> Placeholders(string value) =>
        System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+(:[^}]*)?\}").Select(match => match.Value).Order(StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ElevatorSystem.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("ElevatorSystem.slnx not found above the test output directory.");
    }
}
