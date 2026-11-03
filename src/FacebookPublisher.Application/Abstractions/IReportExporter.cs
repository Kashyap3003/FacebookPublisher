using FacebookPublisher.Application.Reports;

namespace FacebookPublisher.Application.Abstractions;

public interface IReportExporter
{
    byte[] ExportExcel(CampaignReportDto report);

    byte[] ExportCsv(CampaignReportDto report);
}
