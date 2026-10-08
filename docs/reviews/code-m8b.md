# Code review: M8b (client portal screens)

Reviewer: Code Review Agent. Date: 2026-10-08. Commit range: M8b (`d9e7bfa`..`287c911`).

**Verdict: CHANGES-REQUESTED** (0 Blocker, 4 Major, 9 Minor, 4 Consider)

Run results:
- `dotnet build NexaVerify.slnx -warnaserror`: succeeded, 0 warnings, 0 errors.
- `Web.ComponentTests`: 394/394 passed.
- `Architecture.Tests`: 12/12 passed.
- Not run (no Docker): `PortalBffEndToEndTests` and the other integration tests. That file was reviewed by reading only.

M8a findings are not repeated. Where M8b copied an M8a anti-pattern this is stated explicitly (M8a-copy).

## Major

| # | Where | Finding | Suggested change |
|---|-------|---------|------------------|
| M1 | `Services/ApiDocsContent.cs` (idempotency/limits prose in `Pages/Client/ApiDocs.razor:~92-104`; `Meaning()` for `FaceProviderUnavailable`) | The guide documents API behaviour that does not exist. (a) `X-RateLimit-Limit/Remaining/Reset`: nothing in `src/` emits them. Only `Retry-After` is set (`ApiUsageMiddleware.cs:40`, `ServiceCollectionExtensions.cs:104`). (b) `Idempotent-Replayed: true`: no such header is written anywhere. (c) "within 24 hours": no 24 h window exists. Replay is a unique index on the request row (`FaceRecognitionService`, `LicenseMeteringService`). (d) `FACE_PROVIDER_UNAVAILABLE` text says "retry after the Retry-After delay", but 503 from `ErrorType.Unavailable` gets no `Retry-After`. Integrators will code against headers that never arrive. Verified correct: routes, `X-Api-Key`, `Idempotency-Key` (max 100), multipart field `image`, scopes `faces.*`, `X-Signature t=,v1=` HMAC over `t.body`, event names, 5 MB limit, topK 1-20, exactly-one of profileId/externalRef. | Delete (a)-(c) or implement them in the API first. Say "a repeated key returns the original result and is not charged again". Drop the Retry-After promise for 503. Add a test that fails when a header name in the guide is not present in `src/Api` (see T1). |
| M2 | `Components/NotificationBell.razor:63-77` with `Security/TokenRefresh.cs:~160` (`store.TouchAsync`) | The 60 s poll runs on every circuit and every API call touches the session (`TouchAsync` slides the 30-minute idle window, `SessionOptions.IdleTimeoutMinutes`). A tab left open in the client portal therefore never idles out. The idle timeout is defeated by a background call the user did not make. Also, once the session ends the loop keeps polling every minute, each time getting SESSION_EXPIRED. | Poll with a flag (e.g. `ApiCallOptions.Background`) that skips `TouchAsync`. Stop the loop when the result is `SessionExpiredCode`. Add a test: after `IdleTimeout` of polling only, the session is gone. |
| M3 | `Pages/Client/Enroll.razor:72-96`, `Verify.razor`, `Identify.razor` (the `finally` block), `ClientForms.cs:30` (`NewIdempotencyKey`) | Credit-charging calls get a fresh key per click, and the photo is wiped in `finally` even on failure. If a request times out or the gateway returns `API_UNAVAILABLE` after the server committed the charge, the user must re-pick the photo and resubmit with a new key, so the same check can be charged twice. The comment "the same click can never be charged twice" is only true for double-clicks. Test `Every_submit_gets_a_fresh_idempotency_key` locks this in. | Keep the key (and the photo) while the last attempt failed with a transient error (`API_UNAVAILABLE`, 5xx, 429). Mint a new key only after success or after the user changes the photo or fields. Wipe the bytes on success or Remove. |
| M4 | M8a-copy: all 17 pages under `Pages/Client/*`, `Services/Api/ClientApiClients.cs`, `NotificationBell.razor:79` | No `CancellationToken` on any page call (0 occurrences in `Pages/Client` except the dashboard). Every `Api.*Async(...)` is called without a token. Navigating away mid-upload (up to 5 MB multipart plus the 401-replay buffer) keeps the work running. `OnParametersSetAsync` in `ProfileDetail`/`WebhookDetail` can race and show a stale profile when navigating between ids. The API clients accept `ct` but nothing supplies it. | Add a small base or helper (`CircuitCancellation` scoped per component, cancelled in Dispose) and pass its token. Guard `LoadAsync` with a request counter so only the latest response is applied. Same fix as M8a. |

## Minor

| # | Where | Finding | Suggested change |
|---|-------|---------|------------------|
| m1 | `Pages/Client/ApiKeys.razor:60-89,92-129`, `Webhooks.razor:77-98`, `WebhookDetail.razor:~118` | Repeated dialog choreography (M8a-copy): `string? x = null; ShowFormAsync(... capture secret ...); if (saved) { RevealAsync(...); x = null; ReloadAsync(); }` appears 4 times. `raw = null;` / `secret = null;` after the reveal does nothing (strings are immutable and the closure is gone next line), so it is dead code that implies a security property it does not give. | `ShowSecretFormAsync<TForm>(title, form, fields, submit, revealTitle, revealText)` in `DialogExtensions` returning the saved model. Remove the null-assignments. |
| m2 | `ClientForms.cs:10-15,22,29,44`, `FacePhotoInput.razor:21`, `FaceCapture.razor` | The 5 MB limit exists in 3 places (`FaceLimits.MaxImageBytes`, `FacePhotoInput.MaxBytes`, `FaceCapture.MaxBytes`) plus the literal "5 MB" in message strings. `PhotoGuard` says "JPEG or PNG" while the rest (and the API) accept WebP. `ApiKeyForm` hard-codes 50 IPs, 100 000 rpm, 7 days, 100/500 lengths; `WebhookForm`/`InviteUserForm` duplicate `StringLength` values and the role names "ClientUser"/"ClientAdmin" (`Users.razor`). | One `FaceLimits` constant used as the parameter default and formatted into messages. Share allowed types between `FaceCapture` and `PhotoGuard`. Reference the role constants from Contracts. |
| m3 | `ClientForms.cs:249,296,313` | Three private `Blank()` implementations and the inline `string.IsNullOrWhiteSpace(x) ? null : x.Trim()` in `InviteUserForm`. | One `Strings.NullIfBlank(this string?)` extension. |
| m4 | `ApiLogs.razor`, `History.razor` | Copy-pasted filter machinery: `Applied` record, `Apply()`, `Clear()`, `Utc()`, `_version++` remount, date-to-end-of-day. The existing `FilterBar` component is not used. `ToString("d MMM yyyy, HH:mm", InvariantCulture)` is repeated about 20 times across the pages. | Extract `DateRangeFilter` or use `FilterBar`. Add `UiText.DateTime/Date` helpers. |
| m5 | `ClientApiClients.cs:GetKeyUsageAsync` | Fetches the full client dashboard (`client/dashboard?days=`) to read `TopApiKeys`; the API Keys page pays for the whole dashboard query on every load/reload and is silently 0 for users without dashboard permission. | Dedicated `client/api-keys/usage` endpoint, or accept and document the cost. |
| m6 | `FaceCapture.razor` `Accept`, `OnFileAsync`; `TokenRefresh.cs` body buffering | Memory for a 5 MB photo: stream, `MemoryStream`, `ToArray()` (2 copies), base64 data-URL string in the component (~6.7 MB UTF-16, ~13 MB) pushed through the SignalR render diff, then the multipart `ByteArrayContent`, then another `ReadAsByteArrayAsync` copy in `TokenRefresh`. Peak is about 5 copies per user. `Array.Clear` only wipes `image.Data`; the other copies live until GC, so "the portal never keeps images" is overstated. | Resize or downscale the preview client-side (or use an object URL in JS), `buffer.GetBuffer()`/exact-size array to avoid `ToArray`, and soften the wording. Consider a per-circuit upload cap. |
| m7 | `Pages/Client/Enroll.razor:81` | After a successful enrol an extra `GetProfileAsync` is made only to show the template count (second round trip, and silently null on failure). | Return the count in `EnrollFaceResponse` or drop the line. |
| m8 | `ClientForms.cs:ApiKeyForm.EndOfDayUtc`, `ApiKeys.razor` "Expires" column | Expiry date picked in the user's calendar is turned into 23:59:59 UTC and displayed as the UTC date. No time-zone handling (the company has `TimeZone` in its profile); an expiry can be hours off from what the user expects. The pages label times "(UTC)", others do not (`ApiKeys` Last used). | Label consistently, or convert with the company time zone. |
| m9 | A11y (`FaceCapture.razor:41`, `ChargeSummary.razor`, `NotificationBell.razor`) | The upload control is `<InputFile class="nv-sr">` inside a styled `<label>`: confirm a visible focus ring appears on the label (`:focus-within`), otherwise keyboard users cannot see focus. `ChargeSummary` reuses the chart class `nv-bars` with inline `display:block` overrides. The bell list is not refreshed after "Mark as read" (the badge is, the menu keeps bold titles until the next poll). `OnStateChanged` discards the `InvokeAsync` task. | Add `.label:focus-within` style; give `ChargeSummary` its own class; reload `_latest` on `State.Changed`; observe the task. |

## Consider

- C1: `PollAsync` only catches cancellation and disposed exceptions; any other exception faults `_loop`, which `DisposeAsync` then rethrows during circuit teardown. Catch-all with a log at Warning.
- C2: `ApiDocsContent.Endpoints` covers four endpoints; `detect`, `profiles`, `requests` are missing (the guide says nothing about paging or listing). Fine for now, record as a gap.
- C3: `Notifications.razor` and the bell show "When (UTC)" in absolute and relative forms inconsistently.
- C4: `UsageCsvCard` takes "today" from UTC while pickers are local calendar dates (up to a day off near midnight in the user's zone).

## Duplication report

| Pattern | Occurrences | Proposed home |
|---|---|---|
| Secret-reveal dialog flow | ApiKeys x2, Webhooks, WebhookDetail | `DialogExtensions.ShowSecretFormAsync` |
| Photo submit flow (guard, busy, stopwatch, result/error, `finally` wipe) | Enroll, Verify, Identify | `FaceSubmitBase` or a `FaceCheckRunner` service |
| Filter bar and range-to-UTC | ApiLogs, History (and admin pages from M8a) | `DateRangeFilter` component |
| Date formatting strings | about 20 | `UiText.DateTime` |
| `Blank()` helpers | 4 | one extension |
| 5 MB / allowed image types | 3 and 2 | `FaceLimits` |
| Result card (`aria-live` wrapper plus `ChargeSummary`) | Verify, Identify, Enroll | `FaceResultCard` slot component |

## Tests

| # | Finding |
|---|---------|
| T1 | `ApiDocsPageTests` only checks that strings are present (`"Retry-After"`, route names) and that `ErrorCodeList` equals the same `ErrorCodes` it is built from by reflection (tautology). Nothing compares the guide to the real API, which is how M1 slipped through. Add an `Api.IntegrationTests` check (routes from `EndpointDataSource`, header names from a response) or at least a test that each documented header is asserted in an API test. |
| T2 | `ClientAccountPagesTests.cs:63` uses `Thread.Sleep(100)` for a negative assertion ("polling stops"). Prefer awaiting the loop task (expose via `FakeTimeProvider` + `IAsyncDisposable`) or assert on `Notifications.Calls` after `Clock.Advance` and a `WaitForState` bounded wait. The rest of the file uses `WaitForAssertion` and `FakeTimeProvider`, which is deterministic. |
| T3 | `Every_submit_gets_a_fresh_idempotency_key` and `An_api_refusal_..._the_photo_is_still_dropped` assert the behaviour flagged in M3; update when M3 is fixed. |
| T4 | No test for the idle-timeout interaction (M2), cancellation on dispose (M4), or the failed-then-retry path with the same key. |
| T5 | Positives: the 401-replay of a multipart body is covered (`An_upload_is_replayed_with_its_body_and_key_after_a_token_refresh`); permission gating, markup inertness, secret shown once, and BFF E2E "no credential reaches the browser" are meaningful. The E2E test goes through typed clients rather than the Blazor circuit, so UI-to-API wiring is only covered by the fakes. |

## Positive notes

- Circuit lifetime in the bell is handled correctly: `PeriodicTimer` created with the injected `TimeProvider`, event handler removed in `DisposeAsync`, CTS cancelled, loop awaited.
- `InputFile` stream is disposed with `await using` and bounded by `OpenReadStream(MaxBytes)`.
- Authorization on pages matches the API permission for each controller (API logs use `apikeys.read`, as the API does); nav uses the same permissions; "Implemented: false" flags were removed for every page that now exists. Nav labels differ from page titles by design ("Register" / "Register a person").
- Code samples are rendered as text only; no real keys or hosts.
- Dependency rule and architecture tests are intact (no Infrastructure/EF types in Web).
