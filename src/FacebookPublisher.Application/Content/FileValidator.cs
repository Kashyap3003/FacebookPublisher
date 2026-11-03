using FacebookPublisher.Application.Common;
using FacebookPublisher.Domain.Enums;

namespace FacebookPublisher.Application.Content;

public sealed record FileClassification(AttachmentType Type, string MimeType, long MaxBytes);

public static class FileValidator
{
    public static bool TryClassify(string fileName, ContentUploadOptions options, out FileClassification? classification, out string? error)
    {
        classification = null;
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            error = $"'{fileName}' has no file extension.";
            return false;
        }

        if (ContentUploadOptions.ImageTypes.TryGetValue(extension, out var mime))
        {
            classification = new FileClassification(AttachmentType.Image, mime, options.MaxImageBytes);
        }
        else if (ContentUploadOptions.VideoTypes.TryGetValue(extension, out mime))
        {
            classification = new FileClassification(AttachmentType.Video, mime, options.MaxVideoBytes);
        }
        else if (ContentUploadOptions.PdfTypes.TryGetValue(extension, out mime))
        {
            classification = new FileClassification(AttachmentType.Pdf, mime, options.MaxPdfBytes);
        }
        else
        {
            error = $"'{fileName}': file type '{extension}' is not supported.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Validates a full set of files plus text as one post. Returns every problem found.</summary>
    public static IReadOnlyList<string> ValidatePost(
        string? text,
        IReadOnlyList<(string FileName, long Size)> files,
        ContentUploadOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(text) && files.Count == 0)
        {
            errors.Add("Provide a text message, at least one file, or both.");
        }

        if (text is not null && text.Length > options.MaxTextLength)
        {
            errors.Add($"Text is {text.Length:N0} characters; the maximum is {options.MaxTextLength:N0}.");
        }

        if (files.Count > options.MaxAttachments)
        {
            errors.Add($"A post can contain at most {options.MaxAttachments} attachments.");
        }

        var types = new List<AttachmentType>();
        foreach (var (fileName, size) in files)
        {
            if (!TryClassify(fileName, options, out var classification, out var error))
            {
                errors.Add(error!);
                continue;
            }

            types.Add(classification!.Type);
            if (size <= 0)
            {
                errors.Add($"'{fileName}' is empty.");
            }
            else if (size > classification.MaxBytes)
            {
                errors.Add($"'{fileName}' is {size.ToFileSize()}; the limit for {classification.Type} files is {classification.MaxBytes.ToFileSize()}.");
            }
        }

        // Facebook's group composer accepts either photos/videos or a single document, not a mix.
        if (types.Contains(AttachmentType.Pdf) && types.Count > 1)
        {
            errors.Add("A PDF must be the only attachment in a post. Create separate content for photos/videos.");
        }

        return errors;
    }
}
