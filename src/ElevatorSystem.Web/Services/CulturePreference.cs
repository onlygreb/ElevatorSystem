using System.Globalization;
using Microsoft.JSInterop;

namespace ElevatorSystem.Web.Services;

/// <summary>
/// The passenger's language choice, kept in <c>localStorage</c>. It is a display preference only: nothing about the
/// person, and nothing sent to the server. Applied once at startup, before the first render.
/// </summary>
internal sealed class CulturePreference(IJSRuntime js)
{
    public const string StorageKey = "elevator.culture";

    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    public static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    public static IReadOnlyList<CultureInfo> Supported { get; } = [English, Portuguese];

    /// <summary>
    /// The stored choice when it names a supported culture; otherwise Portuguese for a Portuguese-speaking browser
    /// and English for everyone else. Anything else in storage is ignored, never applied.
    /// </summary>
    public static CultureInfo Resolve(string? stored, CultureInfo browserCulture)
    {
        ArgumentNullException.ThrowIfNull(browserCulture);

        if (Supported.FirstOrDefault(culture => string.Equals(culture.Name, stored, StringComparison.OrdinalIgnoreCase)) is { } chosen)
        {
            return chosen;
        }

        return browserCulture.TwoLetterISOLanguageName == Portuguese.TwoLetterISOLanguageName ? Portuguese : English;
    }

    /// <summary>Reads the stored choice and makes it the culture of the whole app, including the page's <c>lang</c>.</summary>
    public async Task<CultureInfo> ApplyStoredAsync()
    {
        string? stored = null;
        try
        {
            stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        }
        catch (JSException)
        {
            // Storage blocked (private mode, policy): fall back to the browser's language.
        }

        var culture = Resolve(stored, CultureInfo.CurrentCulture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // The host page is rendered before the choice is known; screen readers need the real language (WCAG 3.1.1).
        await js.InvokeVoidAsync("document.documentElement.setAttribute", "lang", culture.Name);
        return culture;
    }

    /// <summary>Stores the choice. The caller reloads the app, which applies it from the start.</summary>
    public async Task SaveAsync(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (!Supported.Contains(culture))
        {
            throw new ArgumentOutOfRangeException(nameof(culture), culture.Name, "Unsupported culture.");
        }

        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, culture.Name);
    }
}
