using System.Globalization;
using Bunit.TestDoubles;
using ElevatorSystem.Web.Services;
using ElevatorSystem.Web.Shared;
using Microsoft.AspNetCore.Components;

namespace ElevatorSystem.Web.Tests.Chrome;

public sealed class CulturePickerTests : WebTestContext
{
    [Fact]
    public void Picker_MarksTheCurrentLanguage()
    {
        var picker = Render<CulturePicker>();

        picker.Find("[data-culture='en-US']").GetAttribute("aria-pressed").ShouldBe("true");
        picker.Find("[data-culture='pt-BR']").GetAttribute("aria-pressed").ShouldBe("false");
    }

    [Fact]
    public void Picker_NamesEachLanguageInItsOwnLanguage()
    {
        var picker = Render<CulturePicker>();

        var portuguese = picker.Find("[data-culture='pt-BR']");
        portuguese.TextContent.Trim().ShouldBe("Português (Brasil)");
        portuguese.GetAttribute("lang").ShouldBe("pt-BR");
        picker.Find("[role=group]").GetAttribute("aria-label").ShouldBe("Language");
    }

    [Fact]
    public void ChoosingPortuguese_StoresTheChoiceAndReloads()
    {
        var store = JSInterop.SetupVoid("localStorage.setItem", CulturePreference.StorageKey, "pt-BR");
        store.SetVoidResult();
        var navigation = Services.GetRequiredService<NavigationManager>();
        var picker = Render<CulturePicker>();

        picker.Find("[data-culture='pt-BR']").Click();

        store.Invocations.Count.ShouldBe(1);
        navigation.Uri.ShouldBe(navigation.BaseUri);
        ((BunitNavigationManager)navigation).History.ShouldHaveSingleItem().Options.ForceLoad.ShouldBeTrue();
    }

    [Fact]
    public void ChoosingTheCurrentLanguage_DoesNothing()
    {
        var picker = Render<CulturePicker>();

        picker.Find("[data-culture='en-US']").Click();

        JSInterop.Invocations.Where(invocation => invocation.Identifier == "localStorage.setItem").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("pt-BR", "en-US", "pt-BR")]
    [InlineData("en-US", "pt-BR", "en-US")]
    [InlineData("PT-br", "en-US", "pt-BR")]
    [InlineData(null, "pt-PT", "pt-BR")]
    [InlineData(null, "fr-FR", "en-US")]
    [InlineData("de-DE", "en-GB", "en-US")]
    [InlineData("<img src=x>", "pt-BR", "pt-BR")]
    public void Resolve_PrefersAValidStoredChoiceThenTheBrowserLanguage(string? stored, string browser, string expected) =>
        CulturePreference.Resolve(stored, CultureInfo.GetCultureInfo(browser)).Name.ShouldBe(expected);

    [Fact]
    public async Task ApplyStoredAsync_SetsTheCultureAndThePageLanguage()
    {
        JSInterop.Setup<string?>("localStorage.getItem", CulturePreference.StorageKey).SetResult("pt-BR");
        var lang = JSInterop.SetupVoid("document.documentElement.setAttribute", "lang", "pt-BR");
        lang.SetVoidResult();
        var preference = Services.GetRequiredService<CulturePreference>();
        try
        {
            var culture = await preference.ApplyStoredAsync();

            culture.Name.ShouldBe("pt-BR");
            CultureInfo.DefaultThreadCurrentUICulture.ShouldNotBeNull().Name.ShouldBe("pt-BR", "the whole app, not just this call, switches language");
            CultureInfo.DefaultThreadCurrentCulture.ShouldNotBeNull().Name.ShouldBe("pt-BR");
            lang.Invocations.Count.ShouldBe(1, "screen readers read the page in the language of its lang attribute");
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = null;
            CultureInfo.DefaultThreadCurrentUICulture = null;
        }
    }

    [Fact]
    public async Task SaveAsync_UnsupportedCulture_IsRefused()
    {
        var preference = Services.GetRequiredService<CulturePreference>();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => preference.SaveAsync(CultureInfo.GetCultureInfo("fr-FR")));
    }
}
