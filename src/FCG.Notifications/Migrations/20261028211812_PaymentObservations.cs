using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class PaymentObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageConflicts_Kind_BusinessId_EventId_PayloadHash",
                table: "MessageConflicts");

            migrationBuilder.AddColumn<Guid>(
                name: "GameId",
                table: "NotificationIntents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Price",
                table: "NotificationIntents",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessageType",
                table: "MessageConflicts",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "PaymentObservations",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentObservations", x => x.OrderId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageConflicts_Kind_BusinessId_EventId_MessageType_Payloa~",
                table: "MessageConflicts",
                columns: new[] { "Kind", "BusinessId", "EventId", "MessageType", "PayloadHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentObservations");

            migrationBuilder.DropIndex(
                name: "IX_MessageConflicts_Kind_BusinessId_EventId_MessageType_Payloa~",
                table: "MessageConflicts");

            migrationBuilder.DropColumn(
                name: "GameId",
                table: "NotificationIntents");

            migrationBuilder.DropColumn(
                name: "Price",
                table: "NotificationIntents");

            migrationBuilder.DropColumn(
                name: "MessageType",
                table: "MessageConflicts");

            migrationBuilder.CreateIndex(
                name: "IX_MessageConflicts_Kind_BusinessId_EventId_PayloadHash",
                table: "MessageConflicts",
                columns: new[] { "Kind", "BusinessId", "EventId", "PayloadHash" },
                unique: true);
        }
    }
}
