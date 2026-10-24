using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maldex.Data.Migrations
{
    /// <inheritdoc />
    public partial class SandboxTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TemplateVersionId",
                table: "Tasks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Templates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Templates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TemplateVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    ParentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Origin = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FileStem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WindowsEdition = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    WindowsBuild = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    OfficePresent = table.Column<bool>(type: "bit", nullable: true),
                    AgentVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SysmonConfigSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    MitmCaSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    SourceMediaIds = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SealedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SealedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateVersions_TemplateVersions_ParentVersionId",
                        column: x => x.ParentVersionId,
                        principalTable: "TemplateVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemplateVersions_Templates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemplateVersions_Users_SealedByUserId",
                        column: x => x.SealedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TemplateVersionId",
                table: "Tasks",
                column: "TemplateVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Templates_CurrentVersionId",
                table: "Templates",
                column: "CurrentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Templates_IsDefault",
                table: "Templates",
                column: "IsDefault",
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateVersions_ParentVersionId",
                table: "TemplateVersions",
                column: "ParentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateVersions_SealedByUserId",
                table: "TemplateVersions",
                column: "SealedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateVersions_TemplateId_Number",
                table: "TemplateVersions",
                columns: new[] { "TemplateId", "Number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_TemplateVersions_TemplateVersionId",
                table: "Tasks",
                column: "TemplateVersionId",
                principalTable: "TemplateVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Templates_TemplateVersions_CurrentVersionId",
                table: "Templates",
                column: "CurrentVersionId",
                principalTable: "TemplateVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_TemplateVersions_TemplateVersionId",
                table: "Tasks");

            migrationBuilder.DropForeignKey(
                name: "FK_Templates_TemplateVersions_CurrentVersionId",
                table: "Templates");

            migrationBuilder.DropTable(
                name: "TemplateVersions");

            migrationBuilder.DropTable(
                name: "Templates");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_TemplateVersionId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "TemplateVersionId",
                table: "Tasks");
        }
    }
}
