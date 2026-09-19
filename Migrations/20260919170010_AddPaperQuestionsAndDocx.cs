using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace ExamArchive.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperQuestionsAndDocx : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PaperFile_ContentType",
                table: "PaperFiles");

            migrationBuilder.AddColumn<string>(
                name: "ParseError",
                table: "Papers",
                type: "varchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParseStatus",
                table: "Papers",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotQueued");

            migrationBuilder.AddColumn<DateTime>(
                name: "ParsedAt",
                table: "Papers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Questions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    SubjectId = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "longtext", nullable: false),
                    ContentHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "(UTC_TIMESTAMP())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Questions_Subjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "Subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaperQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    PaperId = table.Column<int>(type: "int", nullable: false),
                    QuestionId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperQuestions", x => x.Id);
                    table.CheckConstraint("CK_PaperQuestion_Ordinal", "`Ordinal` >= 1");
                    table.ForeignKey(
                        name: "FK_PaperQuestions_Papers_PaperId",
                        column: x => x.PaperId,
                        principalTable: "Papers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaperQuestions_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Paper_ParseError",
                table: "Papers",
                sql: "`ParseStatus` IN ('Skipped', 'Failed') OR `ParseError` IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Paper_ParseStatus",
                table: "Papers",
                sql: "`ParseStatus` IN ('NotQueued', 'Queued', 'Parsed', 'Skipped', 'Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaperFile_ContentType",
                table: "PaperFiles",
                sql: "`ContentType` IN ('application/pdf', 'image/jpeg', 'image/png', 'image/webp', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document')");

            migrationBuilder.CreateIndex(
                name: "IX_PaperQuestions_PaperId_Ordinal",
                table: "PaperQuestions",
                columns: new[] { "PaperId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperQuestions_PaperId_QuestionId",
                table: "PaperQuestions",
                columns: new[] { "PaperId", "QuestionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperQuestions_QuestionId",
                table: "PaperQuestions",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_SubjectId_ContentHash",
                table: "Questions",
                columns: new[] { "SubjectId", "ContentHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaperQuestions");

            migrationBuilder.DropTable(
                name: "Questions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Paper_ParseError",
                table: "Papers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Paper_ParseStatus",
                table: "Papers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaperFile_ContentType",
                table: "PaperFiles");

            migrationBuilder.DropColumn(
                name: "ParseError",
                table: "Papers");

            migrationBuilder.DropColumn(
                name: "ParseStatus",
                table: "Papers");

            migrationBuilder.DropColumn(
                name: "ParsedAt",
                table: "Papers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaperFile_ContentType",
                table: "PaperFiles",
                sql: "`ContentType` IN ('application/pdf', 'image/jpeg', 'image/png', 'image/webp')");
        }
    }
}
