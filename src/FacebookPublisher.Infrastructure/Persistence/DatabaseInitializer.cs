using FacebookPublisher.Application.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FacebookPublisher.Infrastructure.Persistence;

public sealed class DatabaseInitializer(IDbContextFactory<AppDbContext> dbFactory, ILogger<DatabaseInitializer> logger)
{
    /// <summary>
    /// Applies migrations and seeds default settings. Campaigns are deliberately not touched here: nothing is resumed
    /// or re-queued at start-up. Campaigns interrupted by a shutdown are recovered (marked Interrupted, mid-publish
    /// groups set to Outcome Unknown) when the user next opens them, and continue only when the user clicks Resume.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);

        if (!await db.Settings.AnyAsync(ct))
        {
            db.Settings.Add(SettingsService.CreateDefaults());
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Default settings created");
        }
    }
}
