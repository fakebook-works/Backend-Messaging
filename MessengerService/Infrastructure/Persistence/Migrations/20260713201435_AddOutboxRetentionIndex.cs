using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessengerService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_processed",
                schema: "messenger",
                table: "outbox_events",
                column: "processed_at",
                filter: "processed_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_events_processed",
                schema: "messenger",
                table: "outbox_events");
        }
    }
}
