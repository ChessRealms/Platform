using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChessRealms.Platform.Infrastructure.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PasswordResetEmailDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PasswordResetEmailDeliveries",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecurityStamp = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordResetEmailDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PasswordResetEmailDeliveries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetEmailDeliveries_Status_CreatedAt",
                schema: "identity",
                table: "PasswordResetEmailDeliveries",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetEmailDeliveries_UserId_CreatedAt",
                schema: "identity",
                table: "PasswordResetEmailDeliveries",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasswordResetEmailDeliveries",
                schema: "identity");
        }
    }
}
