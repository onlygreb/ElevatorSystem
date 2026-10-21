using System.Security.Cryptography.X509Certificates;
using ElevatorSystem.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Api.Tests.Hosting;

/// <summary>Docker Compose starts with no setup: the API makes its own certificate when there is none (ADR-0034).</summary>
public sealed class SelfSignedCertificateTests : IDisposable
{
    private const string Password = "certificate-test-password";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "elevator-cert-" + Guid.NewGuid().ToString("N"));

    private string CertificatePath => Path.Combine(_folder, "https", "elevator.pfx");

    [Fact]
    public void WhenAskedAndMissing_MakesACertificateForThisMachine_ValidForAYear()
    {
        var created = SelfSignedCertificate.EnsureExists(Settings(create: true), new FakeTimeProvider(Now));

        created.ShouldBe(CertificatePath);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, Password);
        certificate.HasPrivateKey.ShouldBeTrue();
        certificate.Subject.ShouldBe("CN=localhost");
        certificate.NotAfter.ToUniversalTime().ShouldBe(Now.AddYears(1).UtcDateTime);
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().ShouldHaveSingleItem();
        names.EnumerateDnsNames().ShouldBe(["localhost"]);
        names.EnumerateIPAddresses().Select(address => address.ToString()).ShouldBe(["127.0.0.1", "::1"]);
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ShouldHaveSingleItem()
            .EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Select(usage => usage.Value).ShouldBe(["1.3.6.1.5.5.7.3.1"], "server authentication");
    }

    [Fact]
    public void AnExistingCertificate_IsNeverReplaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CertificatePath)!);
        File.WriteAllText(CertificatePath, "your own certificate");

        SelfSignedCertificate.EnsureExists(Settings(create: true), new FakeTimeProvider(Now)).ShouldBeNull();

        File.ReadAllText(CertificatePath).ShouldBe("your own certificate");
    }

    [Fact]
    public void WhenNotAsked_MakesNothing()
    {
        SelfSignedCertificate.EnsureExists(Settings(create: false), new FakeTimeProvider(Now)).ShouldBeNull();

        File.Exists(CertificatePath).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private IConfiguration Settings(bool create) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SelfSignedCertificate.Setting] = create ? "true" : "false",
            [SelfSignedCertificate.PathSetting] = CertificatePath,
            [SelfSignedCertificate.PasswordSetting] = Password,
        }).Build();
}
