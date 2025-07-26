using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCoreApi.Migrations
{
    /// <inheritdoc />
    public partial class TaskLockerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "locker_task_id",
                table: "task",
                type: "integer",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "login",
                keyColumn: "login_id",
                keyValue: 1,
                column: "created",
                value: new DateTime(2025, 7, 26, 16, 48, 46, 727, DateTimeKind.Utc).AddTicks(1085));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "locker_task_id",
                table: "task");

            migrationBuilder.UpdateData(
                table: "login",
                keyColumn: "login_id",
                keyValue: 1,
                column: "created",
                value: new DateTime(2025, 6, 21, 20, 36, 55, 373, DateTimeKind.Utc).AddTicks(1603));
        }
    }
}
