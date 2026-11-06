using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FacebookPublisher.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Batch processing, randomized delays and Stop/Resume:
    /// <list type="bullet">
    /// <item>Batch/delay configuration as owned columns on PublisherSettings (defaults) and Campaigns (snapshot).</item>
    /// <item>The previous single delay range (seconds) is carried over into the new "delay between posts" columns.</item>
    /// <item>Persisted batch execution state and execution lease on Campaigns; batch number on CampaignTargets.</item>
    /// <item>Campaign status "Draft" is renamed to "NotStarted".</item>
    /// </list>
    /// </summary>
    public partial class BatchProcessingAndResume : Migration
    {
        private static readonly string[] Tables = ["PublisherSettings", "Campaigns"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Batch/delay configuration columns (defaults: 5–9 groups, 10–30 s, 15–30 min, 3–5 h after 5 batches).
            foreach (var table in Tables)
            {
                AddInt(migrationBuilder, table, "Pacing_MinBatchSize", 5);
                AddInt(migrationBuilder, table, "Pacing_MaxBatchSize", 9);
                AddInt(migrationBuilder, table, "Pacing_PostDelay_Min", 10);
                AddInt(migrationBuilder, table, "Pacing_PostDelay_Max", 30);
                AddUnit(migrationBuilder, table, "Pacing_PostDelay_Unit", "Seconds");
                AddInt(migrationBuilder, table, "Pacing_BatchDelay_Min", 15);
                AddInt(migrationBuilder, table, "Pacing_BatchDelay_Max", 30);
                AddUnit(migrationBuilder, table, "Pacing_BatchDelay_Unit", "Minutes");
                AddInt(migrationBuilder, table, "Pacing_ExtendedDelayAfterBatches", 5);
                AddInt(migrationBuilder, table, "Pacing_ExtendedDelay_Min", 3);
                AddInt(migrationBuilder, table, "Pacing_ExtendedDelay_Max", 5);
                AddUnit(migrationBuilder, table, "Pacing_ExtendedDelay_Unit", "Hours");
            }

            // 2. Persisted batch execution state and execution lease.
            AddInt(migrationBuilder, "Campaigns", "CurrentBatchNumber", 0);
            AddInt(migrationBuilder, "Campaigns", "CurrentBatchSize", 0);
            AddInt(migrationBuilder, "Campaigns", "CurrentBatchProcessed", 0);
            AddInt(migrationBuilder, "Campaigns", "CompletedBatches", 0);
            AddInt(migrationBuilder, "Campaigns", "BatchesSinceExtendedDelay", 0);
            AddInt(migrationBuilder, "Campaigns", "RemainingGroupCount", 0);
            AddInt(migrationBuilder, "Campaigns", "CurrentDelaySeconds", 0);
            AddUnit(migrationBuilder, "Campaigns", "CurrentDelayKind", "None");
            migrationBuilder.AddColumn<DateTime>(name: "CurrentDelayEndsAtUtc", table: "Campaigns", type: "datetime2", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "LastProcessedTargetId", table: "Campaigns", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "LastExecutionAtUtc", table: "Campaigns", type: "datetime2", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "ExecutionOwnerId", table: "Campaigns", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<int>(name: "BatchNumber", table: "CampaignTargets", type: "int", nullable: true);

            // 3. Carry existing data over before the old columns are dropped.
            migrationBuilder.Sql(
                "UPDATE [PublisherSettings] SET [Pacing_PostDelay_Min] = [DefaultMinDelaySeconds], " +
                "[Pacing_PostDelay_Max] = [DefaultMaxDelaySeconds], [Pacing_PostDelay_Unit] = N'Seconds';");
            migrationBuilder.Sql(
                "UPDATE [Campaigns] SET [Pacing_PostDelay_Min] = [MinDelaySeconds], " +
                "[Pacing_PostDelay_Max] = [MaxDelaySeconds], [Pacing_PostDelay_Unit] = N'Seconds';");
            migrationBuilder.Sql("UPDATE [Campaigns] SET [Status] = N'NotStarted' WHERE [Status] = N'Draft';");
            migrationBuilder.Sql(
                "UPDATE c SET [RemainingGroupCount] = (SELECT COUNT(*) FROM [CampaignTargets] t " +
                "WHERE t.[CampaignId] = c.[Id] AND t.[Status] IN (N'Pending', N'CheckingMembership', N'Eligible')) " +
                "FROM [Campaigns] c;");

            // 4. Remove the old single delay range.
            migrationBuilder.DropColumn(name: "DefaultMinDelaySeconds", table: "PublisherSettings");
            migrationBuilder.DropColumn(name: "DefaultMaxDelaySeconds", table: "PublisherSettings");
            migrationBuilder.DropColumn(name: "MinDelaySeconds", table: "Campaigns");
            migrationBuilder.DropColumn(name: "MaxDelaySeconds", table: "Campaigns");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignTargets_CampaignId_Status",
                table: "CampaignTargets",
                columns: new[] { "CampaignId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_CampaignTargets_CampaignId_Status", table: "CampaignTargets");

            AddInt(migrationBuilder, "PublisherSettings", "DefaultMinDelaySeconds", 10);
            AddInt(migrationBuilder, "PublisherSettings", "DefaultMaxDelaySeconds", 20);
            AddInt(migrationBuilder, "Campaigns", "MinDelaySeconds", 10);
            AddInt(migrationBuilder, "Campaigns", "MaxDelaySeconds", 20);

            // Only a seconds-based post delay maps back exactly; other units fall back to the defaults above.
            migrationBuilder.Sql(
                "UPDATE [PublisherSettings] SET [DefaultMinDelaySeconds] = [Pacing_PostDelay_Min], " +
                "[DefaultMaxDelaySeconds] = [Pacing_PostDelay_Max] WHERE [Pacing_PostDelay_Unit] = N'Seconds';");
            migrationBuilder.Sql(
                "UPDATE [Campaigns] SET [MinDelaySeconds] = [Pacing_PostDelay_Min], " +
                "[MaxDelaySeconds] = [Pacing_PostDelay_Max] WHERE [Pacing_PostDelay_Unit] = N'Seconds';");
            migrationBuilder.Sql("UPDATE [Campaigns] SET [Status] = N'Draft' WHERE [Status] = N'NotStarted';");
            migrationBuilder.Sql("UPDATE [Campaigns] SET [Status] = N'Halted' WHERE [Status] IN (N'Stopped', N'Interrupted');");
            migrationBuilder.Sql("UPDATE [CampaignTargets] SET [Status] = N'Failed' WHERE [Status] = N'OutcomeUnknown';");

            foreach (var table in Tables)
            {
                foreach (var column in new[]
                         {
                             "Pacing_MinBatchSize", "Pacing_MaxBatchSize", "Pacing_PostDelay_Min", "Pacing_PostDelay_Max",
                             "Pacing_PostDelay_Unit", "Pacing_BatchDelay_Min", "Pacing_BatchDelay_Max", "Pacing_BatchDelay_Unit",
                             "Pacing_ExtendedDelayAfterBatches", "Pacing_ExtendedDelay_Min", "Pacing_ExtendedDelay_Max",
                             "Pacing_ExtendedDelay_Unit"
                         })
                {
                    DropColumnWithDefault(migrationBuilder, table, column);
                }
            }

            foreach (var column in new[]
                     {
                         "CurrentBatchNumber", "CurrentBatchSize", "CurrentBatchProcessed", "CompletedBatches",
                         "BatchesSinceExtendedDelay", "RemainingGroupCount", "CurrentDelaySeconds", "CurrentDelayKind"
                     })
            {
                DropColumnWithDefault(migrationBuilder, "Campaigns", column);
            }
            migrationBuilder.DropColumn(name: "CurrentDelayEndsAtUtc", table: "Campaigns");
            migrationBuilder.DropColumn(name: "LastProcessedTargetId", table: "Campaigns");
            migrationBuilder.DropColumn(name: "LastExecutionAtUtc", table: "Campaigns");
            migrationBuilder.DropColumn(name: "ExecutionOwnerId", table: "Campaigns");
            migrationBuilder.DropColumn(name: "BatchNumber", table: "CampaignTargets");
        }

        private static void AddInt(MigrationBuilder migrationBuilder, string table, string column, int defaultValue) =>
            migrationBuilder.AddColumn<int>(name: column, table: table, type: "int", nullable: false, defaultValue: defaultValue);

        private static void AddUnit(MigrationBuilder migrationBuilder, string table, string column, string defaultValue) =>
            migrationBuilder.AddColumn<string>(name: column, table: table, type: "nvarchar(40)", maxLength: 40, nullable: false, defaultValue: defaultValue);

        // DropColumn also drops the column's default constraint on SQL Server.
        private static void DropColumnWithDefault(MigrationBuilder migrationBuilder, string table, string column) =>
            migrationBuilder.DropColumn(name: column, table: table);
    }
}
