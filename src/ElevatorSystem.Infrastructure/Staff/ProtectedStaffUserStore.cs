using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace ElevatorSystem.Infrastructure.Staff;

/// <summary>
/// The Identity user store, with the tokens Identity keeps for itself (the TOTP authenticator key and the
/// recovery codes) encrypted with ASP.NET Core Data Protection before they reach the database (threat T-DB-05).
/// A copy of the table alone does not give an attacker anyone's second factor.
/// </summary>
public sealed class ProtectedStaffUserStore(StaffDbContext context, IDataProtectionProvider dataProtection, IdentityErrorDescriber? describer = null)
    : UserStore<StaffUser, IdentityRole<Guid>, StaffDbContext, Guid>(context, describer)
{
    /// <summary>The login provider name Identity uses for its own tokens (authenticator key, recovery codes).</summary>
    public const string InternalLoginProvider = "[AspNetUserStore]";

    public const string ProtectorPurpose = "ElevatorSystem.Staff.InternalTokens.v1";

    private readonly IDataProtector _protector = (dataProtection ?? throw new ArgumentNullException(nameof(dataProtection))).CreateProtector(ProtectorPurpose);

    public override Task SetTokenAsync(StaffUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken)
    {
        var stored = value is not null && loginProvider == InternalLoginProvider ? _protector.Protect(value) : value;
        return base.SetTokenAsync(user, loginProvider, name, stored, cancellationToken);
    }

    public override async Task<string?> GetTokenAsync(StaffUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var stored = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        return stored is not null && loginProvider == InternalLoginProvider ? _protector.Unprotect(stored) : stored;
    }
}
