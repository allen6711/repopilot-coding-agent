using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace RepoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "evaluation_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TaskCount = table.Column<int>(type: "integer", nullable: false),
                    RecallAt5 = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                    CompletionRateToolEnabled = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                    CompletionRateBaseline = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                    ApprovalCoverage = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                    ToolSuccessRate = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                    AvgToolCallsPerCompletedTask = table.Column<decimal>(type: "numeric(6,2)", nullable: true),
                    P50LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    P95LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    Flagged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evaluation_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RootPath = table.Column<string>(type: "text", nullable: false),
                    IndexingStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActiveIndexVersion = table.Column<int>(type: "integer", nullable: true),
                    EmbeddingModelId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EmbeddingDimensions = table.Column<int>(type: "integer", nullable: true),
                    LastIndexedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IncludedFileCount = table.Column<int>(type: "integer", nullable: false),
                    ExcludedFileCount = table.Column<int>(type: "integer", nullable: false),
                    ExclusionBreakdown = table.Column<string>(type: "jsonb", nullable: false),
                    TestConfigJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "evaluation_task_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EvaluationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelevantFileInTop5 = table.Column<bool>(type: "boolean", nullable: false),
                    SuccessConditionMet = table.Column<bool>(type: "boolean", nullable: false),
                    ToolCallCount = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evaluation_task_results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_evaluation_task_results_evaluation_runs_EvaluationRunId",
                        column: x => x.EvaluationRunId,
                        principalTable: "evaluation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "index_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndexVersion = table.Column<int>(type: "integer", nullable: false),
                    RelativePath = table.Column<string>(type: "text", nullable: false),
                    ChunkOrdinal = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    StartLine = table.Column<int>(type: "integer", nullable: false),
                    EndLine = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: true),
                    Symbols = table.Column<string[]>(type: "text[]", nullable: true),
                    Embedding = table.Column<Vector>(type: "vector(384)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_index_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_index_entries_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskDescription = table.Column<string>(type: "text", nullable: false),
                    SeededTaskId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TerminalOutcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    OutcomeReason = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    FailureStage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RevisionAttempt = table.Column<int>(type: "integer", nullable: false),
                    ToolsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovalMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EvaluationRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Plan = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runs", x => x.Id);
                    table.CheckConstraint("ck_runs_revision_attempt", "\"RevisionAttempt\" >= 0 AND \"RevisionAttempt\" <= 2");
                    table.CheckConstraint("ck_runs_terminal_has_reason", "(\"TerminalOutcome\" IS NULL AND \"OutcomeReason\" IS NULL AND \"EndedAt\" IS NULL) OR (\"TerminalOutcome\" IS NOT NULL AND \"OutcomeReason\" IS NOT NULL AND \"EndedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_runs_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "change_proposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionAttempt = table.Column<int>(type: "integer", nullable: false),
                    EntriesJson = table.Column<string>(type: "jsonb", nullable: false),
                    UnifiedDiff = table.Column<string>(type: "text", nullable: false),
                    AffectedPaths = table.Column<string[]>(type: "text[]", nullable: false),
                    DiffHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    DecisionStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_proposals", x => x.Id);
                    table.CheckConstraint("ck_proposals_diff_hash_format", "\"DiffHash\" ~ '^[a-f0-9]{64}$'");
                    table.CheckConstraint("ck_proposals_has_paths", "cardinality(\"AffectedPaths\") >= 1");
                    table.ForeignKey(
                        name: "FK_change_proposals_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "run_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ToolName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ArgumentsSummary = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_events", x => x.Id);
                    table.CheckConstraint("ck_run_events_sequence", "\"Sequence\" >= 1");
                    table.ForeignKey(
                        name: "FK_run_events_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "test_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionAttempt = table.Column<int>(type: "integer", nullable: false),
                    CommandName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    Output = table.Column<string>(type: "text", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    TimedOut = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_results", x => x.Id);
                    table.CheckConstraint("ck_test_results_timeout_not_passed", "NOT (\"TimedOut\" AND \"Passed\")");
                    table.ForeignKey(
                        name: "FK_test_results_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "working_copies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AbsolutePath = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DestroyedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_working_copies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_working_copies_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "approval_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DiffHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_decisions", x => x.Id);
                    table.CheckConstraint("ck_approvals_actor_present", "length(btrim(\"DecidedBy\")) > 0");
                    table.CheckConstraint("ck_approvals_diff_hash_format", "\"DiffHash\" ~ '^[a-f0-9]{64}$'");
                    table.ForeignKey(
                        name: "FK_approval_decisions_change_proposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "change_proposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_approval_decisions_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approval_decisions_ProposalId",
                table: "approval_decisions",
                column: "ProposalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_approval_decisions_RunId",
                table: "approval_decisions",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_change_proposals_RunId_RevisionAttempt",
                table: "change_proposals",
                columns: new[] { "RunId", "RevisionAttempt" });

            migrationBuilder.CreateIndex(
                name: "IX_evaluation_task_results_EvaluationRunId_TaskId_Mode",
                table: "evaluation_task_results",
                columns: new[] { "EvaluationRunId", "TaskId", "Mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_index_entries_RepositoryId_IndexVersion",
                table: "index_entries",
                columns: new[] { "RepositoryId", "IndexVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_index_entries_RepositoryId_IndexVersion_RelativePath_ChunkO~",
                table: "index_entries",
                columns: new[] { "RepositoryId", "IndexVersion", "RelativePath", "ChunkOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_repositories_Slug",
                table: "repositories",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_run_events_RunId_Sequence",
                table: "run_events",
                columns: new[] { "RunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runs_EvaluationRunId",
                table: "runs",
                column: "EvaluationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_runs_RepositoryId_Stage",
                table: "runs",
                columns: new[] { "RepositoryId", "Stage" });

            migrationBuilder.CreateIndex(
                name: "IX_test_results_RunId_RevisionAttempt",
                table: "test_results",
                columns: new[] { "RunId", "RevisionAttempt" });

            migrationBuilder.CreateIndex(
                name: "IX_working_copies_RunId",
                table: "working_copies",
                column: "RunId",
                unique: true);

            // Retrieval indexes, written as explicit DDL rather than through the
            // model builder. Two reasons: a generated tsvector column would need
            // an Npgsql type on the Domain entity, which the layering constraint
            // forbids; and the pgvector operator class and HNSW parameters are
            // provider specifics the model cannot express precisely.
            //
            // Hybrid retrieval (FR-005) runs both arms of every search and fuses
            // them with RRF, so all three indexes are on the hot path, not just
            // the vector one.

            // Lexical arm, part 1: full-text search over chunk content. Generated
            // and stored, so it stays consistent with Content without any code
            // remembering to update it.
            migrationBuilder.Sql("""
                ALTER TABLE index_entries
                ADD COLUMN "ContentSearchVector" tsvector
                GENERATED ALWAYS AS (to_tsvector('simple', "Content")) STORED;
                """);

            migrationBuilder.Sql("""
                CREATE INDEX "IX_index_entries_ContentSearchVector"
                ON index_entries USING GIN ("ContentSearchVector");
                """);

            // Lexical arm, part 2: trigram similarity, which is what makes an
            // exact identifier lookup work when the identifier is not a word
            // boundary the text-search parser recognises (FR-005).
            migrationBuilder.Sql("""
                CREATE INDEX "IX_index_entries_Content_Trigram"
                ON index_entries USING GIN ("Content" gin_trgm_ops);
                """);

            // Vector arm: cosine distance. HNSW rather than IVFFlat because it
            // needs no training pass over existing rows, which matters when an
            // atomic rebuild writes a whole new index version before the swap.
            migrationBuilder.Sql("""
                CREATE INDEX "IX_index_entries_Embedding_Hnsw"
                ON index_entries USING hnsw ("Embedding" vector_cosine_ops)
                WITH (m = 16, ef_construction = 64);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropped explicitly before the tables so a partial rollback cannot
            // leave an index referring to a column that no longer exists.
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_index_entries_Embedding_Hnsw\";");
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_index_entries_Content_Trigram\";");
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_index_entries_ContentSearchVector\";");
            migrationBuilder.Sql(
                "ALTER TABLE index_entries DROP COLUMN IF EXISTS \"ContentSearchVector\";");

            migrationBuilder.DropTable(
                name: "approval_decisions");

            migrationBuilder.DropTable(
                name: "evaluation_task_results");

            migrationBuilder.DropTable(
                name: "index_entries");

            migrationBuilder.DropTable(
                name: "run_events");

            migrationBuilder.DropTable(
                name: "test_results");

            migrationBuilder.DropTable(
                name: "working_copies");

            migrationBuilder.DropTable(
                name: "change_proposals");

            migrationBuilder.DropTable(
                name: "evaluation_runs");

            migrationBuilder.DropTable(
                name: "runs");

            migrationBuilder.DropTable(
                name: "repositories");
        }
    }
}
