using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ElevatorSystem.E2E.Tests;

/// <summary>
/// axe-core finds no WCAG 2.2 A or AA violation on the pages people use, in the states they spend time in. axe covers
/// what a machine can check; keyboard and screen-reader behaviour is covered by the bUnit tests and by hand.
/// </summary>
[Collection(OneElevator.Name)]
public sealed class AccessibilityTests(BrowserFixture fixture)
{
    private static readonly AxeRunOptions Wcag22AA = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"] },
    };

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task PassengerView_OutsideTheBuilding_HasNoViolations(string locale)
    {
        var page = await fixture.NewPageAsync(locale);
        await fixture.OpenAsync(page, "/");
        await Expect(page.GetByTestId("arrive")).ToBeVisibleAsync();

        await AssertNoViolationsAsync(page);
    }

    [Fact]
    public async Task PassengerView_InsideTheCar_HasNoViolations()
    {
        var page = await fixture.NewPageAsync();
        await fixture.OpenAsync(page, "/");
        await page.GetByTestId("arrive").ClickAsync();
        await PassengerJourneyTests.BoardAsync(page);

        await AssertNoViolationsAsync(page);
    }

    [Fact]
    public async Task AdminSignIn_HasNoViolations()
    {
        var page = await fixture.NewPageAsync();
        await fixture.OpenAsync(page, "/admin");
        await Expect(page.GetByTestId("sign-in")).ToBeVisibleAsync();

        await AssertNoViolationsAsync(page);
    }

    [Fact]
    public async Task AdminDashboard_HasNoViolations()
    {
        var page = await fixture.NewPageAsync();
        await AdminTests.SignInAsync(page, fixture);
        await Expect(page.Locator(".demo-card").First).ToBeVisibleAsync();

        await AssertNoViolationsAsync(page);
    }

    private static async Task AssertNoViolationsAsync(IPage page)
    {
        // Let entrance animations settle, so colours are measured as people see them.
        await page.WaitForTimeoutAsync(500);
        var results = await page.RunAxe(Wcag22AA);

        var violations = results.Violations.Select(violation =>
            $"{violation.Id} ({violation.Impact}): {violation.Help} — {string.Join(", ", violation.Nodes.Take(3).Select(node => string.Join(" ", node.Target)))}");
        violations.ShouldBeEmpty();
    }
}
