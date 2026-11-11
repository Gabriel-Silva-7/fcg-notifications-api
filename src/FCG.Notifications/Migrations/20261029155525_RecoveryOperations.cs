using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecoveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConflictId = table.Column<Guid>(type: "uuid", nullable: true),
                    IntentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttemptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Operation = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryAttempts", x => x.Id);
                    table.CheckConstraint("CK_RecoveryAttempts_Target", "(\"Operation\"='ReplayConflict' AND \"ConflictId\" IS NOT NULL AND \"IntentId\" IS NULL) OR (\"Operation\"='ResumeIntent' AND \"IntentId\" IS NOT NULL AND \"ConflictId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_RecoveryAttempts_MessageConflicts_ConflictId",
                        column: x => x.ConflictId,
                        principalTable: "MessageConflicts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecoveryAttempts_NotificationIntents_IntentId",
                        column: x => x.IntentId,
                        principalTable: "NotificationIntents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryAttempts_ConflictId_AttemptedAtUtc",
                table: "RecoveryAttempts",
                columns: new[] { "ConflictId", "AttemptedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryAttempts_IntentId_AttemptedAtUtc",
                table: "RecoveryAttempts",
                columns: new[] { "IntentId", "AttemptedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecoveryAttempts");
        }
    }
}
