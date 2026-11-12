using System.Text.RegularExpressions;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Application.Content;
using FacebookPublisher.Application.Reports;

namespace FacebookPublisher.Web.Endpoints;

public static partial class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        // Serves the signed-in user's own attachments for previews (another user's id answers 404); range processing lets videos seek.
        app.MapGet("/media/{attachmentId:guid}", async (Guid attachmentId, ContentService content, CancellationToken ct) =>
        {
            AttachmentFile? file;
            try
            {
                file = await content.GetAttachmentFileAsync(attachmentId, ct);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            return file is null
                ? Results.NotFound()
                : TypedResults.PhysicalFile(file.FullPath, file.MimeType, enableRangeProcessing: true);
        });

        app.MapGet("/reports/{campaignId:guid}/export/{format}", async (
            Guid campaignId,
            string format,
            ReportService reports,
            IReportExporter exporter,
            CancellationToken ct) =>
        {
            CampaignReportDto report;
            try
            {
                report = await reports.GetCampaignReportAsync(campaignId, ct);
            }
            catch (NotFoundException)
            {
                return Results.NotFound(); // also the answer for another user's campaign
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }

            var baseName = $"campaign-report-{SafeName(report.CampaignName)}-{DateTime.Now:yyyyMMdd-HHmm}";
            return format.ToLowerInvariant() switch
            {
                "xlsx" or "excel" => Results.File(exporter.ExportExcel(report),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", baseName + ".xlsx"),
                "csv" => Results.File(exporter.ExportCsv(report), "text/csv; charset=utf-8", baseName + ".csv"),
                _ => Results.BadRequest("Supported formats: xlsx, csv.")
            };
        });

        return app;
    }

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex UnsafeChars();

    private static string SafeName(string name)
    {
        var safe = UnsafeChars().Replace(name, "-").Trim('-');
        return safe.Length == 0 ? "campaign" : safe[..Math.Min(safe.Length, 50)];
    }
}
