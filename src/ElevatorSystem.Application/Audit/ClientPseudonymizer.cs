using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Application.Audit;

/// <summary>
/// Turns a client address into a keyed pseudonym for the audit trail: <c>keyId:</c> followed by the first
/// 16 bytes of <c>HMAC-SHA256(key, address)</c> in hex. The same address gives the same pseudonym under one
/// key, so repeated attempts from one place can be linked, but without the key the address cannot be
/// recovered or guessed (security skill). Rotating the key breaks the links, by design.
/// </summary>
public sealed class ClientPseudonymizer
{
    private const int PseudonymBytes = 16;

    private readonly byte[] _key;
    private readonly string _keyId;

    public ClientPseudonymizer(IOptions<AuditOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        if (string.IsNullOrEmpty(settings.ClientPseudonymKey))
        {
            // No key configured: a random one for this process, so pseudonyms link only until the next start.
            _key = RandomNumberGenerator.GetBytes(AuditOptions.MinimumKeyBytes);
            _keyId = "p" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
            IsEphemeral = true;
        }
        else
        {
            _key = Convert.FromBase64String(settings.ClientPseudonymKey);
            _keyId = settings.ClientPseudonymKeyId;
        }
    }

    /// <summary>The key was generated at start-up because none is configured.</summary>
    public bool IsEphemeral { get; }

    public string Pseudonymise(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var normalised = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_key, normalised.GetAddressBytes(), mac);
        return _keyId + ":" + Convert.ToHexStringLower(mac[..PseudonymBytes]);
    }
}

/// <summary>Settings of the <c>Audit</c> configuration section.</summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";
    public const int MinimumKeyBytes = 32;

    /// <summary>
    /// Base64 HMAC key (at least 32 bytes) for client pseudonyms. A secret: supply it from a secret store, never
    /// from <c>appsettings</c>. When it is missing, a random key is generated at every start.
    /// </summary>
    public string? ClientPseudonymKey { get; set; }

    /// <summary>Names the key in every pseudonym, so pseudonyms made under different keys are never compared.</summary>
    [RegularExpression("^[a-z0-9-]{1,16}$")]
    public string ClientPseudonymKeyId { get; set; } = "k1";
}

/// <summary>Refuses a configured pseudonym key that is not base64 or is too short.</summary>
public sealed class AuditOptionsValidator : IValidateOptions<AuditOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(options.ClientPseudonymKey))
        {
            return ValidateOptionsResult.Success;
        }

        var key = new byte[options.ClientPseudonymKey.Length];
        return Convert.TryFromBase64String(options.ClientPseudonymKey, key, out var length) && length >= AuditOptions.MinimumKeyBytes
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Audit:ClientPseudonymKey must be base64 for at least {AuditOptions.MinimumKeyBytes} bytes.");
    }
}
