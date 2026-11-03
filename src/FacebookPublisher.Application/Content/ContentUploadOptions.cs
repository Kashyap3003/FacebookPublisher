namespace FacebookPublisher.Application.Content;

public sealed class ContentUploadOptions
{
    public const string SectionName = "ContentUpload";

    public long MaxImageBytes { get; set; } = 10L * 1024 * 1024;

    public long MaxVideoBytes { get; set; } = 1024L * 1024 * 1024;

    public long MaxPdfBytes { get; set; } = 50L * 1024 * 1024;

    public int MaxAttachments { get; set; } = 10;

    public int MaxTextLength { get; set; } = 20_000;

    public static readonly IReadOnlyDictionary<string, string> ImageTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp"
    };

    public static readonly IReadOnlyDictionary<string, string> VideoTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/x-m4v",
        [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo",
        [".wmv"] = "video/x-ms-wmv",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska"
    };

    public static readonly IReadOnlyDictionary<string, string> PdfTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf"
    };

    public static string AcceptAttribute =>
        string.Join(',', ImageTypes.Keys.Concat(VideoTypes.Keys).Concat(PdfTypes.Keys));
}
