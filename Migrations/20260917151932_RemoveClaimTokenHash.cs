using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamArchive.Migrations
{
    /// <inheritdoc />
    public partial class RemoveClaimTokenHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Papers_ClaimTokenHash",
                table: "Papers");

            migrationBuilder.DropColumn(
                name: "ClaimTokenHash",
                table: "Papers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimTokenHash",
                table: "Papers",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Papers_ClaimTokenHash",
                table: "Papers",
                column: "ClaimTokenHash",
                unique: true);
        }
    }
}
