using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Features.Passenger;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests.Features;

[UseCulture("pt-BR")]
public sealed class PortugueseRenderingTests : WebTestContext
{
    [Fact]
    public void PassengerView_InPortuguese_UsesPortugueseHeadings()
    {
        var view = Render<PassengerView>();

        view.FindAll("h2").Select(heading => heading.TextContent)
            .ShouldBe(["Sua viagem", "Fila de chamadas", "Atividade recente", "Andares"]);
    }

    [Fact]
    public void Journey_InPortuguese_HasPortugueseButtonsAndNames()
    {
        var view = Render<PassengerView>();
        view.Find("[data-testid=arrive]").TextContent.ShouldContain("Entrar no prédio");

        view.Find("[data-testid=arrive]").Click();
        view.Find("[data-testid=journey-where]").TextContent.ShouldBe("Você está no andar 1");
        view.Find("[data-testid=call-up]").TextContent.ShouldContain("Chamar para subir");

        Store.AddActivity(new ElevatorActivityDto(TestData.Noon, ElevatorActivityKind.PassengerBoarded, 1, PassengerId: Passengers.PassengerId));
        view.WaitForAssertion(() => view.Find(".car-button[data-floor='7']").GetAttribute("aria-label").ShouldBe("Ir para o andar 7"));
        view.Find("[data-testid=leave]").TextContent.ShouldContain("Sair do elevador");
    }

    [Fact]
    public void Status_InPortuguese_NamesStateAndCause()
    {
        Api.Status = TestData.Status(floor: 2, status: ElevatorStatus.OutOfService, cause: OutOfServiceCause.Maintenance);

        var view = Render<PassengerView>();

        view.Find("[data-testid=status-state]").TextContent.Trim().ShouldBe("Fora de serviço");
        view.Find("[data-testid=status-out-of-service]").TextContent.Trim().ShouldBe("Causa: manutenção");
    }

    [Fact]
    public void Activity_InPortuguese_FormatsNumbersTheBrazilianWay()
    {
        var view = Render<PassengerView>();

        Store.AddActivity(new ElevatorActivityDto(TestData.Noon, ElevatorActivityKind.CallServed, 3,
            Call: new CallDto(CallKind.Hall, 3, CallDirection.Up), RequestCount: 1, WaitTime: TimeSpan.FromSeconds(2.5)));

        view.WaitForAssertion(() => view.Find(".activity__text").TextContent.ShouldBe("Chamada para andar 3 subindo atendida após 2,5 s."));
    }

    [Fact]
    public void Announcer_InPortuguese_AnnouncesInPortuguese()
    {
        var view = Render<PassengerView>();

        Store.AddActivity(TestData.DoorsOpened(5));

        view.WaitForAssertion(() => view.Find("[data-testid=announcer]").TextContent.ShouldBe("Andar 5, portas abrindo"));
    }

    [Fact]
    public void ReconnectBanner_InPortuguese()
    {
        var view = Render<PassengerView>();

        Store.SetConnection(ConnectionState.Reconnecting);

        view.WaitForAssertion(() => view.Find("[data-testid=connection-banner]").TextContent.ShouldContain("Reconectando…"));
    }

    [Fact]
    public void Queue_InPortuguese()
    {
        Api.Status = TestData.Status(pending: [TestData.HallCall(7, CallDirection.Down), TestData.CarCall(2, requestCount: 2)]);

        var view = Render<PassengerView>();

        view.FindAll(".queue-chip__text").Select(chip => chip.TextContent).ShouldBe(["Andar 7, descendo", "Andar 2, pela cabine"]);
        view.Find(".queue-chip__count").TextContent.ShouldBe("2 chamadas");
    }
}
