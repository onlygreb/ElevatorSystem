using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ElevatorSystem.Api.Hosting;

/// <summary>
/// Creates a self-signed HTTPS certificate for this machine when Kestrel's default certificate file does not exist yet
/// and <see cref="Setting"/> is on, so Docker Compose starts with no setup at all (ADR-0034). The certificate names
/// <c>localhost</c>, 127.0.0.1 and ::1 and is valid for a year; browsers warn about it once. An existing file is never
/// touched, so a certificate of your own always wins.
/// </summary>
internal static class SelfSignedCertificate
{
    public const string Setting = "Tls:CreateSelfSignedCertificate";
    public const string PathSetting = "Kestrel:Certificates:Default:Path";
    public const string PasswordSetting = "Kestrel:Certificates:Default:Password";

    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <returns>The path of the certificate it created, or <see langword="null"/> when it created none.</returns>
    public static string? EnsureExists(IConfiguration configuration, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var path = configuration[PathSetting];
        if (!configuration.GetValue<bool>(Setting) || string.IsNullOrEmpty(path) || File.Exists(path))
        {
            return null;
        }

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthentication)], critical: false));

        var now = timeProvider.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, configuration[PasswordSetting]));
        return path;
    }
}
