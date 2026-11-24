using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GameRematches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinishedAt",
                table: "Games",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RematchGameId",
                table: "Games",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RematchOfferedBy",
                table: "Games",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Games_RematchGameId",
                table: "Games",
                column: "RematchGameId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Games_Games_RematchGameId",
                table: "Games",
                column: "RematchGameId",
                principalTable: "Games",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Games_Games_RematchGameId",
                table: "Games");

            migrationBuilder.DropIndex(
                name: "IX_Games_RematchGameId",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "RematchGameId",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "RematchOfferedBy",
                table: "Games");
        }
    }
}
