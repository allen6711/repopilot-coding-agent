using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using RepoPilot.Domain.Entities;

namespace RepoPilot.Infrastructure.Persistence;

/// <summary>
/// Raised when something attempts to modify or delete an append-only record.
/// </summary>
public sealed class AuditRecordImmutableException(string entityType, string operation)
    : InvalidOperationException(
        $"{entityType} records are append-only; {operation} is not permitted (FR-019b). " +
        "An approval, rejection, or recorded event is evidence about a decision that was made — " +
        "correcting it means recording a new fact, not editing the old one.")
{
    public string EntityType { get; } = entityType;

    public string Operation { get; } = operation;
}

/// <summary>
/// Enforces append-only semantics for the audit tables (FR-019b).
/// <para>
/// The database grants are the real boundary — a second application, a
/// migration, or a psql session bypasses anything written in C#. This
/// interceptor exists because in-process mistakes are the likely ones: an
/// <c>Update</c> written by a future feature would otherwise succeed silently
/// and quietly rewrite the record that proves a change was approved.
/// </para>
/// </summary>
public static class AuditImmutability
{
    /// <summary>
    /// Entity types that may only ever be inserted.
    /// </summary>
    private static readonly Type[] AppendOnlyTypes =
    [
        typeof(ApprovalDecision),
        typeof(RunEvent),
    ];

    /// <summary>
    /// Throws when the tracked graph contains a modification or deletion of an
    /// append-only record. Call from <c>SaveChanges</c> before persisting.
    /// </summary>
    public static void Enforce(ChangeTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        foreach (var entry in tracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var type = entry.Entity.GetType();
            foreach (var appendOnly in AppendOnlyTypes)
            {
                if (appendOnly.IsAssignableFrom(type))
                {
                    throw new AuditRecordImmutableException(
                        type.Name,
                        entry.State == EntityState.Modified ? "update" : "delete");
                }
            }
        }
    }
}
