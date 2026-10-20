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
[JsonSerializable(typeof(ElevatorCommandV1))]
[JsonSerializable(typeof(ElevatorUpdateV1))]
[JsonSerializable(typeof(OccupancyUpdateV1))]
[JsonSerializable(typeof(BuildingOccupancyDto))]
[JsonSerializable(typeof(SignInRequest))]
[JsonSerializable(typeof(StaffSessionDto))]
[JsonSerializable(typeof(AntiforgeryTokenDto))]
[JsonSerializable(typeof(TwoFactorSetupDto))]
[JsonSerializable(typeof(EnableTwoFactorRequest))]
[JsonSerializable(typeof(RecoveryCodesDto))]
[JsonSerializable(typeof(ServiceActionRequest))]
[JsonSerializable(typeof(ServiceActionAcceptedResponse))]
[JsonSerializable(typeof(AuditPageDto))]
[JsonSerializable(typeof(AuditVerificationDto))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
