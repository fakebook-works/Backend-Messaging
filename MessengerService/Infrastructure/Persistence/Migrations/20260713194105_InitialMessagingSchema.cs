using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessengerService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialMessagingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "messenger");

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "messenger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    avatar_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    direct_user_low_id = table.Column<long>(type: "bigint", nullable: true),
                    direct_user_high_id = table.Column<long>(type: "bigint", nullable: true),
                    current_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversations", x => x.id);
                    table.CheckConstraint("ck_conversations_current_sequence", "current_sequence >= 0");
                    table.CheckConstraint("ck_conversations_direct_pair", "(type = 'Direct' AND direct_user_low_id IS NOT NULL AND direct_user_high_id IS NOT NULL AND direct_user_low_id > 0 AND direct_user_low_id < direct_user_high_id) OR (type = 'Group' AND direct_user_low_id IS NULL AND direct_user_high_id IS NULL)");
                    table.CheckConstraint("ck_conversations_type", "type IN ('Direct', 'Group')");
                    table.CheckConstraint("ck_conversations_updated_at", "updated_at >= created_at");
                });

            migrationBuilder.CreateTable(
                name: "outbox_events",
                schema: "messenger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    subject_user_id = table.Column<long>(type: "bigint", nullable: true),
                    sequence = table.Column<long>(type: "bigint", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_events", x => x.id);
                    table.CheckConstraint("ck_outbox_events_actor_user_id", "actor_user_id IS NULL OR actor_user_id > 0");
                    table.CheckConstraint("ck_outbox_events_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("ck_outbox_events_sequence", "sequence IS NULL OR sequence >= 0");
                    table.CheckConstraint("ck_outbox_events_subject_user_id", "subject_user_id IS NULL OR subject_user_id > 0");
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "messenger",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.user_id);
                    table.CheckConstraint("ck_users_deleted_state", "(status = 'Active' AND deleted_at IS NULL) OR (status = 'Deleted' AND deleted_at IS NOT NULL)");
                    table.CheckConstraint("ck_users_status", "status IN ('Active', 'Deleted')");
                    table.CheckConstraint("ck_users_user_id_positive", "user_id > 0");
                });

            migrationBuilder.CreateTable(
                name: "conversation_participants",
                schema: "messenger",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_delivered_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    last_read_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation_participants", x => new { x.conversation_id, x.user_id });
                    table.CheckConstraint("ck_conversation_participants_membership_dates", "left_at IS NULL OR left_at >= joined_at");
                    table.CheckConstraint("ck_conversation_participants_receipts", "last_delivered_sequence >= 0 AND last_read_sequence >= 0 AND last_read_sequence <= last_delivered_sequence");
                    table.CheckConstraint("ck_conversation_participants_role", "role IN ('Admin', 'Member')");
                    table.CheckConstraint("ck_conversation_participants_user_id", "user_id > 0");
                    table.ForeignKey(
                        name: "fk_conversation_participants_conversations",
                        column: x => x.conversation_id,
                        principalSchema: "messenger",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_conversation_participants_users",
                        column: x => x.user_id,
                        principalSchema: "messenger",
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "messenger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_user_id = table.Column<long>(type: "bigint", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    client_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    reply_to_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.CheckConstraint("ck_messages_client_message_id", "client_message_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_messages_delete_date", "deleted_at IS NULL OR deleted_at >= created_at");
                    table.CheckConstraint("ck_messages_edit_date", "edited_at IS NULL OR edited_at >= created_at");
                    table.CheckConstraint("ck_messages_sender_user_id", "sender_user_id > 0");
                    table.CheckConstraint("ck_messages_sequence", "sequence > 0");
                    table.ForeignKey(
                        name: "fk_messages_conversations",
                        column: x => x.conversation_id,
                        principalSchema: "messenger",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_messages_reply_to_message",
                        column: x => x.reply_to_message_id,
                        principalSchema: "messenger",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_messages_users",
                        column: x => x.sender_user_id,
                        principalSchema: "messenger",
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "presence",
                schema: "messenger",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    is_online = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_presence", x => x.user_id);
                    table.CheckConstraint("ck_presence_user_id", "user_id > 0");
                    table.ForeignKey(
                        name: "fk_presence_users",
                        column: x => x.user_id,
                        principalSchema: "messenger",
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_attachments",
                schema: "messenger",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_attachments", x => new { x.message_id, x.ordinal });
                    table.CheckConstraint("ck_message_attachments_https_url", "url LIKE 'https://%'");
                    table.CheckConstraint("ck_message_attachments_ordinal", "ordinal >= 0 AND ordinal < 10");
                    table.ForeignKey(
                        name: "fk_message_attachments_messages",
                        column: x => x.message_id,
                        principalSchema: "messenger",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_reactions",
                schema: "messenger",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    emoji = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_reactions", x => new { x.message_id, x.user_id });
                    table.CheckConstraint("ck_message_reactions_emoji", "length(btrim(emoji)) > 0");
                    table.CheckConstraint("ck_message_reactions_updated_at", "updated_at >= created_at");
                    table.CheckConstraint("ck_message_reactions_user_id", "user_id > 0");
                    table.ForeignKey(
                        name: "fk_message_reactions_messages",
                        column: x => x.message_id,
                        principalSchema: "messenger",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_message_reactions_users",
                        column: x => x.user_id,
                        principalSchema: "messenger",
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversation_participants_active_conversation",
                schema: "messenger",
                table: "conversation_participants",
                column: "conversation_id",
                filter: "left_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_conversation_participants_active_user",
                schema: "messenger",
                table: "conversation_participants",
                columns: new[] { "user_id", "conversation_id" },
                filter: "left_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_updated_at",
                schema: "messenger",
                table: "conversations",
                column: "updated_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ux_conversations_direct_pair",
                schema: "messenger",
                table: "conversations",
                columns: new[] { "direct_user_low_id", "direct_user_high_id" },
                unique: true,
                filter: "type = 'Direct'");

            migrationBuilder.CreateIndex(
                name: "ix_message_reactions_user_id",
                schema: "messenger",
                table: "message_reactions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_reply_to_message_id",
                schema: "messenger",
                table: "messages",
                column: "reply_to_message_id");

            migrationBuilder.CreateIndex(
                name: "ux_messages_conversation_sequence",
                schema: "messenger",
                table: "messages",
                columns: new[] { "conversation_id", "sequence" },
                unique: true,
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_messages_sender_client_message",
                schema: "messenger",
                table: "messages",
                columns: new[] { "sender_user_id", "client_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_pending",
                schema: "messenger",
                table: "outbox_events",
                columns: new[] { "next_attempt_at", "created_at" },
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_pending_topic",
                schema: "messenger",
                table: "outbox_events",
                columns: new[] { "topic", "created_at" },
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_presence_online_expiry",
                schema: "messenger",
                table: "presence",
                column: "expires_at",
                filter: "is_online = TRUE");

            migrationBuilder.CreateIndex(
                name: "ix_users_status",
                schema: "messenger",
                table: "users",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_participants",
                schema: "messenger");

            migrationBuilder.DropTable(
                name: "message_attachments",
                schema: "messenger");

            migrationBuilder.DropTable(
                name: "message_reactions",
                schema: "messenger");

            migrationBuilder.DropTable(
                name: "outbox_events",
                schema: "messenger");

            migrationBuilder.DropTable(
                name: "presence",
                schema: "messenger");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "messenger");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "messenger");

            migrationBuilder.DropTable(
                name: "users",
                schema: "messenger");
        }
    }
}
