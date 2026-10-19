using System.Globalization;
using System.Reflection;
using ElevatorSystem.Web.Tests;
using Xunit.v3;

// Every test runs in en-US unless it says otherwise, whatever the machine's language.
[assembly: UseCulture("en-US")]

namespace ElevatorSystem.Web.Tests;

/// <summary>Runs a test with the given formatting and UI culture, then restores the previous ones.</summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class UseCultureAttribute(string culture) : BeforeAfterTestAttribute
{
    private CultureInfo? _previousCulture;
    private CultureInfo? _previousUiCulture;

    public string Culture { get; } = culture;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        _previousCulture = CultureInfo.CurrentCulture;
        _previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(Culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Culture);
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        CultureInfo.CurrentCulture = _previousCulture!;
        CultureInfo.CurrentUICulture = _previousUiCulture!;
    }
}
