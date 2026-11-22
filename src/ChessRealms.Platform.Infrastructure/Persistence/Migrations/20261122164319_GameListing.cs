using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GameListing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "Games",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Games_BlackPlayerId_Status",
                table: "Games",
                columns: new[] { "BlackPlayerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Games_WhitePlayerId_Status",
                table: "Games",
                columns: new[] { "WhitePlayerId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Games_BlackPlayerId_Status",
                table: "Games");

            migrationBuilder.DropIndex(
                name: "IX_Games_WhitePlayerId_Status",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Games");
        }
    }
}
