# Runbook: key and secret rotation

Read this together with [04-security-strategy](../04-security-strategy.md). Rotate on schedule (below), on staff change with access to secrets, and immediately on suspected exposure ([incident-response](incident-response.md)).

| Secret | Setting | Scheduled | Zero-downtime? | Tooling status |
|---|---|---|---|---|
| JWT signing key (ES256) | `Jwt__SigningKeyPem`, `Jwt__SigningKeyId`, `Jwt__AdditionalValidationKeys` | 12 months | yes | supported |
| DB app login password | `ConnectionStrings__Default` (API), `Migrator__AppPassword` | 6 months | yes (two steps) | supported |
| DB admin / sa password | migrator connection | 6 months | n/a | operational |
| Seed / break-glass Super Admin password | `Seed__SuperAdminPassword` | after use | n/a | supported (`recover-superadmin`) |
| **Master key (KEK)** | `Encryption__MasterKeyBase64` | 24 months or on exposure | **no (maintenance window)** | **not yet automated: see section 3** |
| Webhook signing secrets | per endpoint in the portal | client-driven | yes | supported |
| API keys | per key | client-driven / 12 months | yes | supported (regenerate) |
| TLS certificates | proxy / IIS binding | before expiry (alert at 30 days) | yes | operational |
| CI/CD deploy SSH key, GitHub environment secrets | GitHub | 12 months | yes | operational |

## 1. JWT signing key

1. Generate: `openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out jwt_signing_key.new.pem`; vault it.
2. Compute the **public** PEM of the current key (`openssl pkey -in <old> -pubout`) and set `Jwt__AdditionalValidationKeys` to `[{ "KeyId": "<old id>", "PublicKeyPem": "..." }]` (see `JwtOptions`); set `Jwt__SigningKeyId` to a new id (`k2`) and `Jwt__SigningKeyPem` to the new key.
3. Deploy. New tokens carry `kid=k2`; tokens signed by the old key stay valid until they expire (`Jwt:AccessTokenMinutes`, default 15). Refresh tokens are opaque and unaffected.
4. After one access-token lifetime plus clock skew remove the old public key and deploy again. Destroy the old private key per vault policy.
5. Emergency (key leaked): do **not** keep the old key in `AdditionalValidationKeys`; accept that all sessions end (users sign in again). Also revoke refresh tokens if required (see [emergency-revoke](emergency-revoke.md)).

## 2. Database credentials

App login (no downtime): (1) run the Migrator `app-principal` with `Migrator__AppPassword=<new>`; SQL Server keeps one password per login, so first create a *second* login/user for the new credential if you need strictly zero downtime (`Migrator__AppLogin=nexaverify_app2`), (2) update `ConnectionStrings__Default` secret of the API, (3) roll the API, (4) drop the old login. For a short window outage a single `app-principal` run followed by an immediate secret update and restart is acceptable. Admin password: change in SQL Server, update the migrator secret (`migrator_connection`) only; the API never holds it.

## 3. Master key (KEK) and what depends on it

`Encryption__MasterKeyBase64` (32 bytes, Base64) is the root for:

- the wrapping of every client's data key (`tenancy.ClientKeys.WrappedDataKey`, bound to `MasterKeyId` in the authenticated data) which protects biometric templates and webhook secrets;
- TOTP secrets of staff/client users (`iam.UserMfa`, HKDF purpose `mfa-secret`);
- the HMAC keys that sign ledger checkpoints (`licensing.LedgerCheckpoints`, HKDF purpose `ledger-anchor`).

**Current state (be honest in the go/no-go): the repository has no master-key rotation or re-anchor tool.** The code supports one active master key (`Encryption:MasterKeyId`, default `m1`). Changing `MasterKeyBase64` without a migration makes (a) all client data keys undecryptable (biometric data unrecoverable), (b) all MFA secrets unreadable (users locked out of second factor), (c) every ledger checkpoint report as a failed signature. Changing `Encryption__MasterKeyId` alone is just as destructive, because the id is part of the authenticated data of every wrapped key. **Do not rotate the master key by editing the secret or the id.**

Required before the first planned rotation (engineering work item, tracked in `docs/STATUS.md` M10): a Migrator command `rotate-master-key` that, with both the old and the new key supplied as secrets, in a single maintenance window and database transaction per client:

1. unwraps each `ClientKeys.WrappedDataKey` with the old key and re-wraps with the new key and new `MasterKeyId` (data ciphertext does not change);
2. decrypts each `iam.UserMfa` TOTP secret with the old derived key and re-encrypts with the new one;
3. **re-anchors the ledger**: recomputes the HMAC of every existing `LedgerCheckpoints` row with the new `ledger-anchor` key *only after* a clean verification run against the old key (so a tampered ledger cannot be blessed), and logs the new event-7002 lines to the external store;
4. is resumable, idempotent, produces a report, and is rehearsed on a restored production backup first.

Procedure once the tool exists:

1. Freeze changes; announce the window; take a verified full backup and export the current master key to the vault under a versioned name (the old key must remain recoverable for as long as any backup that needs it is retained).
2. Run an on-demand ledger verification: it must be clean (otherwise investigate first; never re-anchor a broken ledger).
3. Stop API nodes; run the rotation command; start nodes with the new `Encryption__MasterKeyBase64` and `Encryption__MasterKeyId`.
4. Verify: a recognition request succeeds for a few clients, an MFA sign-in works, on-demand ledger verification is clean and fresh 7002 lines arrive in the external store, `/health/ready` is 200.
5. Keep the old key sealed in the vault (needed to restore older backups); document the date.

**Interim plan for key compromise** (until the tool exists): treat as a data-breach incident ([incident-response](incident-response.md)); the realistic remediation is to re-enrol affected data under a new key by an engineering-led script, because there is no supported in-place path. Keep the master key in an HSM/vault with audit logging and two-person access to avoid needing this.

## 4. Webhook secrets and API keys (client-facing)

Clients regenerate API keys and rotate webhook secrets in the portal (shown once). Platform staff can force this with the emergency controls ([emergency-revoke](emergency-revoke.md)). After a platform-side exposure (database leak): revoke all keys of affected clients, ask clients to create new ones and new webhook secrets.

## 5. Evidence

For each rotation record: date, who, ticket, what was verified (steps above), where the old material is retained and until when. Store with the release checklist.
