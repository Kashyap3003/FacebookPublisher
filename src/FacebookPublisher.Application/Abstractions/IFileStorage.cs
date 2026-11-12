namespace FacebookPublisher.Application.Abstractions;

public sealed record StoredFile(string StoredFileName, long SizeBytes, string Sha256);

/// <summary>
/// Attachment storage, partitioned per application user ({root}/{ownerId}/{file}). Callers must pass the owner of the
/// record that references the file (taken from an owner-scoped query), never a value supplied by the browser.
/// </summary>
public interface IFileStorage
{
    /// <summary>Streams the content to the owner's folder, enforcing <paramref name="maxBytes"/> and computing a SHA-256 hash.</summary>
    Task<StoredFile> SaveAsync(Guid ownerId, Stream content, string originalFileName, long maxBytes, CancellationToken cancellationToken);

    Stream OpenRead(Guid ownerId, string storedFileName);

    string GetFullPath(Guid ownerId, string storedFileName);

    bool Exists(Guid ownerId, string storedFileName);

    void Delete(Guid ownerId, string storedFileName);
}
