using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using ElevatorSystem.Contracts;
using ElevatorSystem.Infrastructure.Messaging;

namespace ElevatorSystem.Messaging.Tests;

/// <summary>
/// Every command the JSON contract knows can travel through the broker under the same type name. A command added to
/// the contract but not to the envelope would only fail against a real broker, as SchedulingCommandV1 once did.
/// </summary>
public sealed class EnvelopeTypeTests
{
    public static TheoryData<string, string> EveryCommand
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var derived in typeof(ElevatorCommandV1).GetCustomAttributes<JsonDerivedTypeAttribute>())
            {
                data.Add(derived.DerivedType.Name, (string)derived.TypeDiscriminator!);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void TypeOf_NamesEveryContractCommand_AsTheContractDoes(string commandType, string discriminator)
    {
        var type = typeof(ElevatorCommandV1).Assembly.GetType($"ElevatorSystem.Contracts.{commandType}")!;
        var command = (ElevatorCommandV1)RuntimeHelpers.GetUninitializedObject(type);

        RabbitMqEnvelope.TypeOf(command).ShouldBe(discriminator);
    }
}
