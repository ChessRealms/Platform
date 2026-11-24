using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrivateLobbyLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrivateLobbies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    JoinedPlayerId = table.Column<Guid>(type: "uuid", nullable: true),
                    GameId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivateLobbies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrivateLobbies_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrivateLobbies_CreatorId_RequestId",
                table: "PrivateLobbies",
                columns: new[] { "CreatorId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrivateLobbies_CreatorId_Status_ExpiresAt",
                table: "PrivateLobbies",
                columns: new[] { "CreatorId", "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PrivateLobbies_GameId",
                table: "PrivateLobbies",
                column: "GameId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrivateLobbies");
        }
    }
}
