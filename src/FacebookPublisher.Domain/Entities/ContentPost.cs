using FacebookPublisher.Domain.Common;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Domain.Entities;

public class ContentPost : BaseEntity, IOwnedEntity
{
    /// <summary>Application user who created this content. Attachments inherit ownership from it.</summary>
    public Guid OwnerId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Text { get; set; }

    /// <summary>SHA-256 over the text and every attachment's content; used to detect duplicate publishing.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public ICollection<ContentAttachment> Attachments { get; set; } = new List<ContentAttachment>();

    public ICollection<Campaign> Campaigns { get; set; } = new List<Campaign>();
}

public class ContentAttachment : BaseEntity
{
    public Guid ContentPostId { get; set; }

    public ContentPost? ContentPost { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>Name of the file inside the application's storage folder.</summary>
    public string StoredFileName { get; set; } = string.Empty;

    public string MimeType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public AttachmentType Type { get; set; }

    public int SortOrder { get; set; }
}
