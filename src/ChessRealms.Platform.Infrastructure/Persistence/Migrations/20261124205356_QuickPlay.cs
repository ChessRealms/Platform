using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QuickPlay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuickPlayTickets",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuickPlayTickets", x => new { x.PlayerId, x.Id });
                    table.ForeignKey(
                        name: "FK_QuickPlayTickets_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuickPlayTickets_ExpiresAt",
                table: "QuickPlayTickets",
                column: "ExpiresAt",
                filter: "\"Status\" = 'Waiting'");

            migrationBuilder.CreateIndex(
                name: "IX_QuickPlayTickets_GameId",
                table: "QuickPlayTickets",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_QuickPlayTickets_PlayerId",
                table: "QuickPlayTickets",
                column: "PlayerId",
                unique: true,
                filter: "\"Status\" = 'Waiting'");

            migrationBuilder.CreateIndex(
                name: "IX_QuickPlayTickets_PlayerId_Sequence",
                table: "QuickPlayTickets",
                columns: new[] { "PlayerId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_QuickPlayTickets_Sequence",
                table: "QuickPlayTickets",
                column: "Sequence",
                filter: "\"Status\" = 'Waiting'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuickPlayTickets");
        }
    }
}
