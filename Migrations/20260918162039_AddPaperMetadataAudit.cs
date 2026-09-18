using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace ExamArchive.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperMetadataAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaperMetadataAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    PaperId = table.Column<int>(type: "int", nullable: false),
                    EditedByUserId = table.Column<int>(type: "int", nullable: true),
                    EditedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    OldSubjectId = table.Column<int>(type: "int", nullable: false),
                    NewSubjectId = table.Column<int>(type: "int", nullable: false),
                    OldExamType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    NewExamType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    OldMonth = table.Column<int>(type: "int", nullable: false),
                    NewMonth = table.Column<int>(type: "int", nullable: false),
                    OldYear = table.Column<int>(type: "int", nullable: false),
                    NewYear = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperMetadataAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperMetadataAudits_Papers_PaperId",
                        column: x => x.PaperId,
                        principalTable: "Papers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaperMetadataAudits_Users_EditedByUserId",
                        column: x => x.EditedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PaperMetadataAudits_EditedByUserId",
                table: "PaperMetadataAudits",
                column: "EditedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperMetadataAudits_PaperId_EditedAt",
                table: "PaperMetadataAudits",
                columns: new[] { "PaperId", "EditedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaperMetadataAudits");
        }
    }
}
