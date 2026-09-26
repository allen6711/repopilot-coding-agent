using System.ComponentModel.DataAnnotations;

namespace RepoPilot.Application.Configuration;

/// <summary>
/// Where disposable run working copies live. Every run owns exactly one
/// directory under <see cref="Root"/>, created before its first file access and
/// destroyed when it reaches a terminal outcome (FR-024a, FR-026a).
/// </summary>
public sealed class WorkspaceOptions
{
    public const string SectionName = "RepoPilot:Workspace";

    /// <summary>Root directory for run working copies.</summary>
    [Required]
    public string Root { get; init; } = "./.workspace";

    /// <summary>
    /// Root directory containing registered repository fixtures. Registered as a
    /// read-only root in the path guard: no code path may open anything beneath
    /// it for writing (FR-016a).
    /// </summary>
    [Required]
    public string FixturesRoot { get; init; } = "./evals/fixtures";
}

/// <summary>
/// Bounds on concurrent run execution (FR-013a, FR-013b).
/// </summary>
public sealed class RunConcurrencyOptions
{
    public const string SectionName = "RepoPilot:Runs";

    /// <summary>
    /// Maximum number of runs executing at once. Runs beyond this queue rather
    /// than being refused. A run awaiting approval does not occupy a slot, so an
    /// unanswered approval cannot stall the queue.
    /// </summary>
    [Range(1, 64)]
    public int MaxConcurrent { get; init; } = 4;

    /// <summary>Maximum revision attempts after a failed test run (FR-012).</summary>
    [Range(0, 2)]
    public int MaxRevisionAttempts { get; init; } = 2;
}

/// <summary>
/// Limits on how much retrieved content reaches the agent (FR-006).
/// </summary>
public sealed class RetrievalOptions
{
    public const string SectionName = "RepoPilot:Retrieval";

    /// <summary>
    /// Total characters of retrieved content a single run may consume. Exceeding
    /// it refuses the retrieval rather than silently truncating.
    /// </summary>
    [Range(1_000, 10_000_000)]
    public int ContextBudgetChars { get; init; } = 60_000;

    /// <summary>Default number of fused results returned by a search.</summary>
    [Range(1, 50)]
    public int DefaultResultLimit { get; init; } = 8;

    /// <summary>Reciprocal Rank Fusion constant. 60 is the standard default.</summary>
    [Range(1, 1_000)]
    public int RrfK { get; init; } = 60;
}

/// <summary>
/// Indexing limits and chunk geometry (FR-002, FR-003b).
/// </summary>
public sealed class IndexingOptions
{
    public const string SectionName = "RepoPilot:Indexing";

    /// <summary>Files larger than this are excluded with reason <c>size</c>.</summary>
    [Range(1_024, 1_048_576)]
    public int MaxFileBytes { get; init; } = 262_144;

    /// <summary>Lines per chunk.</summary>
    [Range(10, 500)]
    public int ChunkLines { get; init; } = 60;

    /// <summary>Overlapping lines between consecutive chunks of the same file.</summary>
    [Range(0, 100)]
    public int ChunkOverlapLines { get; init; } = 15;

    /// <summary>Embedding vector dimensions. Pinned per repository at first index.</summary>
    [Range(64, 4_096)]
    public int EmbeddingDimensions { get; init; } = 384;
}

/// <summary>
/// The allowed set. A repository outside it is refused at registration (FR-001).
/// </summary>
public sealed class AllowedRepositoryOptions
{
    public const string SectionName = "RepoPilot:AllowedRepositories";

    /// <summary>Fixture slugs this deployment permits.</summary>
    public IReadOnlyList<string> Slugs { get; init; } = [];
}

/// <summary>
/// Caps on a change proposal. A proposal exceeding any of these is refused at
/// creation, which is what makes SC-004 hold — every proposal that reaches a
/// reviewer is one that can be shown in full (FR-011a).
/// </summary>
public sealed class ProposalLimitOptions
{
    public const string SectionName = "RepoPilot:Proposals";

    [Range(1, 100)]
    public int MaxEntries { get; init; } = 20;

    [Range(1_024, 1_048_576)]
    public int MaxBytesPerFile { get; init; } = 262_144;

    [Range(1_024, 8_388_608)]
    public int MaxTotalBytes { get; init; } = 524_288;
}
