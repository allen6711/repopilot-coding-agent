using System.Text;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using RepoPilot.Domain.Proposals;
using RepoPilot.Domain.Workspace;

namespace RepoPilot.Infrastructure.Proposals;

/// <summary>
/// Renders a proposal as a unified diff for review (FR-011).
/// <para>
/// Display only. The change-content hash is computed from the proposal's bytes,
/// never from this output — so a change to diff rendering cannot invalidate a
/// historical approval, and a reviewer's decision stays bound to content rather
/// than to formatting.
/// </para>
/// </summary>
public sealed class DiffRenderer
{
    private const int ContextLines = 3;

    /// <summary>
    /// Renders every entry against the current working copy.
    /// </summary>
    /// <param name="workspace">The run's working copy, read to obtain the "before" side.</param>
    /// <param name="entries">The proposed changes.</param>
    public string Render(WorkspaceRoot workspace, IReadOnlyList<ProposalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(entries);

        var output = new StringBuilder();

        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            var resolved = PathGuard.Resolve(workspace, entry.Path, AccessIntent.Read);
            var before = resolved.IsAllowed && File.Exists(resolved.FullPath)
                ? File.ReadAllText(resolved.FullPath!)
                : string.Empty;

            output.Append(RenderOne(entry, before));
        }

        return output.ToString();
    }

    private static string RenderOne(ProposalEntry entry, string before)
    {
        var output = new StringBuilder();

        var oldLabel = entry.Operation == ProposalOperation.Create
            ? "/dev/null"
            : $"a/{entry.Path}";

        output.Append("--- ").AppendLine(oldLabel);
        output.Append("+++ ").AppendLine($"b/{entry.Path}");

        var diff = InlineDiffBuilder.Diff(before, entry.NewContent);

        // Only changed regions plus a little context. A whole-file dump for a
        // one-line change buries the change a reviewer is deciding on, and
        // SC-004 depends on that decision being practical to make.
        var lines = diff.Lines;
        var include = new bool[lines.Count];

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Type is ChangeType.Inserted or ChangeType.Deleted or ChangeType.Modified)
            {
                var from = Math.Max(0, i - ContextLines);
                var to = Math.Min(lines.Count - 1, i + ContextLines);
                for (var j = from; j <= to; j++)
                {
                    include[j] = true;
                }
            }
        }

        var inHunk = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!include[i])
            {
                inHunk = false;
                continue;
            }

            if (!inHunk)
            {
                output.AppendLine($"@@ line {i + 1} @@");
                inHunk = true;
            }

            var marker = lines[i].Type switch
            {
                ChangeType.Inserted => '+',
                ChangeType.Deleted => '-',
                ChangeType.Modified => '!',
                _ => ' ',
            };

            output.Append(marker).AppendLine(lines[i].Text);
        }

        return output.ToString();
    }
}
