using Microsoft.AspNetCore.Identity;

namespace FacebookPublisher.Infrastructure.Identity;

/// <summary>
/// A Group Publisher account (ASP.NET Core Identity). Its <see cref="IdentityUser{TKey}.Id"/> is the owner id stored on
/// every group, content item, campaign and settings row, and names the user's upload and browser-profile folders.
/// Application accounts are completely separate from the Facebook accounts users connect in their browser profile.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>
    /// Fixed id of the placeholder account the multi-user migration creates to hold data that existed before accounts
    /// were introduced. It cannot sign in; the first administrator provisioned from configuration takes it over.
    /// </summary>
    public static readonly Guid LegacyOwnerId = new("6f1c3a52-9d47-4e0b-8a2f-5b7e1d0c9a31");

    public string? DisplayName { get; set; }

    /// <summary>Disabled accounts cannot sign in, are signed out within a minute and cannot run campaigns.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>True only for the migration placeholder until an administrator takes it over.</summary>
    public bool IsLegacyOwner { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAtUtc { get; set; }
}
