using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maldex.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemplateVersionCurrentUntil : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CurrentUntil",
                table: "TemplateVersions",
                type: "datetimeoffset",
                nullable: true);

            // Existing non-current Ready versions were current before (Plan 1 only kept the previous current one):
            // backfill when they stopped being current with the time they were sealed.
            migrationBuilder.Sql(@"
UPDATE v SET CurrentUntil = v.SealedAt
FROM TemplateVersions v
WHERE v.Status = 'Ready'
  AND NOT EXISTS (SELECT 1 FROM Templates t WHERE t.CurrentVersionId = v.Id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentUntil",
                table: "TemplateVersions");
        }
    }
}
