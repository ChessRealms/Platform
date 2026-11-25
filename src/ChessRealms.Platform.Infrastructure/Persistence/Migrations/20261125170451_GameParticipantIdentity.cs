using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GameParticipantIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlackPlayerKind",
                table: "Games",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "Account");

            migrationBuilder.AddColumn<string>(
                name: "WhitePlayerKind",
                table: "Games",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "Account");

            migrationBuilder.Sql("""
                UPDATE "Games"
                SET "WhitePlayerKind" = CASE WHEN "WhiteGuestName" IS NOT NULL THEN 'Guest' ELSE 'Account' END,
                    "BlackPlayerKind" = CASE WHEN "BlackGuestName" IS NOT NULL THEN 'Guest' ELSE 'Account' END
                WHERE "WhiteGuestName" IS NOT NULL OR "BlackGuestName" IS NOT NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "BlackPlayerKind",
                table: "Games",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8)",
                oldMaxLength: 8,
                oldDefaultValue: "Account");

            migrationBuilder.AlterColumn<string>(
                name: "WhitePlayerKind",
                table: "Games",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8)",
                oldMaxLength: 8,
                oldDefaultValue: "Account");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlackPlayerKind",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "WhitePlayerKind",
                table: "Games");
        }
    }
}
