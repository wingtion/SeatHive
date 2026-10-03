using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeatHive.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeAnnouncementAndRefund : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnnouncedAt",
                schema: "worker",
                table: "SimulatedCharges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundedAt",
                schema: "worker",
                table: "SimulatedCharges",
                type: "timestamp with time zone",
                nullable: true);

            // The results of the charges that exist were announced right after the charge, so they count as announced.
            // Without this they would count as "result not announced yet" and never be cleaned up.
            // Charges of the last hour are left alone: the result of one of them may still be on its way while
            // this runs, and marking it would keep it from being announced. Those few rows are then never cleaned up.
            migrationBuilder.Sql("UPDATE worker.\"SimulatedCharges\" SET \"AnnouncedAt\" = \"ChargedAt\" WHERE \"ChargedAt\" < now() - interval '1 hour';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnnouncedAt",
                schema: "worker",
                table: "SimulatedCharges");

            migrationBuilder.DropColumn(
                name: "RefundedAt",
                schema: "worker",
                table: "SimulatedCharges");
        }
    }
}
