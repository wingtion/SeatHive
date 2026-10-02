using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeatHive.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUserRoleAndUniqueEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Users that existed before roles are normal users.
            migrationBuilder.Sql("UPDATE \"Users\" SET \"Role\" = 'User' WHERE \"Role\" = '';");

            // Emails are stored lowercase from now on. If two existing accounts differ only by casing,
            // the unique index below fails and the duplicates must be resolved by hand first.
            migrationBuilder.Sql("UPDATE \"Users\" SET \"Email\" = lower(btrim(\"Email\"));");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Users");
        }
    }
}
