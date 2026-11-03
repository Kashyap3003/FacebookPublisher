using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FacebookPublisher.Application.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace FacebookPublisher.Web.Endpoints;

/// <summary>
/// Single-user sign-in for hosted demos (Hosting:RequireLogin). Credentials come from configuration
/// (e.g. Azure App Service settings), never from source code.
/// </summary>
public static class AccountEndpoints
{
    public const string LoginPath = "/login";
    public const string LogoutPath = "/logout";

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(LoginPath, (string? returnUrl, string? error) =>
                Results.Content(LoginPage(SafeReturnUrl(returnUrl), error is not null), "text/html; charset=utf-8"))
            .AllowAnonymous();

        app.MapPost(LoginPath, async (HttpContext http, IOptions<HostingOptions> options, ILogger<HostingOptions> logger) =>
            {
                var form = await http.Request.ReadFormAsync();
                var returnUrl = SafeReturnUrl(form["returnUrl"]);
                var hosting = options.Value;

                if (!Matches(form["username"], hosting.LoginUsername) || !Matches(form["password"], hosting.LoginPassword))
                {
                    logger.LogWarning("Failed demo sign-in from {RemoteIp}", http.Connection.RemoteIpAddress);
                    await Task.Delay(1000); // slows down password guessing
                    return Results.Redirect($"{LoginPath}?error=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
                }

                var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, hosting.LoginUsername)], CookieAuthenticationDefaults.AuthenticationScheme);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
                    new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });
                logger.LogInformation("Demo user signed in from {RemoteIp}", http.Connection.RemoteIpAddress);
                return Results.Redirect(returnUrl);
            })
            .AllowAnonymous()
            .DisableAntiforgery();

        app.MapGet(LogoutPath, async (HttpContext http) =>
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect(LoginPath);
            })
            .AllowAnonymous();

        return app;
    }

    private static bool Matches(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    // Only local paths, so the login page cannot be used to redirect visitors to another site.
    private static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";

    private static string LoginPage(string returnUrl, bool failed) => $$"""
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
                .card { background: #fff; border: 1px solid #e4e7ef; border-radius: 14px; padding: 32px; width: 100%; max-width: 380px;
                        box-shadow: 0 10px 30px rgba(15, 23, 42, .08); box-sizing: border-box; }
                h1 { font-size: 1.3rem; margin: 0 0 4px; }
                p { color: #64748b; margin: 0 0 24px; font-size: .9rem; }
                label { display: block; font-size: .85rem; font-weight: 500; margin-bottom: 6px; }
                input { width: 100%; box-sizing: border-box; padding: 10px 12px; border: 1px solid #cbd5e1; border-radius: 8px;
                        font-size: .95rem; margin-bottom: 16px; }
                input:focus { outline: 2px solid #4f46e5; border-color: #4f46e5; }
                button { width: 100%; padding: 11px; border: 0; border-radius: 8px; background: #4f46e5; color: #fff;
                         font-weight: 600; font-size: .95rem; cursor: pointer; }
                button:hover { background: #4338ca; }
                .error { background: #fef2f2; color: #b91c1c; border: 1px solid #fecaca; border-radius: 8px;
                         padding: 10px 12px; font-size: .85rem; margin-bottom: 16px; }
            </style>
        </head>
        <body>
            <form class="card" method="post" action="{{LoginPath}}">
                <h1>Group Publisher</h1>
                <p>Sign in to access the demo.</p>
                {{(failed ? "<div class=\"error\">Incorrect username or password.</div>" : "")}}
                <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl)}}" />
                <label for="username">Username</label>
                <input id="username" name="username" autocomplete="username" required autofocus />
                <label for="password">Password</label>
                <input id="password" name="password" type="password" autocomplete="current-password" required />
                <button type="submit">Sign in</button>
            </form>
        </body>
        </html>
        """;
}
