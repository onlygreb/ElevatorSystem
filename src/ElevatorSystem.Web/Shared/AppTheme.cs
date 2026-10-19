using MudBlazor;

namespace ElevatorSystem.Web.Shared;

/// <summary>
/// The MudBlazor theme, built from the same colour values as <c>wwwroot/css/tokens.css</c> (tests keep the two in step).
/// Light and dark follow the operating system, like the tokens.
/// </summary>
internal static class AppTheme
{
    public const string FontStack = "\"Segoe UI Variable\", \"Segoe UI\", system-ui, -apple-system, \"Helvetica Neue\", Arial, sans-serif";

    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = Light.Accent,
            PrimaryContrastText = Light.OnAccent,
            Secondary = Light.TextMuted,
            Info = Light.Accent,
            InfoContrastText = Light.OnAccent,
            Success = Light.Success,
            Warning = Light.Warning,
            WarningContrastText = Light.OnWarning,
            Error = Light.Error,
            ErrorContrastText = Light.OnError,
            Background = Light.Background,
            Surface = Light.Surface,
            AppbarBackground = Light.Surface,
            AppbarText = Light.Text,
            TextPrimary = Light.Text,
            TextSecondary = Light.TextMuted,
            Divider = Light.Divider,
            LinesDefault = Light.Divider,
        },
        PaletteDark = new PaletteDark
        {
            Primary = Dark.Accent,
            PrimaryContrastText = Dark.OnAccent,
            Secondary = Dark.TextMuted,
            Info = Dark.Accent,
            InfoContrastText = Dark.OnAccent,
            Success = Dark.Success,
            Warning = Dark.Warning,
            WarningContrastText = Dark.OnWarning,
            Error = Dark.Error,
            ErrorContrastText = Dark.OnError,
            Background = Dark.Background,
            Surface = Dark.Surface,
            AppbarBackground = Dark.Surface,
            AppbarText = Dark.Text,
            TextPrimary = Dark.Text,
            TextSecondary = Dark.TextMuted,
            Divider = Dark.Divider,
            LinesDefault = Dark.Divider,
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = [.. FontStack.Split(", ")] },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "10px" },
    };

    /// <summary>Token values for the light theme (<c>:root</c> in tokens.css).</summary>
    internal static class Light
    {
        public const string Background = "#f4f6f8";
        public const string Surface = "#ffffff";
        public const string Divider = "#dce2e8";
        public const string Text = "#17202a";
        public const string TextMuted = "#4b5663";
        public const string Accent = "#0f6b74";
        public const string OnAccent = "#ffffff";
        public const string Warning = "#8a5a00";
        public const string OnWarning = "#ffffff";
        public const string Error = "#b3261e";
        public const string OnError = "#ffffff";
        public const string Success = "#1e6b3a";
    }

    /// <summary>Token values for the dark theme (<c>prefers-color-scheme: dark</c> in tokens.css).</summary>
    internal static class Dark
    {
        public const string Background = "#0e1419";
        public const string Surface = "#161e26";
        public const string Divider = "#2a3541";
        public const string Text = "#e7ecf1";
        public const string TextMuted = "#aab5c0";
        public const string Accent = "#5cc6cf";
        public const string OnAccent = "#0e1419";
        public const string Warning = "#ffc94d";
        public const string OnWarning = "#0e1419";
        public const string Error = "#ffb4ab";
        public const string OnError = "#0e1419";
        public const string Success = "#7fd69b";
    }
}
