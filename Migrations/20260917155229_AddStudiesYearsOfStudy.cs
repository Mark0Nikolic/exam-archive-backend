using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamArchive.Migrations
{
    /// <inheritdoc />
    public partial class AddStudiesYearsOfStudy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MajorSubject_YearOfStudy",
                table: "MajorSubjects");

            migrationBuilder.AddColumn<int>(
                name: "YearsOfStudy",
                table: "Studies",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "Studies",
                keyColumn: "Id",
                keyValue: 1,
                column: "YearsOfStudy",
                value: 3);

            migrationBuilder.UpdateData(
                table: "Studies",
                keyColumn: "Id",
                keyValue: 2,
                column: "YearsOfStudy",
                value: 2);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Studies_YearsOfStudy",
                table: "Studies",
                sql: "`YearsOfStudy` >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MajorSubject_YearOfStudy",
                table: "MajorSubjects",
                sql: "`YearOfStudy` >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Studies_YearsOfStudy",
                table: "Studies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MajorSubject_YearOfStudy",
                table: "MajorSubjects");

            migrationBuilder.DropColumn(
                name: "YearsOfStudy",
                table: "Studies");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MajorSubject_YearOfStudy",
                table: "MajorSubjects",
                sql: "`YearOfStudy` >= 1 AND `YearOfStudy` <= 6");
        }
    }
}
