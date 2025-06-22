using System;
using System.Collections.Generic;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiCoreApi.Migrations
{
    /// <inheritdoc />
    public partial class Evaluations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evaluation",
                columns: table => new
                {
                    evaluation_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    agent_name = table.Column<string>(type: "text", nullable: false),
                    questions = table.Column<List<EvaluationQuestionModel>>(type: "jsonb", nullable: false),
                    last_run = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_score = table.Column<int>(type: "integer", nullable: false),
                    evaluation_llm_model = table.Column<string>(type: "text", nullable: false),
                    evaluation_prompt = table.Column<string>(type: "text", nullable: false),
                    created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<int>(type: "integer", nullable: false),
                    progress = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evaluation", x => x.evaluation_id);
                });

            migrationBuilder.CreateTable(
                name: "evaluation_history",
                columns: table => new
                {
                    evaluation_history_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    evaluation_id = table.Column<int>(type: "integer", nullable: false),
                    agent_name = table.Column<string>(type: "text", nullable: false),
                    questions = table.Column<List<EvaluationHistoryQuestionModel>>(type: "jsonb", nullable: false),
                    evaluation_llm_model = table.Column<string>(type: "text", nullable: false),
                    evaluation_prompt = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evaluation_history", x => x.evaluation_history_id);
                });

            migrationBuilder.UpdateData(
                table: "login",
                keyColumn: "login_id",
                keyValue: 1,
                column: "created",
                value: new DateTime(2025, 6, 21, 20, 36, 55, 373, DateTimeKind.Utc).AddTicks(1603));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evaluation");

            migrationBuilder.DropTable(
                name: "evaluation_history");

            migrationBuilder.UpdateData(
                table: "login",
                keyColumn: "login_id",
                keyValue: 1,
                column: "created",
                value: new DateTime(2025, 5, 28, 8, 41, 28, 173, DateTimeKind.Utc).AddTicks(5));
        }
    }
}
