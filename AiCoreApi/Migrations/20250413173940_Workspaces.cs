using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiCoreApi.Migrations
{
    /// <inheritdoc />
    public partial class Workspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "workspace_id",
                table: "ingestion",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "workspace_id",
                table: "debug_log",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "workspace_id",
                table: "connection",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "workspace_id",
                table: "agents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workspaces",
                columns: table => new
                {
                    workspace_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspaces", x => x.workspace_id);
                });

            migrationBuilder.CreateTable(
                name: "tags_x_workspaces",
                columns: table => new
                {
                    tags_tag_id = table.Column<int>(type: "integer", nullable: false),
                    workspaces_workspace_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tags_x_workspaces", x => new { x.tags_tag_id, x.workspaces_workspace_id });
                    table.ForeignKey(
                        name: "fk_tags_x_workspaces_tags_tags_tag_id",
                        column: x => x.tags_tag_id,
                        principalTable: "tags",
                        principalColumn: "tag_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tags_x_workspaces_workspaces_workspaces_workspace_id",
                        column: x => x.workspaces_workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "workspace_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "login",
                keyColumn: "login_id",
                keyValue: 1,
                column: "created",
                value: new DateTime(2025, 4, 13, 17, 39, 39, 810, DateTimeKind.Utc).AddTicks(4300));

            migrationBuilder.CreateIndex(
                name: "ix_tags_x_workspaces_workspaces_workspace_id",
                table: "tags_x_workspaces",
                column: "workspaces_workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tags_x_workspaces");

            migrationBuilder.DropTable(
                name: "workspaces");

            migrationBuilder.DropColumn(
                name: "workspace_id",
                table: "ingestion");

            migrationBuilder.DropColumn(
                name: "workspace_id",
                table: "debug_log");

            migrationBuilder.DropColumn(
                name: "workspace_id",
                table: "connection");

            migrationBuilder.DropColumn(
                name: "workspace_id",
                table: "agents");

            migrationBuilder.UpdateData(
                table: "login",
                keyColumn: "login_id",
                keyValue: 1,
                column: "created",
                value: new DateTime(2025, 3, 7, 11, 37, 33, 611, DateTimeKind.Utc).AddTicks(4272));
        }
    }
}
