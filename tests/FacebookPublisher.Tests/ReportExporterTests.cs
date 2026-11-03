using System.Text;
using ClosedXML.Excel;
using FacebookPublisher.Application.Campaigns;
using FacebookPublisher.Application.Reports;
using FacebookPublisher.Domain.Enums;
using FacebookPublisher.Infrastructure.Reporting;

namespace FacebookPublisher.Tests;

public class ReportExporterTests
{
    private static CampaignReportDto SampleReport()
    {
        var targets = new List<TargetProgressDto>
        {
            new(Guid.NewGuid(), Guid.NewGuid(), "Makers, \"Local\"", "https://www.facebook.com/groups/makers", 0,
                MembershipStatus.Member, TargetStatus.Published, DateTime.UtcNow, null, DateTime.UtcNow),
            new(Guid.NewGuid(), Guid.NewGuid(), "=HYPERLINK(\"x\")", "https://www.facebook.com/groups/evil", 1,
                MembershipStatus.NotMember, TargetStatus.SkippedNotMember, null, "Not a member", DateTime.UtcNow)
        };
        return new CampaignReportDto(Guid.NewGuid(), "Launch", "Announcement", CampaignStatus.Completed, null, true, 10, 20,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
            CampaignCountsDto.From(targets.Select(t => t.Status)),
            [new ReasonCountDto("Skipped (Not a Member)", "Not a member", 1)],
            targets, DateTime.UtcNow);
    }

    [Fact]
    public void Csv_escapes_quotes_and_neutralises_formulas()
    {
        var csv = Encoding.UTF8.GetString(new ReportExporter().ExportCsv(SampleReport()));

        Assert.Contains("\"Makers, \"\"Local\"\"\"", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
        Assert.Contains("Total Groups Provided,2", csv);
        Assert.Contains("Published Successfully,1", csv);
    }

    [Fact]
    public void Excel_contains_summary_and_group_sheets()
    {
        var bytes = new ReportExporter().ExportExcel(SampleReport());

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.True(workbook.Worksheets.Contains("Summary"));
        var groups = workbook.Worksheet("Groups");
        Assert.Equal("Group Name", groups.Cell(1, 2).GetString());
        Assert.Equal("Published Successfully", groups.Cell(2, 5).GetString());
        Assert.Equal("Skipped (Not a Member)", groups.Cell(3, 5).GetString());
    }
}
