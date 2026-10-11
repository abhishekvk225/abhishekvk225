# Runbook: key and secret rotation

Read this together with [04-security-strategy](../04-security-strategy.md). Rotate on schedule (below), on staff change with access to secrets, and immediately on suspected exposure ([incident-response](incident-response.md)).

| Secret | Setting | Scheduled | Zero-downtime? | Tooling status |
|---|---|---|---|---|
| JWT signing key (ES256) | `Jwt__Keys__N__*` + `Jwt__CurrentKeyId` (or the legacy `Jwt__SigningKeyPem`, `Jwt__SigningKeyId`, `Jwt__AdditionalValidationKeys`) | 12 months | yes | supported (section 1) |
| DB app login password | `ConnectionStrings__Default` (API), `Migrator__AppPassword` | 6 months | yes (two steps) | supported |
| DB admin / sa password | migrator connection | 6 months | n/a | operational |
| Seed / break-glass Super Admin password | `Seed__SuperAdminPassword` | after use | n/a | supported (`recover-superadmin`) |
| **Master key (KEK)** | `Encryption__Keys__N__*` + `Encryption__CurrentKeyId` (or the legacy `Encryption__MasterKeyBase64`) | 24 months or on exposure | **yes (online, no maintenance window)** | supported: `Migrator rotate-master-key` (section 3) |
| Webhook signing secrets | per endpoint in the portal | client-driven | yes | supported |
| API keys | per key | client-driven / 12 months | yes | supported (regenerate) |
| Portal Data Protection key ring | `DataProtection__KeyPath` | automatic (90-day keys) | yes | supported (section 1b) |
| TLS certificates | proxy / IIS binding | before expiry (alert at 30 days) | yes | operational |
| CI/CD deploy SSH key, GitHub environment secrets | GitHub | 12 months | yes | operational |

## 1. JWT signing key

The API holds a JWT **keyring**: it signs with `Jwt:CurrentKeyId` and accepts tokens signed by any key in the ring (matched by the `kid` header) until the entry is removed. The old single-key settings still work and count as ring entries, so nothing has to change for a running deployment.

```
Jwt__Keys__0__KeyId=k1            Jwt__Keys__0__PublicKeyPem=<public PEM of the old key>      (validation only)
Jwt__Keys__1__KeyId=k2            Jwt__Keys__1__SigningKeyPem=<private PEM of the new key>     (secret)
Jwt__CurrentKeyId=k2
```

1. Generate: `openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out jwt_signing_key.k2.pem`; vault it. Compute the old key's public PEM with `openssl pkey -in <old> -pubout`.
2. Deploy the ring above to **all** API nodes (a node that does not know `k2` yet rejects tokens signed by a node that does, so roll the nodes with `Jwt__Keys` containing both keys first if you roll gradually: add `k2` as a validation-only entry everywhere, then switch `CurrentKeyId`). Startup refuses duplicate ids, a current key without a private key, an unreadable PEM or a non-P-256 key, and logs `kid` plus a fingerprint of each public key (never the key).
3. New tokens carry `kid=k2`; tokens signed by `k1` stay valid until they expire (`Jwt:AccessTokenMinutes`, default 15). Refresh tokens are opaque and unaffected.
4. After one access-token lifetime plus clock skew remove the `k1` entry and deploy again. Destroy the old private key per vault policy.
5. Emergency (key leaked): do **not** keep the old key in the ring; accept that all sessions end (users sign in again). Revoke refresh tokens if required (see [emergency-revoke](emergency-revoke.md)).

The legacy form (`Jwt__SigningKeyPem` + `Jwt__SigningKeyId` + `Jwt__AdditionalValidationKeys`) is equivalent and may be used instead.

## 1b. Portal Data Protection key ring

The Blazor portal protects its session and MFA-pending cookies with ASP.NET Core Data Protection. `DataProtection__KeyPath` must be a persistent, shared, access-restricted directory (the portal refuses to start without it outside Development). Data Protection **rotates its own keys** (new key every 90 days by default, old keys are kept so existing cookies still decrypt): you do not rotate it on a schedule. Guidance:

- Keep the whole directory in the backup set and never delete old key files while sessions that need them can exist (sessions are at most the absolute refresh lifetime, 30 days; keep keys for at least 120 days).
- Treat the directory like a secret (restrict to the service account; the keys are plaintext on disk unless a certificate/DPAPI protector is configured: see the open item in `docs/STATUS.md`).
- Forcing everyone to sign in again (suspected exposure of the key ring): stop the portal nodes, move the directory aside, start the nodes (a fresh ring is created), and also revoke refresh tokens ([emergency-revoke](emergency-revoke.md)). Do this on **all** nodes at the same time.
- Never give the API's JWT key and the portal's key ring the same custody: they protect different things.

## 2. Database credentials

App login (no downtime): (1) run the Migrator `app-principal` with `Migrator__AppPassword=<new>`; SQL Server keeps one password per login, so first create a *second* login/user for the new credential if you need strictly zero downtime (`Migrator__AppLogin=nexaverify_app2`), (2) update `ConnectionStrings__Default` secret of the API, (3) roll the API, (4) drop the old login. For a short window outage a single `app-principal` run followed by an immediate secret update and restart is acceptable. Admin password: change in SQL Server, update the migrator secret (`migrator_connection`) only; the API never holds it.

## 3. Master key (KEK): online rotation

### What the master key protects (and what it does not)

| Artifact | Where | How it records the key id | What a rotation does |
|---|---|---|---|
| Client data keys (DEK) | `tenancy.ClientKeys.WrappedDataKey` | column `MasterKeyId`; the id is also in the authenticated data | unwrap with the row's key, wrap with the current key, conditional `UPDATE` |
| TOTP secrets | `iam.UserMfa.SecretEnc` | header at the start of the payload (`NXE1` + key id); payloads written before the keyring have no header and belong to the legacy key | decrypt, encrypt with the current key, conditional `UPDATE` |
| Ledger checkpoints | `licensing.LedgerCheckpoints` | column `KeyId` (inside the MAC) | verify the whole chain, then re-sign each checkpoint under the current key; originals are *superseded*, never deleted |
| Face templates, webhook secrets | encrypted with the per-client DEK | (via the DEK) | **nothing**: their ciphertext does not change; only the DEK is re-wrapped |
| MFA recovery codes, API keys, refresh tokens | unkeyed SHA-256 hashes | n/a | **nothing**: they do not depend on the master key |

### The keyring

```
Encryption__Keys__0__Id=2025-a     Encryption__Keys__0__KeyBase64=<32 random bytes, base64>   (secret)
Encryption__Keys__1__Id=2026-a     Encryption__Keys__1__KeyBase64=<32 random bytes, base64>   (secret)
Encryption__CurrentKeyId=2026-a
```

- Encryption, wrapping and signing always use `CurrentKeyId`; reading uses the key the record names. An id that is not in the ring is an error everywhere (`UnknownMasterKeyException`, a failed health check, a ledger "key not in keyring" finding): **there is never a silent fallback to another key**.
- Key ids are 1-50 characters of letters, digits, `.`, `_`, `-`. Startup fails on a missing/short/invalid key, a duplicate id, identical key material under two ids, or a `CurrentKeyId` that is not in the ring. Logs show the id and a fingerprint (first 8 hex of SHA-256 of the key) only. Supply the keys as files in `NEXAVERIFY_SECRETS_DIR` (`Encryption__Keys__0__KeyBase64`, ...) or through the secret store, like the other secrets.
- **Backward compatible:** the legacy `Encryption__MasterKeyBase64` (+ optional `Encryption__MasterKeyId`, default `m1`) is a ring entry under that id and is also the key that data written before the keyring existed belongs to. A deployment that changes nothing keeps working. To adopt the ring, keep the legacy variable **and** add the new key under `Keys`; the old key stays current until `CurrentKeyId` says otherwise.
- `GET /health/keyring` (anonymous, like the other health endpoints) returns status, `currentKeyId`, `keyIds`, `unknownKeyIdsInUse` and `rewrapPending`, never key material, fingerprints or counts. `/health/ready` includes the same check: **unready** if live data references an id the ring does not hold; **degraded (still ready)** while data waits to be re-keyed.

### Procedure: scheduled rotation (A -> B), online

1. **Prepare.** Generate B (`openssl rand -base64 32`), vault it under a versioned name (two-person access). Take a verified full backup ([backup-restore](backup-restore.md)); the backup is only restorable with the key that was current when it was taken, so **keep A for as long as any backup that needs it is retained**.
2. **Baseline.** `Migrator verify-keyring` must exit 0 and `Migrator verify-ledger` must exit 0 (investigate first otherwise: the rotation will not anchor over a broken chain).
3. **Add B, keep A current.** Deploy the ring `{A, B}` with `CurrentKeyId = A` to **all** API and portal nodes (every node must be able to read B before anyone writes it). Check `GET /health/keyring` on each node shows both ids.
4. **Switch.** Set `CurrentKeyId = B` on all nodes (rolling restart; nodes on A and nodes on B can run side by side because both keys are in every ring). New data is now written under B.
5. **Rehearse** on a restored production backup first: `Migrator rotate-master-key --dry-run` then the real run with the same ring.
6. **Count.** `Migrator rotate-master-key --dry-run` (counts only; no backup flag needed). Expect pending counts per artifact.
7. **Rotate.** `Migrator rotate-master-key --confirm-backup --batch-size=200` (add `--max-batches=N` to work in slices, `--resume` when continuing; see exit codes below). Run it with the same ring and secrets as the API (`NEXAVERIFY_SECRETS_DIR`); it refuses an ephemeral keyring. The API keeps serving while it runs.
8. **Check.** `Migrator rotation-status`: 100% complete, `Safe to retire 'A': YES`. `Migrator verify-keyring` exit 0. Fresh event-7002 lines for the re-signed checkpoints arrive in the external log store. A recognition request, an MFA sign-in and `/health/ready` work.
9. **Retire A.** Remove A from the ring on all nodes and redeploy; run `verify-keyring` and `/health/keyring` again (`unknownKeyIdsInUse` must be empty). Keep A sealed in the vault (older backups, section "Rollback"). Record date, ticket and verification (section 5).

Optional: `Encryption__LazyRewrap=true` (off by default) starts a background job that moves client data keys and MFA secrets onto the current key in small batches (`LazyRewrapBatchSize`, `LazyRewrapIntervalSeconds`, one node at a time through a database lock). It never re-anchors ledger checkpoints and never retires a key: you still run `rotate-master-key` (it will find little left to do on keys/MFA, and does the ledgers) and `rotation-status`.

### How the online rotation stays safe

- **Per-row conditional `UPDATE`:** a data key or MFA secret is only replaced if the row still holds exactly the bytes that were read (and the old key id). A concurrent writer, a second runner or an offboarding that destroys the key turns it into a counted no-op; the next pass re-reads. Each row is its own short transaction. A reader always sees either (old key id, old bytes) or (new key id, new bytes); both decrypt because both keys are in the ring.
- **Exactly once, idempotent, restartable:** the selection predicate is "key id is not the current one", so a crash, `Ctrl+C`, `--max-batches` or a second run simply continues. A complete rotation re-run changes nothing. `--resume` documents intent (and is logged); state lives in the data, not in a side table.
- **Ledgers:** for each license with checkpoints under an older key, the whole chain is verified first (hash chain, balance, every checkpoint's MAC under the key that signed it). Only if the license is clean, in **one transaction**, the checkpoints are re-signed with the same content (head hash, entry count, balance, original timestamp) under the current key and a head checkpoint is added; every copy is verified; then the originals are marked superseded. Superseded rows stay in the table as evidence but no longer count, which is what allows A to be retired. A broken chain, a forged/unverifiable checkpoint, a checkpoint under an unknown key or a license deleted together with its ledger **aborts the whole rotation before any other data is touched** (exit 3, event 7102 at Critical, audit `keyrotation.aborted_ledger_broken`): investigate per [incident-response](incident-response.md); never "fix" by re-anchoring.
- **Audit and logs:** audit entries `keyrotation.finished` / `keyrotation.aborted_ledger_broken` (counts only), log events 7100-7109 (7110 unknown key in use). No key material, plaintext, MAC or wrapped key appears in logs, arguments, exceptions or audit values; row ids and key ids do.

### Commands

| Command | Purpose | Exit codes |
|---|---|---|
| `Migrator rotate-master-key [--dry-run] [--batch-size=N] [--max-batches=N] [--resume] [--confirm-backup]` | move everything to `CurrentKeyId`. `--confirm-backup` is required unless `--dry-run`. | 0 complete, 4 incomplete (budget reached or rows kept changing: run again with `--resume`), 3 ledger broken (aborted), 1 failed (unknown key in use, undecryptable rows, ephemeral keyring, unexpected error), 2 usage |
| `Migrator rotation-status` | counts per key id per artifact, % complete, safe-to-retire per old key | 0, 3 if data references an id not in the ring, 1 ephemeral keyring |
| `Migrator verify-keyring [--sample=N]` | decrypts a sample under every key (data keys, MFA secrets, checkpoint MACs) and runs the full ledger verification | 0 ok, 3 ledger/checkpoint broken, 1 a key cannot decrypt / unknown id / could not run |

Options use the `--name=value` form. All three need the same keyring and connection (`ConnectionStrings__Default`, an administrative principal) as the migrator already uses.

### Rollback

- **Before retiring A** nothing is irreversible: A is still in the ring and still reads everything it wrote. Rotation is a one-way ratchet per key id for the **ledger anchors**: checkpoints are append-only and unique per (license, entry, key), and A's superseded checkpoints stay in the table, so the tool refuses to make A current again once it has been rotated away from (exit 1, message names the key). To "go back", add a **fresh** key C and rotate to it: client data keys and MFA secrets move to C like any other rotation, the ledger is re-anchored under C. Until then a node pointed back at `CurrentKeyId = A` still *reads* everything (A and B are both in the ring), it just writes new data under A.
- **After retiring A** the ring cannot read data that is still under A, which by the status check is none. If something unexpected needs A (an older backup restored), add A back to the ring from the vault; everything under A becomes readable again, and `rotation-status` shows what is where.
- A restored older backup contains old checkpoints/keys under A: restore, add A to the ring, run `verify-keyring`, then rotate again.

### Compromise procedure (master key exposed or suspected)

Rotation changes which key protects the data; it does not make a copy of the old key made by an attacker useless **for data the attacker also holds**. Decide the scope first.

1. **Declare an incident** ([incident-response](incident-response.md)). Preserve evidence (logs, a database snapshot). Treat it as a data breach if the database, a backup or the secret store may also have been read: the old key together with a copy of the database decrypts every data key and TOTP secret in that copy.
2. **Rotate immediately, online:** generate B on a clean host, vault it, run the scheduled procedure steps 3-9 without waiting for a window. Expedite: `--batch-size=500`, several `rotation-status` checks, `Encryption__LazyRewrap=true` as a safety net. The old key must not stay in the ring any longer than the status says it is needed.
3. **Contain what rotation cannot fix:** data keys are re-wrapped but the **data keys themselves are unchanged**, so anyone who copied the database and the old key can still decrypt a copy of biometric templates and webhook secrets. If the database or a backup was exfiltrated, also: (a) rotate every client's data key and re-encrypt their templates (engineering-led; the data keys have a version column, a new version is the supported shape), (b) ask clients to rotate webhook secrets and API keys ([emergency-revoke](emergency-revoke.md)), (c) reset MFA for all staff and clients (new TOTP secrets: the old secrets are in the copy), (d) revoke all refresh tokens and rotate the JWT key (section 1, emergency form).
4. **Destroy the old key** only after the retention of every backup that needs it has been decided: backups encrypted under it remain a liability; consider re-taking backups after the rotation and expiring the old ones early.
5. **Ledger integrity:** an attacker with the old *anchor* key could forge checkpoints. After rotation the ledger is re-verified against the chain and re-anchored; compare the new event-7002 lines in the external log store with the chain. If tampering is suspected, do not re-anchor: follow the break procedure first.
6. Record the evidence (section 5) and the decision on the open items above.

## 4. Webhook secrets and API keys (client-facing)

Clients regenerate API keys and rotate webhook secrets in the portal (shown once). Platform staff can force this with the emergency controls ([emergency-revoke](emergency-revoke.md)). After a platform-side exposure (database leak): revoke all keys of affected clients, ask clients to create new ones and new webhook secrets.

## 5. Evidence

For each rotation record: date, who, ticket, what was verified (steps above), where the old material is retained and until when. Store with the release checklist.
