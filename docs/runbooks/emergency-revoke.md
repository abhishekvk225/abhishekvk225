# Runbook: emergency revoke and kill switch

Use when a client's API keys are leaked/abused, a client must be cut off immediately, or a platform account is compromised. All calls require the platform permission `apikeys.emergency-revoke` (Super Admin by default), a written **reason**, and are audited. Base URL: `https://<api-host>/api/v1`, bearer token of a platform user (MFA-complete).

## 1. Decide the blast radius

| Situation | Control | Effect | Propagation |
|---|---|---|---|
| One key leaked | `POST /admin/clients/{clientId}/api-keys/{keyId}/revoke` | That key is refused (`401`) | Immediate on the node that handled the revoke; other nodes within `ApiAuth:CacheSeconds` (default **5 s**) |
| Client's keys all suspect | `POST /admin/clients/{clientId}/api-keys/revoke-all` | Every active key revoked; client must create new keys | same |
| Client abusing / dispute / breach at client | `PUT /admin/clients/{clientId}/api-access` body `{"disabled":true,"reason":"..."}` | Kill switch: every key refused with `API_ACCESS_DISABLED`, keys stay intact (reversible) | same |
| Client entirely | `POST /admin/clients/{id}/suspend` (reason required) | Client suspended; portal users blocked | immediate for new sign-ins, sessions end on next token refresh |
| License abuse | `POST /admin/licenses/{id}/suspend` or `.../revoke` | Metered calls refused (`LICENSE_SUSPENDED`) | immediate |
| Staff account compromised | deactivate the user (`PUT /admin/users/{id}` with `isActive:false`), `POST /admin/users/{id}/mfa/reset` after recovery, rotate its passwords | Sessions end at refresh-token use; access tokens live up to `Jwt:AccessTokenMinutes` (15 min) | see 4 |
| Everything (platform incident) | Take the API out of rotation at the proxy / stop the containers | Hard stop | immediate |

Reasons are free text, mandatory, and appear in the audit log: write what and why, not who.

## 2. Examples

```bash
TOKEN=...   # platform staff access token
API=https://api.example.com/api/v1
CLIENT=<client guid>

# kill switch on
curl -sS -X PUT "$API/admin/clients/$CLIENT/api-access" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"disabled":true,"reason":"INC-1234 suspected key leak at client"}'

# read the state
curl -sS "$API/admin/clients/$CLIENT/api-access" -H "Authorization: Bearer $TOKEN"

# revoke every key
curl -sS -X POST "$API/admin/clients/$CLIENT/api-keys/revoke-all" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"INC-1234 rotate after leak"}'
```

The portal does not surface these controls yet (no UI references them); use the API (curl, or the Development-only Swagger UI against staging). Rehearse once so the commands are at hand.

## 3. Verify

1. A call with the revoked key returns `401` (or `403 API_ACCESS_DISABLED`) from every node: test against each node directly, wait `ApiAuth:CacheSeconds` + a few seconds.
2. Audit log contains the action with the reason (`GET /admin/clients/{id}/activity`).
3. The client's webhooks keep working unless you also disable the endpoints (they use client-owned secrets, not API keys); if the webhook secret leaked, ask the client to rotate it.

## 4. What these controls do not do

- They do not invalidate **access tokens already issued** to portal/staff users (valid up to 15 minutes) and do not delete data. To end staff sessions faster, change the signing key id ([key-rotation](key-rotation.md) section 1, emergency path), which logs out everyone.
- Per-node caches: if `ApiAuth:CacheSeconds` was raised, the propagation bound is that value. Rate-limit/quota counters are per node.
- Revoking does not tell the client; tell them through the agreed channel.

## 5. Undo

Kill switch: `PUT .../api-access` with `{"disabled":false,"reason":"..."}`. Revoked keys cannot be reactivated; the client creates new ones. Record the end of the incident in the ticket.
