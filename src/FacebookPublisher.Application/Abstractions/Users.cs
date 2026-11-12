namespace FacebookPublisher.Application.Abstractions;

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string User = "User";
}

/// <summary>
/// The signed-in application user, always resolved on the server from the authenticated identity (never from values
/// sent by the browser). Throws <see cref="UnauthorizedAccessException"/> when nobody is signed in or the account has
/// been disabled.
/// </summary>
public interface ICurrentUser
{
    Task<Guid> GetUserIdAsync(CancellationToken cancellationToken = default);

    Task<bool> IsAdminAsync(CancellationToken cancellationToken = default);
}

/// <summary>Account state checks that do not depend on a signed-in user (used by background execution).</summary>
public interface IUserAccounts
{
    /// <summary>True when the account exists and is not disabled.</summary>
    Task<bool> IsActiveAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record UserDto(
    Guid Id,
    string Email,
    string? DisplayName,
    bool IsAdmin,
    bool IsDisabled,
    bool IsLockedOut,
    DateTime CreatedAtUtc,
    DateTime? LastLoginAtUtc);

public sealed record CreateUserRequest(string Email, string? DisplayName, string Password, bool IsAdmin);

/// <summary>Administrator-only user management. Implementations verify the caller is an administrator.</summary>
public interface IUserAdministration
{
    Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default);

    Task UpdateAsync(Guid userId, string? displayName, bool isAdmin, CancellationToken cancellationToken = default);

    /// <summary>Disabling signs the user out everywhere within a minute and stops their running campaigns.</summary>
    Task SetDisabledAsync(Guid userId, bool disabled, CancellationToken cancellationToken = default);

    Task ResetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default);
}
