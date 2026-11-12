using Microsoft.Extensions.Logging;

namespace FacebookPublisher.BrowserAutomation;

/// <summary>
/// Location and housekeeping of per-user browser profiles: {root}/{userId:N}. The folder name comes only from the
/// application user id, so one user's profile can never be selected for another user. The root lives under
/// App_Data (outside wwwroot) and is never served over HTTP.
/// </summary>
public sealed class BrowserProfileStore(string root, ILogger<BrowserProfileStore> logger)
{
    /// <summary>Lock files Chromium keeps in a profile while a browser process uses it.</summary>
    private static readonly string[] LockFiles = ["SingletonLock", "SingletonCookie", "SingletonSocket", "lockfile"];

    public string Root { get; } = Path.GetFullPath(root);

    public string GetProfilePath(Guid ownerId)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An application user is required.", nameof(ownerId));
        }
        return Path.Combine(Root, ownerId.ToString("N"));
    }

    public bool Exists(Guid ownerId) => Directory.Exists(GetProfilePath(ownerId));

    public string EnsureCreated(Guid ownerId)
    {
        var path = GetProfilePath(ownerId);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// One-time move of the single-user profile (recorded before multi-user support) to the owner's profile folder,
    /// so the administrator who inherited the existing data keeps their Facebook login. Never overwrites an existing
    /// profile and never moves a folder from inside the profiles root (another user's profile).
    /// </summary>
    public bool TryMoveLegacyProfile(Guid ownerId, string legacyPath)
    {
        var target = GetProfilePath(ownerId);
        var source = Path.GetFullPath(legacyPath);
        if (Directory.Exists(target) || !Directory.Exists(source)
            || source.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, Root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Root);
            Directory.Move(source, target);
            logger.LogInformation("Moved the pre-multi-user browser profile to the profile of user {OwnerId}", ownerId);
            return true;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not move the pre-multi-user browser profile for user {OwnerId}; the user can reconnect Facebook instead", ownerId);
            return false;
        }
    }

    /// <summary>
    /// Removes lock files left behind by a crashed browser. A lock held by a running browser cannot be deleted
    /// (Windows keeps it open), so a false result means the profile is genuinely in use and must not be opened.
    /// </summary>
    public bool TryClearStaleLocks(Guid ownerId)
    {
        var path = GetProfilePath(ownerId);
        var allCleared = true;
        foreach (var name in LockFiles)
        {
            var file = Path.Combine(path, name);
            try
            {
                if (File.Exists(file) || IsSymbolicLink(file))
                {
                    File.Delete(file);
                    logger.LogInformation("Removed stale browser lock {LockFile} for user {OwnerId}", name, ownerId);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                allCleared = false;
            }
        }
        return allCleared;
    }

    /// <summary>Deletes the user's profile (Facebook cookies, storage, cache): the "disconnect Facebook" action.</summary>
    public void Delete(Guid ownerId)
    {
        var path = GetProfilePath(ownerId);
        for (var attempt = 1; Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                // The browser process may still be releasing files right after closing.
                Thread.Sleep(300 * attempt);
            }
        }
        logger.LogInformation("Deleted the browser profile of user {OwnerId}", ownerId);
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
