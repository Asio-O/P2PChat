# Agent Note: Crypto algorithm comments corrected to match the implementation

Status: implemented

## Problem

The source comments described algorithms the code does not implement, and every discrepancy pointed at the **same other** cryptographic suite:

| Location | Comment (wrong) | Actual implementation |
|---|---|---|
| `NodeInfo.PublicKey` | Ed25519 public key (32 bytes) | ECDH nistP256, SubjectPublicKeyInfo (DER, 91 bytes) |
| `IEncryptionService.GenerateKeyPair` | Generate ECDH key pair (X25519) | `ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)` |
| `IEncryptionService.Sign` / `Verify` | Ed25519 sign / verify (or ECDsa fallback) | `ECDsa.Create(nistP256)` + SHA-256 |
| `AesGcmEncryptionService` class comment | ECDH(X25519) key agreement | ECDH(nistP256) |
| `KeyExchangeMessage.EphemeralPublicKey` | Sender ephemeral public key (X25519, 32 bytes) | P-256 SubjectPublicKeyInfo (DER, 91 bytes) |

This was not imprecise wording — it named a **different suite**. X25519 / Ed25519 public keys are a fixed 32 bytes; a P-256 SubjectPublicKeyInfo is 91 bytes of DER. Any code written against the comments — in particular code parsing or constructing `EphemeralPublicKey` or `NodeInfo.PublicKey` as a fixed 32-byte value — would simply fail.

The comments were carrying a **cross-package contract**: `IEncryptionService` lives in `P2PChat.Core` and its implementation in `P2PChat.Crypto`, and nothing at compile time can detect that "the algorithm the comment names" and "the algorithm the implementation uses" disagree.

## Decision

Correct the comments to the implementation. The code does not change.

The shipped cryptographic suite (now stated in the comments):

- Key agreement: **ECDH nistP256**
- Session key derivation: **HKDF-SHA256** (32-byte output)
- Encryption: **AES-256-GCM**
- Sign / verify: **ECDSA P-256 + SHA-256**
- Key encoding: public keys as **SubjectPublicKeyInfo (DER)**, private keys as **ECPrivateKey (DER)**, produced by `ExportSubjectPublicKeyInfo` / `ExportECPrivateKey` and paired with the matching Import methods.

The corrected comments also carry two **negative guarantees**: `NodeInfo.PublicKey` states explicitly that it is *not* Ed25519, and `KeyExchangeMessage.EphemeralPublicKey` states explicitly that it is *not* X25519 — a fixed 32-byte layout does not apply.

## Alternatives considered

**Change the code to match the comments, i.e. actually implement X25519 + Ed25519.** Rejected: no functional requirement asks for a different algorithm; the P-256 suite shipped in the initial commit and passes the end-to-end tests; and switching would change the on-wire byte format of `EphemeralPublicKey` and `NodeInfo.PublicKey` (91-byte DER → fixed 32 bytes), which is a breaking protocol change. Make the documentation follow reality, not the reverse.

**Delete the algorithm names from the comments, leaving only neutral wording like "generate a key pair" and "sign".** Rejected: the algorithm's identity is a contract the caller must know — it determines the public key's byte length and encoding, which is exactly where this drift created risk. Removing the names makes the contract vaguer, not clearer.

**Leave the comments alone and document the truth only at the implementation.** Rejected: the wrong comments sit on the public abstractions in `P2PChat.Core`, the first thing most readers see. Documenting at the implementation does not address the root cause.

**Add a compile-time or test-time assertion so the comments and implementation cannot drift again.** Not adopted (not rejected): comment text cannot be verified automatically, and the only automatable check is against **behavior**, which existing tests already cover. The real guardrail is writing the contract into the comment and holding it in review — which is the trade-off this repository currently accepts.

## Consequences

- Readers are no longer pointed at a nonexistent X25519 / Ed25519 design, and anyone implementing parsing from the comments avoids the 32-byte vs 91-byte trap.
- The on-wire and on-disk byte formats are **unchanged**: `NodeInfo.PublicKey` is still a 91-byte SPKI DER, as is `EphemeralPublicKey`. Existing `identity.json` files and current node IDs are unaffected.
- This change is comments only, with no behavior change, so every existing test passes as before — which also means **no test can prevent a recurrence** of this kind of drift. Comment/implementation agreement rests on review, not tooling.
- Recorded for future reference: this project has **never** used X25519 or Ed25519. Those two names in the historical comments were documentation errors, not an abandoned earlier implementation.
