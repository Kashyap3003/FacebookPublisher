using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FacebookPublisher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticPublishingWithChrome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New defaults: fully automatic posting and Google Chrome. Applied to the existing settings row as well.
            migrationBuilder.Sql("UPDATE [PublisherSettings] SET [RequireApprovalByDefault] = 0, [BrowserChannel] = N'chrome';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [PublisherSettings] SET [RequireApprovalByDefault] = 1, [BrowserChannel] = N'msedge';");
        }
    }
}
