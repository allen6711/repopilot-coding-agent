using System.Text.Json;
using RepoPilot.Application.Capabilities;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Proposals;
using RepoPilot.Domain.Entities;
using RepoPilot.Domain.Proposals;
using RepoPilot.Infrastructure.Proposals;

namespace RepoPilot.Agent.Capabilities;

/// <summary>
/// Creates a change proposal (permission class: no direct write).
/// <para>
/// This type has no file-writing dependency injected into it — not a writer it
/// declines to call, but no writer at all. That is deliberate: Principle I says
/// proposing must not mutate anything, and the strongest way to say so is to
/// make it impossible to express. The unit test asserting the working copy is
/// byte-identical afterwards is checking a property the constructor already
/// guarantees.
/// </para>
/// </summary>
public sealed class ProposePatchCapability(
    IProposalStore proposals,
    IRunStore runs,
    ProposalValidator validator,
    DiffRenderer renderer) : ICapability
{
    public string Name => "propose_patch";

    public async Task<CapabilityResult> InvokeAsync(
        CapabilityContext context, string argumentsJson, CancellationToken ct = default)
    {
        var args = Arguments.Parse(argumentsJson);
        var summary = Arguments.OptionalString(args, "summary") ?? string.Empty;
        var entries = ReadEntries(args);

        validator.Validate(context.Workspace, entries);

        var run = await runs.FindAsync(context.RunId, ct)
            ?? throw new InvalidOperationException($"Run {context.RunId} not found.");

        var diffHash = DiffHash.Compute(entries);
        var unifiedDiff = renderer.Render(context.Workspace, entries);

        var proposal = new ChangeProposal
        {
            RunId = context.RunId,
            RevisionAttempt = run.RevisionAttempt,
            EntriesJson = JsonSerializer.Serialize(entries),
            UnifiedDiff = unifiedDiff,
            AffectedPaths = [.. entries.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal)],
            DiffHash = diffHash,
            DecisionStatus = ProposalDecisionStatus.Pending,
        };

        await proposals.AddAsync(proposal, ct);

        // The hash goes back to the caller because it is what the reviewer will
        // echo when deciding (FR-020a). The diff does not: it is for the review
        // view, and returning it here would charge it against the run's context
        // budget for no benefit to the model.
        var result = JsonSerializer.Serialize(new
        {
            proposal_id = proposal.Id,
            affected_paths = proposal.AffectedPaths,
            diff_hash = proposal.DiffHash,
            summary,
        });

        return new CapabilityResult(
            result,
            result.Length,
            $$"""{"entries":{{entries.Count}},"diff_hash":"{{diffHash}}"}""");
    }

    private static List<ProposalEntry> ReadEntries(JsonElement args)
    {
        if (!args.TryGetProperty("entries", out var entriesElement) ||
            entriesElement.ValueKind != JsonValueKind.Array)
        {
            throw new ProposalRejectedException(
                ProposalRejectionReason.Empty, "The proposal has no 'entries' array.");
        }

        var entries = new List<ProposalEntry>();

        foreach (var element in entriesElement.EnumerateArray())
        {
            var path = Arguments.RequireString(element, "path");
            var operationText = Arguments.RequireString(element, "operation");
            var newContent = Arguments.RequireString(element, "new_content");

            var operation = operationText switch
            {
                "create" => ProposalOperation.Create,
                "modify" => ProposalOperation.Modify,
                _ => throw new ProposalRejectedException(
                    ProposalRejectionReason.Empty,
                    $"'{operationText}' is not a known operation; expected create or modify."),
            };

            entries.Add(new ProposalEntry(path, operation, newContent));
        }

        return entries;
    }
}
