using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeatHive.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSeatVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "Seats");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Seats",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }
    }
}
