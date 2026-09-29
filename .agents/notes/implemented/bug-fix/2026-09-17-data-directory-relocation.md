# Agent Note: Data directory relocated to the user home directory

Status: implemented

## Problem

The program wrote all runtime data under `<program directory>/data/`, which produced two failures that actually occurred:

- **It could not start from a read-only directory.** When the program sat in a read-only location or a path requiring administrator rights (`Program Files`, a controlled share), the first write to `data/` failed and the identity key could not be generated.
- **A redeploy destroyed the identity.** Publishing over the program directory also deleted `identity.json`, `session_keys.json`, `group_keys.json`, and `contacts.json`. Upgrading once meant a new node identity, with historical session keys and contacts gone.

At the same time `P2PCHAT_DATA_DIR` was the **only** way to locate the directory: the default path was unusable on its own, so users had to set that variable explicitly before the program worked.

## Decision

Runtime data lives in **`.p2pc/` under the user home directory**, decoupled from the program's install location.

- Default path: `%USERPROFILE%\.p2pc` (Windows) / `$HOME/.p2pc` (Unix).
- Home resolution falls back step by step: `USERPROFILE` → `HOME` → `LocalApplicationData`. If all three are empty it throws `InvalidOperationException`, telling the caller to set `P2PCHAT_DATA_DIR` instead.
- `P2PCHAT_DATA_DIR` is kept but demoted from **required** to **optional override**, narrowed to same-machine multi-instance isolation.
- `DataPath.Root` and `SetRoot` are now locked (`_initLock` plus double-check), removing a race on concurrent first access; `SetRoot` validates against null/blank and normalizes to a full path.
- Added `DataPath.GetDirectory(name)` (resolve and create a subdirectory on demand) and `DataPath.Reset()` (clear the cache; test use).
- The log directory now uses `GetDirectory("logs")` instead of hand-rolling `Path.Combine(Root, "logs")`.
- Both the startup log and the `P2PCHAT_SELFTEST` output print the data directory, so "where did the data actually go" is answerable.

Implementation: `src/P2PChat.Core/Extensions/DataPath.cs`.

## Data directory layout

| File | Contents |
|---|---|
| `identity.json` | Long-term identity key pair (ECDH P-256) |
| `session_keys.json` | Per-peer session keys |
| `group_keys.json` | Per-group keys |
| `contacts.json` | Contact list |
| `peers.txt` | Known nodes (loaded at startup, saved on exit) |
| `logs/p2pchat-YYYYMMDD.log` | Daily-rolling runtime log |

Supporting changes: `.gitignore` gained `.p2pc/` and `contacts.json`; the root `README.md` gained a data-directory section and file inventory; the prose and assertion labels in `scripts/config-probe.ps1` and `scripts/e2e-verify.ps1` were updated to match.

## Alternatives considered

**Keep `<program directory>/data/` and surface the write failure to the user.** Rejected: a read-only directory is a normal deployment shape, and requiring the user to relocate the program before it will start passes a deployment constraint onto the operator; the identity loss on redeploy would also remain.

**Use `LocalApplicationData` (`%LOCALAPPDATA%`) as the default.** Rejected: a deeper path with more platform variance, and it binds to an "application" notion rather than a "user" one. `.p2pc` under the home directory is more legible and far easier for a user to back up or move. That directory is retained as the last-resort fallback rather than the default.

**Keep `P2PCHAT_DATA_DIR` required.** Rejected: that is the "default does not work" status quo — no variable, no working program. Demoting it to an optional override lets the default path stand on its own.

**Offer no isolation mechanism and let same-machine instances share one identity.** Rejected: the end-to-end tests must bring up two nodes on one machine that peer with each other; sharing one `identity.json` gives both the same node ID, so neither can discover the other. `P2PCHAT_DATA_DIR` is therefore kept.

## Consequences

- The program can live anywhere, including read-only directories, and redeploying no longer touches user identity or chat data.
- Multiple copies of the program for one user naturally share a single identity, matching "one user, one identity".
- **Cost: existing data is not migrated.** Identity keys, session keys, and contacts in the old `<program directory>/data/` are not read, so an upgraded user sees a new identity and an empty contact list. This change ships no migration path; preserving an old identity requires copying the old `data/` contents into `~/.p2pc/` by hand.
- **Cost: multi-instance setups must set `P2PCHAT_DATA_DIR` explicitly.** Two instances that forget it share one `identity.json` and end up with the same node ID, invisible to each other. That failure mode is documented in `scripts/e2e-verify.ps1` and covered by assertion A24.
- The data directory is now derived from a three-level fallback, so "where the data is" no longer follows from a single environment variable; when diagnosing, trust the path printed in the startup log.
