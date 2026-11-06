using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using FacebookPublisher.Application.Abstractions;
using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Application.Reports;

namespace FacebookPublisher.Infrastructure.Reporting;

public sealed class ReportExporter : IReportExporter
{
    private static readonly string[] DetailHeaders =
        ["#", "Group Name", "Group URL", "Membership Status", "Publishing Status", "Published At (UTC)", "Error / Details"];

    public byte[] ExportExcel(CampaignReportDto report)
    {
        using var workbook = new XLWorkbook();

        var summary = workbook.Worksheets.Add("Summary");
        summary.Cell(1, 1).Value = "Campaign Execution Report";
        summary.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(16);

        var row = 3;
        foreach (var (label, value) in SummaryRows(report))
        {
            summary.Cell(row, 1).Value = label;
            summary.Cell(row, 1).Style.Font.SetBold();
            summary.Cell(row, 2).Value = value;
            row++;
        }

        row += 1;
        summary.Cell(row, 1).Value = "Reasons for skipped and failed groups";
        summary.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(13);
        row++;
        var reasonHeader = row;
        summary.Cell(row, 1).Value = "Category";
        summary.Cell(row, 2).Value = "Reason";
        summary.Cell(row, 3).Value = "Count";
        summary.Range(row, 1, row, 3).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#E8EEF7"));
        row++;
        foreach (var reason in report.Reasons)
        {
            summary.Cell(row, 1).Value = reason.Category;
            summary.Cell(row, 2).Value = reason.Reason;
            summary.Cell(row, 3).Value = reason.Count;
            row++;
        }
        if (report.Reasons.Count == 0)
        {
            summary.Cell(row, 1).Value = "None";
        }
        summary.Column(1).Width = 28;
        summary.Column(2).Width = 90;
        summary.Column(2).Style.Alignment.SetWrapText();
        summary.Column(3).Width = 10;
        summary.Range(reasonHeader, 1, Math.Max(row - 1, reasonHeader), 3).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);

        var details = workbook.Worksheets.Add("Groups");
        for (var i = 0; i < DetailHeaders.Length; i++)
        {
            details.Cell(1, i + 1).Value = DetailHeaders[i];
        }
        var r = 2;
        foreach (var t in report.Targets)
        {
            details.Cell(r, 1).Value = t.Order + 1;
            details.Cell(r, 2).Value = t.GroupName;
            details.Cell(r, 3).Value = t.GroupUrl;
            details.Cell(r, 3).SetHyperlink(new XLHyperlink(t.GroupUrl));
            details.Cell(r, 4).Value = t.MembershipStatus.ToDisplay();
            details.Cell(r, 5).Value = t.Status.ToDisplay();
            if (t.PublishedAtUtc is { } published)
            {
                details.Cell(r, 6).Value = published;
                details.Cell(r, 6).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
            }
            details.Cell(r, 7).Value = t.ErrorMessage ?? string.Empty;
            r++;
        }

        var table = details.Range(1, 1, Math.Max(r - 1, 1), DetailHeaders.Length).CreateTable("GroupResults");
        table.Theme = XLTableTheme.TableStyleMedium2;
        details.Columns(1, 6).AdjustToContents();
        details.Column(7).Width = 80;
        details.Column(7).Style.Alignment.SetWrapText();
        details.SheetView.FreezeRows(1);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] ExportCsv(CampaignReportDto report)
    {
        var sb = new StringBuilder();

        foreach (var (label, value) in SummaryRows(report))
        {
            AppendRow(sb, label, value);
        }
        sb.AppendLine();

        AppendRow(sb, "Reason Category", "Reason", "Count");
        foreach (var reason in report.Reasons)
        {
            AppendRow(sb, reason.Category, reason.Reason, reason.Count.ToString(CultureInfo.InvariantCulture));
        }
        sb.AppendLine();

        AppendRow(sb, DetailHeaders);
        foreach (var t in report.Targets)
        {
            AppendRow(sb,
                (t.Order + 1).ToString(CultureInfo.InvariantCulture),
                t.GroupName,
                t.GroupUrl,
                t.MembershipStatus.ToDisplay(),
                t.Status.ToDisplay(),
                t.PublishedAtUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                t.ErrorMessage ?? string.Empty);
        }

        // UTF-8 BOM so Excel opens non-ASCII group names correctly.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static IEnumerable<(string Label, string Value)> SummaryRows(CampaignReportDto report)
    {
        static string Format(DateTime? value) =>
            value?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "—";

        yield return ("Campaign", report.CampaignName);
        yield return ("Content", report.ContentTitle);
        yield return ("Status", report.Status.ToDisplay());
        if (!string.IsNullOrWhiteSpace(report.StatusReason))
        {
            yield return ("Status Reason", report.StatusReason);
        }
        yield return ("Created", Format(report.CreatedAtUtc));
        yield return ("Started", Format(report.StartedAtUtc));
        yield return ("Completed", Format(report.CompletedAtUtc));
        yield return ("Batch Size", $"{report.Pacing.MinBatchSize}–{report.Pacing.MaxBatchSize} groups");
        yield return ("Delay Between Posts", DurationText.Format(report.Pacing.PostDelay));
        yield return ("Delay Between Batches", DurationText.Format(report.Pacing.BatchDelay));
        yield return ("Extended Delay", $"{DurationText.Format(report.Pacing.ExtendedDelay)} after every {report.Pacing.ExtendedDelayAfterBatches} batches");
        yield return ("Completed Batches", report.CompletedBatches.ToString(CultureInfo.InvariantCulture));
        yield return ("Manual Approval", report.RequireApproval ? "Yes" : "No");
        yield return ("Total Groups Provided", report.Counts.Total.ToString(CultureInfo.InvariantCulture));
        yield return ("Published Successfully", report.Counts.Published.ToString(CultureInfo.InvariantCulture));
        yield return ("Skipped", report.Counts.Skipped.ToString(CultureInfo.InvariantCulture));
        yield return ("Inaccessible", report.Counts.Inaccessible.ToString(CultureInfo.InvariantCulture));
        yield return ("Failed", report.Counts.Failed.ToString(CultureInfo.InvariantCulture));
        yield return ("Outcome Unknown", report.Counts.OutcomeUnknown.ToString(CultureInfo.InvariantCulture));
        yield return ("Cancelled", report.Counts.Cancelled.ToString(CultureInfo.InvariantCulture));
        yield return ("Not Yet Processed", report.Counts.Remaining.ToString(CultureInfo.InvariantCulture));
        yield return ("Report Generated", Format(report.GeneratedAtUtc));
    }

    private static void AppendRow(StringBuilder sb, params string[] values)
    {
        sb.AppendJoin(',', values.Select(Escape)).Append("\r\n");
    }

    private static string Escape(string value)
    {
        // Neutralise spreadsheet formula injection from group names / error text.
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
        {
            value = "'" + value;
        }
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
