using System.Globalization;
using ElevatorSystem.Web.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Web.Tests;

/// <summary>The real .resx-backed localizer, and a scope that switches the current culture for one test.</summary>
internal static class TestLocalisation
{
    public static IStringLocalizer<SharedResource> Localizer() =>
        new StringLocalizer<SharedResource>(
            new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));

    /// <summary>Sets both the formatting and the UI culture until disposed.</summary>
    public static IDisposable Culture(string name) => new CultureScope(CultureInfo.GetCultureInfo(name));

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(CultureInfo culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
