using System.ComponentModel.DataAnnotations;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.Staff;

/// <summary>Settings of the <c>Staff</c> configuration section.</summary>
public sealed class StaffOptions
{
    public const string SectionName = "Staff";

    /// <summary>Distributed mode: the connection string of the PostgreSQL database that holds the <c>staff</c> schema.</summary>
    [Required]
    public string ConnectionStringName { get; set; } = "elevatordb";

    /// <summary>
    /// Accounts created at start-up when no account with that e-mail exists yet; an existing account is never
    /// changed. There is no public registration, so this is how the first admin appears. Passwords come from a
    /// secret store (Aspire parameters, user secrets), never from <c>appsettings</c>.
    /// </summary>
    public IList<StaffAccountSeed> Accounts { get; } = [];

    /// <summary>
    /// A test account that signs in with its password only, for demonstrations and testing (ADR-0030); off unless both the
    /// e-mail and the password are set. It is marked as a test account when it is created, so an existing account is never
    /// exempted, and it loses the exemption as soon as this setting is removed.
    /// </summary>
    public StaffAccountSeed? TestAccount { get; set; }

    /// <summary>The test account, when one is configured.</summary>
    public StaffAccountSeed? ConfiguredTestAccount =>
        TestAccount is { Email.Length: > 0, Password.Length: > 0 } account ? account : null;
}

/// <summary>An account to create at start-up.</summary>
public sealed class StaffAccountSeed
{
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";

    public StaffRole Role { get; set; } = StaffRole.Admin;
}
