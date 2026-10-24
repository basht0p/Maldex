using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maldex.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemplateVersionFileStemUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TemplateVersions_FileStem",
                table: "TemplateVersions",
                column: "FileStem",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TemplateVersions_FileStem",
                table: "TemplateVersions");
        }
    }
}
