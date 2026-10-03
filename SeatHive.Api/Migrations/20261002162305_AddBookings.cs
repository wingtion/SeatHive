using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SeatHive.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Bookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SeatId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bookings_Seats_SeatId",
                        column: x => x.SeatId,
                        principalTable: "Seats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Bookings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Move the existing bookings before the old columns are dropped.
            // The booking time was never stored, so the migration time is used.
            // Seats booked by a user id that does not exist cannot be kept (the booking needs a real user)
            // and become free.
            migrationBuilder.Sql(
                """
                INSERT INTO "Bookings" ("SeatId", "UserId", "Status", "CreatedAt", "ConfirmedAt")
                SELECT s."Id", s."UserId", 'Confirmed', now(), now()
                FROM "Seats" s
                JOIN "Users" u ON u."Id" = s."UserId"
                WHERE s."IsBooked";
                """);

            migrationBuilder.DropIndex(
                name: "IX_Seats_EventId",
                table: "Seats");

            migrationBuilder.DropColumn(
                name: "IsBooked",
                table: "Seats");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Seats");

            migrationBuilder.CreateIndex(
                name: "IX_Seats_EventId_Section_Row_SeatNumber",
                table: "Seats",
                columns: new[] { "EventId", "Section", "Row", "SeatNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_SeatId",
                table: "Bookings",
                column: "SeatId",
                unique: true,
                filter: "\"Status\" IN ('Held', 'Confirmed')");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_UserId",
                table: "Bookings",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Seats_EventId_Section_Row_SeatNumber",
                table: "Seats");

            migrationBuilder.AddColumn<bool>(
                name: "IsBooked",
                table: "Seats",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "Seats",
                type: "integer",
                nullable: true);

            // Put the active bookings back on the seats before the table is dropped.
            migrationBuilder.Sql(
                """
                UPDATE "Seats" s
                SET "IsBooked" = true, "UserId" = b."UserId"
                FROM "Bookings" b
                WHERE b."SeatId" = s."Id" AND b."Status" IN ('Held', 'Confirmed');
                """);

            migrationBuilder.DropTable(
                name: "Bookings");

            migrationBuilder.CreateIndex(
                name: "IX_Seats_EventId",
                table: "Seats",
                column: "EventId");
        }
    }
}
