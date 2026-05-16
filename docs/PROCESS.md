# KusPus development process

Gate-driven flow to keep implementation honest to PRD/TECH_SPEC and prevent AI drift.

## Why this exists

Plain LLM coding drifts: writes past spec scope, adds unrequested abstractions, papers over bugs with try/catch, generates plausible-but-wrong tests. The spec ([PRD.md](PRD.md), [TECH_SPEC.md](TECH_SPEC.md)) is the contract. The gates are the discipline that keeps the implementation on it.

## Three gates

### 1. Cluster gate — mid-phase, after a testable cluster of changes

A **cluster** is the smallest grouping of files that produces a testable unit of behavior (typically 1–5 files). Clusters are defined by testability: types-only changes don't get their own gate unless they ship with a behavior that exercises them.

After each cluster, the implementing agent runs and reports:

| Check | What it asks |
|---|---|
| **Compile** | `dotnet build` zero warnings (TreatWarningsAsErrors is enabled) |
| **Cluster tests** | Tests for the cluster's behavior pass |
| **Spec citation** | Every new public type/method names the TECH_SPEC § that requires it |
| **Anti-bloat scan** | Is any code unjustified by cluster scope? (extra abstractions, premature generalization, "future-proofing") |
| **Dead-branch scan** | Error handlers for things that can't happen? Validation for cases the spec says are impossible? |
| **Comment audit** | Every comment explains WHY (a constraint, an invariant, a workaround) — never WHAT |
| **Silent-failure scan** | No empty `catch {}`, no swallowed exceptions, no log-and-continue at boundaries |
| **Deviation note** | Each deliberate divergence from spec, with rationale |

A cluster gate is reportable in under 100 words. If it fails, fix before the next cluster.

### 2. Phase gate — end of phase, before marking the task complete

| Check | What it asks |
|---|---|
| **All cluster gates passed** | Roll-up of the phase's clusters |
| **Full test suite** | `dotnet test` green across the solution, not just the new tests |
| **Independent review** | A general-purpose subagent reads the phase diff + the relevant TECH_SPEC § and reports drift, scope creep, missing pieces, bugs. Adversarial framing. |
| **Spec coverage ledger** | Each TECH_SPEC § the phase claims to satisfy, mapped to file:line |
| **Explicitly deferred** | Anything scoped out of the phase + why |

The independent-review step costs ~one subagent invocation. Worth the latency for catching what the primary loop missed.

### 3. Milestone gate — Phase 6, 9, 10, 12, 13

For phases that ship runnable user-facing features, walk 1–2 entries from the PRD §11.3 manual test matrix (M-01..M-37) on the actual machine. The author confirms behavior; bugs found here go back to fix before the phase closes.

## Order of operations within a phase

1. Read the relevant TECH_SPEC § end-to-end.
2. Break the phase into clusters by testability. State them up front.
3. For each cluster: write the test first → implement → run cluster gate → report.
4. End-of-phase: run phase gate → report → only then mark the phase task complete.

## Anti-patterns this is designed to prevent

- **Adding code the spec didn't ask for** (caught by spec citation + anti-bloat scan).
- **Try/catch as a way to make tests pass** (caught by silent-failure scan).
- **Half-finished implementations marked complete** (caught by phase gate's spec coverage ledger).
- **AI confidently writing wrong code that the AI itself reviewed favorably** (caught by independent subagent review with no shared context).
- **Drift discovered six phases later** (caught by gating each phase before the next starts).
