using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Infrastructure.Identity;
using FacebookPublisher.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FacebookPublisher.Infrastructure.Persistence;

/// <summary>
/// Start-up tasks: migrations, roles, the first administrator and one-time moves of pre-multi-user files.
/// Campaigns are deliberately not touched: nothing is resumed or re-queued at start-up. Campaigns interrupted by a
/// shutdown are recovered (per user) when their owner next opens the app, and continue only when they click Resume.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<AppDbContext> dbFactory,
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IOptions<InitialAdminOptions> initialAdmin,
    LocalFileStorage storage,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.Database.MigrateAsync(ct);
        }

        foreach (var role in new[] { AppRoles.Admin, AppRoles.User })
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new IdentityRole<Guid>(role));
            }
        }

        await EnsureAdministratorAsync();
        await MoveLegacyUploadsAsync(ct);
    }

    /// <summary>
    /// Creates the first administrator from configuration when no active administrator exists. If the multi-user
    /// migration created a placeholder owner for existing data, the administrator takes that account over, so all
    /// existing groups, content, campaigns and settings become theirs. The password is never logged.
    /// </summary>
    private async Task EnsureAdministratorAsync()
    {
        var options = initialAdmin.Value;
        var admins = await users.GetUsersInRoleAsync(AppRoles.Admin);
        if (admins.Any(a => !a.IsDisabled))
        {
            if (!string.IsNullOrEmpty(options.Password))
            {
                logger.LogWarning("An administrator already exists; InitialAdmin:Password is ignored. Remove it from the configuration.");
            }
            return;
        }

        var email = options.Email?.Trim();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(options.Password))
        {
            logger.LogError(
                "No administrator account exists. Set InitialAdmin:Email and InitialAdmin:Password (for example as the " +
                "environment variables InitialAdmin__Email and InitialAdmin__Password) and restart the application.");
            return;
        }

        var user = await users.FindByEmailAsync(email);
        var legacy = await users.FindByIdAsync(ApplicationUser.LegacyOwnerId.ToString());
        if (user is null && legacy is { IsLegacyOwner: true })
        {
            user = legacy; // take over the placeholder that owns the pre-existing data
        }

        if (user is null)
        {
            user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, LockoutEnabled = true, DisplayName = options.DisplayName };
            if (!Succeeded(await users.CreateAsync(user, options.Password), "create the initial administrator"))
            {
                return;
            }
        }
        else
        {
            var tookOverLegacy = user.IsLegacyOwner;
            user.UserName = email;
            user.Email = email;
            user.EmailConfirmed = true;
            user.DisplayName ??= options.DisplayName;
            user.IsDisabled = false;
            user.IsLegacyOwner = false;
            user.LockoutEnabled = true;
            user.LockoutEnd = null;
            if (!Succeeded(await users.UpdateAsync(user), "update the initial administrator"))
            {
                return;
            }
            if (!await users.HasPasswordAsync(user)
                && !Succeeded(await users.AddPasswordAsync(user, options.Password), "set the initial administrator password"))
            {
                return;
            }
            if (tookOverLegacy)
            {
                logger.LogInformation("Initial administrator {UserId} took over the data that existed before multi-user support", user.Id);
            }
        }

        foreach (var role in new[] { AppRoles.User, AppRoles.Admin })
        {
            if (!await users.IsInRoleAsync(user, role))
            {
                await users.AddToRoleAsync(user, role);
            }
        }
        logger.LogInformation("Initial administrator {UserId} is ready. Remove InitialAdmin:Password from the configuration.", user.Id);
    }

    private bool Succeeded(IdentityResult result, string action)
    {
        if (result.Succeeded)
        {
            return true;
        }
        logger.LogError("Could not {Action}: {Errors}", action, string.Join(" ", result.Errors.Select(e => e.Description)));
        return false;
    }

    /// <summary>Moves uploads stored before multi-user support (flat folder) into their owner's folder.</summary>
    private async Task MoveLegacyUploadsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AsUnscoped();
        var attachments = await db.Attachments.AsNoTracking()
            .Select(a => new { a.StoredFileName, a.ContentPost!.OwnerId })
            .ToListAsync(ct);

        var moved = 0;
        foreach (var attachment in attachments)
        {
            try
            {
                if (storage.MoveLegacyFile(attachment.OwnerId, attachment.StoredFileName))
                {
                    moved++;
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not move upload {StoredFileName} to its owner's folder", attachment.StoredFileName);
            }
        }
        if (moved > 0)
        {
            logger.LogInformation("Moved {Count} existing upload(s) into their owners' folders", moved);
        }
    }
}
