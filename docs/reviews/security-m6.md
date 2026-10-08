# Security Review - M6 API Management (API keys, usage limits, request logs, webhooks)

Reviewer: Security Agent. Baseline: `docs/04-security-strategy.md` (T3, T8, T10, T11, A10), `docs/03`, `docs/02`.
Branch `claude/face-recognition-saas-nv5djm`, commits b16d054 and 5683fc7. Static review only; SQL Server integration tests were not run (Docker needed, per instruction).

## Verdict: PASS-WITH-CONDITIONS

No Critical or High finding. The core design is sound: 256-bit CSPRNG secrets stored as SHA-256, constant-time compare with a dummy hash, uniform failures before the secret is proven, scope subset rule at create/update, API-key scopes limited to face operations (a key cannot manage keys or webhooks), a two-stage SSRF guard (registration plus connect-time re-check on the connected address, no redirects, no proxy), encrypted webhook secrets shown once, and a transactional outbox with `READPAST` claiming.
Conditions to close before M6 sign-off: M-1 to M-4 (fix or explicit written acceptance). Low items go to the backlog.

## Tools run
| Tool | Result |
|---|---|
| `dotnet list package --vulnerable --include-transitive` | Ran; no vulnerable packages in any project |
| Secret grep (`nxv_live_...`, `whsec_...`) over source/docs/config | No hits in tracked files |
| gitleaks | Not installed locally; CI runs `gitleaks/gitleaks-action@v2` (ci.yml:50), plus CodeQL and dependency-review workflows |
| Raw SQL / `IgnoreQueryFilters` grep | Only `WebhookStore` raw SQL (parameterised, in the whitelisted Platform folder); no `IgnoreQueryFilters` in src |
| SQL Server integration tests | Not run (Docker) |

## Findings

| ID | Sev | Location | Summary |
|---|---|---|---|
| M-1 | Medium | `ApiUsageLimiter.cs:27-29,62`, `ApiKey.cs:~117`, `ApiValidators.cs:9` | Rate limit and daily quota are bypassable / not durable |
| M-2 | Medium | `WebhookDispatcher.cs:106-117`, `WebhookService.cs:226-246,260-276` | Shared dispatcher can be starved; client-controlled outbound amplification |
| M-3 | Medium | `ApiKeyService.cs:162-201` | Regenerate bypasses the creator-subset scope rule and the key cap |
| M-4 | Medium | `Program.cs:66-75` | `Webhooks:AllowUnsafeTargets` guard only covers `Production` |
| L-1 | Low | `ApiKeyAuthenticator.cs:117-118` | Negative-result cache is unbounded (memory) |
| L-2 | Low | `ApiKeyAuthenticator.cs:87`, `ApiKeyService.cs:135,158,199` | Revoke/update invalidation is node-local (up to 15 s stale elsewhere) |
| L-3 | Low | `ApiKeyAuthenticator.cs:113,139-154` | Corrupt client IP allow-list parses to "no restriction" (fail-open) |
| L-4 | Low | `WebhookUrlGuard.cs:118-125` | `IsPublic` misses 6to4, Teredo, IPv4-compatible and other special IPv6 ranges |
| L-5 | Low | `ApiRequestLogWriter.cs:44-57`, `ApiUsageMiddleware.cs:37-45,59` | One shared drop-write log queue; 429s are logged; log-flood by one tenant evicts others' entries |
| L-6 | Low | `WebhookDispatcher.cs:147,161`, `WebhookStore.cs:30` | Poison delivery (decrypt failure) is retried every 2 min forever; lease shorter than worst-case batch |
| L-7 | Low | `WebhookDispatcher.cs:123-141` | Dispatcher ignores client suspension; delivery `ClientId` never cross-checked against the endpoint's |
| L-8 | Low | `AuthController.cs:36,55,62` | `[Authorize]`-only endpoints are reachable by API-key principals (Potential) |
| L-9 | Low | `ApiKeyService.cs:83,99` | Active-key cap is check-then-insert (race) |
| I-1 | Info | multiple | Accepted/observational items (see below) |

### M-1 Rate limit and quota bypass / non-durability (Medium)
Evidence and paths:
1. A key may carry `RateLimitPerMinute` up to 100,000 (`ApiKey.Apply`, validators) and `ApiUsageLimiter.TryAcquireAsync` uses `credentialLimitPerMinute ?? perMinute` (line 62). The comment on the class says "a key may lower or raise its own limit". The client's plan-level `api.rateLimitPerMinute` (platform-managed) is therefore only a default: any Client Admin with `apikeys.manage` can raise their own per-minute ceiling 300x by editing a key. The only remaining brake is the daily quota.
2. All counters (`_minute`, `_day`) are in process memory. With N nodes the effective daily quota is N x configured; every restart/deploy/crash resets the day counter to zero. The daily quota is a commercial control (docs/03), so a client can exceed it by forcing restarts or simply by node count. Credit deduction still protects revenue (ledger), so impact is availability/fairness, not money.
3. JWT users are partitioned per user (`credential = user.ActorId ?? clientId`), so a client with K users and M keys gets (K+M) per-minute budgets; there is no per-client per-minute cap.
Fix: clamp a key's limit to `min(keyLimit, clientLimit)` (or require a platform permission to exceed); add a per-client minute window in addition to per-credential; persist/aggregate the daily counter (SQL `UPDATE ... WHERE Count < Quota` or a Redis counter) before multi-node deployment, or document single-node as a deployment constraint.

### M-2 Webhook dispatcher starvation and outbound amplification (Medium)
Evidence: a single `WebhookDispatcher` claims the globally oldest 50 deliveries and runs them with `MaxDegreeOfParallelism = 8` (lines 97-117). There is no per-endpoint or per-client concurrency/fairness limit. `SendTestAsync` (WebhookService:226) and `RetryAsync` (260; resets `Attempts = 0`) have no throttle and no cap on queued rows. `ConnectTimeout` is 5 s, request timeout up to 30 s (clamped), and the claim lease is 2 min.
Attack: a client creates up to 10 endpoints pointing at a public tarpit host (accepts TCP, never answers), then loops `POST .../test` (limited only by the 300/min per-IP global limiter and the 1000s of rows it can queue) and manual retries. Each delivery occupies a worker slot for 5-30 s; with 8 slots, deliveries of all other tenants (older `NextAttemptAt` first, but the attacker's rows are continuously due and re-queued) are delayed by minutes. Secondary: the platform becomes a signed-POST reflector against any third-party public host chosen by the attacker (every delivery is an outbound request, retried 8 times; manual retry resets the counter).
Impact: cross-tenant delivery latency (availability), unbounded `WebhookDeliveries` growth, abuse of the platform's egress IP reputation.
Fix: per-endpoint and per-client in-flight cap (e.g. group claimed rows by `EndpointId`, 1-2 concurrent per endpoint, fair round-robin across clients); rate limit `test`/`retry` (e.g. 5/min/endpoint) and cap pending rows per endpoint; make lease >= worst-case batch time (see L-6); circuit-break an endpoint that times out repeatedly (the 20-failure auto-disable exists but test events are excluded from it by design, which is exactly the abuse path).

### M-3 API key regeneration bypasses the creator-subset rule and the cap (Medium)
Evidence: `RegenerateAsync` copies `old.ScopeList` into the new key and returns the raw secret (lines 176-200) without calling `CheckScopesAsync`. The rule in docs/04 section 3 is "API-key scopes are a subset of the creator's permissions"; `Create` and `Update` enforce it, `Regenerate` does not. Also `CountActiveAsync >= MaxApiKeys` is not evaluated, and with `GraceMinutes > 0` (up to 7 days) the old key stays active, so each regeneration adds one live key.
Attack 1 (privilege escalation, needs a custom client role): user A holds `apikeys.manage` but only `faces.verify`; key K (created by an admin) has `faces.erase`. A calls regenerate on K and receives a fresh secret with `faces.erase` that A could not have granted. In the default role matrix only Client Admin has `apikeys.manage` and all face scopes, so it is exploitable only with custom roles - hence Medium, not High.
Attack 2 (cap bypass): regenerate with 7-day grace repeatedly; each call creates another active key beyond `limits.maxApiKeys` (each with its own rate budget, see M-1).
Fix: run `CheckScopesAsync(old.ScopeList)` in `RegenerateAsync` (return 403 if the actor lacks any scope), enforce the active-key cap counting the replacement (a grace regeneration needs +1 headroom), and use the key's `RowVersion` to serialise concurrent regenerations.

### M-4 Unsafe-webhook switch is guarded only for `Production` (Medium)
Evidence: `Program.cs:66` runs the switch check only `if (app.Environment.IsProduction())`. `AllowUnsafeTargets` (WebhookUrlGuard:60-62 and WebhookDispatcher:53) disables https enforcement and all private/loopback/metadata checks. A Staging, Test or custom-named production-like environment (common: `ASPNETCORE_ENVIRONMENT=Staging` with real data and cloud metadata at 169.254.169.254) that inherits the setting from a shared config template becomes a full SSRF primitive: a Client Admin registers `http://169.254.169.254/...`; the dispatcher POSTs to it (blind SSRF with status code returned in `LastStatusCode`, enough to probe internal services and reach metadata endpoints that accept POST/PUT).
The same pattern already existed for the other dev switches (`Jwt:AllowEphemeralKey` etc.), so the exposure is consistent with prior design but is higher-impact here.
Fix: invert the rule - refuse these switches unless `IsDevelopment()` (and an explicit test environment name used by the integration factory); log a startup error naming the key.

### L-1 Unbounded negative cache of unknown prefixes (Low)
`LoadAsync` stores `null` for every unknown prefix for 15 s in `IMemoryCache` (no `SizeLimit`; expiry scan is lazy, about every minute). The prefix space is 36^8, so each request with a random well-shaped key creates a new entry. The only brake is the global limiter (300/min/IP, /64 for IPv6). A botnet of ~10k IPs creates ~3M entries per minute (hundreds of MB) and also forces 3M DB lookups. Not demonstrated at scale (Potential).
Fix: do not cache negatives per prefix; use a small fixed-size negative cache/Bloom filter, or set `SizeLimit` on a dedicated cache instance; add a stricter per-IP limiter for requests that fail key authentication (docs/04 section 2 says failures are "throttled per IP" - only the global 300/min applies today).

### L-2 Node-local invalidation (Low, matches documented limit)
`Invalidate` removes only the local cache entry. On other nodes a revoked/edited/shortened key keeps working for up to 15 s (`CacheTtl`); the touch-write is also cached for 1 min but is informational. This is within the "known limits" philosophy (30-60 s) and shorter than most, so Low. Note that revocation reason codes (`API_KEY_REVOKED`) only appear after the secret is proven, so there is no enumeration oracle. Fix when scaling out: publish invalidation (Redis pub/sub or `RowVersion` check on the cached hit) or reduce TTL to 5 s.

### L-3 Corrupt client IP allow-list fails open (Low)
`ParseList` returns `[]` on `JsonException`/empty, and `IpRules.Allows([])` means "no restriction". If the `integration.allowedIps` setting row is ever malformed (manual DB edit, future validator regression), the account silently loses its IP restriction. Per-key rules fail closed (unparseable entries never match) which is the right behaviour. Fix: treat a parse failure of a present, non-empty value as deny-all and log.
Related note: `IpRules.IsValid`/`ValidateListItem` accept CIDRs with host bits set (`203.0.113.7/24`), but `System.Net.IPNetwork.TryParse` rejects them at match time, so such a rule silently never matches (fail-closed, but a confusing lock-out). Normalise or reject at validation.

### L-4 `WebhookUrlGuard.IsPublic` coverage gaps (Low)
Covered correctly: loopback, 0/8, RFC1918, CGNAT, link-local/metadata, TEST-NETs, benchmarking, multicast/reserved, IPv4-mapped (mapped to v4 first), fc00::/7, fe80::/10, site-local, 2001:db8::/32, 100::/64, NAT64 64:ff9b::/96. Decimal/octal/hex literals are normalised by `Uri` and the connect-time check re-validates the actual connected address; IDN uses `IdnHost`. DNS rebinding is closed because the check runs on the addresses actually connected (ConnectCallback) and redirects/proxy are off - this part passes.
Not covered: IPv6 6to4 `2002::/16` and Teredo `2001::/32` (embed arbitrary IPv4 including private ones; relevant only if the egress network routes them), IPv4-compatible `::a.b.c.d`, `::/8` generally, `2001::/23`, `192.88.99.0/24`, and ISATAP-style addresses. Exploitability depends on network routing of those transition prefixes (Potential). Fix: allow-list approach - accept only `2000::/3` for IPv6 minus the special ranges above, and block `192.88.99.0/24`.
Also: any destination port is allowed on public hosts (Info; consider restricting to 443/8443 to limit port scanning of third parties).

### L-5 Request-log queue fairness and flooding (Low)
All tenants share one 20,000-slot `DropWrite` channel. A tenant (or a leaked key) issuing requests at its own rate limit - including requests rejected with 429 (logged at ApiUsageMiddleware:44) - can fill the queue and cause other tenants' entries to be dropped silently (only a counter/warning is logged), weakening the usage/audit trail (repudiation, T10). Each row also costs DB storage for 90 days. The log content itself is safe: route template from the matched endpoint (not raw path), "unmatched" for 404s, no bodies/query strings, user-agent truncated to 200 chars and stored via parameters (no injection). Verified no `MarkupString` rendering of `UserAgent`.
Fix: per-client sub-queues or a cap per client per second, sample or aggregate 429 entries, and expose the drop count as a metric/alert.

### L-6 Poison deliveries and lease sizing (Low)
`DeliverAsync` catches only `HttpRequestException`/`TaskCanceledException`/`InvalidOperationException`. A `CryptographicException` from `DecryptAsync` (key rotation mistake, crypto-shredded client) escapes to the outer logger; `Attempts` is not incremented and the row's `NextAttemptAt` was set to now+2 min by the claim, so it is retried every 2 minutes forever and logs an error each time (log noise, wasted slots). Also the 2-minute lease can be shorter than the worst case for a batch (50 rows / 8 workers x up to 35 s = about 245 s), so a row can be claimed again by another node while still in flight, causing duplicate POSTs (at-least-once; receivers must de-duplicate on `X-Event-Id`, which is stable). Fix: mark such deliveries failed/abandoned, compute lease as `batchTimeout + margin`, and re-check `Status`/lease token before sending.

### L-7 Dispatcher tenant-awareness (Low)
The dispatcher runs entirely in platform scope (necessary) but loads the endpoint by `delivery.EndpointId` without asserting `endpoint.ClientId == delivery.ClientId`, and does not check client status, so a suspended client's endpoints still receive events (also true for `recognition.completed` staged before suspension). `WebhookPublisher.PublishAsync(clientId, ...)` trusts the `clientId` argument while `ListActiveForEventAsync` relies on the ambient tenant filter; if a future platform-scope job calls it, endpoints of every tenant would be written with one client's id and event data would cross tenants. Today the only caller (`FaceRecognitionService`) runs in the request's tenant scope, so no exploit exists now. Fix: filter `ListActiveForEventAsync` by `ClientId == clientId` explicitly, assert equality in the dispatcher, and skip suspended clients (reuse `IClientAccessGuard`).

### L-8 `[Authorize]`-only endpoints and API-key principals (Low, Potential)
`AuthController` `logout`, `change-password`, `me` carry plain `[Authorize]`, which an API-key principal satisfies. They resolve the user by `ActorId` (the key id), so `change-password`/`logout` should find no user, and `me` may return an error or an unexpected shape; I could not confirm behaviour without running the stack. Confirm with a test (key calling these returns 401/403 and never touches a user row) or require `ActorType == User` on them.

### L-9 Key cap race (Low)
`CountActiveAsync >= MaxApiKeys` then insert is not atomic; parallel creates can exceed the cap by the degree of parallelism. Cosmetic security impact; fix with a serialising update on the client row or a filtered unique/constraint approach.

### I-1 Informational and verified-OK items
- Key entropy/format: `nxv_live_` + 8 chars from a 36-letter alphabet via `RandomNumberGenerator.GetInt32` + 32 random bytes base64url (256-bit secret). The 8-char prefix is an identifier only (about 41 bits); the unique index on `KeyPrefix` exists. A prefix collision on create would surface as a unique-violation 500 rather than a retry (`PrefixExistsAsync` is unused) - probability negligible.
- Timing/enumeration: unknown prefix and wrong secret both run the same SHA-256 and `FixedTimeEquals` against a dummy hash and return the identical `API_KEY_INVALID`. Revoked/expired/IP-denied/suspended statuses are revealed only after the secret is proven. Cache hit vs miss does not distinguish existence because misses for known prefixes are cached identically. PASS.
- Policy-scheme selection: if `X-Api-Key` is present (even empty or invalid) the API-key scheme is chosen and a failure is final (no fallback to the bearer token); if both are valid the key wins. No escalation: key principals carry only face scopes, `PermissionAuthorizationHandler` grants API keys exactly their `scope` claims (and only client-scoped permissions), and `apikeys.*`/`webhooks.manage` are not assignable (`ApiKeyScopes.Assignable`). A key cannot create or edit keys. PASS.
- IP allow-list: uses `Connection.RemoteIpAddress` (never a raw `X-Forwarded-For`); forwarded headers are honoured only after `UseForwardedHeaders` with mandatory known proxies/networks, `/0` rejected, `ForwardLimit` 1; IPv4-mapped addresses are unmapped on both the request and rule side; IPv6 handled by `IPNetwork`. Both per-key and client-level lists must pass. Caveat: if the app is deployed behind a proxy without enabling forwarded headers, the proxy IP is what gets compared (fail-closed, but allow-lists will not work). PASS.
- Webhook signature: `t=<unix>,v1=HMAC-SHA256(secret, t + "." + body)`; the timestamp is regenerated on every attempt, so receivers can enforce a replay window (recommend documenting a 5-minute tolerance and de-duplication on `X-Event-Id`). The secret is 256-bit random, AES-GCM encrypted per client (AAD handled by `IClientEncryption`), shown once; rotation takes effect immediately with no overlap period (receivers will fail until updated; consider a dual-signature grace window). The test event is delivered even to a disabled endpoint only when explicitly requested for that endpoint.
- Delivery response handling: only the status code is stored (no body, no headers), so there is no response-exfiltration channel from SSRF; redirects not followed (3xx = failure); `UseProxy = false`; TLS validation not overridden; headers read only (body never buffered), so response-size DoS is not possible; payload contains no biometric or personal data (request id, operation, outcome, credits).
- Outbox claim concurrency: single statement `UPDATE ... OUTPUT` with `UPDLOCK, READPAST, ROWLOCK` and a lease; claims are exclusive across nodes. See L-6 for lease sizing.
- Tenant isolation: all new entities implement `ITenantOwned` and are read through tenant-filtered repositories; cross-tenant lookups return 404; background jobs run in explicit, named platform scopes; the usage limiter reads settings in the caller's own tenant scope. No `IgnoreQueryFilters`. Requires the integration suite (not run here) to confirm RLS coverage of the three new tables (`ApiKeys`, `WebhookEndpoints`, `WebhookDeliveries`, `ApiRequestLogs`) - confirm they are registered in the RLS policy installer.
- Fixed-window per-minute limiter allows up to 2x bursts across a window boundary (design trade-off).
- Webhook URLs may embed secrets in the query string and are shown in the DTO/audit log (`webhook.created` records `Url`); advise clients not to put credentials in the URL, or mask the query in audit.
- A key survives deactivation of its creator (by design for integrations); revocation is manual. Consider showing "created by" and offering a bulk-revoke when a user is deactivated.
- Swagger/OpenAPI only mapped in Development; CORS allow-list unchanged; dependency/secret scanning present in CI.

## Re-verification
First review of M6; nothing to re-verify. After fixes for M-1 to M-4, request re-review; I will re-check specifically: key regeneration with a restricted-role actor (M-3), the clamp of per-key limit and per-client minute cap (M-1), non-Development startup refusal for `Webhooks:AllowUnsafeTargets` (M-4), and per-endpoint concurrency/test-event throttling (M-2).

## Suggested security tests to add (not added in this pass)
1. `Regenerate` by a user lacking one of the key's scopes returns 403.
2. Setting `RateLimitPerMinute` above the client limit does not raise the effective limit.
3. Startup throws in Staging when `Webhooks:AllowUnsafeTargets=true`.
4. `IsPublic` theory rows: `2002:0a00:0001::1`, `2001:0:4136:e378:8000:63bf:3fff:fdd2`, `::10.0.0.1`, `192.88.99.1`.
5. API-key principal calling `/auth/change-password`, `/auth/me`, `/auth/logout` is rejected.
6. Poison delivery (undecryptable secret) is abandoned after N attempts.
