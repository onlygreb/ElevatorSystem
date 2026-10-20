using System.Globalization;
using System.Text.RegularExpressions;
using ElevatorSystem.Web.Shared;

namespace ElevatorSystem.Web.Tests.Chrome;

/// <summary>
/// Reads tokens.css and checks every text/background pair the UI uses reaches WCAG AA (4.5:1) in both themes,
/// and that the MudBlazor theme uses the same values.
/// </summary>
public sealed partial class ThemeTokenTests
{
    // (text token, background token) pairs that put text on a surface somewhere in the UI.
    public static TheoryData<string, string> TextPairs => new()
    {
        { "--es-color-text", "--es-color-bg" },
        { "--es-color-text", "--es-color-surface" },
        { "--es-color-text", "--es-color-sunken" },
        { "--es-color-text-muted", "--es-color-bg" },
        { "--es-color-text-muted", "--es-color-surface" },
        { "--es-color-text-muted", "--es-color-sunken" },
        { "--es-color-on-accent", "--es-color-accent" },
        { "--es-color-on-lamp", "--es-color-lamp" },
        { "--es-color-on-warning", "--es-color-warning" },
        { "--es-color-on-error", "--es-color-error" },
        { "--es-color-on-car", "--es-color-car" },
        { "--es-color-on-chrome", "--es-color-chrome" },
        { "--es-color-on-chrome", "--es-color-chrome-2" },
    };

    public static TheoryData<string> Themes => ["light", "dark"];

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void TextPair_InLightTheme_MeetsAaContrast(string text, string background) =>
        AssertContrast(Tokens.Value.Light, text, background, 4.5);

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void TextPair_InDarkTheme_MeetsAaContrast(string text, string background) =>
        AssertContrast(Tokens.Value.Dark, text, background, 4.5);

    [Theory]
    [MemberData(nameof(Themes))]
    public void ButtonOutlinesAndFocusRing_StandOutFromTheSurface(string theme)
    {
        var tokens = theme == "light" ? Tokens.Value.Light : Tokens.Value.Dark;

        // WCAG 1.4.11: boundaries of controls and the focus indicator need 3:1 against what is next to them.
        AssertContrast(tokens, "--es-color-outline", "--es-color-surface", 3);
        AssertContrast(tokens, "--es-color-focus", "--es-color-surface", 3);
        AssertContrast(tokens, "--es-color-focus", "--es-color-bg", 3);
    }

    [Fact]
    public void MudTheme_UsesTheLightTokens()
    {
        var light = Tokens.Value.Light;

        AppTheme.Light.Background.ShouldBe(light["--es-color-bg"]);
        AppTheme.Light.Surface.ShouldBe(light["--es-color-surface"]);
        AppTheme.Light.Divider.ShouldBe(light["--es-color-divider"]);
        AppTheme.Light.Text.ShouldBe(light["--es-color-text"]);
        AppTheme.Light.TextMuted.ShouldBe(light["--es-color-text-muted"]);
        AppTheme.Light.Accent.ShouldBe(light["--es-color-accent"]);
        AppTheme.Light.OnAccent.ShouldBe(light["--es-color-on-accent"]);
        AppTheme.Light.Warning.ShouldBe(light["--es-color-warning"]);
        AppTheme.Light.OnWarning.ShouldBe(light["--es-color-on-warning"]);
        AppTheme.Light.Error.ShouldBe(light["--es-color-error"]);
        AppTheme.Light.OnError.ShouldBe(light["--es-color-on-error"]);
        AppTheme.Light.Success.ShouldBe(light["--es-color-success"]);
    }

    [Fact]
    public void MudTheme_UsesTheDarkTokens()
    {
        var dark = Tokens.Value.Dark;

        AppTheme.Dark.Background.ShouldBe(dark["--es-color-bg"]);
        AppTheme.Dark.Surface.ShouldBe(dark["--es-color-surface"]);
        AppTheme.Dark.Divider.ShouldBe(dark["--es-color-divider"]);
        AppTheme.Dark.Text.ShouldBe(dark["--es-color-text"]);
        AppTheme.Dark.TextMuted.ShouldBe(dark["--es-color-text-muted"]);
        AppTheme.Dark.Accent.ShouldBe(dark["--es-color-accent"]);
        AppTheme.Dark.OnAccent.ShouldBe(dark["--es-color-on-accent"]);
        AppTheme.Dark.Warning.ShouldBe(dark["--es-color-warning"]);
        AppTheme.Dark.OnWarning.ShouldBe(dark["--es-color-on-warning"]);
        AppTheme.Dark.Error.ShouldBe(dark["--es-color-error"]);
        AppTheme.Dark.OnError.ShouldBe(dark["--es-color-on-error"]);
        AppTheme.Dark.Success.ShouldBe(dark["--es-color-success"]);
    }

    [Fact]
    public void DarkTheme_DefinesEveryColourTheLightThemeDoes()
    {
        var light = Tokens.Value.Light.Keys;
        var dark = Tokens.Value.Dark.Keys;

        light.Except(dark).ShouldBeEmpty("a colour missing in dark mode would fall back to its light value");
    }

    [Fact]
    public void ReducedMotion_TurnsOffTheCarAndDoorAnimations()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ElevatorSystem.Web", "wwwroot", "css", "app.css"));

        var block = ReducedMotionBlock().Match(css);
        block.Success.ShouldBeTrue("app.css needs a prefers-reduced-motion block");
        block.Value.ShouldContain(".cabin");
        block.Value.ShouldContain(".cabin__door");
        block.Value.ShouldContain("transition-duration: 0s !important");
    }

    private static readonly Lazy<(Dictionary<string, string> Light, Dictionary<string, string> Dark)> Tokens = new(() =>
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ElevatorSystem.Web", "wwwroot", "css", "tokens.css"));
        var darkStart = css.IndexOf("@media (prefers-color-scheme: dark)", StringComparison.Ordinal);
        darkStart.ShouldBeGreaterThan(0, "tokens.css needs a dark theme block");
        return (Colours(css[..darkStart]), Colours(css[darkStart..]));
    });

    private static Dictionary<string, string> Colours(string css) =>
        ColourToken().Matches(css).ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value.ToLowerInvariant());

    private static void AssertContrast(Dictionary<string, string> tokens, string foreground, string background, double minimum)
    {
        var ratio = ContrastRatio(tokens[foreground], tokens[background]);

        ratio.ShouldBeGreaterThanOrEqualTo(minimum,
            $"{foreground} {tokens[foreground]} on {background} {tokens[background]} is only {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1");
    }

    // WCAG 2.2 relative luminance and contrast ratio.
    private static double ContrastRatio(string first, string second)
    {
        var (lighter, darker) = (Luminance(first), Luminance(second)) is var (a, b) && a > b ? (a, b) : (b, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string hex)
    {
        static double Channel(string hex, int offset)
        {
            var value = int.Parse(hex.AsSpan(1 + offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(hex, 0)) + (0.7152 * Channel(hex, 2)) + (0.0722 * Channel(hex, 4));
    }

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

    [GeneratedRegex(@"(--es-color-[a-z0-9-]+):\s*(#[0-9a-fA-F]{6});")]
    private static partial Regex ColourToken();

    [GeneratedRegex(@"@media \(prefers-reduced-motion: reduce\) \{.*?\n\}", RegexOptions.Singleline)]
    private static partial Regex ReducedMotionBlock();
}
