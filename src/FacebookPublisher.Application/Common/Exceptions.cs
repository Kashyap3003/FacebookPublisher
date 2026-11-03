namespace FacebookPublisher.Application.Common;

/// <summary>A user-correctable problem. The message is safe to display in the UI.</summary>
public sealed class AppValidationException : Exception
{
    public AppValidationException(string message)
        : this(message, [message])
    {
    }

    public AppValidationException(string message, IReadOnlyList<string> errors)
        : base(message)
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

public sealed class NotFoundException(string entity, object key)
    : Exception($"{entity} '{key}' was not found.");
