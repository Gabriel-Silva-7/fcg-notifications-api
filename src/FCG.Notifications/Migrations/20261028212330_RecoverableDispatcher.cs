using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class RecoverableDispatcher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "NotificationIntents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastFailureCode",
                table: "NotificationIntents",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAtUtc",
                table: "NotificationIntents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "NotificationIntents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LeaseVersion",
                table: "NotificationIntents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAtUtc",
                table: "NotificationIntents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SentAtUtc",
                table: "NotificationIntents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseVersion = table.Column<long>(type: "bigint", nullable: false),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationAttempts_NotificationIntents_IntentId",
                        column: x => x.IntentId,
                        principalTable: "NotificationIntents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationAttempts_IntentId_LeaseVersion",
                table: "NotificationAttempts",
                columns: new[] { "IntentId", "LeaseVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationAttempts");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "NotificationIntents");

            migrationBuilder.DropColumn(
                name: "LastFailureCode",
                table: "NotificationIntents");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                table: "NotificationIntents");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "NotificationIntents");

            migrationBuilder.DropColumn(
                name: "LeaseVersion",
                table: "NotificationIntents");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                table: "NotificationIntents");

            migrationBuilder.DropColumn(
                name: "SentAtUtc",
                table: "NotificationIntents");
        }
    }
}
