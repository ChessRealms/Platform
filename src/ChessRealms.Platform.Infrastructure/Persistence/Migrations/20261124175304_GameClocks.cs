using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GameClocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeControl",
                table: "PrivateLobbies",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "BlackTimeRemaining",
                table: "Games",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeControl",
                table: "Games",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TurnDeadline",
                table: "Games",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "WhiteTimeRemaining",
                table: "Games",
                type: "interval",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeControl",
                table: "PrivateLobbies");

            migrationBuilder.DropColumn(
                name: "BlackTimeRemaining",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "TimeControl",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "TurnDeadline",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "WhiteTimeRemaining",
                table: "Games");
        }
    }
}
