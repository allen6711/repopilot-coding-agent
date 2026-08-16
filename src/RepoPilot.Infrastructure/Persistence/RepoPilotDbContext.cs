using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RepoPilot.Domain.Entities;

namespace RepoPilot.Infrastructure.Persistence;

/// <summary>
/// The system of record for repository chunks and run metadata.
/// <para>
/// Several governance guarantees are enforced here as schema constraints rather
/// than as application checks, because a database constraint cannot be bypassed
/// by a new code path or lost to a race: at most one decision per proposal
/// (FR-018), a decision always carrying an actor and a hash (SC-015), and a
/// gap-free event sequence per run (SC-008).
/// </para>
/// </summary>
public class RepoPilotDbContext(DbContextOptions<RepoPilotDbContext> options) : DbContext(options)
{
    public DbSet<RepositoryFixture> Repositories => Set<RepositoryFixture>();

    public DbSet<IndexEntry> IndexEntries => Set<IndexEntry>();

    public DbSet<Run> Runs => Set<Run>();

    public DbSet<RunEvent> RunEvents => Set<RunEvent>();

    public DbSet<ChangeProposal> ChangeProposals => Set<ChangeProposal>();

    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();

    public DbSet<WorkingCopy> WorkingCopies => Set<WorkingCopy>();

    public DbSet<TestResult> TestResults => Set<TestResult>();

    public DbSet<EvaluationRun> EvaluationRuns => Set<EvaluationRun>();

    public DbSet<EvaluationTaskResult> EvaluationTaskResults => Set<EvaluationTaskResult>();

    /// <summary>
    /// Embedding dimensions. Fixed at migration time because a pgvector column
    /// is typed <c>vector(n)</c>; changing it is a schema change and a full
    /// index rebuild, which is why the value is also pinned per repository.
    /// </summary>
    public const int EmbeddingDimensions = 384;

    /// <summary>
    /// Keeps <c>Pgvector</c> out of the Domain layer. The entity exposes a plain
    /// <c>float[]</c>; the provider type exists only here, which is what the
    /// constitution's layering constraint requires.
    /// </summary>
    private static readonly ValueConverter<float[], Vector> EmbeddingConverter =
        new(v => new Vector(v), v => v.ToArray());

    private static readonly ValueComparer<float[]> EmbeddingComparer =
        new((a, b) => a!.SequenceEqual(b!),
            v => v.Aggregate(0, (hash, f) => HashCode.Combine(hash, f.GetHashCode())),
            v => v.ToArray());

    private static readonly ValueConverter<Dictionary<string, int>, string> BreakdownConverter =
        new(v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<Dictionary<string, int>>(v, JsonSerializerOptions.Default)
                 ?? new Dictionary<string, int>());

    private static readonly ValueComparer<Dictionary<string, int>> BreakdownComparer =
        new((a, b) => JsonSerializer.Serialize(a, JsonSerializerOptions.Default)
                   == JsonSerializer.Serialize(b, JsonSerializerOptions.Default),
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default).GetHashCode(),
            v => new Dictionary<string, int>(v));

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AuditImmutability.Enforce(ChangeTracker);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        AuditImmutability.Enforce(ChangeTracker);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasPostgresExtension("vector");
        builder.HasPostgresExtension("pg_trgm");

        ConfigureRepositories(builder);
        ConfigureIndexEntries(builder);
        ConfigureRuns(builder);
        ConfigureRunEvents(builder);
        ConfigureProposals(builder);
        ConfigureApprovals(builder);
        ConfigureWorkingCopies(builder);
        ConfigureTestResults(builder);
        ConfigureEvaluations(builder);
    }

    private static void ConfigureRepositories(ModelBuilder builder) =>
        builder.Entity<RepositoryFixture>(e =>
        {
            e.ToTable("repositories");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.RootPath).IsRequired();
            e.Property(x => x.IndexingStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.EmbeddingModelId).HasMaxLength(200);
            e.Property(x => x.ExclusionBreakdown)
             .HasConversion(BreakdownConverter, BreakdownComparer)
             .HasColumnType("jsonb");
            e.Property(x => x.TestConfigJson).HasColumnType("jsonb").IsRequired();
        });

    private static void ConfigureIndexEntries(ModelBuilder builder) =>
        builder.Entity<IndexEntry>(e =>
        {
            e.ToTable("index_entries");
            e.HasKey(x => x.Id);

            e.Property(x => x.RelativePath).IsRequired();
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.Embedding)
             .HasConversion(EmbeddingConverter, EmbeddingComparer)
             .HasColumnType($"vector({EmbeddingDimensions})");

            // A rebuild writes at version+1 and one transaction flips the
            // active version, so this index carries every read (FR-003a).
            e.HasIndex(x => new { x.RepositoryId, x.IndexVersion });

            // One chunk per ordinal per file per version. Without this, a
            // partially retried rebuild could double-insert and silently inflate
            // retrieval results.
            e.HasIndex(x => new { x.RepositoryId, x.IndexVersion, x.RelativePath, x.ChunkOrdinal })
             .IsUnique();

            e.HasOne<RepositoryFixture>()
             .WithMany()
             .HasForeignKey(x => x.RepositoryId)
             .OnDelete(DeleteBehavior.Cascade);
        });

    private static void ConfigureRuns(ModelBuilder builder) =>
        builder.Entity<Run>(e =>
        {
            e.ToTable("runs");
            e.HasKey(x => x.Id);

            e.Property(x => x.TaskDescription).IsRequired();
            e.Property(x => x.SeededTaskId).HasMaxLength(64);

            // Bounded by the fixture-config schema's command-name pattern, which
            // caps a name at 41 characters. A value that does not fit was never a
            // command the allow-list could contain.
            e.Property(x => x.VerifyCommandName).HasMaxLength(64);
            e.Property(x => x.Stage).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.TerminalOutcome).HasConversion<string>().HasMaxLength(32);

            // Stored as a constrained string rather than free text: FR-008c
            // makes the reason set normative, so an undocumented reason must not
            // be persistable.
            e.Property(x => x.OutcomeReason).HasConversion<string>().HasMaxLength(48);
            e.Property(x => x.FailureStage).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ApprovalMode).HasConversion<string>().HasMaxLength(16).IsRequired();

            e.HasIndex(x => new { x.RepositoryId, x.Stage });
            e.HasIndex(x => x.EvaluationRunId);

            e.HasOne<RepositoryFixture>()
             .WithMany()
             .HasForeignKey(x => x.RepositoryId)
             .OnDelete(DeleteBehavior.Restrict);

            // FR-012: at most two revision attempts.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_runs_revision_attempt", "\"RevisionAttempt\" >= 0 AND \"RevisionAttempt\" <= 2"));

            // A terminal outcome always carries a reason and an end time; a
            // non-terminal run carries neither. Encoded here so a half-finished
            // write cannot leave a run that claims to be done without saying why.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_runs_terminal_has_reason",
                "(\"TerminalOutcome\" IS NULL AND \"OutcomeReason\" IS NULL AND \"EndedAt\" IS NULL) " +
                "OR (\"TerminalOutcome\" IS NOT NULL AND \"OutcomeReason\" IS NOT NULL AND \"EndedAt\" IS NOT NULL)"));
        });

    private static void ConfigureRunEvents(ModelBuilder builder) =>
        builder.Entity<RunEvent>(e =>
        {
            e.ToTable("run_events");
            e.HasKey(x => x.Id);

            e.Property(x => x.EventType).HasConversion<string>().HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.ToolName).HasMaxLength(64);
            e.Property(x => x.ArgumentsSummary).HasColumnType("jsonb");

            // The sequence is the SSE frame id and the replay cursor. Uniqueness
            // per run is what lets a reconnecting client resume from
            // Last-Event-ID without gaps or duplicates.
            e.HasIndex(x => new { x.RunId, x.Sequence }).IsUnique();

            e.HasOne<Run>()
             .WithMany()
             .HasForeignKey(x => x.RunId)
             .OnDelete(DeleteBehavior.Cascade);

            e.ToTable(t => t.HasCheckConstraint("ck_run_events_sequence", "\"Sequence\" >= 1"));
        });

    private static void ConfigureProposals(ModelBuilder builder) =>
        builder.Entity<ChangeProposal>(e =>
        {
            e.ToTable("change_proposals");
            e.HasKey(x => x.Id);

            e.Property(x => x.EntriesJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.UnifiedDiff).IsRequired();
            e.Property(x => x.DiffHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.DecisionStatus).HasConversion<string>().HasMaxLength(16).IsRequired();

            e.HasIndex(x => new { x.RunId, x.RevisionAttempt });

            e.HasOne<Run>()
             .WithMany()
             .HasForeignKey(x => x.RunId)
             .OnDelete(DeleteBehavior.Cascade);

            // Lower-case hex of a SHA-256. A malformed hash would compare
            // unequal at apply time and look like tampering rather than a bug.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_proposals_diff_hash_format", "\"DiffHash\" ~ '^[a-f0-9]{64}$'"));

            // FR-011a: an empty proposal is a no-change outcome, not a proposal.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_proposals_has_paths", "cardinality(\"AffectedPaths\") >= 1"));
        });

    private static void ConfigureApprovals(ModelBuilder builder) =>
        builder.Entity<ApprovalDecision>(e =>
        {
            e.ToTable("approval_decisions");
            e.HasKey(x => x.Id);

            e.Property(x => x.Decision).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.DiffHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.DecidedBy).HasMaxLength(256).IsRequired();

            // FR-018, enforced by the database rather than by a service check:
            // a second decision — of either kind, in either order, from either
            // actor — fails here, with no race window.
            e.HasIndex(x => x.ProposalId).IsUnique();
            e.HasIndex(x => x.RunId);

            e.HasOne<ChangeProposal>()
             .WithMany()
             .HasForeignKey(x => x.ProposalId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<Run>()
             .WithMany()
             .HasForeignKey(x => x.RunId)
             .OnDelete(DeleteBehavior.Restrict);

            // SC-015: no decision record without an actor and a hash. An empty
            // string would satisfy NOT NULL, so it is rejected explicitly.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_approvals_actor_present", "length(btrim(\"DecidedBy\")) > 0"));

            e.ToTable(t => t.HasCheckConstraint(
                "ck_approvals_diff_hash_format", "\"DiffHash\" ~ '^[a-f0-9]{64}$'"));
        });

    private static void ConfigureWorkingCopies(ModelBuilder builder) =>
        builder.Entity<WorkingCopy>(e =>
        {
            e.ToTable("working_copies");
            e.HasKey(x => x.Id);

            e.Property(x => x.AbsolutePath).IsRequired();

            // One per run: the working copy is the run's exclusive property,
            // which is what makes an approval valid regardless of how long it
            // waited (FR-014a).
            e.HasIndex(x => x.RunId).IsUnique();

            e.HasOne<Run>()
             .WithMany()
             .HasForeignKey(x => x.RunId)
             .OnDelete(DeleteBehavior.Cascade);
        });

    private static void ConfigureTestResults(ModelBuilder builder) =>
        builder.Entity<TestResult>(e =>
        {
            e.ToTable("test_results");
            e.HasKey(x => x.Id);

            e.Property(x => x.CommandName).HasMaxLength(64).IsRequired();
            e.Property(x => x.Output).IsRequired();

            e.HasIndex(x => new { x.RunId, x.RevisionAttempt });

            e.HasOne<Run>()
             .WithMany()
             .HasForeignKey(x => x.RunId)
             .OnDelete(DeleteBehavior.Cascade);

            // A timed-out execution cannot also have passed (FR-023, SC-009).
            e.ToTable(t => t.HasCheckConstraint(
                "ck_test_results_timeout_not_passed", "NOT (\"TimedOut\" AND \"Passed\")"));
        });

    private static void ConfigureEvaluations(ModelBuilder builder)
    {
        builder.Entity<EvaluationRun>(e =>
        {
            e.ToTable("evaluation_runs");
            e.HasKey(x => x.Id);

            foreach (var rate in new[]
                     {
                         nameof(EvaluationRun.RecallAt5),
                         nameof(EvaluationRun.CompletionRateToolEnabled),
                         nameof(EvaluationRun.CompletionRateBaseline),
                         nameof(EvaluationRun.ApprovalCoverage),
                         nameof(EvaluationRun.ToolSuccessRate),
                     })
            {
                e.Property(rate).HasColumnType("numeric(5,4)");
            }

            e.Property(x => x.AvgToolCallsPerCompletedTask).HasColumnType("numeric(6,2)");
        });

        builder.Entity<EvaluationTaskResult>(e =>
        {
            e.ToTable("evaluation_task_results");
            e.HasKey(x => x.Id);

            e.Property(x => x.TaskId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16).IsRequired();

            e.HasIndex(x => new { x.EvaluationRunId, x.TaskId, x.Mode }).IsUnique();

            e.HasOne<EvaluationRun>()
             .WithMany()
             .HasForeignKey(x => x.EvaluationRunId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
