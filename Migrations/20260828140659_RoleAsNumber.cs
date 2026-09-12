using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExamArchive.Migrations
{
    /// <inheritdoc />
    public partial class RoleAsNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_User_Role",
                table: "Users");

            // Not AlterColumn, which EF scaffolded here and which would ask MySQL to
            // read 'Admin' as an integer — silently zeroing every role. The values
            // are translated into a new column instead, and ELSE 0 is left failing
            // the new constraint rather than guessing a role.
            migrationBuilder.Sql("ALTER TABLE `Users` ADD COLUMN `RoleNumber` int NOT NULL DEFAULT 0;");

            migrationBuilder.Sql(
                "UPDATE `Users` SET `RoleNumber` = CASE `Role` "
                + "WHEN 'SuperAdmin' THEN 1 "
                + "WHEN 'Admin' THEN 2 "
                + "WHEN 'Moderator' THEN 3 "
                + "WHEN 'User' THEN 4 "
                + "ELSE 0 END;");

            migrationBuilder.Sql("ALTER TABLE `Users` DROP COLUMN `Role`;");
            migrationBuilder.Sql("ALTER TABLE `Users` CHANGE `RoleNumber` `Role` int NOT NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_Role",
                table: "Users",
                sql: "`Role` IN (1, 2, 3, 4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_User_Role",
                table: "Users");

            // Cannot fully undo the change: SuperAdmin has no name in the constraint
            // restored below, so rolling back a database that has one fails there
            // rather than silently demoting them.
            migrationBuilder.Sql("ALTER TABLE `Users` ADD COLUMN `RoleName` varchar(20) NOT NULL DEFAULT '';");

            migrationBuilder.Sql(
                "UPDATE `Users` SET `RoleName` = CASE `Role` "
                + "WHEN 1 THEN 'SuperAdmin' "
                + "WHEN 2 THEN 'Admin' "
                + "WHEN 3 THEN 'Moderator' "
                + "WHEN 4 THEN 'User' "
                + "ELSE '' END;");

            migrationBuilder.Sql("ALTER TABLE `Users` DROP COLUMN `Role`;");
            migrationBuilder.Sql("ALTER TABLE `Users` CHANGE `RoleName` `Role` varchar(20) NOT NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_Role",
                table: "Users",
                sql: "`Role` IN ('User', 'Moderator', 'Admin')");
        }
    }
}
