using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FacebookPublisher.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Multi-user support (ASP.NET Core Identity + per-user data ownership), preserving all existing data:
    /// <list type="number">
    /// <item>Creates the Identity tables.</item>
    /// <item>If the database already contains data, creates one placeholder owner account (fixed id, no password,
    /// disabled) that holds it. The first administrator provisioned from configuration at start-up takes this account
    /// over, so all existing groups, content, attachments, campaigns, campaign history and settings become theirs.</item>
    /// <item>Adds OwnerId as nullable, back-fills it, then makes it required with foreign keys (no table is dropped).</item>
    /// <item>Settings become one row per user (primary key moves from the fixed Id = 1 to OwnerId); the old editable
    /// browser-profile path is kept as LegacyBrowserProfilePath so that profile can be moved to the owner once.</item>
    /// <item>Group URLs become unique per user instead of globally.</item>
    /// </list>
    /// </summary>
    public partial class MultiUserIdentity : Migration
    {
        /// <summary>Must match <c>ApplicationUser.LegacyOwnerId</c>.</summary>
        private const string LegacyOwnerId = "6f1c3a52-9d47-4e0b-8a2f-5b7e1d0c9a31";

        private static readonly string[] OwnedTables = ["FacebookGroups", "ContentPosts", "Campaigns", "PublisherSettings"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------- 1. Identity tables ----------
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsDisabled = table.Column<bool>(type: "bit", nullable: false),
                    IsLegacyOwner = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(name: "IX_AspNetRoleClaims_RoleId", table: "AspNetRoleClaims", column: "RoleId");
            migrationBuilder.CreateIndex(name: "RoleNameIndex", table: "AspNetRoles", column: "NormalizedName",
                unique: true, filter: "[NormalizedName] IS NOT NULL");
            migrationBuilder.CreateIndex(name: "IX_AspNetUserClaims_UserId", table: "AspNetUserClaims", column: "UserId");
            migrationBuilder.CreateIndex(name: "IX_AspNetUserLogins_UserId", table: "AspNetUserLogins", column: "UserId");
            migrationBuilder.CreateIndex(name: "IX_AspNetUserRoles_RoleId", table: "AspNetUserRoles", column: "RoleId");
            migrationBuilder.CreateIndex(name: "EmailIndex", table: "AspNetUsers", column: "NormalizedEmail");
            migrationBuilder.CreateIndex(name: "UserNameIndex", table: "AspNetUsers", column: "NormalizedUserName",
                unique: true, filter: "[NormalizedUserName] IS NOT NULL");

            // ---------- 2. Placeholder owner for pre-existing data (only if there is any) ----------
            migrationBuilder.Sql($"""
                IF EXISTS (SELECT 1 FROM [FacebookGroups]) OR EXISTS (SELECT 1 FROM [ContentPosts])
                   OR EXISTS (SELECT 1 FROM [Campaigns]) OR EXISTS (SELECT 1 FROM [PublisherSettings])
                INSERT INTO [AspNetUsers] ([Id], [DisplayName], [IsDisabled], [IsLegacyOwner], [CreatedAtUtc], [UserName],
                    [NormalizedUserName], [Email], [NormalizedEmail], [EmailConfirmed], [PasswordHash], [SecurityStamp],
                    [ConcurrencyStamp], [PhoneNumberConfirmed], [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount])
                VALUES ('{LegacyOwnerId}', N'Existing data owner', 1, 1, SYSUTCDATETIME(), N'legacy-owner',
                    N'LEGACY-OWNER', NULL, NULL, 0, NULL, CONVERT(nvarchar(36), NEWID()),
                    CONVERT(nvarchar(36), NEWID()), 0, 0, 1, 0);
                """);

            // ---------- 3. Ownership columns: add nullable, back-fill, then require ----------
            foreach (var table in OwnedTables)
            {
                migrationBuilder.AddColumn<Guid>(name: "OwnerId", table: table, type: "uniqueidentifier", nullable: true);
                migrationBuilder.Sql($"UPDATE [{table}] SET [OwnerId] = '{LegacyOwnerId}' WHERE [OwnerId] IS NULL;");
                migrationBuilder.AlterColumn<Guid>(name: "OwnerId", table: table, type: "uniqueidentifier", nullable: false,
                    oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);
            }

            // ---------- 4. Settings: one row per user, keyed by owner ----------
            migrationBuilder.AddColumn<string>(name: "LegacyBrowserProfilePath", table: "PublisherSettings",
                type: "nvarchar(500)", maxLength: 500, nullable: true);
            migrationBuilder.Sql("UPDATE [PublisherSettings] SET [LegacyBrowserProfilePath] = NULLIF([BrowserProfilePath], N'');");
            migrationBuilder.DropPrimaryKey(name: "PK_PublisherSettings", table: "PublisherSettings");
            migrationBuilder.DropColumn(name: "Id", table: "PublisherSettings");
            migrationBuilder.DropColumn(name: "BrowserProfilePath", table: "PublisherSettings");
            migrationBuilder.AddPrimaryKey(name: "PK_PublisherSettings", table: "PublisherSettings", column: "OwnerId");

            // ---------- 5. Indexes and foreign keys ----------
            migrationBuilder.DropIndex(name: "IX_FacebookGroups_Url", table: "FacebookGroups");
            migrationBuilder.CreateIndex(name: "IX_FacebookGroups_OwnerId_Url", table: "FacebookGroups",
                columns: new[] { "OwnerId", "Url" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_ContentPosts_OwnerId", table: "ContentPosts", column: "OwnerId");
            migrationBuilder.CreateIndex(name: "IX_Campaigns_OwnerId_Status", table: "Campaigns", columns: new[] { "OwnerId", "Status" });

            foreach (var table in OwnedTables)
            {
                migrationBuilder.AddForeignKey(
                    name: $"FK_{table}_AspNetUsers_OwnerId",
                    table: table,
                    column: "OwnerId",
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            }
        }

        /// <summary>
        /// Reverts to the single-user schema. This is only lossless while the database holds one user's data: settings
        /// of all but the earliest-created owner are dropped (the single-user schema allows one settings row), and if
        /// several users saved the same group URL, only the oldest copy can be kept under the global unique index.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in OwnedTables)
            {
                migrationBuilder.DropForeignKey(name: $"FK_{table}_AspNetUsers_OwnerId", table: table);
            }

            migrationBuilder.DropIndex(name: "IX_Campaigns_OwnerId_Status", table: "Campaigns");
            migrationBuilder.DropIndex(name: "IX_ContentPosts_OwnerId", table: "ContentPosts");
            migrationBuilder.DropIndex(name: "IX_FacebookGroups_OwnerId_Url", table: "FacebookGroups");

            // Settings back to a single row with Id = 1.
            migrationBuilder.Sql("""
                DELETE s FROM [PublisherSettings] s
                WHERE s.[OwnerId] <> (SELECT TOP 1 p.[OwnerId] FROM [PublisherSettings] p
                                      LEFT JOIN [AspNetUsers] u ON u.[Id] = p.[OwnerId]
                                      ORDER BY u.[CreatedAtUtc]);
                """);
            migrationBuilder.DropPrimaryKey(name: "PK_PublisherSettings", table: "PublisherSettings");
            migrationBuilder.AddColumn<int>(name: "Id", table: "PublisherSettings", type: "int", nullable: false, defaultValue: 1);
            migrationBuilder.AddColumn<string>(name: "BrowserProfilePath", table: "PublisherSettings",
                type: "nvarchar(500)", maxLength: 500, nullable: false, defaultValue: "");
            migrationBuilder.Sql("UPDATE [PublisherSettings] SET [BrowserProfilePath] = ISNULL([LegacyBrowserProfilePath], N'');");
            migrationBuilder.DropColumn(name: "LegacyBrowserProfilePath", table: "PublisherSettings");
            migrationBuilder.AddPrimaryKey(name: "PK_PublisherSettings", table: "PublisherSettings", column: "Id");

            // Global uniqueness of group URLs again: keep the oldest copy of a URL saved by several users.
            migrationBuilder.Sql("""
                DELETE g FROM [FacebookGroups] g
                WHERE EXISTS (SELECT 1 FROM [FacebookGroups] o
                              WHERE o.[Url] = g.[Url] AND (o.[CreatedAtUtc] < g.[CreatedAtUtc]
                                    OR (o.[CreatedAtUtc] = g.[CreatedAtUtc] AND o.[Id] < g.[Id])));
                """);
            migrationBuilder.CreateIndex(name: "IX_FacebookGroups_Url", table: "FacebookGroups", column: "Url", unique: true);

            foreach (var table in OwnedTables)
            {
                migrationBuilder.DropColumn(name: "OwnerId", table: table);
            }

            migrationBuilder.DropTable(name: "AspNetRoleClaims");
            migrationBuilder.DropTable(name: "AspNetUserClaims");
            migrationBuilder.DropTable(name: "AspNetUserLogins");
            migrationBuilder.DropTable(name: "AspNetUserRoles");
            migrationBuilder.DropTable(name: "AspNetUserTokens");
            migrationBuilder.DropTable(name: "AspNetRoles");
            migrationBuilder.DropTable(name: "AspNetUsers");
        }
    }
}
