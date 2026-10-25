using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maldex.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaLibraryAndBuildJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "BaseVersionId",
                table: "TemplateJobs",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<int>(
                name: "EditionIndex",
                table: "TemplateJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EditionName",
                table: "TemplateJobs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExpectedCurrentVersionId",
                table: "TemplateJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaIds",
                table: "TemplateJobs",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "MediaFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    Contents = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AddedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaFiles_Users_AddedByUserId",
                        column: x => x.AddedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TemplateJobs_BuildHasNoBase",
                table: "TemplateJobs",
                sql: "([Kind] = 'Build' AND [BaseVersionId] IS NULL) OR ([Kind] <> 'Build' AND [BaseVersionId] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_AddedByUserId",
                table: "MediaFiles",
                column: "AddedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_Sha256_Ready",
                table: "MediaFiles",
                column: "Sha256",
                unique: true,
                filter: "[Status] = 'Ready'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaFiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TemplateJobs_BuildHasNoBase",
                table: "TemplateJobs");

            migrationBuilder.DropColumn(
                name: "EditionIndex",
                table: "TemplateJobs");

            migrationBuilder.DropColumn(
                name: "EditionName",
                table: "TemplateJobs");

            migrationBuilder.DropColumn(
                name: "ExpectedCurrentVersionId",
                table: "TemplateJobs");

            migrationBuilder.DropColumn(
                name: "MediaIds",
                table: "TemplateJobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "BaseVersionId",
                table: "TemplateJobs",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
