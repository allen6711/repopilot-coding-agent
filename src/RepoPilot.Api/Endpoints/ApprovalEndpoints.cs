using RepoPilot.Api.Contracts;
using RepoPilot.Api.Middleware;
using RepoPilot.Application.Ports;
using RepoPilot.Application.Runs;
using RepoPilot.Application.UseCases;
using RepoPilot.Domain.Entities;

namespace RepoPilot.Api.Endpoints;

/// <summary>
/// The approval surface — the only endpoint that can unlock a file write
/// (Principle I, FR-015a, FR-018, FR-020a).
/// <para>
/// Every refusal here happens before the decision row is written. A decision
/// that was recorded and then found invalid would already have unlocked the
/// write it was meant to gate, so ordering is not an implementation detail.
/// </para>
/// </summary>
public static class ApprovalEndpoints
{
    public static void MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/runs/{runId:guid}/approval", DecideAsync).WithTags("runs");
    }

    private static async Task<IResult> DecideAsync(
        Guid runId,
        DecideProposalRequest request,
        HttpRequest httpRequest,
        DecideProposalUseCase decide,
        RunOrchestrator orchestrator,
        IProposalStore proposals,
        RunQueue queue,
        CancellationToken ct)
    {
        if (!ActorIdentityBinding.TryRead(httpRequest, out var actor, out var problem))
        {
            return problem;
        }

        if (!TryParseDecision(request.Decision, out var decision))
        {
            return Results.Problem(
                title: "Unknown decision",
                detail: $"'{request.Decision}' is not a decision. Use 'approve' or 'reject'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var proposal = await proposals.FindAsync(request.ProposalId, ct);

        if (proposal is null)
        {
            return Results.Problem(
                title: "Proposal not found",
                detail: $"No proposal with id {request.ProposalId}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (proposal.RunId != runId)
        {
            // The path and the body must agree. Letting them differ would allow a
            // decision made while reviewing one run to be recorded against
            // another.
            return Results.Problem(
                title: "Proposal belongs to a different run",
                detail: $"Proposal {request.ProposalId} belongs to run {proposal.RunId}.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        try
        {
            var recorded = await decide.DecideAsync(
                request.ProposalId, decision, request.DiffHash, actor.Value, ApprovalMode.Interactive, ct);

            if (decision == ApprovalDecisionKind.Approve)
            {
                // Queued, not executed inline. The apply-and-test segment
                // re-acquires a slot like any other work, so a batch of approvals
                // arriving together does not put every one of them into execution
                // at once (FR-013b).
                await queue.EnqueueAsync(runId, ct);
            }
            else
            {
                // A rejection is the end of the run. Ending it here rather than
                // queueing keeps the response honest: by the time the reviewer
                // gets a 201, the run is already rejected.
                await orchestrator.RecordRejectionAsync(runId, ct);
            }

            return Results.Created($"/api/runs/{runId}/approval", ApprovalDecisionDto.From(recorded));
        }
        catch (ProposalAlreadyDecidedException ex)
        {
            // FR-018. 409 regardless of what the second decision said — a
            // rejection arriving after an approval is just as much a duplicate.
            return Results.Problem(
                title: "Proposal already decided",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (DecisionRefusedException ex)
        {
            return ex.Reason switch
            {
                DecisionRefusalReason.ProposalNotFound => Results.Problem(
                    title: "Proposal not found",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status404NotFound),

                // FR-020a. The reviewer decided on content other than what is
                // stored, so the approval would authorise something they did not
                // see.
                DecisionRefusalReason.HashMismatch => Results.Problem(
                    title: "Diff hash does not match",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity),

                _ => Results.Problem(
                    title: "Decision refused",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        }
    }

    private static bool TryParseDecision(string? value, out ApprovalDecisionKind decision)
    {
        switch (value)
        {
            case "approve":
                decision = ApprovalDecisionKind.Approve;
                return true;

            case "reject":
                decision = ApprovalDecisionKind.Reject;
                return true;

            default:
                decision = default;
                return false;
        }
    }
}
