using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GameCreationOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreationOrigin",
                table: "Games",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "DirectInvitation");

            migrationBuilder.Sql("""
                UPDATE "Games"
                SET "CreationOrigin" = 'PrivateLobby'
                WHERE "Id" IN (SELECT "GameId" FROM "PrivateLobbies" WHERE "GameId" IS NOT NULL);

                UPDATE "Games"
                SET "CreationOrigin" = 'QuickPlay'
                WHERE "Id" IN (SELECT "GameId" FROM "QuickPlayTickets" WHERE "GameId" IS NOT NULL);

                UPDATE "Games"
                SET "CreationOrigin" = 'Rematch'
                WHERE "Id" IN (SELECT "RematchGameId" FROM "Games" WHERE "RematchGameId" IS NOT NULL);
                """);

            migrationBuilder.AlterColumn<string>(
                name: "CreationOrigin",
                table: "Games",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldDefaultValue: "DirectInvitation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreationOrigin",
                table: "Games");
        }
    }
}
