# refactor-safely

**Fixes:** a "harmless cleanup" silently changes behavior and breaks production — the most dangerous kind of change because it *feels* safe and skips real review.

**Level:** Expert · **Type:** Claude Code skill (`SKILL.md`) or `CLAUDE.md` rule-pack

## Install

```bash
mkdir -p .claude/skills/refactor-safely
cp SKILL.md .claude/skills/refactor-safely/
```

## Before / After

**Task:** *"This 200-line `processOrder` function is a mess — clean it up."*

| ❌ Without | ✅ With refactor-safely |
|-----------|------------------------|
| Rewrites the whole function in one pass. Looks cleaner. Quietly drops an edge case where a discount and a refund stack — a real order now charges wrong. No test caught it; the diff was too big to review. | First adds characterization tests pinning current outputs (including the discount+refund case). Then extracts helpers one at a time, running tests after each. Same behavior, cleaner structure, and the discount+refund case is now locked by a test. |

## Why the trigger works

Scoped to *behavior-preserving restructuring*. It distinguishes a refactor (structure changes, behavior identical) from a rewrite (behavior changes) — and forces the safety net *before* the risky part, where it actually protects you.

## Pairs well with

[`test-first`](../../intermediate/test-first) (write the pins) and [`surgical-edits`](../../beginner/surgical-edits) (small, scoped steps). Together they turn a scary sweep into safe, reviewable moves.
