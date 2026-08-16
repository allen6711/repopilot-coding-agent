# Capability contract

What the agent can do, what enforces each limit, and what it would take to add an eighth capability.

The normative per-capability schemas live in
[`specs/001-governed-agent-run/contracts/agent-tools.md`](../specs/001-governed-agent-run/contracts/agent-tools.md).
This page is the reader-facing companion: it explains the shape of the system those schemas describe,
so that someone about to change a capability knows which parts are load-bearing.

## The set is closed

There are exactly seven capabilities. The list is a hard-coded, frozen dictionary in
`RepoPilot.Domain.Capabilities.CapabilityRegistry` — not something assembled by scanning for
attributes, and not something a host can extend at startup.

| Capability | Permission class | Who may invoke it |
|---|---|---|
| `list_files` | `Read` | Model |
| `search_code` | `Read` | Model |
| `read_file` | `Read` | Model |
| `search_docs` | `Read` | Model |
| `propose_patch` | `NoDirectWrite` | Model |
| `apply_patch` | `WriteWithApproval` | Orchestrator |
| `run_tests` | `SandboxExecution` | Orchestrator |

Adding an entry is not a code change. Principle III requires measured evaluation evidence before the
tool surface grows, so the registry is deliberately awkward to extend: a new capability needs a
constitution amendment citing the evaluation that motivated it.

## Every invocation goes through one place

`ToolInvoker` wraps every call. It applies the permission class, checks the calling surface, charges
the context budget, opens the trace span, and writes the audit record.

The audit record is written in a `finally`. That is the part worth understanding: a capability that
throws still produces a recorded action with `failed` status, so a refused path, a disallowed
command, and an exhausted budget are all countable rather than invisible. SC-010's count of refused
out-of-workspace attempts is read back out of those records — see `RefusalReport`.

A capability implements behaviour only. It cannot forget a control, because it never applies one.

## The two surfaces

`apply_patch` and `run_tests` are invoked by the orchestrator, never offered to the model.

This is not defence in depth for its own sake. If the model could call apply mid-turn, the
transition into the applying stage would be inferred from model output rather than implemented in
backend code, which Principle IV forbids. Passing the wrong surface to the invoker is refused with a
`CapabilitySurfaceViolationException` — the check is on the call, not on the tool list.

The evaluation baseline narrows the model surface further, to `propose_patch` alone, and supplies
retrieved context up front instead. See `OfferedCapabilities`.

## What each control actually is

| Claim | Enforced by | Not enforced by |
|---|---|---|
| File access stays inside the run's working copy | `PathGuard.Resolve`, before any handle is opened | The system prompt |
| The registered fixture is never written to | `WorkspaceRoot.ReadOnly` — a property of the root, checked independently of the path | Convention in callers |
| Nothing is applied without an approval | `ApplyPatchCapability` re-reads the decision record and compares the hash | The UI disabling a button |
| Only allow-listed commands run | `run_tests` takes a command *name*; there is no parameter that accepts a command line | Validating a command string |
| Secrets do not reach model context | `SecretRedactor` at indexing, at read, and on sandbox output | Asking the model not to look |
| Retrieved content is bounded | `RunContextBudget`, per run, refusing rather than truncating | A per-call size cap |

The right-hand column matters as much as the middle one. Repository content is data, not instruction
(FR-026d): a file that tells the agent it is pre-approved changes nothing, because no control
consults the model's belief about whether it is approved. `evals/fixtures/adversarial-content/`
exists to keep that checkable.

## Refusals are values, not exceptions

`PathGuard.Resolve` returns a `PathResolution` rather than throwing, so the caller can record the
refusal as a countable failed action. `ResolveOrThrow` exists for the few places where a refusal is
genuinely exceptional — prefer the first.

A refusal message carries `PathAccessRefusedException.RefusalMarker` immediately before the reason.
That format is a seam, not an accident: the refusal reaches the audit trail as text, and SC-010's
count is produced by reading those strings back.

## Adding or changing a capability

1. Does the evaluation show the current set failing at something? If not, stop — that is what
   Principle III asks.
2. Amend the constitution, citing the measurement.
3. Add the descriptor to `CapabilityRegistry` with its permission class and surface.
4. Implement `ICapability`. Do not add controls inside it; the invoker owns those.
5. Add the JSON schema to `RepoPilotAgent.SchemaFor` and a description to `Descriptions`.
6. Register it in `RepoPilotServices.AddRepoPilotCore` — the shared composition root, so the API and
   the evaluation harness get it at the same time.
7. Update `CapabilityRegistry.ExpectedCount` and the contract document.

Step 6 is the one people miss. Registering in only one host means the evaluation measures a
different system from the one that ships, and nothing fails to tell you.

## What is deliberately absent

No shell. No network from the sandbox. No capability that writes outside a working copy. No way for
the model to name a command line, a file outside the workspace, or a stage transition.

Each absence is a control expressed as a missing affordance, which is stronger than a check: a model
cannot ask for something the interface has no way to express.
