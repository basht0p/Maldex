using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maldex.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemplateJobSlotHeldAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SlotHeldAt",
                table: "TemplateJobs",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SlotHeldAt",
                table: "TemplateJobs");
        }
    }
}
