using System.Security.Cryptography;
using System.Text;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Domain.Entities;
using FacebookPublisher.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FacebookPublisher.Application.Content;

public sealed record UploadFile(string FileName, long Size, Func<Stream> OpenReadStream);

public sealed record CreateContentRequest(string Title, string? Text, IReadOnlyList<UploadFile> Files);

public sealed record AttachmentDto(Guid Id, string FileName, string MimeType, long SizeBytes, AttachmentType Type);

public sealed record ContentSummaryDto(
    Guid Id,
    string Title,
    string? TextPreview,
    int ImageCount,
    int VideoCount,
    int PdfCount,
    int CampaignCount,
    DateTime CreatedAtUtc);

public sealed record ContentDetailDto(
    Guid Id,
    string Title,
    string? Text,
    IReadOnlyList<AttachmentDto> Attachments,
    DateTime CreatedAtUtc);

public sealed record AttachmentFile(string FullPath, string MimeType, string FileName);

public sealed class ContentService(
    IApplicationDbContextFactory dbFactory,
    IFileStorage storage,
    IOptions<ContentUploadOptions> options,
    ILogger<ContentService> logger)
{
    private readonly ContentUploadOptions _options = options.Value;

    public ContentUploadOptions Options => _options;

    public async Task<IReadOnlyList<ContentSummaryDto>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var items = await db.Contents
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.Text,
                Images = c.Attachments.Count(a => a.Type == AttachmentType.Image),
                Videos = c.Attachments.Count(a => a.Type == AttachmentType.Video),
                Pdfs = c.Attachments.Count(a => a.Type == AttachmentType.Pdf),
                Campaigns = c.Campaigns.Count,
                c.CreatedAtUtc
            })
            .ToListAsync(ct);

        return items.Select(c => new ContentSummaryDto(
                c.Id,
                c.Title,
                c.Text is { Length: > 160 } ? c.Text[..160] + "…" : c.Text,
                c.Images,
                c.Videos,
                c.Pdfs,
                c.Campaigns,
                c.CreatedAtUtc))
            .ToList();
    }

    public async Task<ContentDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var content = await db.Contents
            .AsNoTracking()
            .Include(c => c.Attachments)
            .FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Content", id);

        return ToDetail(content);
    }

    public async Task<Guid> CreateAsync(CreateContentRequest request, CancellationToken ct = default)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim();

        var errors = new List<string>();
        if (title.Length is 0 or > 200)
        {
            errors.Add("Title is required and must be at most 200 characters.");
        }
        errors.AddRange(FileValidator.ValidatePost(text, request.Files.Select(f => (f.FileName, f.Size)).ToList(), _options));
        if (errors.Count > 0)
        {
            throw new AppValidationException("The content is not valid.", errors);
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var ownerId = db.RequireOwnerId();
        var content = new ContentPost { OwnerId = ownerId, Title = title, Text = text };
        var savedFiles = new List<string>();
        try
        {
            var order = 0;
            foreach (var file in request.Files)
            {
                FileValidator.TryClassify(file.FileName, _options, out var classification, out _);
                await using var stream = file.OpenReadStream();
                var stored = await storage.SaveAsync(ownerId, stream, file.FileName, classification!.MaxBytes, ct);
                savedFiles.Add(stored.StoredFileName);

                content.Attachments.Add(new ContentAttachment
                {
                    OriginalFileName = Path.GetFileName(file.FileName),
                    StoredFileName = stored.StoredFileName,
                    MimeType = classification.MimeType,
                    SizeBytes = stored.SizeBytes,
                    Sha256 = stored.Sha256,
                    Type = classification.Type,
                    SortOrder = order++
                });
            }

            content.ContentHash = ComputeContentHash(text, content.Attachments.Select(a => a.Sha256));

            db.Contents.Add(content);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Created content {ContentId} '{Title}' with {AttachmentCount} attachment(s)",
                content.Id, content.Title, content.Attachments.Count);
            return content.Id;
        }
        catch
        {
            foreach (var stored in savedFiles)
            {
                storage.Delete(ownerId, stored);
            }
            throw;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var content = await db.Contents
            .Include(c => c.Attachments)
            .FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Content", id);

        if (await db.Campaigns.AnyAsync(c => c.ContentPostId == id, ct))
        {
            throw new AppValidationException("This content is used by one or more campaigns and is kept for campaign history.");
        }

        var files = content.Attachments.Select(a => a.StoredFileName).ToList();
        db.Contents.Remove(content);
        await db.SaveChangesAsync(ct);

        foreach (var file in files)
        {
            storage.Delete(content.OwnerId, file);
        }
        logger.LogInformation("Deleted content {ContentId}", id);
    }

    public async Task<AttachmentFile?> GetAttachmentFileAsync(Guid attachmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        // The owner-scoped query returns nothing for another user's attachment, so the endpoint answers 404.
        var attachment = await db.Attachments.AsNoTracking()
            .Where(a => a.Id == attachmentId)
            .Select(a => new { a.StoredFileName, a.MimeType, a.OriginalFileName, OwnerId = a.ContentPost!.OwnerId })
            .FirstOrDefaultAsync(ct);
        if (attachment is null || !storage.Exists(attachment.OwnerId, attachment.StoredFileName))
        {
            return null;
        }
        return new AttachmentFile(storage.GetFullPath(attachment.OwnerId, attachment.StoredFileName), attachment.MimeType, attachment.OriginalFileName);
    }

    internal static ContentDetailDto ToDetail(ContentPost content) => new(
        content.Id,
        content.Title,
        content.Text,
        content.Attachments
            .OrderBy(a => a.SortOrder)
            .Select(a => new AttachmentDto(a.Id, a.OriginalFileName, a.MimeType, a.SizeBytes, a.Type))
            .ToList(),
        content.CreatedAtUtc);

    private static string ComputeContentHash(string? text, IEnumerable<string> attachmentHashes)
    {
        var builder = new StringBuilder();
        builder.Append(text?.Trim() ?? string.Empty).Append('|');
        foreach (var hash in attachmentHashes)
        {
            builder.Append(hash).Append('|');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
