using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeatHive.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddSimulatedCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SimulatedCharges",
                schema: "worker",
                columns: table => new
                {
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    ChargedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulatedCharges", x => x.IdempotencyKey);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SimulatedCharges",
                schema: "worker");
        }
    }
}
