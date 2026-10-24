using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maldex.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemplateJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TemplateJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TemplateId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Network = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfiguringSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastConsoleAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublishOnSuccess = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SealRequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelRequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateJobs_TemplateVersions_BaseVersionId",
                        column: x => x.BaseVersionId,
                        principalTable: "TemplateVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemplateJobs_TemplateVersions_ResultVersionId",
                        column: x => x.ResultVersionId,
                        principalTable: "TemplateVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemplateJobs_Templates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemplateJobs_Users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TemplateJobs_BaseVersionId",
                table: "TemplateJobs",
                column: "BaseVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateJobs_RequestedByUserId",
                table: "TemplateJobs",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateJobs_ResultVersionId",
                table: "TemplateJobs",
                column: "ResultVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateJobs_TemplateId_Active",
                table: "TemplateJobs",
                column: "TemplateId",
                unique: true,
                filter: "[State] <> 'Published' AND [State] <> 'Sealed' AND [State] <> 'Failed' AND [State] <> 'Cancelled'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TemplateJobs");
        }
    }
}
