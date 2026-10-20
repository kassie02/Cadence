---
name: refactor-safely
description: Use when restructuring existing code without intending to change its behavior — extracting functions, renaming across files, splitting modules, or swapping an implementation. Makes the agent pin current behavior with tests first, change in small verified steps, and prove behavior is unchanged, so a "cleanup" never silently breaks something.
---

# Refactor Safely

A refactor changes *structure*, not *behavior*. The whole risk is changing behavior by accident. Defend against it.

## The procedure

1. **Pin the behavior first.** Before touching anything, make sure the code you're about to move is covered by tests. If it isn't, write **characterization tests** that capture what it currently does (even quirks) and get them green. You cannot refactor safely what you cannot observe.

2. **Refactor in small steps.** Make one structural change at a time — extract a function, rename a symbol, move a file. Keep each step mechanical and reversible.

3. **Run the tests after every step.** Green after each step means the behavior held. If a step goes red, you know exactly which change broke it — revert just that step.

4. **Keep refactor commits separate from behavior changes.** Never mix "I reorganized this" with "I also fixed a bug / changed logic" in the same diff. A reviewer should be able to trust a refactor diff changes nothing functional. If you spot a real bug mid-refactor, note it and handle it in its own change.

## Signs you've left "refactor" and entered "rewrite"

- The tests need to change to keep passing → you're altering behavior, not refactoring. Stop and make that an explicit, separate decision.
- The diff is large and you can't point to the equivalent old code for each new line → break it into smaller steps.

## Why this matters

"Just cleaning this up" is one of the most common ways working software breaks, because the change feels safe and goes unreviewed. Characterization tests plus small, individually-verified steps turn a risky sweep into a sequence of provably-behavior-preserving moves.
