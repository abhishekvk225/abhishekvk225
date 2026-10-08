# Code review M7 (Usage & Dashboards)

Scope: merge 727f801 on top of M6. Verdict: **CHANGES-REQUESTED** (0 Blocker, 3 Major, 11 Minor).

Verification run: `dotnet build -warnaserror` is clean (0 warnings). Unit suites pass: Application 145, Domain 41, Infrastructure.UnitTests 55, Architecture 12. Not run (needs Docker/SQL Server): all Api.IntegrationTests, so EF-to-SQL translation (group keys such as `ExpiresAt <= now`, `CreatedAt.Date`, `LongCount(predicate)`) and index usage are unverified.

Overall: well layered (Application owns rules and interfaces; Infrastructure holds projection-only GROUP BY queries; controllers are thin). Aggregation happens in SQL, with no N+1 in the dashboards. Ledger verification is keyset-paged and has a single verifier implementation. The findings below are mainly multi-node behaviour, the alert job's repeated work, and test determinism.

## Major

| # | Where | Finding | Suggested change |
|---|-------|---------|------------------|
| M1 | `LedgerVerificationJob.cs:36-47`, `LedgerVerification.cs` `LedgerVerificationGate` (per-process `SemaphoreSlim`) | Multi-node: the gate is per process, but the job is a hosted service on every node. Each node runs a full platform ledger scan every `IntervalHours`, which duplicates load and DB reads. A real break yields N Critical logs and N duplicate `ledger.verification_failed` audit rows per run. The class doc says "they never overlap", which holds only on one node. The same break is also re-audited every night. | Take a DB-backed lease before running (e.g. `sp_getapplock` with a session-scoped lock, or a `JobLeases` row with expiry), and make the gate the same lease. Skip the run, with an Information log, when it is held. Optionally dedupe the audit by (license, first broken entry) so only new breaks are audited. Check how `FaceRetentionSweeper` and `LicenseExpirySweeper` do this and reuse it. |
| M2 | `LicenseAlerts.cs` `RaiseDueAsync` (`ExistsAsync` per due alert), `LicenseAlertProcessor.RunOnceAsync` | N+1 repeated forever. `ListLicensesAsync` returns every license still in a window (low balance, within 7 days, or expired within 3 days). All of them are re-listed hourly and each gets one `ExistsAsync` round trip per due alert (plus the unique-index race handling), and a new DI scope per client. For 10k low-balance licenses that is about 10k queries per hour per node, with all candidates also held in memory (`licenses`/`keys` lists). The "never scans every license" claim is only partly true. | Batch the existence check per client: `SELECT SubjectId, AlertType, Bucket FROM LicenseAlerts WHERE SubjectId IN (@ids)`, then filter in memory. Better, anti-join already-alerted buckets in the candidate query, or process page by page instead of materialising everything. Keep the unique index as the race backstop. |
| M3 | `LicenseAlertTests.cs:61-64,180`, `DashboardTests.cs:166`, `UsageReportTests.cs:16` | Wall-clock and midnight flakiness. `Today` in UsageReportTests is a `static readonly` captured at class load, while the server computes "today" per request from `TimeProvider`. Any test run that spans 00:00 UTC (or a class loaded just before midnight) fails the CSV equality assertions. Dashboards bucket by UTC date, so seeded "now" rows can land in a different day than the assertions expect. `n.CreatedAt > DateTime.UtcNow.AddMinutes(-5)` and `licenseEnds: UtcNow.AddDays(10)` depend on real time and on 7-day / 30-day thresholds being far from the boundary. | Use a fake `TimeProvider` in the test host (the services already take `TimeProvider`) and pin it mid-day; compute `Today` from the same fake. Seed with explicit dates instead of `UtcNow` offsets. |

## Minor

| # | Where | Finding | Suggested change |
|---|-------|---------|------------------|
| m1 | `CsvStreamResult.cs:30-36` | Error handling mid-stream. `await using` disposes the `StreamWriter` in the failure path too, which flushes the buffered header line. If the first query fails, the response starts as 200 with only the header row and the connection is then aborted, so the client never gets a 500 problem document. For a later failure the truncation is detected only by the aborted connection, and nothing is logged here. | Wrap in try/catch. On exception, do not flush on dispose (set `AutoFlush=false` and skip dispose, or write to the body via `PipeWriter`). Log at Error with the correlation id and rethrow. Optionally prime the first query before sending headers. |
| m2 | `CsvStreamResult.cs:26` | UTF-8 without BOM: Excel opens non-ASCII client names as mojibake. This is a known choice for CSV, but the exports target spreadsheets (the CSV-injection logic says so). | Emit a BOM, or document it and test it. |
| m3 | `UsageReports.cs` `RecordExportAsync` | The audit entry "report.exported" is committed before any data is produced, so a failed or aborted stream is still recorded as exported. Acceptable for an access audit but should be stated. | Say so in the XML doc, or add `Outcome`. |
| m4 | `Infrastructure/.../DashboardQueries.cs` `StreamClientUsageAsync` / `StreamAdminUsageAsync` | The reader (and the connection with RLS session context) stays open for the whole download. The client export yields at most about 92 x ops x outcomes rows, so streaming adds nothing there. The admin export is days x clients x ops, which can be large, and a slow consumer pins a pooled connection. | Fetch in keyset pages (day, client code, op) of N rows and release the connection between pages, or buffer when the range is small. |
| m5 | `DashboardQueries.cs` (Infra) `GetTopApiKeysAsync` | The signature has no `clientId` while the sibling methods do, so correctness relies on the tenant query filter. In platform scope it would silently return all tenants' keys. | Pass `clientId` and filter explicitly (defence in depth) or document it as tenant-only in the interface. |
| m6 | `DashboardQueries.cs` `GetTopClientsAsync` | Top clients rank by Consume only (gross), while `CreditsPerDay` and the admin export report refunds and net. The two views can disagree on what "credits consumed" means. | Subtract refunds, or label the field "gross". |
| m7 | Low-balance rule in 4 places | The "low balance" expression and its percent are duplicated: `LicenseAlertRules.Evaluate`, `LicenseAlertCandidates.ListLicensesAsync`, `DashboardQueries.GetLowBalanceLicensesAsync`, plus `DashboardMath.PercentRemaining`. There are also two independent options: `DashboardOptions.LowBalancePercent` and `LicenseAlertOptions.LowBalancePercent` (defaults agree today, config can drift, so the dashboard and the notifications disagree). The same applies to `ExpiringWithinDays` vs the notice days. | One shared expression (a `LicenseSpecs.LowBalance(percent)` expression) and one option source (dashboards read the alert thresholds, or a shared `LicenseThresholdOptions`). |
| m8 | `DashboardServices.cs` `DashboardWindow.ResolveDays` vs `DashboardQueryValidator` | The range is validated twice (validator and service), with slightly different messages, and with `IsSuccess` / `IsFailure` styles mixed (`UsageReports.cs` uses `IsFailure`). | Keep the service as the single check (it protects direct callers) and have the validator call it, as `UsageReportQueryValidator` does with `ReportRange`. |
| m9 | File naming | `Application/Dashboards/DashboardQueries.cs` holds `IDashboardQueries` and the row records. The Infrastructure class has the same file name, so two files are named alike. `UsageReports.cs` bundles `CsvFormat`, `ReportRange`, two validators and the service. Several `Microsoft.Extensions.Options.IOptions` fully-qualified uses in `LedgerVerification.cs` and `LicenseAlerts.cs` instead of a using. | Rename to `IDashboardQueries.cs` / `DashboardQueryRows.cs`, move `CsvFormat` to `Common/`, add the `using`. |
| m10 | `LedgerVerificationOptions` / `VerifyLedger` endpoint | The on-demand endpoint runs a platform-wide scan inside one HTTP request. A gateway timeout is likely at scale, and the client abort cancels the work with no report. The 409 is per node only (see M1). | Return 202 and a run id, or cap/paginate by client for the interactive path, and keep the full scan for the job. |
| m11 | Indexes (migration `AddUsageDashboards`) | Two wide covering indexes (`IX_LicenseTransactions_ClientId_CreatedAt` and `IX_LicenseTransactions_CreatedAt`, each including Type/Credits/Operation) are added to the append-only billing table. They speed up the dashboards but add write amplification on the hot charge commit path. The two indexes overlap heavily. | Measure the charge latency impact. The platform-scope admin dashboard needs only the CreatedAt one; consider a filtered or columnstore alternative, or move reporting to a rollup table. Also confirm `IX_ApiRequestLogs_CreatedAt` / `_ClientId_CreatedAt` includes are worth it versus the log writer throughput. |

## Other observations (no action required)

- `Percentile` returns the bucket upper edge, so a capped value reports `cap + bucket` (10,025 ms). This is a documented over-estimate but exceeds the cap; consider reporting `cap` for the last bucket.
- `DashboardDto.To` is exclusive (a `DateTime`) but reads as inclusive in the UI; consider a `DateOnly` last day.
- `GetWebhookHealthAsync` issues five sequential counts; they could be one query with conditional aggregation.
- `ExpiringThresholds()` plus the startup `Validate` (final < first) is good. The `Range` attributes are good.
- Ledger verifier: a single shared implementation, recheck only for balance-only mismatches, `GetHeadAsync` read after the ledger: sound reasoning. Cancellation flows through; `Task.Delay` is cancellable.
- Alert flow: one transaction for alert + webhook staging, `UniqueConstraintViolationException` plus `ClearTracked` handles the multi-node race correctly (the dedupe is safe across nodes; only efficiency is the problem, see M2).
- Security: `CsvFormat.Text` neutralises formula prefixes, including after leading whitespace; quoting uses SearchValues. Tests cover it.

## Duplication report

1. Low-balance predicate and percent: 4 locations, 2 option classes (m7).
2. Day-window validation: validator + service (m8).
3. `clientId`-optional query building (`Where(ClientId == id)`) repeated 3 times in `DashboardQueries`; extract a small `ForClient(query, clientId)` helper.
4. Join-with-client attention queries (`GetExpiring` / `GetLowBalance`) share the count + page pattern; extract a private helper.
5. Both `Stream*` methods use the same `await foreach … yield return` wrapper; return `rows.AsAsyncEnumerable()` directly (the `EnumeratorCancellation` plumbing is then unnecessary if the token is applied by the caller via `WithCancellation`).
6. Job scaffolding (initial delay, `PeriodicTimer`, enabled flag, cancel catch) is now repeated in `LedgerVerificationJob` and `LicenseAlertJob` (and the other sweepers): candidate for a small `PeriodicJob` base class with the lease from M1.

## Refactor suggestions

- Introduce a `JobLease` abstraction (DB applock), used by the ledger job, alert job and sweepers.
- Replace per-alert `ExistsAsync` with a set-based check (M2).
- Fake `TimeProvider` in the integration host (M3).
- Share the license threshold specs between rules, candidate query and dashboard queries (m7).

## Re-review

First pass only; no fixes have been reviewed yet.
