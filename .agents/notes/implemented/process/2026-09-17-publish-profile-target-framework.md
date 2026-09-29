# Agent Note: Publish profile TargetFramework corrected from net10.0 to net11.0

Status: implemented

## Problem

`src/P2PChat.App/Properties/PublishProfiles/FolderProfile.pubxml` pinned `<TargetFramework>net10.0</TargetFramework>`, while every project in the solution (including `Directory.Build.props`) targets `net11.0`, and `global.json` pins SDK `11.0.100-rc.1.26425.128`.

When the TFM in a publish profile disagrees with the projects' actual TFM, the profile no longer points at any existing build output; `PublishDir` was likewise pinned to `bin\Release\net10.0\publish\win-x64\`, which does not match the real layout. Publishing through that profile (or selecting it in the IDE) therefore fails or writes to the wrong directory, and the error surfaces as "no net10.0 target" — a symptom far removed from the actual cause, a stale configuration.

## Decision

Correct the profile's TFM and publish directory to the real values:

- `<TargetFramework>net10.0</TargetFramework>` → `net11.0`
- `<PublishDir>bin\Release\net10.0\publish\win-x64\</PublishDir>` → `bin\Release\net11.0\win-x64\publish\`

The directory shape is corrected too: the old value was `net10.0\publish\win-x64\`, whereas .NET actually produces `net11.0\win-x64\publish\` (the RID immediately follows the TFM, with `publish` last).

All other publish settings are unchanged: `Release` configuration, `Any CPU`, `win-x64`, `SelfContained=true`, `PublishSingleFile=true`, `PublishReadyToRun=false` — consistent with the Native-AOT single-file publish.

## Alternatives considered

**Delete the publish profile and pass command-line arguments instead (`dotnet publish -r win-x64 …`).** Rejected: there are enough AOT and single-file switches that pinning them in a version-controlled profile is less error-prone than having every publisher assemble a command line; the IDE publish wizard also depends on the file.

**Make the profile multi-target (`net10.0;net11.0`).** Rejected: the projects target only `net11.0`, so a second target would be a build target no code supports and nothing consumes.

**Leave net10.0 in place and change it ad hoc at publish time.** Rejected: that is the status quo — a stale config sits in the repository until publishing exposes it as "target not found", which costs more to diagnose than one correction.

**Remove `TargetFramework` from the profile so it inherits from the project.** Not adopted: `FolderProfile.pubxml` is an IDE-generated file whose fields the publish wizard reads and writes, so a deleted field may be written back. Correcting rather than removing was chosen, at the cost of the TFM now appearing in both `Directory.Build.props` and the publish profile.

## Consequences

- Publishing through `FolderProfile` (command line or IDE) resolves `net11.0` correctly, with output under `bin\Release\net11.0\win-x64\publish\`.
- **Residual risk: the TFM now has two sources of truth.** `Directory.Build.props` and `FolderProfile.pubxml` each state `net11.0`, and they can drift again. No automated check was added — a TFM change in `Directory.Build.props` is invisible to the publish profile, so the next TFM bump must edit both.
- This change does not alter the behavior of published artifacts; it corrects the build and publish path only. The AOT single-file switches are untouched.
