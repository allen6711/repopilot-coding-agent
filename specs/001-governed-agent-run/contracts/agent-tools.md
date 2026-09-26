# Contract: Agent Capabilities

**Feature**: 001-governed-agent-run | **Date**: 2026-08-10

The MVP exposes exactly seven capabilities — no more. Each declares a permission class that is
enforced at the call site inside `ToolInvoker`, never by prompt instruction (Principle III).

**Terminology**: "capability" is the formal term used in the specification and in these contracts.
"Tool" is an accepted alias, used in the constitution and README and in anything the model sees,
because that is the word the agent runtime uses. The two always refer to the same seven things.

## Permission classes

| Class | Meaning | Enforcement point |
|---|---|---|
| `Read` | May read indexed content or files inside the run's working copy. Never writes. | `PathGuard` + exclusion predicate + size/context budget, checked before I/O |
| `NoDirectWrite` | Returns a structured value describing an intended change. Touches no file. | Capability has no filesystem writer injected; unit test asserts the working copy is byte-identical after invocation |
| `WriteWithApproval` | May write, and only to the run's working copy, and only when a matching approval exists. | Loads `ApprovalDecision`, recomputes the proposal diff hash, compares, refuses on absence or mismatch |
| `SandboxExecution` | May execute a named command from the fixture allow-list inside an isolated container. | Command resolved by name against `repopilot.fixture.json`; container created with no network, read-only root, dropped capabilities, non-root user, and a hard timeout |

## Stage-scoped exposure

| Stage | Capabilities offered to the model |
|---|---|
| `retrieving`, `planning`, `proposing` | `list_files`, `search_code`, `read_file`, `search_docs`, `propose_patch` |
| `applying`, `testing` | none — the orchestrator invokes `apply_patch` and `run_tests` directly |

`apply_patch` and `run_tests` are the same implementations behind the same guards; only the point of
invocation differs. The reason is Principle IV: if the model could call `apply_patch` mid-turn, the
transition into `applying` would be inferred from model output instead of implemented in backend
code. Nothing is added to or removed from the seven-capability set.

## Common invariants

Every invocation, whoever calls it:

1. Records a `RunEvent` with tool name, run id, redacted argument summary, start/end time, and
   status — written in a `finally` block so a throwing capability still produces a `failed` record.
2. Opens an OpenTelemetry span as a child of the current stage span.
3. Charges its returned content against the run's retrieved-context budget; exceeding it fails the
   call with `context_budget_exceeded` rather than silently truncating.
4. Resolves every caller-supplied path through `PathGuard.Resolve(workspaceRoot, candidate)`, which
   canonicalizes, rejects any path resolving outside the root through a symbolic link — including
   links already present in the fixture — and refuses before any I/O. A refusal is recorded as a
   failed action so it can be counted (FR-024, FR-024b, FR-024c, SC-010).
5. Resolves paths against exactly one root: the run's own disposable working copy, which is created
   on entering `retrieving` and therefore exists before the first read of the run (FR-024a). The
   registered fixture is never opened for writing on any path (FR-016a).
6. Treats repository content as data, never as instruction. No text read from the repository can
   relax a permission class, a path check, a size limit, or the approval requirement (FR-026d).

---

## 1. `list_files` — `Read`

List repository files within the allowed root.

```json
{
  "name": "list_files",
  "description": "List files in the repository working copy under a directory, optionally filtered by glob. Returns repository-relative paths only. Excluded content (binaries, build output, dependency directories, oversized files, and secret-bearing files) is never listed.",
  "input_schema": {
    "type": "object",
    "properties": {
      "directory": { "type": "string", "description": "Repository-relative directory. Defaults to the repository root." },
      "glob": { "type": "string", "description": "Optional glob filter, e.g. **/*.cs" },
      "max_results": { "type": "integer", "minimum": 1, "maximum": 500, "default": 200 }
    },
    "required": [],
    "additionalProperties": false
  }
}
```

**Returns**: `{ "paths": string[], "truncated": boolean }`
**Errors**: `path_outside_workspace`, `directory_not_found`

## 2. `search_code` — `Read`

Hybrid lexical + vector search over indexed source content.

```json
{
  "name": "search_code",
  "description": "Search indexed repository source for code relevant to a query. Combines exact identifier matching with meaning-based search and returns fused results. Use an identifier verbatim when you know it; use a natural-language description when you do not.",
  "input_schema": {
    "type": "object",
    "properties": {
      "query": { "type": "string", "minLength": 1 },
      "limit": { "type": "integer", "minimum": 1, "maximum": 20, "default": 8 }
    },
    "required": ["query"],
    "additionalProperties": false
  }
}
```

**Returns**: array of `{ relative_path, chunk_id, content, start_line, end_line, score, language }`
(FR-004).
**Errors**: `repository_not_indexed`, `context_budget_exceeded`

## 3. `read_file` — `Read`

Read bounded file content from the working copy.

```json
{
  "name": "read_file",
  "description": "Read the contents of a file in the repository working copy, optionally a line range. Returns content with line numbers so you can reference exact locations.",
  "input_schema": {
    "type": "object",
    "properties": {
      "path": { "type": "string", "description": "Repository-relative path" },
      "start_line": { "type": "integer", "minimum": 1 },
      "end_line": { "type": "integer", "minimum": 1 }
    },
    "required": ["path"],
    "additionalProperties": false
  }
}
```

**Returns**: `{ path, content, start_line, end_line, truncated }`
**Errors**: `path_outside_workspace`, `file_not_found`, `file_excluded`, `file_too_large`,
`context_budget_exceeded`

## 4. `search_docs` — `Read`

Search indexed documentation (README, `docs/`, `*.md`).

```json
{
  "name": "search_docs",
  "description": "Search the repository's README and documentation for guidance, conventions, and setup details. Use this before assuming a project convention.",
  "input_schema": {
    "type": "object",
    "properties": {
      "query": { "type": "string", "minLength": 1 },
      "limit": { "type": "integer", "minimum": 1, "maximum": 20, "default": 5 }
    },
    "required": ["query"],
    "additionalProperties": false
  }
}
```

**Returns**: same shape as `search_code`, restricted to documentation entries.

## 5. `propose_patch` — `NoDirectWrite`

Return a structured change proposal. Writes nothing.

```json
{
  "name": "propose_patch",
  "description": "Propose a change as the complete new content of each affected file. This does not modify anything — it creates a proposal for human review. Include every file you intend to change and nothing else. If no change is needed, do not call this tool; say so instead.",
  "input_schema": {
    "type": "object",
    "properties": {
      "summary": { "type": "string", "description": "One or two sentences on what the change does" },
      "entries": {
        "type": "array",
        "minItems": 1,
        "maxItems": 20,
        "items": {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "operation": { "type": "string", "enum": ["create", "modify"] },
            "new_content": { "type": "string" }
          },
          "required": ["path", "operation", "new_content"],
          "additionalProperties": false
        }
      }
    },
    "required": ["summary", "entries"],
    "additionalProperties": false
  }
}
```

**Returns**: `{ proposal_id, affected_paths, diff_hash, unified_diff }`
**Errors**: `path_outside_workspace`, `too_many_entries`, `content_too_large`, `binary_content`,
`empty_proposal`

**Enforcement**: the capability has no filesystem writer injected. A unit test asserts that the
working copy hash is unchanged after invocation (Principle I, mandatory test list).

## 6. `apply_patch` — `WriteWithApproval`

Apply an approved proposal to the disposable working copy. Orchestrator-invoked.

```json
{
  "name": "apply_patch",
  "description": "Apply an approved change proposal to the run's disposable working copy. Refuses unless a recorded approval exists for this exact proposal content.",
  "input_schema": {
    "type": "object",
    "properties": {
      "proposal_id": { "type": "string", "format": "uuid" }
    },
    "required": ["proposal_id"],
    "additionalProperties": false
  }
}
```

**Preconditions, checked in order, all fatal**:

1. An `ApprovalDecision` exists for `proposal_id` with `decision = approve` — else
   `approval_required` (FR-014).
2. The recomputed canonical diff hash of the stored proposal equals the approval's `diff_hash` —
   else `diff_hash_mismatch` (FR-020).
3. Every entry path resolves inside `{workspaceRoot}/runs/{runId}` — else
   `path_outside_workspace` (FR-016, FR-024).
4. The run's stage is `applying` — else `illegal_stage`.

**Returns**: `{ applied_paths, applied_at }`
**Atomicity**: all entries are written to temporary files inside the working copy and moved into
place together; a failure part-way leaves no partially applied change (edge case: conflict).

## 7. `run_tests` — `SandboxExecution`

Execute an allow-listed test command in an isolated container. Orchestrator-invoked.

```json
{
  "name": "run_tests",
  "description": "Run a named test command from the repository's committed test configuration inside an isolated container with no network access and a hard timeout.",
  "input_schema": {
    "type": "object",
    "properties": {
      "command_name": { "type": "string", "description": "Name from the fixture's committed command allow-list" }
    },
    "required": ["command_name"],
    "additionalProperties": false
  }
}
```

**Enforcement**: `command_name` is resolved against `repopilot.fixture.json`; anything not present is
refused with `command_not_allowed` before a container is created (FR-022). There is no parameter
that accepts a command string — the shape of the schema is itself part of the control.

**Container configuration** (Principle II):

| Setting | Value |
|---|---|
| Image | The fixture's declared pre-baked image, with dependencies restored at build time |
| Network | `none` |
| Root filesystem | read-only, with a small `tmpfs` at `/tmp` |
| Mounts | the run's working copy at `/workspace`, read-write; nothing else |
| User | non-root (`1000:1000`) |
| Capabilities | all dropped; `no-new-privileges` |
| Limits | memory, NanoCPU, and PID caps from configuration |
| Environment | none inherited from the host; no credentials of any kind |
| Timeout | from the fixture config; enforced by container kill, reported as `timed_out` (FR-023) |

**Returns**: `{ passed, exit_code, output, duration_ms, timed_out }` — `output` is redacted for
secrets before it is stored, displayed, or supplied to the agent for a revision attempt (FR-025b).
**Errors**: `command_not_allowed`, `image_unavailable`, `sandbox_unavailable`,
`sandbox_not_terminable` — the latter two map to the run outcome reasons
`isolated_env_unavailable` and `isolated_env_not_terminable` (FR-008c). The capability error names
stay runtime-flavoured; the recorded run reason does not.

---

## What is deliberately absent

No shell capability, no arbitrary command execution, no network fetch, no package installation, no
git operation, no capability that writes outside the working copy, and no capability that can push,
branch, or merge. Adding any of these requires a constitution amendment citing measured evaluation
evidence, not a pull-request argument (Principle III).
