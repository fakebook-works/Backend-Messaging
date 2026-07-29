using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessengerService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpandMessageTextForEditHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "text",
                schema: "messenger",
                table: "messages",
                type: "character varying(200000)",
                maxLength: 200000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10000)",
                oldMaxLength: 10000,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "text",
                schema: "messenger",
                table: "messages",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200000)",
                oldMaxLength: 200000,
                oldNullable: true);
        }
    }
}
