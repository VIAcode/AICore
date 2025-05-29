using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCoreApi.Migrations
{
    /// <inheritdoc />
    public partial class AgentFlowName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "flow_name",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "login",
                keyColumn: "login_id",
                keyValue: 1,
                column: "created",
                value: new DateTime(2025, 5, 28, 8, 41, 28, 173, DateTimeKind.Utc).AddTicks(5));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "flow_name",
                table: "agents");

            migrationBuilder.UpdateData(
                table: "login",
                keyColumn: "login_id",
                keyValue: 1,
                column: "created",
                value: new DateTime(2025, 4, 13, 17, 39, 39, 810, DateTimeKind.Utc).AddTicks(4300));
        }
    }
}
