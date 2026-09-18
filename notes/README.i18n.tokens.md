# The Sidecar and Bilingual Tokens

Every Agent Note pair carries a **sidecar** consistency record: a small machine-checkable file attesting that the Chinese and English members agree. This file defines the sidecar's contents, the two in-file tokens, and the manual checks a reviewer performs in place of the pairing gate.

## What a pair is

- Translation is **Chinese-first**: the Chinese file is the body of record — authored first and kept authoritative.
- The pair is `<note>.md` (English) plus `<note>.zh.md` (Chinese), mirroring `README.md` / `README.zh.md` in this directory. The base name always holds the English member, so a note is discoverable at its obvious path.
- Both members carry the same header tokens and the same section names. `# Agent Note: `, the `Status:` line, and the canonical section names stay in English verbatim in both files — only the title and the body prose are translated. Translating section names would break the sidecar's cross-member comparison.
- A pair is **in sync** when the header tokens match, both members carry every section name of the pair's skeleton, and no `<!-- bilingual: pending -->` token is present in either file.

## The sidecar

The sidecar lives at `<note>.i18n.yaml` next to the pair. It is created whenever a pair is created and re-recorded whenever either member changes. A pair with no sidecar is treated as out of sync.

It carries:

- the pair's two paths;
- the heading and `Status:` line extracted from each member;
- the list of section names in each member;
- the token state of each member (present / absent);
- the date the record was last re-recorded.

The sidecar records **structure and header agreement only** — it is not a translation-quality judgment and does not diff body prose. Whether the English body faithfully renders the Chinese is a reviewer's call.

## Tokens

| Token | Meaning | Where it appears |
|---|---|---|
| `<!-- bilingual: pending -->` | The English counterpart exists but its body is not yet translated; needs a human to fill the body. | Top of the English file, immediately under the `Status:` line. |
| `<!-- bilingual: stub-only -->` | The English counterpart is intentionally minimal (only header + section headings), with the body explicitly deferred. | Top of the English file, in place of `<!-- bilingual: pending -->` when the stub is meant to remain a TODO. |

## Reviewer checks

Reviewers reject a PR that:

- Adds a Chinese file without creating the matching English counterpart.
- Adds an English counterpart without a matching Chinese source file (orphan English note).
- Removes `<!-- bilingual: pending -->` without actually translating the body in the same PR.
- Creates or changes either member of a pair without creating or re-recording its sidecar.
- Leaves the two members disagreeing on header tokens or section names.
