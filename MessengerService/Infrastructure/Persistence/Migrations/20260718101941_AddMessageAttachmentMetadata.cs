using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessengerService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageAttachmentMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_message_attachments_https_url",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.AddColumn<string>(
                name: "asset_id",
                schema: "messenger",
                table: "message_attachments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_type",
                schema: "messenger",
                table: "message_attachments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "duration_ms",
                schema: "messenger",
                table: "message_attachments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "height",
                schema: "messenger",
                table: "message_attachments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "media_type",
                schema: "messenger",
                table: "message_attachments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "original_name",
                schema: "messenger",
                table: "message_attachments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "size_bytes",
                schema: "messenger",
                table: "message_attachments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "thumbnail_url",
                schema: "messenger",
                table: "message_attachments",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "width",
                schema: "messenger",
                table: "message_attachments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_message_attachments_https_url",
                schema: "messenger",
                table: "message_attachments",
                sql: "url LIKE '/media/files/%' OR url LIKE 'https://%'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_message_attachments_media_type",
                schema: "messenger",
                table: "message_attachments",
                sql: "media_type IS NULL OR media_type IN ('image', 'video', 'audio', 'file')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_message_attachments_metadata_nonnegative",
                schema: "messenger",
                table: "message_attachments",
                sql: "(size_bytes IS NULL OR size_bytes >= 0) AND (width IS NULL OR width >= 0) AND (height IS NULL OR height >= 0) AND (duration_ms IS NULL OR duration_ms >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_message_attachments_https_url",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_message_attachments_media_type",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_message_attachments_metadata_nonnegative",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "asset_id",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "content_type",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "duration_ms",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "height",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "media_type",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "original_name",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "size_bytes",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "thumbnail_url",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "width",
                schema: "messenger",
                table: "message_attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_message_attachments_https_url",
                schema: "messenger",
                table: "message_attachments",
                sql: "url LIKE 'https://%'");
        }
    }
}
