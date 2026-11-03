using FacebookPublisher.Application.Content;

namespace FacebookPublisher.Tests;

public class FileValidatorTests
{
    private readonly ContentUploadOptions _options = new() { MaxImageBytes = 1000, MaxVideoBytes = 5000, MaxPdfBytes = 2000, MaxAttachments = 3 };

    [Fact]
    public void Text_only_post_is_valid()
    {
        Assert.Empty(FileValidator.ValidatePost("Hello", [], _options));
    }

    [Fact]
    public void Empty_post_is_rejected()
    {
        Assert.NotEmpty(FileValidator.ValidatePost("   ", [], _options));
    }

    [Fact]
    public void Images_and_video_can_be_combined()
    {
        Assert.Empty(FileValidator.ValidatePost(null, [("a.jpg", 10), ("b.PNG", 10), ("c.mp4", 10)], _options));
    }

    [Fact]
    public void Pdf_must_be_the_only_attachment()
    {
        var errors = FileValidator.ValidatePost(null, [("doc.pdf", 10), ("a.jpg", 10)], _options);
        Assert.Contains(errors, e => e.Contains("PDF must be the only attachment"));
    }

    [Fact]
    public void Oversized_and_unsupported_files_are_reported()
    {
        var errors = FileValidator.ValidatePost("x", [("big.jpg", 1001), ("script.exe", 10)], _options);
        Assert.Contains(errors, e => e.Contains("big.jpg"));
        Assert.Contains(errors, e => e.Contains(".exe"));
    }

    [Fact]
    public void Too_many_attachments_are_rejected()
    {
        var errors = FileValidator.ValidatePost(null, [("1.jpg", 1), ("2.jpg", 1), ("3.jpg", 1), ("4.jpg", 1)], _options);
        Assert.Contains(errors, e => e.Contains("at most 3"));
    }

    [Fact]
    public void Text_length_is_limited()
    {
        var options = new ContentUploadOptions { MaxTextLength = 5 };
        Assert.NotEmpty(FileValidator.ValidatePost("123456", [], options));
    }
}
