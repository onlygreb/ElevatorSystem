using System.Net;
using ElevatorSystem.Application.Audit;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Application.Tests.Audit;

/// <summary>Keyed pseudonyms for client addresses in the audit trail (security skill).</summary>
public sealed class ClientPseudonymizerTests
{
    private static readonly string KeyA = Convert.ToBase64String([.. Enumerable.Repeat((byte)0xA5, 32)]);
    private static readonly string KeyB = Convert.ToBase64String([.. Enumerable.Repeat((byte)0x5A, 32)]);

    [Fact]
    public void SameAddressAndKey_GiveTheSamePseudonym()
    {
        var pseudonymizer = Create(KeyA);

        pseudonymizer.Pseudonymise(IPAddress.Parse("198.51.100.7"))
            .ShouldBe(pseudonymizer.Pseudonymise(IPAddress.Parse("198.51.100.7")));
    }

    [Fact]
    public void Pseudonym_NamesTheKey_AndHides128BitsOfTheMac()
    {
        var pseudonym = Create(KeyA, keyId: "2026-10").Pseudonymise(IPAddress.Parse("198.51.100.7"));

        pseudonym.ShouldMatch("^2026-10:[0-9a-f]{32}$");
    }

    [Fact]
    public void DifferentKeys_GiveUnrelatedPseudonyms()
    {
        var address = IPAddress.Parse("198.51.100.7");

        Create(KeyA).Pseudonymise(address).ShouldNotBe(Create(KeyB).Pseudonymise(address));
    }

    [Fact]
    public void AnIPv4MappedAddress_IsTheSameClientAsItsIPv4Form()
    {
        var pseudonymizer = Create(KeyA);

        pseudonymizer.Pseudonymise(IPAddress.Parse("::ffff:198.51.100.7"))
            .ShouldBe(pseudonymizer.Pseudonymise(IPAddress.Parse("198.51.100.7")));
    }

    [Fact]
    public void WithoutAConfiguredKey_UsesARandomKeyPerProcess()
    {
        var first = new ClientPseudonymizer(Options.Create(new AuditOptions()));
        var second = new ClientPseudonymizer(Options.Create(new AuditOptions()));

        first.IsEphemeral.ShouldBeTrue();
        first.Pseudonymise(IPAddress.Loopback).ShouldNotBe(second.Pseudonymise(IPAddress.Loopback));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void Validator_RefusesAKeyThatIsNotBase64OrTooShort(string key)
    {
        new AuditOptionsValidator().Validate(null, new AuditOptions { ClientPseudonymKey = key }).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Validator_AcceptsA256BitKey_OrNone()
    {
        var validator = new AuditOptionsValidator();

        validator.Validate(null, new AuditOptions { ClientPseudonymKey = KeyA }).Succeeded.ShouldBeTrue();
        validator.Validate(null, new AuditOptions()).Succeeded.ShouldBeTrue();
    }

    private static ClientPseudonymizer Create(string key, string keyId = "k1") =>
        new(Options.Create(new AuditOptions { ClientPseudonymKey = key, ClientPseudonymKeyId = keyId }));
}
