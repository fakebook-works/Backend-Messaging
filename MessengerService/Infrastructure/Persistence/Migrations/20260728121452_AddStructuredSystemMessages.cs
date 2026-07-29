using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessengerService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStructuredSystemMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "messenger",
                table: "messages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "User");

            migrationBuilder.AddColumn<string>(
                name: "system_event",
                schema: "messenger",
                table: "messages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "system_subject_user_id",
                schema: "messenger",
                table: "messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_system_subject_user_id",
                schema: "messenger",
                table: "messages",
                column: "system_subject_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_kind",
                schema: "messenger",
                table: "messages",
                sql: "kind IN ('User', 'System')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_system_event",
                schema: "messenger",
                table: "messages",
                sql: "system_event IS NULL OR system_event IN ('MemberAdded', 'MemberRemoved', 'MemberLeft', 'AdminGranted', 'AdminRevoked', 'GroupRenamed', 'GroupAvatarChanged')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_system_shape",
                schema: "messenger",
                table: "messages",
                sql: "(kind = 'User' AND system_event IS NULL AND system_subject_user_id IS NULL) OR (kind = 'System' AND system_event IS NOT NULL AND ((system_event IN ('GroupRenamed', 'GroupAvatarChanged') AND system_subject_user_id IS NULL) OR (system_event NOT IN ('GroupRenamed', 'GroupAvatarChanged') AND system_subject_user_id IS NOT NULL)) AND text IS NULL AND reply_to_message_id IS NULL AND edited_at IS NULL AND deleted_at IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_messages_system_subject_users",
                schema: "messenger",
                table: "messages",
                column: "system_subject_user_id",
                principalSchema: "messenger",
                principalTable: "users",
                principalColumn: "user_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_messages_system_subject_users",
                schema: "messenger",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_system_subject_user_id",
                schema: "messenger",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_kind",
                schema: "messenger",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_system_event",
                schema: "messenger",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_system_shape",
                schema: "messenger",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "messenger",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "system_event",
                schema: "messenger",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "system_subject_user_id",
                schema: "messenger",
                table: "messages");
        }
    }
}
