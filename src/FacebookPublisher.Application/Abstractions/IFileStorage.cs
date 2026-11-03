namespace FacebookPublisher.Application.Abstractions;

public sealed record StoredFile(string StoredFileName, long SizeBytes, string Sha256);

public interface IFileStorage
{
    /// <summary>Streams the content to storage, enforcing <paramref name="maxBytes"/> and computing a SHA-256 hash.</summary>
    Task<StoredFile> SaveAsync(Stream content, string originalFileName, long maxBytes, CancellationToken cancellationToken);

    Stream OpenRead(string storedFileName);

    string GetFullPath(string storedFileName);

    bool Exists(string storedFileName);

    void Delete(string storedFileName);
}
