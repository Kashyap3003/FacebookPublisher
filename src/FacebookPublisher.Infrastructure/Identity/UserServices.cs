using System.ComponentModel.DataAnnotations;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Infrastructure.Identity;

/// <summary>Configuration for provisioning the first administrator (section "InitialAdmin", or InitialAdmin__* variables).</summary>
public sealed class InitialAdminOptions
{
    public const string SectionName = "InitialAdmin";

    public string? Email { get; set; }

    /// <summary>Used once, when no administrator exists yet. Remove it from configuration afterwards.</summary>
    public string? Password { get; set; }

    public string? DisplayName { get; set; }
}

public sealed class UserAccounts(IDbContextFactory<AppDbContext> dbFactory) : IUserAccounts
{
    public async Task<bool> IsActiveAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AnyAsync(u => u.Id == userId && !u.IsDisabled && !u.IsLegacyOwner, cancellationToken);
    }
}

/// <summary>
/// Administrator-only account management. Each operation uses its own DI scope (and therefore its own Identity
/// DbContext), because Blazor Server circuits are long-lived and must not share one context across operations.
/// </summary>
public sealed class UserAdministration(
    IServiceScopeFactory scopeFactory,
    ICurrentUser currentUser,
    ICampaignExecutionTracker tracker,
    ILogger<UserAdministration> logger) : IUserAdministration
{
    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(cancellationToken);
        using var scope = scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var adminIds = (await users.GetUsersInRoleAsync(AppRoles.Admin)).Select(u => u.Id).ToHashSet();
        var all = await users.Users.AsNoTracking()
            .Where(u => !u.IsLegacyOwner)
            .OrderBy(u => u.Email)
            .ToListAsync(cancellationToken);
        return all.Select(u => ToDto(u, adminIds.Contains(u.Id))).ToList();
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        var callerId = await EnsureAdminAsync(cancellationToken);
        var email = request.Email?.Trim() ?? string.Empty;
        if (!new EmailAddressAttribute().IsValid(email) || email.Length > 256)
        {
            throw new AppValidationException("Enter a valid email address.");
        }

        using var scope = scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(email) is not null)
        {
            throw new AppValidationException("An account with this email already exists.");
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = CleanName(request.DisplayName),
            LockoutEnabled = true
        };
        ThrowIfFailed(await users.CreateAsync(user, request.Password ?? string.Empty), "The account could not be created.");
        ThrowIfFailed(await users.AddToRoleAsync(user, AppRoles.User), "The account could not be created.");
        if (request.IsAdmin)
        {
            ThrowIfFailed(await users.AddToRoleAsync(user, AppRoles.Admin), "The administrator role could not be assigned.");
        }

        logger.LogInformation("User {UserId} created by administrator {AdminId} (admin: {IsAdmin})", user.Id, callerId, request.IsAdmin);
        return ToDto(user, request.IsAdmin);
    }

    public async Task UpdateAsync(Guid userId, string? displayName, bool isAdmin, CancellationToken cancellationToken = default)
    {
        var callerId = await EnsureAdminAsync(cancellationToken);
        using var scope = scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await FindAsync(users, userId);

        var wasAdmin = await users.IsInRoleAsync(user, AppRoles.Admin);
        if (wasAdmin && !isAdmin)
        {
            if (userId == callerId)
            {
                throw new AppValidationException("You cannot remove your own administrator role.");
            }
            await EnsureAnotherActiveAdminAsync(users, userId);
        }

        user.DisplayName = CleanName(displayName);
        ThrowIfFailed(await users.UpdateAsync(user), "The account could not be updated.");
        if (isAdmin && !wasAdmin)
        {
            ThrowIfFailed(await users.AddToRoleAsync(user, AppRoles.Admin), "The administrator role could not be assigned.");
        }
        else if (!isAdmin && wasAdmin)
        {
            ThrowIfFailed(await users.RemoveFromRoleAsync(user, AppRoles.Admin), "The administrator role could not be removed.");
            await users.UpdateSecurityStampAsync(user); // role change takes effect at the next revalidation
        }
        logger.LogInformation("User {UserId} updated by administrator {AdminId} (admin: {IsAdmin})", userId, callerId, isAdmin);
    }

    public async Task SetDisabledAsync(Guid userId, bool disabled, CancellationToken cancellationToken = default)
    {
        var callerId = await EnsureAdminAsync(cancellationToken);
        if (disabled && userId == callerId)
        {
            throw new AppValidationException("You cannot disable your own account.");
        }

        using var scope = scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await FindAsync(users, userId);
        if (disabled && await users.IsInRoleAsync(user, AppRoles.Admin))
        {
            await EnsureAnotherActiveAdminAsync(users, userId);
        }

        user.IsDisabled = disabled;
        // A lockout end in the far future also blocks password sign-in at the Identity level.
        user.LockoutEnabled = true;
        user.LockoutEnd = disabled ? DateTimeOffset.MaxValue : null;
        if (!disabled)
        {
            user.AccessFailedCount = 0;
        }
        ThrowIfFailed(await users.UpdateAsync(user), "The account could not be updated.");
        // New security stamp: existing sign-in cookies are rejected at the next validation (within a minute).
        await users.UpdateSecurityStampAsync(user);

        if (disabled)
        {
            var stopped = tracker.RequestCancelForOwner(userId);
            logger.LogInformation("User {UserId} disabled by administrator {AdminId}; {Stopped} running campaign(s) stopped", userId, callerId, stopped);
        }
        else
        {
            logger.LogInformation("User {UserId} enabled by administrator {AdminId}", userId, callerId);
        }
    }

    public async Task ResetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default)
    {
        var callerId = await EnsureAdminAsync(cancellationToken);
        using var scope = scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await FindAsync(users, userId);

        var token = await users.GeneratePasswordResetTokenAsync(user);
        ThrowIfFailed(await users.ResetPasswordAsync(user, token, newPassword ?? string.Empty), "The password could not be changed.");
        if (!user.IsDisabled)
        {
            await users.ResetAccessFailedCountAsync(user);
            await users.SetLockoutEndDateAsync(user, null);
        }
        logger.LogInformation("Password of user {UserId} reset by administrator {AdminId}", userId, callerId);
    }

    private async Task<Guid> EnsureAdminAsync(CancellationToken cancellationToken)
    {
        var callerId = await currentUser.GetUserIdAsync(cancellationToken);
        if (!await currentUser.IsAdminAsync(cancellationToken))
        {
            logger.LogWarning("User {UserId} attempted an administrator operation", callerId);
            throw new UnauthorizedAccessException("Only administrators can manage users.");
        }
        return callerId;
    }

    private static async Task<ApplicationUser> FindAsync(UserManager<ApplicationUser> users, Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is null || user.IsLegacyOwner ? throw new NotFoundException("User", userId) : user;
    }

    private static async Task EnsureAnotherActiveAdminAsync(UserManager<ApplicationUser> users, Guid excludingUserId)
    {
        var admins = await users.GetUsersInRoleAsync(AppRoles.Admin);
        if (!admins.Any(a => a.Id != excludingUserId && !a.IsDisabled))
        {
            throw new AppValidationException("At least one active administrator must remain.");
        }
    }

    private static string? CleanName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 200)];

    private static void ThrowIfFailed(IdentityResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new AppValidationException(message, result.Errors.Select(e => e.Description).ToList());
        }
    }

    private static UserDto ToDto(ApplicationUser u, bool isAdmin) => new(
        u.Id,
        u.Email ?? u.UserName ?? string.Empty,
        u.DisplayName,
        isAdmin,
        u.IsDisabled,
        !u.IsDisabled && u.LockoutEnd is { } end && end > DateTimeOffset.UtcNow,
        u.CreatedAtUtc,
        u.LastLoginAtUtc);
}
