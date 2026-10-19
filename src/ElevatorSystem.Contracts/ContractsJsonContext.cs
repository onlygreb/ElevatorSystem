using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElevatorSystem.Contracts;

/// <summary>
/// Source-generated serialization for every contract: web defaults (camelCase properties) with enums
/// written as names. Server and client both use it, so the wire format is defined in one place.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HallCallRequest))]
[JsonSerializable(typeof(CarCallRequest))]
[JsonSerializable(typeof(RequestAcceptedResponse))]
[JsonSerializable(typeof(ElevatorConfigurationDto))]
[JsonSerializable(typeof(ElevatorStatusDto))]
[JsonSerializable(typeof(ElevatorActivityDto))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
