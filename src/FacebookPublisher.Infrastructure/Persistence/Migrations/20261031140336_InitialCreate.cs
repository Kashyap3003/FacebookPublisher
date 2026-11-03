using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FacebookPublisher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: true),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentPosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FacebookGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastMembershipStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FacebookGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublisherSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    DefaultMinDelaySeconds = table.Column<int>(type: "int", nullable: false),
                    DefaultMaxDelaySeconds = table.Column<int>(type: "int", nullable: false),
                    RequireApprovalByDefault = table.Column<bool>(type: "bit", nullable: false),
                    ApprovalTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    MaxGroupsPerCampaign = table.Column<int>(type: "int", nullable: false),
                    DuplicateWindowHours = table.Column<int>(type: "int", nullable: false),
                    BrowserProfilePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    BrowserChannel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Headless = table.Column<bool>(type: "bit", nullable: false),
                    NavigationTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    UploadTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    SessionLastVerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SessionLastVerifiedResult = table.Column<bool>(type: "bit", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublisherSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MinDelaySeconds = table.Column<int>(type: "int", nullable: false),
                    MaxDelaySeconds = table.Column<int>(type: "int", nullable: false),
                    RequireApproval = table.Column<bool>(type: "bit", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Campaigns_ContentPosts_ContentPostId",
                        column: x => x.ContentPostId,
                        principalTable: "ContentPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContentAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentPostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    StoredFileName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MimeType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentAttachments_ContentPosts_ContentPostId",
                        column: x => x.ContentPostId,
                        principalTable: "ContentPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CampaignTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FacebookGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    GroupUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    MembershipStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PublishAttempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignTargets_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CampaignTargets_FacebookGroups_FacebookGroupId",
                        column: x => x.FacebookGroupId,
                        principalTable: "FacebookGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_ContentPostId",
                table: "Campaigns",
                column: "ContentPostId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_CreatedAtUtc",
                table: "Campaigns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Status",
                table: "Campaigns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignTargets_CampaignId_FacebookGroupId",
                table: "CampaignTargets",
                columns: new[] { "CampaignId", "FacebookGroupId" },
                unique: true,
                filter: "[FacebookGroupId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignTargets_FacebookGroupId",
                table: "CampaignTargets",
                column: "FacebookGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignTargets_GroupUrl_Status_PublishedAtUtc",
                table: "CampaignTargets",
                columns: new[] { "GroupUrl", "Status", "PublishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignTargets_UpdatedAtUtc",
                table: "CampaignTargets",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ContentAttachments_ContentPostId",
                table: "ContentAttachments",
                column: "ContentPostId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentPosts_ContentHash",
                table: "ContentPosts",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_FacebookGroups_Name",
                table: "FacebookGroups",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_FacebookGroups_Url",
                table: "FacebookGroups",
                column: "Url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignTargets");

            migrationBuilder.DropTable(
                name: "ContentAttachments");

            migrationBuilder.DropTable(
                name: "PublisherSettings");

            migrationBuilder.DropTable(
                name: "Campaigns");

            migrationBuilder.DropTable(
                name: "FacebookGroups");

            migrationBuilder.DropTable(
                name: "ContentPosts");
        }
    }
}
