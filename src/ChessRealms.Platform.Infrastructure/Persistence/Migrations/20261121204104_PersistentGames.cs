using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistentGames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WhitePlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlackPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    RulesRecord = table.Column<string>(type: "jsonb", nullable: false),
                    Fen = table.Column<string>(type: "text", nullable: false),
                    SideToMove = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Winner = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    EndReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Games");
        }
    }
}
