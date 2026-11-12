using System.Security.Claims;
using FacebookPublisher.Application.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;

namespace FacebookPublisher.Web.Identity;

/// <summary>
/// Resolves the signed-in user on the server: from the HTTP request (endpoints, prerendering) or from the Blazor
/// circuit's authentication state (interactive components). The id always comes from the authentication cookie,
/// never from request data. Disabled accounts are rejected on every check (cached for a few seconds).
/// </summary>
public sealed class CurrentUser(
    IHttpContextAccessor httpContextAccessor,
    IServiceProvider services,
    IUserAccounts accounts) : ICurrentUser
{
    private static readonly TimeSpan ActiveCheckCacheDuration = TimeSpan.FromSeconds(10);

    private Guid _checkedUserId;
    private DateTime _checkedUntilUtc;

    public async Task<Guid> GetUserIdAsync(CancellationToken cancellationToken = default)
    {
        var principal = await GetPrincipalAsync();
        if (UserClaims.GetUserId(principal) is not { } userId)
        {
            throw new UnauthorizedAccessException("You are not signed in.");
        }

        if (_checkedUserId != userId || DateTime.UtcNow > _checkedUntilUtc)
        {
            if (!await accounts.IsActiveAsync(userId, cancellationToken))
            {
                throw new UnauthorizedAccessException("Your account has been disabled.");
            }
            _checkedUserId = userId;
            _checkedUntilUtc = DateTime.UtcNow + ActiveCheckCacheDuration;
        }
        return userId;
    }

    public async Task<bool> IsAdminAsync(CancellationToken cancellationToken = default)
    {
        await GetUserIdAsync(cancellationToken);
        return (await GetPrincipalAsync()).IsInRole(AppRoles.Admin);
    }

    private async Task<ClaimsPrincipal> GetPrincipalAsync()
    {
        var httpUser = httpContextAccessor.HttpContext?.User;
        if (httpUser?.Identity?.IsAuthenticated == true)
        {
            return httpUser;
        }

        // Interactive Blazor circuit: there is no current HTTP request, use the circuit's authentication state.
        var authStateProvider = services.GetService<AuthenticationStateProvider>();
        if (authStateProvider is null)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
        try
        {
            return (await authStateProvider.GetAuthenticationStateAsync()).User;
        }
        catch (InvalidOperationException)
        {
            // Outside a circuit (e.g. a plain HTTP request without a signed-in user).
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
    }
}

public static class UserClaims
{
    public static Guid? GetUserId(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true
        && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;

    public static string DisplayName(ClaimsPrincipal principal) =>
        principal.FindFirstValue("display_name") ?? principal.Identity?.Name ?? "Signed in";
}
