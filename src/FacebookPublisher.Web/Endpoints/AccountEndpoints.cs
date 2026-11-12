using System.Net;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace FacebookPublisher.Web.Endpoints;

/// <summary>
/// Group Publisher sign-in and sign-out (ASP.NET Core Identity). This is the application login only — Facebook
/// accounts are connected separately, by each user, in their own browser profile. Passwords are never logged.
/// </summary>
public static class AccountEndpoints
{
    public const string LoginPath = "/login";
    public const string LogoutPath = "/logout";

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(LoginPath, async (HttpContext http, IAntiforgery antiforgery, UserManager<ApplicationUser> users, string? returnUrl, string? error) =>
            {
                if (http.User.Identity?.IsAuthenticated == true)
                {
                    return Results.Redirect(SafeReturnUrl(returnUrl));
                }
                var tokens = antiforgery.GetAndStoreTokens(http);
                var hasAdmin = (await users.GetUsersInRoleAsync(AppRoles.Admin)).Any(u => !u.IsDisabled);
                return Results.Content(LoginPage(SafeReturnUrl(returnUrl), error, tokens, hasAdmin), "text/html; charset=utf-8");
            })
            .AllowAnonymous();

        app.MapPost(LoginPath, async (
                HttpContext http,
                IAntiforgery antiforgery,
                UserManager<ApplicationUser> users,
                SignInManager<ApplicationUser> signIn,
                ILogger<ApplicationUser> logger) =>
            {
                if (!await IsValidAntiforgeryAsync(antiforgery, http))
                {
                    return Results.Redirect($"{LoginPath}?error=expired");
                }

                var form = await http.Request.ReadFormAsync();
                var returnUrl = SafeReturnUrl(form["returnUrl"]);
                var login = form["email"].ToString().Trim();
                var password = form["password"].ToString();
                var remember = form["remember"] == "on";

                var user = login.Length == 0 ? null : await users.FindByEmailAsync(login) ?? await users.FindByNameAsync(login);
                string? error = null;
                if (user is null || user.IsLegacyOwner)
                {
                    error = "invalid";
                }
                else if (user.IsDisabled)
                {
                    // Only reveal that the account is disabled to someone who knows its password.
                    error = await users.CheckPasswordAsync(user, password) ? "disabled" : "invalid";
                }
                else
                {
                    var result = await signIn.PasswordSignInAsync(user, password, isPersistent: remember, lockoutOnFailure: true);
                    if (result.Succeeded)
                    {
                        user.LastLoginAtUtc = DateTime.UtcNow;
                        await users.UpdateAsync(user);
                        logger.LogInformation("User {UserId} signed in", user.Id);
                        return Results.Redirect(returnUrl);
                    }
                    error = result.IsLockedOut ? "locked" : "invalid";
                }

                logger.LogWarning("Failed sign-in ({Reason}) from {RemoteIp}", error, http.Connection.RemoteIpAddress);
                await Task.Delay(750); // slows down password guessing
                return Results.Redirect($"{LoginPath}?error={error}&returnUrl={Uri.EscapeDataString(returnUrl)}");
            })
            .AllowAnonymous()
            .DisableAntiforgery(); // validated explicitly above

        app.MapPost(LogoutPath, async (HttpContext http, IAntiforgery antiforgery, SignInManager<ApplicationUser> signIn, ILogger<ApplicationUser> logger) =>
            {
                if (!await IsValidAntiforgeryAsync(antiforgery, http))
                {
                    return Results.Redirect("/");
                }
                var userId = UserIdOf(http);
                await signIn.SignOutAsync();
                logger.LogInformation("User {UserId} signed out", userId);
                return Results.Redirect(LoginPath);
            })
            .AllowAnonymous()
            .DisableAntiforgery(); // validated explicitly above

        return app;
    }

    private static async Task<bool> IsValidAntiforgeryAsync(IAntiforgery antiforgery, HttpContext http)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static string? UserIdOf(HttpContext http) =>
        http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // Only local paths, so the login page cannot be used to redirect visitors to another site.
    private static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";

    private static string ErrorText(string? error) => error switch
    {
        "invalid" => "Incorrect email or password.",
        "locked" => "Too many failed attempts. This account is locked for 15 minutes.",
        "disabled" => "This account has been disabled. Contact your administrator.",
        "expired" => "The sign-in form expired. Please try again.",
        _ => string.Empty
    };

    private static string LoginPage(string returnUrl, string? error, AntiforgeryTokenSet tokens, bool hasAdmin)
    {
        var message = ErrorText(error);
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>Sign in · Group Publisher</title>
                <style>
                    :root { color-scheme: light; }
                    body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f4f6fb;
                           font-family: Inter, system-ui, -apple-system, 'Segoe UI', sans-serif; color: #0f172a; padding: 16px; }
                    .card { background: #fff; border: 1px solid #e4e7ef; border-radius: 14px; padding: 32px; width: 100%; max-width: 400px;
                            box-shadow: 0 10px 30px rgba(15, 23, 42, .08); box-sizing: border-box; }
                    .brand { display: flex; align-items: center; gap: 10px; margin-bottom: 18px; }
                    .logo { width: 36px; height: 36px; border-radius: 10px; background: #4f46e5; color: #fff; display: grid; place-items: center; font-weight: 700; }
                    h1 { font-size: 1.25rem; margin: 0; }
                    p { color: #64748b; margin: 0 0 22px; font-size: .9rem; }
                    label { display: block; font-size: .85rem; font-weight: 500; margin-bottom: 6px; }
                    input[type=email], input[type=password], input[type=text] { width: 100%; box-sizing: border-box; padding: 10px 12px;
                            border: 1px solid #cbd5e1; border-radius: 8px; font-size: .95rem; margin-bottom: 16px; }
                    input:focus { outline: 2px solid #4f46e5; border-color: #4f46e5; }
                    .remember { display: flex; align-items: center; gap: 8px; font-size: .85rem; color: #334155; margin-bottom: 18px; }
                    button { width: 100%; padding: 11px; border: 0; border-radius: 8px; background: #4f46e5; color: #fff;
                             font-weight: 600; font-size: .95rem; cursor: pointer; }
                    button:hover { background: #4338ca; }
                    .error { background: #fef2f2; color: #b91c1c; border: 1px solid #fecaca; border-radius: 8px;
                             padding: 10px 12px; font-size: .85rem; margin-bottom: 16px; }
                    .notice { background: #fffbeb; color: #92400e; border: 1px solid #fde68a; border-radius: 8px;
                              padding: 10px 12px; font-size: .82rem; margin-bottom: 16px; line-height: 1.45; }
                    code { background: #fef3c7; padding: 0 3px; border-radius: 3px; }
                    .foot { margin-top: 18px; font-size: .78rem; color: #94a3b8; text-align: center; }
                </style>
            </head>
            <body>
                <form class="card" method="post" action="{{LoginPath}}">
                    <div class="brand"><div class="logo">GP</div><h1>Group Publisher</h1></div>
                    <p>Sign in with your Group Publisher account.</p>
                    {{(hasAdmin ? "" : "<div class=\"notice\"><strong>First-time setup:</strong> no administrator account exists yet. Set <code>InitialAdmin__Email</code> and <code>InitialAdmin__Password</code> and restart the application.</div>")}}
                    {{(message.Length == 0 ? "" : $"<div class=\"error\" role=\"alert\">{WebUtility.HtmlEncode(message)}</div>")}}
                    <input type="hidden" name="{{WebUtility.HtmlEncode(tokens.FormFieldName)}}" value="{{WebUtility.HtmlEncode(tokens.RequestToken)}}" />
                    <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl)}}" />
                    <label for="email">Email</label>
                    <input id="email" name="email" type="email" autocomplete="username" required autofocus />
                    <label for="password">Password</label>
                    <input id="password" name="password" type="password" autocomplete="current-password" required />
                    <label class="remember"><input type="checkbox" name="remember" /> Keep me signed in</label>
                    <button type="submit">Sign in</button>
                    <div class="foot">Accounts are created by your administrator. Your Facebook account is connected separately, after signing in.</div>
                </form>
            </body>
            </html>
            """;
    }
}
