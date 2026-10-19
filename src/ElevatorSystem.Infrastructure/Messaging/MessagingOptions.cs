using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// How to reach RabbitMQ, bound from the <c>Messaging</c> configuration section and validated when the host starts.
/// Either <see cref="Uri"/> (for example an Aspire connection string) or <see cref="Host"/> with credentials.
/// The password is a secret: it comes from user-secrets, Aspire parameters or environment secrets, never from
/// appsettings files, and this type never prints it.
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary><c>amqp://</c> or <c>amqps://</c> URI; when set it takes precedence over the separate settings.</summary>
    public Uri? Uri { get; set; }

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    /// <summary>The dedicated virtual host. Deployments use <c>elevator</c>; the default suits a local broker.</summary>
    public string VirtualHost { get; set; } = "/";

    public string? UserName { get; set; }

    public string? Password { get; set; }

    /// <summary>Connect with TLS. Required outside local development (messaging skill).</summary>
    public bool UseTls { get; set; }

    /// <summary>The connection name shown in the broker's management UI, for example the service name.</summary>
    public string ClientName { get; set; } = "ElevatorSystem";

    /// <summary>Names this service's update queues (<c>elevator.updates.{SubscriberName}.{id}</c>).</summary>
    public string SubscriberName { get; set; } = "subscriber";

    /// <summary>How long to wait for the broker to accept a connection.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a publish waits for the broker's confirm before it fails with <c>MessagingUnavailableException</c>.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Delay between reconnection attempts, both for automatic recovery and for the first connection.</summary>
    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most unacknowledged messages a consumer holds at once.</summary>
    public ushort PrefetchCount { get; set; } = 32;

    /// <summary>Endpoint description safe for logs and health reports: no user name or password.</summary>
    public string Describe() => Uri is { } uri
        ? $"{uri.Scheme}://{uri.Host}:{(uri.IsDefaultPort ? (uri.Scheme == "amqps" ? 5671 : 5672) : uri.Port)}{uri.AbsolutePath}"
        : $"{(UseTls ? "amqps" : "amqp")}://{Host}:{Port}/{Uri.EscapeDataString(VirtualHost)}";

    public override string ToString() => Describe();

    /// <summary>Configuration values that could not be parsed; reported by the validator when the host starts.</summary>
    internal List<string> BindingErrors { get; } = [];

    /// <summary>Reads the <see cref="SectionName"/> section without reflection; malformed values are reported on start.</summary>
    internal static void Bind(IConfiguration section, MessagingOptions options)
    {
        var errors = options.BindingErrors;
        if (section["Uri"] is { Length: > 0 } uri)
        {
            options.Uri = System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed : null;
            if (options.Uri is null)
            {
                errors.Add("Messaging:Uri is not an absolute URI.");
            }
        }

        options.Host = section["Host"] ?? options.Host;
        options.VirtualHost = section["VirtualHost"] ?? options.VirtualHost;
        options.UserName = section["UserName"] ?? options.UserName;
        options.Password = section["Password"] ?? options.Password;
        options.ClientName = section["ClientName"] ?? options.ClientName;
        options.SubscriberName = section["SubscriberName"] ?? options.SubscriberName;
        options.Port = Parse(section, "Port", options.Port, errors, (string text, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value));
        options.UseTls = Parse(section, "UseTls", options.UseTls, errors, bool.TryParse);
        options.ConnectTimeout = Parse(section, "ConnectTimeout", options.ConnectTimeout, errors, ParseTimeSpan);
        options.PublishTimeout = Parse(section, "PublishTimeout", options.PublishTimeout, errors, ParseTimeSpan);
        options.RecoveryInterval = Parse(section, "RecoveryInterval", options.RecoveryInterval, errors, ParseTimeSpan);
        options.PrefetchCount = Parse(section, "PrefetchCount", options.PrefetchCount, errors, (string text, out ushort value) => ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value));
    }

    private delegate bool TryParse<T>(string text, out T value);

    private static bool ParseTimeSpan(string text, out TimeSpan value) =>
        TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value);

    private static T Parse<T>(IConfiguration section, string key, T current, List<string> errors, TryParse<T> tryParse)
    {
        if (section[key] is not { Length: > 0 } text)
        {
            return current;
        }

        if (tryParse(text, out var value))
        {
            return value;
        }

        // The key only: a malformed value might be a misplaced secret.
        errors.Add($"Messaging:{key} has an invalid value.");
        return current;
    }
}

/// <summary>Checks <see cref="MessagingOptions"/> when the host starts, so a misconfigured service fails fast.</summary>
internal sealed class MessagingOptionsValidator : IValidateOptions<MessagingOptions>
{
    public ValidateOptionsResult Validate(string? name, MessagingOptions options)
    {
        List<string> errors = [.. options.BindingErrors];

        if (options.Uri is { } uri)
        {
            if (uri.Scheme is not ("amqp" or "amqps"))
            {
                errors.Add("Messaging:Uri must use the amqp or amqps scheme.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.Host))
            {
                errors.Add("Messaging:Host or Messaging:Uri is required.");
            }

            if (options.Port is < 1 or > 65535)
            {
                errors.Add("Messaging:Port must be between 1 and 65535.");
            }

            if (string.IsNullOrWhiteSpace(options.VirtualHost))
            {
                errors.Add("Messaging:VirtualHost is required.");
            }

            if (string.IsNullOrWhiteSpace(options.UserName) || string.IsNullOrEmpty(options.Password))
            {
                errors.Add("Messaging:UserName and Messaging:Password are required; the broker accepts no anonymous connections.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.SubscriberName) || options.SubscriberName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            errors.Add("Messaging:SubscriberName must be letters, digits, '-' or '_'.");
        }

        CheckPositive(options.ConnectTimeout, "ConnectTimeout", errors);
        CheckPositive(options.PublishTimeout, "PublishTimeout", errors);
        CheckPositive(options.RecoveryInterval, "RecoveryInterval", errors);

        if (options.PrefetchCount is < 1 or > 1000)
        {
            errors.Add("Messaging:PrefetchCount must be between 1 and 1000.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckPositive(TimeSpan value, string key, List<string> errors)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5))
        {
            errors.Add($"Messaging:{key} must be more than zero and at most five minutes.");
        }
    }
}
