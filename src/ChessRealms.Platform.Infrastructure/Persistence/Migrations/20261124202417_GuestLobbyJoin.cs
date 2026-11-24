using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GuestLobbyJoin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "JoinedAsGuest",
                table: "PrivateLobbies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BlackGuestName",
                table: "Games",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhiteGuestName",
                table: "Games",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GuestPlayers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SessionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestPlayers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuestPlayers_SessionHash",
                table: "GuestPlayers",
                column: "SessionHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuestPlayers");

            migrationBuilder.DropColumn(
                name: "JoinedAsGuest",
                table: "PrivateLobbies");

            migrationBuilder.DropColumn(
                name: "BlackGuestName",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "WhiteGuestName",
                table: "Games");
        }
    }
}
