using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class WelcomeIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RecipientName = table.Column<string>(type: "text", nullable: true),
                    RecipientEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ContactEventVersion = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PreparedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationIntents", x => x.Id);
                    table.CheckConstraint("CK_NotificationIntents_ReadyContact", "\"State\" = 'PendingContact' OR (\"RecipientName\" IS NOT NULL AND \"RecipientEmail\" IS NOT NULL AND \"ContactEventVersion\" IS NOT NULL AND \"ContactEventVersion\" > 0)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationIntents_Kind_BusinessId",
                table: "NotificationIntents",
                columns: new[] { "Kind", "BusinessId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationIntents_State_CreatedAtUtc",
                table: "NotificationIntents",
                columns: new[] { "State", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationIntents");
        }
    }
}
