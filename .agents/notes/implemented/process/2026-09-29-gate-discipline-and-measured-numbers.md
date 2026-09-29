# Agent Note: Gate evidence discipline — a value without a run and an author is not evidence

Status: implemented

## Problem

This repository has a class of failure that leaves nothing behind in the code. A gate run reports green while the product is broken. A number is quoted from a document that recorded a different run. A warning count is carried forward from a record that was itself wrong. No commit introduces any of these, no test catches them, and fixing one changes nothing under `src/` — they are process failures, and they are the expensive ones, because each converts an unverified claim into one that reads as verified.

The damage compounds in a specific way. A gate value means "this value, from this run, on this tree". The moment the run and the author are dropped, the value stops being evidence and becomes a rumour — and quoting is the failure mode, because a value nobody re-derives is a value nobody notices going stale. A stale value does not look stale. It reads exactly like a correct one, and the documents most likely to be quoted are summaries and code blocks, which are the easiest to lift verbatim.

Those lessons lived in two working documents at the repository root, and both are now frozen under `archived/`. Archived notes are never edited and never current authority, so a rule that exists only there has stopped being a rule. This note is the active home for the discipline; it records rules and therefore contains no measured values of its own.

## Decision

Six rules. Each is stated with the specific accident it exists to prevent — a rule that cannot name one is a preference, not gate discipline.

## 1. The formal gate is a fixed command set, and an incremental build is never evidence

The build gate is `dotnet build P2PChat.slnx -t:Rebuild --no-incremental`, and the accompanying test run does not take `--no-build`.

These two switches are **both** written, and they are not alternatives. Whichever one a future reader decides is redundant is the one carrying the requirement.

The accident they prevent: timestamp-based incrementality can decide that a file containing a compile error needs no recompilation, so the error never reaches the compiler, and `--no-build` then executes the binaries produced by an earlier run, which pass. **The two hide each other**, and their combination is indistinguishable from a real pass — same green, same exit code, no diagnostic. A compile error in a test project survived in this repository until the build was forced to rebuild; the incremental run had reported clean the whole time.

The rest of the gate is `dotnet test P2PChat.slnx`, `dotnet publish src/P2PChat.App/P2PChat.App.csproj -c Release`, and `scripts/e2e-verify.ps1`. *Which* of these a given change needs is a different question and belongs to the pre-push skill; this note governs what a recorded value means once one exists.

## 2. A value reported by someone else finds problems; it never records results

A gate value is a claim about a run. Copying a teammate's reported number into a table attributes it to a run the recorder did not perform, and a later reader cannot tell the two apart, because the sentence is identical either way.

Teammate runs are the fastest way to find a real problem and the easiest way to manufacture a false guarantee. The rule is not distrust: it is that **the recorder must be the runner**. A gate table's attribution column names whoever ran the command, and a value the table's own author did not measure is evidence about somebody else's tree.

## 3. A cell that cannot be attributed to a run stays empty

Every cell carries a measured value and the name of whoever measured it. A cell that cannot be filled that way stays empty and says so.

The accident: a table awaiting a run is under pressure to look finished, and the most reachable value is the plausible one from an earlier recorded run. **A value with no run behind it is worse than a blank.** A blank says "not measured", which prompts a re-run; a filled cell says "measured" and nobody looks again. Emptiness is a legitimate state of a gate table, and it has to be visible in the table rather than inferred from an absence.

## 4. One quantity, one home

The authoritative table decides. Every other mention of the same quantity — a summary line, a fenced block, a second table — is corrected to match it in the same change.

The accident: the same value is restated in several places and only one of them gets edited. Correcting the authoritative table alone leaves a copy that reads exactly like a correct one, and the places most likely to be quoted verbatim are the summaries and code blocks. **Declaring the authoritative table the winner by convention is not sufficient; the copies have to be edited with it.**

The operable form: after editing a gate table, search the repository for each value that changed and confirm every occurrence agrees. This repository has carried a single value in two disagreeing forms within one document, alongside a total that did not equal the sum of the breakdown it was derived from — the kind of arithmetic that survives indefinitely when nobody re-adds the parts.

## 5. A changed third-party AOT warning count is re-judged from the output, never inherited

The count is a human judgement about a tool's output, which is why it survives being copied. An existing record in this repository counted one third-party assembly as producing two diagnostics when it produces one, and that error had already been quoted forward before it was found. **Copying a count re-propagates the error with fresh authority**, because a number written down later looks as though it was checked then.

The second half is a prohibition: this cell is never abbreviated to zero. Its value is the outcome of a human judgement that the third-party code paths are not trim- or AOT-friendly and that this product does not take them. **No tool certified it**, and rewriting it as a clean zero converts a judgement into an unearned pass.

## 6. An end-to-end gate's red runs are part of its result

Only the final green gets recorded, and that hides which layer the fault was in — the expensive half to recover.

Both root causes found in this repository's end-to-end script were in the script, not the product. One assertion addressed a later phase's instance using an earlier phase's node identity, a data-directory difference that makes the two identities genuinely different; that assertion had been passing **only because the code path it exercised performed no verification of its input**, so correcting the addressing turned a pass into a failure. The other passed arguments to a command in the wrong order, so a value intended as one field was stored as two concatenated ones. Both were located by reading the decisive line in the output rather than by guessing, and both are the kind of fault that reappears whenever a script grows a phase.

A gate that has only ever reported green gives a later reader no way to distinguish a hardened script from one that was never wrong.

## Alternatives considered

**Move the gate to CI and stop running it locally.** Rejected: this repository is single-machine with no CI, and part of the suite exercises real DHT bootstrap and real TCP, so a CI run would not be the same observation as a local one. The deeper cost is that without a local discipline the same questions get answered later, in a place with less context, where the value has no author left to ask.

**Keep gate values out of version control and generate them at run time.** Rejected: it makes "which run is this?" uncheckable — a regenerated value cannot be compared against the claim made in a document last week. What is lost is attribution to a run and an author, and that attribution is the entire payload of this discipline.

**Record only the total, without the per-project breakdown.** Rejected: the parts and the total have to agree, because the total is the only cheap cross-check available on the parts. The arithmetic this prevents was exactly that: a total that did not equal the sum of the breakdown it came from, in a document whose whole purpose was to be quotable.

## Consequences

- Running the gate costs real time — a full local pass including the end-to-end script takes minutes — and this discipline requires the tree to stay still for that whole window. A value produced on a moving tree describes a tree that no longer exists.
- **Gate values have a shelf life.** Any change under `src/` or `tests/` expires every value recorded for the preceding run. This note carries no numbers for the same reason: the rules are durable, the values are the part that goes stale.
- Nothing here is automated. No rule prevents a stale copy from being written; the search-the-value step in rule 4 is a habit, not a gate, and a reviewer who does not perform it gets no signal that it was skipped.
- The rules bind whoever records a value, which is why they are obligations rather than advice. A gate whose author cannot be named is not a gate.

## Related

- [Push-time scoped checks](../../../skills/quality-gate-pre-push/SKILL.md) — decides *which* checks to run before a push; this note governs what a recorded value means. They overlap in exactly one place, and the overlap is deliberate: a focused run is checked against the number of tests it was supposed to select, because "focused" and "ran fewer" look identical in the output.
- [The handoff working document that carried these lessons](../../archived/process/2026-09-28-p2pchat-handoff.md) and [its repair-plan companion](../../archived/process/2026-09-20-p2pchat-repair-plan.md) — frozen snapshots, cited for the history this note takes over and not as current authority.
- [Publish profile TargetFramework corrected from net10.0 to net11.0](./2026-09-17-publish-profile-target-framework.md) — the other `process` note, and the one place this repository already accepted a residual with two sources of truth and no automated check; rule 4 is the general form of that exposure.
