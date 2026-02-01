using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daedalus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMissingTaskColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CodeAnalysisRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    RepositoryUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    RepositoryBranch = table.Column<string>(type: "text", nullable: true),
                    CommitSha = table.Column<string>(type: "text", nullable: true),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    StartLine = table.Column<int>(type: "integer", nullable: true),
                    EndLine = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Requirements = table.Column<string>(type: "jsonb", nullable: false),
                    ReferencedDocuments = table.Column<string>(type: "jsonb", nullable: true),
                    ExternalReferences = table.Column<string>(type: "jsonb", nullable: true),
                    LocalWorkTreePath = table.Column<string>(type: "text", nullable: true),
                    FeatureBranchName = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentIteration = table.Column<int>(type: "integer", nullable: false),
                    MaxIterations = table.Column<int>(type: "integer", nullable: false),
                    CompletionPromise = table.Column<string>(type: "text", nullable: true),
                    LastPromptSent = table.Column<string>(type: "text", nullable: true),
                    LastAiResponse = table.Column<string>(type: "text", nullable: true),
                    PullRequestUrl = table.Column<string>(type: "text", nullable: true),
                    CommitShaFinal = table.Column<string>(type: "text", nullable: true),
                    ValidationResult = table.Column<string>(type: "jsonb", nullable: true),
                    HasFailedValidation = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedTo = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodeAnalysisRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastHeartbeat = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TasksCompleted = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    ProjectName = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisIterations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeAnalysisRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    IterationNumber = table.Column<int>(type: "integer", nullable: false),
                    BranchName = table.Column<string>(type: "text", nullable: true),
                    CommitSha = table.Column<string>(type: "text", nullable: true),
                    PromptSent = table.Column<string>(type: "text", nullable: false),
                    AiResponse = table.Column<string>(type: "text", nullable: false),
                    CompilationSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    TestsPassed = table.Column<bool>(type: "boolean", nullable: true),
                    ValidationErrors = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisIterations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalysisIterations_CodeAnalysisRequests_CodeAnalysisRequest~",
                        column: x => x.CodeAnalysisRequestId,
                        principalTable: "CodeAnalysisRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Phase = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ParallelGroup = table.Column<int>(type: "integer", nullable: false),
                    EstimatedComplexity = table.Column<int>(type: "integer", nullable: false),
                    Prompt = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CompletionPromise = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    MaxIterations = table.Column<int>(type: "integer", nullable: false),
                    CurrentSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Result = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: true),
                    IterationCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Learnings = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    LearningsUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Dependencies = table.Column<List<string>>(type: "text[]", nullable: false),
                    FilesToModify = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IterationNumber = table.Column<int>(type: "integer", nullable: false),
                    Prompt = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    LlmResponse = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    CompletionPromiseFound = table.Column<bool>(type: "boolean", nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExecutionDuration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskExecutions_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisIteration_CreatedAt",
                table: "AnalysisIterations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisIteration_RequestId",
                table: "AnalysisIterations",
                column: "CodeAnalysisRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_CodeAnalysis_CreatedAt",
                table: "CodeAnalysisRequests",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CodeAnalysis_Priority",
                table: "CodeAnalysisRequests",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_CodeAnalysis_RepositoryUrl",
                table: "CodeAnalysisRequests",
                column: "RepositoryUrl");

            migrationBuilder.CreateIndex(
                name: "IX_CodeAnalysis_Status",
                table: "CodeAnalysisRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Session_IsActive",
                table: "ExecutionSessions",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Session_LastHeartbeat",
                table: "ExecutionSessions",
                column: "LastHeartbeat");

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecution_ExecutedAt",
                table: "TaskExecutions",
                column: "ExecutedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecution_SessionId",
                table: "TaskExecutions",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskExecution_TaskId",
                table: "TaskExecutions",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Task_CreatedAt",
                table: "Tasks",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Task_SessionId",
                table: "Tasks",
                column: "CurrentSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Task_Status",
                table: "Tasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ProjectId",
                table: "Tasks",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisIterations");

            migrationBuilder.DropTable(
                name: "ExecutionSessions");

            migrationBuilder.DropTable(
                name: "TaskExecutions");

            migrationBuilder.DropTable(
                name: "CodeAnalysisRequests");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
