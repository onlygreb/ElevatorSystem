using System.Net;
using System.Net.Http.Json;
using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace ElevatorSystem.Api.Tests.Endpoints;

/// <summary>Anonymous passengers who walk in and out of the car themselves, end to end through the API (ADR-0026).</summary>
public sealed class PassengerEndpointTests
{
    [Fact]
    public async Task AJourney_ArriveWalkInChooseAFloorAndWalkOut()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await factory.StatusAsync(status => status is { Status: ElevatorStatus.DoorOpen, CurrentFloor: 1 });

        var me = await JoinAsync(client);
        await PassengerAsync(factory, me.PassengerId, passenger => passenger.Floor == 1);
        (await client.PostJsonAsync(ApiRoutes.PassengerRoute(me.PassengerId) + "/board", "{}")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await PassengerAsync(factory, me.PassengerId, passenger => passenger.InCar);
        (await client.PostJsonAsync(ApiRoutes.PassengerRoute(me.PassengerId) + "/car-call", """{"floor":3}""")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.StatusAsync(status => status is { Status: ElevatorStatus.DoorOpen, CurrentFloor: 3 });
        (await client.PostJsonAsync(ApiRoutes.PassengerRoute(me.PassengerId) + "/leave", "{}")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var arrived = await PassengerAsync(factory, me.PassengerId, passenger => passenger is { InCar: false, Floor: 3 });

        arrived.ShouldBe(new PassengerDto(me.PassengerId, 3, false, null, null, false));
        using var response = await client.GetRouteAsync(ApiRoutes.PassengerRoute(me.PassengerId));
        (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.PassengerDto, Http.Cancellation)).ShouldBe(arrived);
    }

    [Fact]
    public async Task CallingTheCar_MarksThePassengerWaiting_AndBringsTheCar()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await factory.StatusAsync();
        var me = await JoinAsync(client);
        await PassengerAsync(factory, me.PassengerId, _ => true);
        await RideAsync(factory, client, me.PassengerId, toFloor: 5);
        var other = await JoinAsync(client);
        await PassengerAsync(factory, other.PassengerId, _ => true);

        using var call = await client.PostJsonAsync(ApiRoutes.PassengerRoute(other.PassengerId) + "/hall-call", """{"direction":"Up"}""");

        call.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await PassengerAsync(factory, other.PassengerId, passenger => passenger.Waiting == CallDirection.Up);
        await factory.StatusAsync(status => status is { Status: ElevatorStatus.DoorOpen, CurrentFloor: 1 });
    }

    [Fact]
    public async Task WalkingIn_WhenTheDoorIsNotOpenOnTheirFloor_IsReportedAsRefused()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await factory.StatusAsync();
        var rider = await JoinAsync(client);
        await PassengerAsync(factory, rider.PassengerId, _ => true);
        await RideAsync(factory, client, rider.PassengerId, toFloor: 6);
        var late = await JoinAsync(client);
        await PassengerAsync(factory, late.PassengerId, _ => true);
        await using var hub = await RealTime.HubClient.ConnectAsync(factory);

        using var accepted = await client.PostJsonAsync(ApiRoutes.PassengerRoute(late.PassengerId) + "/board", "{}");
        var answer = await accepted.Content.ReadFromJsonAsync(ContractsJsonContext.Default.PassengerAcceptedResponse, Http.Cancellation);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, "the engine decides; the outcome is on the hub");
        var refused = await hub.NextActivityAsync(activity => activity.RequestId == answer!.RequestId);
        refused.Kind.ShouldBe(ElevatorActivityKind.PassengerRefused);
        refused.PassengerRefusal.ShouldBe(PassengerRefusalReason.DoorNotOpenHere);
        refused.PassengerAction.ShouldBe(PassengerAction.Board);
    }

    [Theory]
    [InlineData("/car-call", """{"floor":11}""")]
    [InlineData("/car-call", """{"floor":"3"}""")]
    [InlineData("/hall-call", """{"direction":"Sideways"}""")]
    [InlineData("/hall-call", """{}""")]
    public async Task InvalidSteps_AreRefusedAtTheDoor(string route, string json)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostJsonAsync(ApiRoutes.PassengerRoute(Guid.NewGuid()) + route, json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnUnknownPassenger_IsNotFound()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetRouteAsync(ApiRoutes.PassengerRoute(Guid.NewGuid()));

        (await response.ShouldBeProblemAsync(HttpStatusCode.NotFound)).Reason().ShouldBe(nameof(PassengerRefusalReason.UnknownPassenger));
    }

    [Fact]
    public async Task EveryArrival_GetsItsOwnRandomId()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var first = await JoinAsync(client);
        var second = await JoinAsync(client);

        first.PassengerId.ShouldNotBe(second.PassengerId);
        first.PassengerId.ToString("D")[14].ShouldBe('4', "a random (version 4) id, unrelated to time or order");
    }

    internal static async Task<PassengerAcceptedResponse> JoinAsync(HttpClient client)
    {
        using var response = await client.PostJsonAsync(ApiRoutes.Passengers, "{}");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.PassengerAcceptedResponse, Http.Cancellation))!;
    }

    /// <summary>Walks a passenger into the car waiting open at their floor and out again at <paramref name="toFloor"/>.</summary>
    internal static async Task RideAsync(ApiFactory factory, HttpClient client, Guid passengerId, int toFloor)
    {
        var start = factory.Services.GetRequiredService<ElevatorStateCache>().Passenger(passengerId)!.Floor;
        await factory.StatusAsync(status => status is { Status: ElevatorStatus.DoorOpen } && status.CurrentFloor == start);
        (await client.PostJsonAsync(ApiRoutes.PassengerRoute(passengerId) + "/board", "{}")).Dispose();
        await PassengerAsync(factory, passengerId, passenger => passenger.InCar);
        (await client.PostJsonAsync(ApiRoutes.PassengerRoute(passengerId) + "/car-call", $$"""{"floor":{{toFloor}}}""")).Dispose();
        await factory.StatusAsync(status => status is { Status: ElevatorStatus.DoorOpen } && status.CurrentFloor == toFloor);
        (await client.PostJsonAsync(ApiRoutes.PassengerRoute(passengerId) + "/leave", "{}")).Dispose();
        await PassengerAsync(factory, passengerId, passenger => !passenger.InCar && passenger.Floor == toFloor);
    }

    internal static Task<PassengerDto> PassengerAsync(ApiFactory factory, Guid passengerId, Func<PassengerDto, bool> match)
    {
        var cache = factory.Services.GetRequiredService<ElevatorStateCache>();
        return Eventually.ReturnsAsync(
            () => Task.FromResult(cache.Passenger(passengerId) is { } passenger && match(passenger) ? passenger : null),
            "the passenger to be where expected");
    }
}
