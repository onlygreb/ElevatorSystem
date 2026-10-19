using System.Text.Json;
using System.Text.Json.Serialization;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api;

/// <summary>
/// Applies the wire format defined by <see cref="ContractsJsonContext"/> to a serializer used by the host,
/// so REST responses and hub messages serialize the contracts exactly as the browser client reads them.
/// </summary>
internal static class ContractsJson
{
    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // First in the chain: contract types use the source-generated metadata; anything else
        // (ProblemDetails, framework types) falls through to the resolvers already configured.
        options.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default);
        options.Converters.Add(new JsonStringEnumConverter());
    }
}
