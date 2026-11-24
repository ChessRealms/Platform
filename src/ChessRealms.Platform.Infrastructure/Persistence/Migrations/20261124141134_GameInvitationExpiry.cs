using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GameInvitationExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InvitationExpiresAt",
                table: "Games",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Games"
                SET "InvitationExpiresAt" = COALESCE("CreatedAt", statement_timestamp()) + interval '5 minutes'
                WHERE "Status" = 'Pending'
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Games_WhitePlayerId_BlackPlayerId_CreatedAt",
                table: "Games",
                columns: new[] { "WhitePlayerId", "BlackPlayerId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Games_WhitePlayerId_BlackPlayerId_CreatedAt",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "InvitationExpiresAt",
                table: "Games");
        }
    }
}
