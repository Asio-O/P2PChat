# Agent Note: Plain mode had no stdin input channel

Status: implemented

## Problem

`P2PChatTui`'s linear (plain) mode had an output side but **no input side**. The first statement of `ReadKeyOrNull()` is `if (_plainMode) return null;`, and the main loop only hands input to the command dispatcher when it receives a key — so while `_plainMode` is set, **every byte of stdin is silently discarded**. The program still starts, still prints the messages it receives, still keeps its DHT alive, but nothing the user types produces an action: `/add`, `/connect`, `/msg`, `/group` and the rest are all unreachable.

**This is not a scripting-only problem.** There are three ways into `_plainMode`:

- the `P2PCHAT_PLAIN=1` environment variable (turned on explicitly by scripts and CI);
- the `catch` around `Console.Title` / `Console.Clear()` in `RunAsync`;
- the `catch` in `ReadKeyOrNull`, plus the downgrade in `Render()` when it catches `InvalidOperationException`.

The latter two are **automatic downgrades**: any user whose console initialization or full-screen rendering fails falls into plain mode. So **anyone whose stdout is redirected, who runs in CI, or who pipes the process is affected** — a pipe only solved "how do I collect the output"; the input side was still empty, and the node became a receiver that could never send. This is also why `scripts/e2e-verify.ps1` Phase 2's A30/A31 could not pass: the script writes `/msg nodeB <plaintext>` into nodeA2's stdin, nodeA2 never reads it, so no message is ever sent.

## Decision

Plain mode now reads stdin **by line**, mirroring the line-at-a-time output semantics it already had (`Console.WriteLine` in `DrainInbox`). The change is seven steps, all in `src/P2PChat.UI/Views/P2PChatTui.cs`:

1. Add `using System.Threading.Channels;` and the field `private readonly Channel<string> _plainInput = Channel.CreateUnbounded<string>();` (with a full XML comment explaining why plain mode must read by line and why it lives on a background task).
2. In `RunAsync`, start background loop 4 when `_plainMode` is set: `_ = ReadPlainInputAsync(ct)`.
3. `ReadPlainInputAsync(CancellationToken)` is the whole of that channel's IO: the blocking `Console.ReadLine()` is wrapped in `Task.Run(...)` so it never occupies the main loop; each non-empty line is `WriteAsync`-ed into `_plainInput`; a `null` line (stdin has hit EOF, e.g. the script closed the pipe after injecting) breaks the loop, and the `finally` calls `_plainInput.Writer.TryComplete()` so the main loop's `TryRead` returns `false` steadily instead of spinning.
4. The main loop's plain branch consumes whole lines read by the background task: it first tops up the start idempotently (step 7), then `_plainInput.Reader.TryRead(out var line)`, and on a hit `SubmitLine(line); continue;`. The branch sits after the key branch and before the 50 ms poll.
5. `SubmitLine(string)` is extracted as the dispatcher both input paths share: `Trim()` first and return on an empty string; a leading `/` goes to `ProcessCommandAsync`, otherwise it goes to `SendMessageAsync` when a conversation is selected, and to a system message when none is. `SubmitInput()` (interactive mode's `Enter` key) now calls it, keeping only the `_input` clear and repaint. Command semantics are therefore identical on both paths.
6. **The interactive mode `ReadKey` path is completely untouched**: `ReadKeyOrNull`'s `Console.KeyAvailable` + `Console.ReadKey(intercept: true)`, every branch of `HandleKey`, the character-by-character editing of `_input`, and `_dirty`-driven `Render` all stay exactly as they were.
7. The stdin reader starts **lazily and idempotently**: `EnsurePlainInputStarted(CancellationToken)` is added, using `Interlocked.Exchange(ref _plainInputStarted, 1)` as an atomic check-and-set, and it is called once at the top of `RunAsync` and once in the main loop's plain branch — where it must come **before** `TryRead` (topping up after a `TryRead` would drop a line that arrived the instant the reader started). Lazy is mandatory because the two automatic downgrade paths (`ReadKeyOrNull`'s `catch` and `Render()`'s `InvalidOperationException`) can flip `_plainMode` to `true` **partway through a run**; checking it only once at the top of `RunAsync` leaves the program back in "never sees a key and the stdin reader was never started" silence, with a channel that never completes and a main loop that merely spins — not even EOF would exit it. **The first version of the fix was written exactly that way: this hole was exposed by the fix itself and closed inside the same fix** — checking only at startup is not enough.

## Alternatives considered

**Extract stdin behind an `IPlainInputSource` abstraction and test through it.** Rejected: that is a testability tax paid in advance. The part of the fix that actually needs locking down is "how a line of text becomes a command or a message", and that logic is now testable because `SubmitLine(string)` was extracted; the channel itself is a single `Console.ReadLine`, and wrapping it buys an interface plus a fake implementation that must be kept in sync for no proportionate gain.

**Have plain mode use `Console.KeyAvailable` / `Console.ReadKey` instead (i.e. drop the early `return null`).** Rejected: when stdin is redirected or the console is non-interactive, `Console.ReadKey` either throws or blocks waiting for a key that will never arrive — that trades "discarded input" for "the process hangs". Character-at-a-time keys also depend on `Enter`, which forces a key-encoding convention on script injection.

**Leave the code alone and fix only the e2e script** (have the script bypass the TUI and call the services, or switch to the input-less `P2PCHAT_SELFTEST` shape). Rejected: that leaves a product defect with the user. Because the downgrade into plain mode is automatic, real users whose console initialization fails cannot send messages either. The script can work around it; users cannot.

**Call `Console.ReadLine()` synchronously in the main loop.** Rejected: `ReadLine` blocks, so the moment stdin withholds a newline the whole loop — including `DrainInbox` and `Render` — stalls and incoming peer messages stop being displayed. It has to sit on a background task with the main loop doing a non-blocking `TryRead`.

**Split the stdin line into simulated key events and feed them to `HandleKey`.** Rejected: that splits input semantics into two sets ("characters plus special keys" and "whole lines") while `HandleKey` is the interactive mode's UI layer. Plain mode only ever has a whole line; there is no `ConsoleKeyInfo` to construct.

## Consequences

- In plain mode, `/add`, `/connect`, `/msg`, `/group` and the other commands are usable exactly as in interactive mode, sharing the same `SubmitLine` → `ProcessCommandAsync` / `SendMessageAsync` dispatch.
- Empty lines are dropped (guarded by `line.Length > 0` before delivery), so no empty message is ever sent.
- After stdin closes (EOF) the channel is completed, the main loop simply returns to its 50 ms poll, and the node keeps receiving until it is shut down: it neither spins nor exits on its own.
- The two sides of plain mode are now a pair: it writes line-at-a-time via `Console.WriteLine` and reads line-at-a-time via `Console.ReadLine`.
- Interactive behavior is unchanged — the key path and the `_input` editing path are untouched word for word; after `SubmitInput` delegates to `SubmitLine`, the dispatch semantics are equivalent to before.
- **A mid-run downgrade is no longer a dead end.** When `_plainMode` flips to `true` partway through a run — via the `catch` in `ReadKeyOrNull` or the downgrade in `Render()` — the main loop's plain branch idempotently tops up the stdin reader start, so the program does not fall back to the pre-fix silence. This hole was exposed by the fix itself and closed inside the same fix; it is recorded here so that nobody "simplifies" the startup check back into a one-shot test at the top of `RunAsync`.
- **Cost: plain mode is line semantics.** Character-level editing, cursor movement, and `Tab` to switch conversations remain interactive-mode only; in plain mode the user switches conversations with commands such as `/msg <nodeId>`.
- The only new dependency is the BCL's `System.Threading.Channels`, which does not touch the project's AOT creed: no reflection, no `MakeGenericType`, no new serialized type, no formatter registration.

## Testing

- The end-to-end plaintext round trip is covered by **A30 / A31** of `scripts/e2e-verify.ps1`: A30 asserts that the plaintext nodeA2 injected via stdin appears in nodeB2's log, A31 asserts it appears in nodeB2's combined StdOut + Log output; A32 asserts in the other direction that the sending node does not echo that plaintext itself.
- The regression guard is filled in by `tests/P2PChat.Integration.Tests/PlainModeInputTests.cs`, covering the dispatch logic this fix actually changed. Its shape is worth stating: `P2PChat.UI` has no test project reference and `SubmitLine` / `ReadPlainInputAsync` are both `private`, so calling them in-process would mean changing product code or introducing reflection (both explicitly ruled out). The guard therefore asserts a **structural contract against the product source** rather than running a real stdin in-process.
- Script Phase 2 still writes stdin character by character at 30 ms intervals. Input is now read by line, so character-by-character writes are still correct (`Console.ReadLine` returns only once it sees the newline) — the pacing is simply no longer necessary.

## Deferred

- No `IPlainInputSource` abstraction was introduced; it is only worth adding if a fake stdin is ever needed for finer-grained in-process testing.
- The character-by-character pacing in e2e script Phase 2 can be reduced to a single whole-line write (tracked separately with the script changes).

## Related

- [Direct connect via an explicit endpoint](../feature/2026-09-20-static-peer-explicit-endpoint.md) — in plain mode, `/add <nodeId> <ip:port>` is the non-interactive way to wire two nodes together.
- [/connect <ip:port> — blind connect](../feature/2026-09-21-blind-connect.md) — the same "command is dispatched and executed" path, reached through a different entry point.
