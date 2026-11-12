using FacebookPublisher.Application.Common;
using FacebookPublisher.Web.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace FacebookPublisher.Web.Components.Shared;

/// <summary>Common busy/error/success handling so every page reports problems the same way.</summary>
public abstract class AppComponentBase : ComponentBase
{
    [Inject] protected ILogger<AppComponentBase> Logger { get; set; } = default!;

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    /// <summary>The signed-in user's id, used to ignore live events that belong to other users.</summary>
    protected async Task<Guid?> GetCurrentUserIdAsync() =>
        AuthenticationState is null ? null : UserClaims.GetUserId((await AuthenticationState).User);

    protected bool Busy { get; private set; }

    protected IReadOnlyList<string> Errors { get; private set; } = [];

    protected string? Success { get; set; }

    protected void ClearMessages()
    {
        Errors = [];
        Success = null;
    }

    protected void ShowError(string message) => Errors = [message];

    /// <summary>Runs an action, translating validation failures into messages and logging unexpected errors.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, string? successMessage = null)
    {
        if (Busy)
        {
            return false;
        }

        Busy = true;
        ClearMessages();
        try
        {
            await action();
            if (successMessage is not null)
            {
                Success = successMessage;
            }
            return true;
        }
        catch (AppValidationException ex)
        {
            Errors = ex.Errors;
        }
        catch (NotFoundException ex)
        {
            Errors = [ex.Message];
        }
        catch (FacebookPublisher.Application.Automation.AutomationHaltedException ex)
        {
            Errors = [ex.Message];
        }
        catch (UnauthorizedAccessException ex)
        {
            Errors = [$"{ex.Message} Please sign in again."];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected UI error");
            Errors = [$"Something went wrong: {ex.Message}"];
        }
        finally
        {
            Busy = false;
        }
        return false;
    }
}
