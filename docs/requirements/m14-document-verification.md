# M14 — Digital Verification Platform: implementation requirements (product owner)

Source of truth for module M14. Every agent working on M14 must read this file in full. Status and decisions are tracked in `docs/STATUS.md` and `docs/07-document-verification.md`.

## 1. Objective
Extend NexaVerify from a face-recognition SaaS into a modular identity and document verification platform.
The first release must implement:

1. Bank Statement Verification
2. PAN Verification
3. Passport Verification

All other document-verification modules must remain clearly marked as Upcoming until implemented and tested.
Preserve the existing Clean Architecture, multi-tenant isolation, licensing and credit ledger, authentication, authorization, API conventions, audit logging, and Blazor Server + MudBlazor UI.

## 2. Mandatory Repository Assessment
Before writing code:

* Read `docs/STATUS.md`, `docs/01-architecture.md`, `docs/02-database-design.md`, `docs/03-api-specification.md`, `docs/04-security-strategy.md`, `docs/05-agents-and-roadmap.md`, and `docs/06-ui-information-architecture.md`.
* Inspect the existing Domain, Contracts, Application, Infrastructure, API, Blazor, database migrations, test projects, and agent definitions.
* Identify existing reusable document-processing, file-storage, licensing, authorization, audit, and tenant-isolation capabilities.
* Reuse existing abstractions wherever appropriate. Do not duplicate existing functionality.
* Create an implementation plan with identified files, database changes, API contracts, UI changes, security risks, and test cases.
* Implement the changes in the existing solution. Do not merely produce sample code, mockup screens, or a design document.

## 3. Verification Module Architecture
Create a reusable verification architecture following the repository's dependency rules.
Introduce or extend appropriate abstractions such as:

* `IDocumentVerificationService`
* `IDocumentParser`
* `IOcrProvider`
* `IBankStatementParser`
* `IPanVerificationProvider`
* `IPassportVerificationProvider`
* `IDocumentStorageService`

Use these names only where they fit the existing codebase; avoid duplicate or unnecessary abstractions.
Each verification module must have its own application service, request and response contracts, validation, authorization requirements, provider integration, and unit tests.
Separate the following outcomes:

* Document uploaded successfully.
* Information extracted successfully.
* Format and business-rule checks completed.
* Document consistency checks completed.
* External authoritative verification completed.
* Verification failed or requires manual review.

Never report a document as authentic solely because OCR extraction or format validation succeeded.
External provider integrations must be configurable, securely authenticated, and replaceable. If credentials or an approved provider are unavailable, return an explicit status such as `ProviderNotConfigured` or `NotVerified`. Do not fabricate successful verification results.

## 4. Bank Statement Verification — Release 1
Implement an end-to-end bank statement verification workflow.

Input and processing
* Accept supported PDF and image formats using configurable file-size and page-count limits.
* Validate MIME type, file signature, extension, page count, and file size.
* Support scanned and digitally generated PDFs through appropriate parsing and OCR providers.
* Extract available account-holder name, bank name, account-number representation, statement period, opening balance, closing balance, transaction dates, descriptions, debits, credits, and available balances.
* Normalize extracted transactions into a consistent response model.
* Support configurable bank-specific parsing rules without hardcoding bank assumptions into controllers.
* Validate transaction ordering, statement periods, balance continuity, arithmetic consistency, and duplicate records where the necessary information is available.
* Detect extraction failures, incomplete statements, and suspicious inconsistencies. Treat these as review signals, not conclusive fraud findings.

Results — return a structured result containing:
* Verification request ID and status.
* Extracted account and statement metadata.
* Normalized transaction records.
* Validation checks and their individual outcomes.
* Warnings and review reasons.
* Provider verification outcome, if performed.
* Processing timestamps and correlation ID.

Do not log account numbers, transaction descriptions, uploaded documents, or other sensitive financial information.

## 5. PAN Verification — Release 1
* Validate the PAN format using configurable, jurisdiction-appropriate rules.
* Accept supported PAN card images and PDFs.
* Extract the PAN identifier, holder name, and other supported fields through OCR.
* Compare extracted values with the information supplied by the client, using normalized comparison rules.
* Return field-level match outcomes without exposing sensitive information unnecessarily.
* Support authoritative PAN verification through an approved, configurable provider.
* Handle invalid formats, unreadable documents, name mismatches, provider timeouts, unavailable providers, and inconclusive responses.
* Never claim that a PAN is active, genuine, or officially verified unless the relevant authoritative check actually establishes that outcome.

## 6. Passport Verification — Release 1
* Accept supported passport images and PDFs.
* Extract available passport number, holder name, nationality, date of birth, issue date, expiry date, and other supported fields.
* Parse and validate the machine-readable zone (MRZ) where present and supported.
* Validate dates, expiry, required fields, MRZ check digits, and consistency between available document fields.
* Compare extracted details with the information submitted by the client.
* Integrate an approved authoritative verification provider where available.
* Return field-level validation outcomes, warnings, and manual-review reasons.
* Do not claim that a passport is genuine or officially verified based only on its visual appearance, OCR output, or MRZ validity.

## 7. API Contracts and Security
Follow the existing API route, versioning, error-handling, and `Result<T>` conventions.
Expose endpoints for:
* Submitting a bank statement verification request.
* Submitting a PAN verification request.
* Submitting a passport verification request.
* Retrieving a verification result by request ID.
* Listing verification history with pagination and filters.
* Checking processing status where asynchronous processing is required.

Use the actual project route conventions and permission keys when implementing endpoints.
Requirements:
* Thin API controllers.
* FluentValidation for request validation.
* Cancellation tokens on asynchronous operations.
* Authentication, authorization, and tenant isolation.
* Configurable upload and processing limits.
* Idempotency for billable verification requests.
* Safe retry handling and protection against duplicate credit deductions.
* Rate limiting and appropriate abuse protection.
* Consistent ProblemDetails responses and stable error codes.
* Correlation IDs and security-conscious audit events.
* No sensitive document contents or PII in application logs.

Never accept `ClientId` from an untrusted request as the source of tenant identity.

## 8. Licensing and Credit Deduction
Integrate each verification module with the existing license and credit ledger.
* Define configurable credit costs per verification type and, where appropriate, per processing stage.
* Reuse the existing atomic, FEFO-based credit deduction mechanism.
* Ensure concurrent requests cannot overdraw a license.
* Prevent duplicate deductions on idempotent retries.
* Define explicitly whether failed processing, provider failures, or manual reviews consume credits.
* Persist the deduction and verification status transitions consistently.
* Ensure each billable operation is traceable to its tenant, verification request, and immutable ledger transaction.
* Never implement a separate balance-update mechanism that bypasses the existing ledger.

## 9. Database and Document Storage
Extend the existing EF Core model and migrations only after assessing existing entities.
Persist the minimum necessary information, including:
* Verification request and tenant ownership.
* Verification type and processing status.
* Extracted structured data, subject to data-minimization requirements.
* Field-level validation outcomes and provider result references.
* Credit transaction reference.
* Audit metadata, timestamps, and correlation information.

Requirements:
* Enforce tenant isolation using existing global query filters, save interceptors, and SQL Server Row-Level Security conventions.
* Apply restrictive access controls to sensitive identity and financial documents.
* Store uploaded documents using the existing secure storage abstraction, or introduce one if genuinely needed.
* Encrypt sensitive stored content, avoid public file URLs, and use short-lived authorized download links where required.
* Configure retention, deletion, and backup policies appropriate to sensitive documents.
* Never store uploaded documents in publicly accessible directories or commit real customer documents or secrets to source control.

## 10. Client Portal — MudBlazor
Add a professional, responsive Document Verification section to the client portal. Include:
* Bank Statement Verification.
* PAN Verification.
* Passport Verification.
* Verification History.
* Verification Details.

The upload experience must provide file selection, file validation, submission progress, clear status indicators, validation warnings, and actionable errors.
The verification history grid should include request ID, verification type, submission date, status, and available actions. Put secondary actions such as View Details and Download Report in the row action menu rather than creating unnecessary duplicate sidebar entries.
Verification details should display extracted fields, individual check outcomes, provider verification status, warnings, and credit usage where authorized.
Use existing layouts, reusable components, permission keys, design tokens, and navigation behavior. Follow the established Admin and Client portal patterns. Do not introduce JavaScript alerts or bypass the API by accessing Infrastructure or EF Core directly from Blazor.

## 11. Platform Admin
Extend the administration interface to support:
* Verification module availability and configuration status.
* Configurable credit costs per verification type.
* Provider configuration status without revealing secrets.
* Verification volume, success/failure counts, provider errors, and credit consumption.
* Tenant-scoped operational investigation with appropriate permissions.
* Audit history for sensitive administrative changes.

Keep provider credentials in secure configuration or a secrets manager. Never display raw secrets in the UI or logs.

## 12. Upcoming Modules
Prepare the navigation and module catalogue for future capabilities, including:
* Aadhaar Verification.
* Driving Licence Verification.
* Voter ID Verification.
* DigiLocker Integration.
* Additional Government Document Verification.
* Additional KYC and fraud-detection capabilities.

These modules must display an Upcoming status, be non-operational, and must not deduct credits or imply that verification is available. Do not implement fake successful workflows or placeholder endpoints that claim to perform real verification.
Make it possible to add future verification modules through the established module/provider architecture without rewriting licensing, authentication, tenant isolation, or audit infrastructure.

## 13. Agent Workflow
Use the existing `.claude/agents/` roster and `docs/05-agents-and-roadmap.md` delivery pipeline.
1. Architect: review existing design and define module boundaries.
2. Database: design schema, tenant isolation, and migrations.
3. Backend: implement contracts, services, provider abstractions, API endpoints, and licensing integration.
4. UI: implement client and admin portal screens.
5. Security: assess file uploads, PII handling, authorization, provider integration, and tenant isolation.
6. QA: add and execute unit, integration, API, and UI tests.
7. Code Review: report findings without modifying product code.
8. DevOps: document configuration, secrets, deployment, migrations, and operational requirements.

Keep existing agent restrictions. Coordinate dependencies and do not allow parallel agents to modify the same files without an agreed ownership plan.

## 14. Testing and Definition of Done
Add tests covering:
* Valid and invalid requests for all three modules.
* OCR success, unreadable documents, and malformed files.
* Bank statement arithmetic and transaction consistency.
* PAN format checks and field mismatches.
* Passport expiry, MRZ validation, and field mismatches.
* Provider success, failure, timeout, and unconfigured states.
* Authorization failures and cross-tenant access attempts.
* File-upload security and resource limits.
* Concurrent credit deduction and idempotent retries.
* Verification status transitions and audit behavior.
* API contracts, UI validation, navigation, and responsive layouts.

Use deterministic fake providers for tests, clearly separated from production integrations. Integration tests must use the repository's established test infrastructure.
Before marking a module complete:
* Restore and build the solution in Release configuration.
* Run relevant unit, integration, architecture, and security tests.
* Validate EF model and migration consistency.
* Update `docs/STATUS.md`, API documentation, database documentation, security documentation, and the roadmap.
* Document provider credentials and external dependencies that remain outstanding.
* Report completed functionality, test results, unresolved issues, and anything that requires external provider access.

Do not mark a module fully verified or production-ready while its required provider integration, security checks, or acceptance tests remain incomplete.
