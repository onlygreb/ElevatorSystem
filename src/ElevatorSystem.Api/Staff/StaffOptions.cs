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
