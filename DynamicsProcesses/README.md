# Validation Services — Automated Nonprofit Validation Pipeline

This document describes the **`validationservices`** execution path of the `DynamicsProcesses` console
application — the most complex of the several independent paths that branch out of the program's single
entry point. It explains how the pipeline **handles, scores, orchestrates, routes/channels, and completes
Validation Request Cases** in Microsoft Dynamics 365 / Dataverse.

> The application is a .NET Framework 4.7.2 batch console app that connects to Dataverse with a client-secret
> service principal and is scheduled to run repeatedly. Secrets, environment URLs, and API keys are supplied
> through `App.config` / environment variables at runtime and are **not** part of this documentation.

---

## 1. What This Pipeline Does

- A **Validation Request Case** is a Dynamics `incident` of `casetypecode = 5` that represents a nonprofit
  organization asking to have its legal/charitable status verified so it can receive TechSoup benefits.
- The pipeline automatically decides whether each request should be **Qualified**, **Disqualified**,
  **routed to a human queue**, **emailed for more information**, or **flagged for fraud review** — replacing
  manual analyst triage with a config-driven, evidence-based scoring engine.
- It draws evidence from TechSoup's **CTP** trust platform, **IRS** tax-exempt data, the organization's
  **website**, **domain WHOIS / DNS**, **IP geolocation**, **email-domain** reputation, and existing
  **Dynamics account** matches, then writes a full audit trail of its reasoning back onto the case as notes.

---

## 2. Entry Point & Path Selection

- **`DynamicsInterface.Main(string[] args)`** is the program's only entry point. `args[0]` selects the path
  through a `switch`: `orgqualifiedemail`, `retryonyxupdate`, `automatedvalidation`, **`validationservices`**,
  `fraudreview`, `irsrevocation`, `getctporgids`.
- For `validationservices`, `Main` sets up the Dataverse client (`getDynamicsClient`, client-secret auth),
  initializes the Azure Table semaphore client, resolves the current environment (dev/qa/stage/prod), and calls
  **`DynamicsProcessesValidationServices.processValidationServices()`**.
- Optional CLI arguments refine the run: `args[1]` overrides the queue name CSV (default `ValidationServices`),
  and special tokens trigger maintenance sub-routines instead of the main loop:
  - `valservicesoutreachresponded` → `getCustomerHasRespondedEmailOutreach()`
  - `emailoutreachabandoned` → `setOldCasesInOutreachQueueToAbandoned()`
  - `emailoutreachresponded` → the US-NonC3 variant `ValidationServicesUSNonC3.getCustomerHasRespondedEmailOutreach()`

---

## 3. Execution Model & Concurrency

- **Queue-driven**: the pipeline reads Dynamics **queues** by name, then iterates their `queueitem` rows
  (oldest first) — each pointing at a Validation Request Case.
- **Distributed semaphore** (`ParallelProcessesHelper`): before processing a case, the worker acquires a lock
  keyed `CaseId_<guid>_<env>` in an Azure **Table Storage** "semaphores" table
  (`tryAcquireSemaphoreAsync` → process → `releaseSemaphoreAsync`). This lets multiple concurrent runs safely
  share the queue without double-processing a case. Locks use ETag optimistic concurrency with retry/jitter;
  `cleanUpExpiredSemaphores` removes stale locks (default 2-hour expiry).
- **Resilience**: every stage is wrapped in try/catch with structured logging to rolling log files
  (`DynamicsInterface.writeToLog`); a per-case `errorStack` accumulates errors without aborting the batch.

---

## 4. Configuration Model (Layered Overrides)

- The behavior of every case is governed by a JSON config (`AutomatedValDefinition`) loaded from Dynamics at
  start-up; the run aborts early if `isAutomatedValidationActive` is false.
- **`getValidationConfigParameters()`** resolves the effective settings for each case using a precedence chain
  so a single engine can serve many programs/countries/clients:
  - **Base** `validationServices` → **country** override (`countries[country]`) → **customer** override
    (`customers[name]`) → **customer-country** override → **US-NonC3** override (`orgDesignations[us]`).
- Resolved parameters (stored in `ConfigParams`) include, among others:
  - `autoCloseEnabled`, `initialQueue`, `postAutoCloseQueue`, `postNoAutoCloseQueue`, `duplicateReviewQueue`
  - `fraudReviewEnabled`, `fraudReviewQueue`, `maxDispositionRetrievalAttempts`
  - Email-outreach block: `autoOrgOutreachEnabled`, `outreachEmailTemplate`, `outreachSenderMailboxQueue`,
    `outreachQueueName`, `outreachQueueHighPriority`, `skipEmailOutreachIfArtifactPresent`, `artifactWaitTimeMinutes`
  - `closeAbandonedRequests` + `numberOfDaysInQueueToCloseAbandoned`, `includeCtpOrgMatch`, `targetedValidations`
  - NonC3 flags: `isNonC3Validation`, `nonC3Designations`, `nonC3AutomatedValDefinition`

---

## 5. The Orchestration Pipeline (Handling a Case)

The call chain that processes one case is:

```
processValidationServices()        // loop queues → queue items, acquire semaphore
  └─ processValidationRequest()     // load case + requesting account, read DispositionRequest note
       └─ determineProcessBehavior() // NonC3 fork? approval gate? run scoring?
            ├─ getProcessingApproval()              // throttle / segment timing
            ├─ ValidationServicesHelper.getValidationScoreMatrix()  // SCORING ENGINE
            └─ determineAction()     // route / complete based on the disposition
```

- **`processValidationRequest`** loads the `incident` and its customer `account` (the "validation requestor"),
  reads the **DispositionRequest** JSON embedded in a case note
  (`ValidationServicesHelper.getDispositionRequestFromCaseNote`), resolves config, and calls the behavior router.
- **`determineProcessBehavior`**:
  - If the request is a **US Non-501(c)(3)** designation for TechSoup Global, it forks to the dedicated
    `ValidationServicesUSNonC3` path (see §9) and returns.
  - Otherwise it checks whether the case already carries a **`--- Disposition Details ---`** system note.
    If not, it calls `getProcessingApproval` and (if approved) runs the **score matrix** to obtain one.
  - Once a disposition exists, it calls **`determineAction`** to route/complete the case.
- **`getProcessingApproval`** is the **throttle**: it enforces a maximum number of status checks
  (`config.maximumCount`) and a time interval that widens by "segment" as a case is retried more often — so the
  engine politely re-polls slow external dispositions instead of hammering them.

---

## 6. The Scoring Engine — `getValidationScoreMatrix`

`ValidationServicesHelper.getValidationScoreMatrix(...)` is the heart of the verdict. It:

- **Fetches the disposition** for the transaction from the CTP score-matrix service. If `score_matrix_status`
  is not `completed`, it increments `ts_validationstatuscheckscount`, stamps `ts_validationrequestlaststatuscheck`,
  and after `maxDispositionRetrievalAttempts` routes the case to manual review (status **104697**) — otherwise it
  returns and retries on a later run.
- **Evaluates five independent signals**, persisting each onto `ts_validation*` case fields:
  - **Organization validity** (`ts_validationdispositionrulesorgvalid`): IRS record present **and** org-name
    match (Levenshtein ≥ 0.60) **and** IRS `SUBSECTION = "03"` **and** EIN/legal-identifier match **and** not on
    the IRS revocation list.
  - **Agent validity** (`ts_validationdispositionrulesagentvalid`): agent disposition "is" **and** the agent's
    email domain matches the org website domain **and** auto-close enabled.
  - **Trustworthiness / risk** (`ts_validationdispositiontrustworthy`): from CTP `risk_disposition`.
  - **Activity-code validity** (`ts_validationdispositionrulesactivitycodevalid`): IRS NTEE code is white-listed
    or in the internal list or matches the final code, **and** is not on the sensitive list.
  - **Legal equivalence** (`ts_validationlegalequivalencedisposition`) for non-US equivalency determinations.
- **Computes the recommended action** (`ts_validationdispositionaction`) — translating CTP's `manual` /
  `autoclose` rule into "Manual – Further Evaluation Needed" or "AutoClose – Qualify/Disqualify", and setting
  `ts_validationdispositionrulesautoclosequalify` when **all** signals pass.
- **Writes a `--- Disposition Details ---` system note** containing the full score matrix and a reference URL,
  on both the request case and its parent qualification case, as the durable audit record.

System notes are the pipeline's journal: `processSystemNote` / `existsSystemNote` / `getSystemNote` /
`removeSystemNote` create idempotent, titled annotations (each tagged with a `NoteSpecialDirectives` JSON
marker) that record every decision and gate the flow (e.g., "has this case been scored yet?").

---

## 7. Fraud Detection (Evidence Gathering)

`validationServicesEvaluateForFraud(...)` runs before auto-close and aggregates independent red-flag checks;
**any** flag marks the case fraudulent, routes it to the **fraud review queue** (status **104602**), and writes a
`-- Potential Fraud --` note listing findings:

- **Website analysis** (`WebsiteAnalysisService` / the in-file `analyzeWebsite`): a nine-phase, evidence-based
  legitimacy assessment using **HtmlAgilityPack** — domain analysis (flags free/social-media platforms),
  accessibility/retry, content parsing (metadata, JSON-LD Organization schema, navigation, contacts), structural
  scoring, **organization-name match** and **address match** against the site, trust-signal detection,
  red-flag detection, and content-quality scoring → a weighted 0–1 legitimacy score. A generic domain, an
  inaccessible site, or a near-zero name match each raise a fraud flag.
- **Domain WHOIS / DNS** (`EnhancedDomainValidator` over `WhoisJsonService`): non-US registrant or registrar,
  recently registered (< 90 days) or soon-expiring domain, non-US hosting IPs, suspicious registrar, hold status,
  privacy redaction, missing MX/SPF, and nameserver inconsistency → a cumulative risk score / level.
- **IP geolocation** (`NetworkValidationService.ValidateIPAddressAsync`): the registration IP (looked up by agent
  email) is flagged if it resolves outside the US.
- **Email domain** (`EmailDomainValidator`): detects free vs. disposable/temporary providers and fraud indicators;
  organizational domains are escalated to the deeper WHOIS check.

These services are also reusable libraries: **`AccountMatchService`** performs fuzzy de-duplication of an
organization against existing Dynamics accounts using five similarity algorithms (Levenshtein, Jaro-Winkler,
Dice, token-sort, cosine) over name/address/postal/legal-ID/website/phone, returning confidence-graded matches.

---

## 8. Routing / Channeling & Completion — `determineAction`

Once a disposition exists, `determineAction` channels the case down one of several terminal or holding paths,
**by queue and by case status code**:

- **CTP-org provisioning hold**: if the case is "Awaiting CTPOrgId Provisioning" (status **104701 / 104704**)
  and the CTP org id has arrived, it syncs status from the parent qualification case, notifies the requestor,
  links the transaction/agent into the CTP org, and routes to the post-auto-close queue.
- **Existing-account check** (`findValidationRequestAccountMatches`): strong matches route the case to manual
  review (**104697**) or terminate, preventing duplicate organization creation.
- **Nonprofit Verification Agent**: `initiateNonprofitVerificationAgent(caseId)` hands the case to the external
  AI verification service (the Azure Function app) for an additional opinion.
- **Fraud check** (§7): on any flag, stop and route to fraud review.
- **Auto-close → Qualify**: if `autoCloseEnabled` and the disposition signals pass (org + agent + trustworthy +
  activity-code valid, or that combination with a resolvable activity-code reference), it calls
  **`initiateOrgIncorporation`** (see below).
- **Routing & email outreach** (`applyRoutingRules`): if the case cannot auto-qualify, the engine evaluates
  email-outreach criteria; if met it emails the organization for more evidence, otherwise it applies queue
  routing rules and defaults to manual review (**104697** → `postNoAutoCloseQueue`).
- **Outreach-queue maintenance**: when the case is sitting in the outreach queue, abandoned-request aging applies
  (status **102059 – Abandoned** after the configured day threshold).

### Completion via Organization Incorporation — `initiateOrgIncorporation`

- Locates or **creates the organization account** from the case (`createOrgFromCase`), creates the parent
  **qualification case** (`processQualCaseFromValidationRequest`), creates the **agent contact**, and connects the
  agent to the account with the appropriate verification status.
- If the new account has no CTP org id yet, the case is parked at **104701 (Validated – Awaiting CTPOrgId
  Provisioning)**; once provisioned, the case is set to **102056 (Qualified)**, routed to the post-auto-close
  queue, and the transaction id + agent are written back into the CTP org object.
- If the org/agent could not be established, the case falls back to manual review (**104697**).

---

## 9. The US Non-501(c)(3) ("NonC3") Sub-Path

US organizations claiming exemption under sections **other than 501(c)(3)** need extra scrutiny, so
`ValidationServicesUSNonC3` runs a parallel `determineProcessBehavior` / `determineAction` with NonC3-specific
config, templates, and queues:

- **Pre-existing account / EIN de-duplication**: existing validation request for the same EIN →
  **Cancelled (102074)**; existing account with a mismatched designation → outreach + manual review.
- **Subsection-mismatch handling**: compares the requested designation against the IRS subsection and branches
  to disqualify, outreach, or "requires further evaluation" with explanatory system notes (e.g.
  `--- Validation Request & IRS SubSections Don't Match ---`).
- **Fraud check** and **auto-close → incorporation** mirror the main path (qualify **102056** / disqualify **102057**).
- **Email-outreach workflow** with NonC3 templates (`orgNotEligibleEmailTemplate`,
  `orgIs501c3PerDispositionTemplate`, `orgDesigDiscrepancyEmailTemplate`).

---

## 10. The Email-Outreach Lifecycle (Channeling for Missing Evidence)

When a case lacks enough signal to auto-qualify, the pipeline solicits evidence from the organization and tracks
the conversation through case status and queues:

- **Send** (`processValidationRequestEmailOutreach`): renders a Dynamics email template from a sender mailbox
  queue to the request email, sets the case to **104698 (Awaiting Customer Response)**, and moves it to the
  outreach queue (high-priority variant if the org has open orders). Duplicate sends are suppressed.
- **Respond** (`getCustomerHasRespondedEmailOutreach`): a FetchXML sweep finds cases set to **104699 (Customer Has
  Responded)** in outreach queues and moves them to the manual queue for analyst follow-up.
- **Abandon** (`setOldCasesInOutreachQueueToAbandoned`): cases still "Awaiting…" past
  `numberOfDaysInQueueToCloseAbandoned` are set to **102059 (Abandoned)**.

---

## 11. Status-Code & Queue Reference

| Code | `ts_casestatus` meaning | Typical trigger |
|------|-------------------------|-----------------|
| 102056 | OQ – Qualified | All signals pass / incorporation complete |
| 102057 | OQ – Disqualified | Ineligible designation / subsection mismatch |
| 102059 | OQ – Abandoned | No customer response within threshold |
| 102074 | OQ – Cancelled | Duplicate validation request for same EIN |
| 104602 | OQ – Fraud Review | Any fraud flag raised |
| 104697 | OQ – AutoValidation – Requires Further Evaluation | Manual review / unresolved routing |
| 104698 | OQ – Awaiting Customer Response | Outreach email sent |
| 104699 | OQ – Customer Has Responded | Inbound reply detected |
| 104701 | OQ – Validated – Awaiting CTPOrgId Provisioning | Qualified but CTP org id pending |
| 104704 | OQ – Disqualified – Awaiting CTPOrgId Provisioning | Disqualified, provisioning pending |

- **Queues** are config-driven names resolved per case: `initialQueue` (intake), `postAutoCloseQueue`
  (completed/qualified/disqualified), `postNoAutoCloseQueue` (human review), `duplicateReviewQueue`,
  `fraudReviewQueue`, and the outreach queues. `DynamicsProcessesHelper.addCaseToQueue` creates queues on demand.

---

## 12. Supporting Components

- **`DynamicsProcessesValidationServices.cs`** — the orchestrator: queue loop, behavior/action routing, fraud
  evaluation, outreach lifecycle, and the in-file website-content scoring helpers (`analyzeWebsite`,
  `CalculateOrgNameMatchScore`, `CalculateAddressMatchScore`, Levenshtein/normalization utilities).
- **`ValidationServicesHelper.cs`** — the scoring engine and CTP/Dynamics workhorse: `getValidationScoreMatrix`,
  disposition-note read/write, org/qual-case/agent creation, CTP object hydration & external-reference linking,
  account-match invocation, and the simplified fraud evaluator.
- **`ValidationServicesUSNonC3.cs`** — the US NonC3 sub-path and its outreach handling.
- **`NetworkValidationService.cs` / `WhoisJsonService.cs` / `EmailDomainValidator.cs` /
  `EnhancedDomainValidator.cs` / `AccountMatchService.cs`** — the validation/fraud signal library
  (website, IP, email, address, phone, domain WHOIS/DNS, and fuzzy account matching).
- **`WebsiteAnalysisService.cs`** — the nine-phase website legitimacy analyzer.
- **`DynamicsProcessesHelper.cs` / `ProcessHelper.cs`** — Dynamics case/queue/status/annotation helpers,
  organization-qualification management, email/template sending, CTP integration, SQL stored-proc access, and
  HTTP utilities with retry.
- **`ParallelProcessesHelper.cs`** — the Azure Table Storage distributed semaphore.
- **`CaseDefinition.cs`** — the strongly-typed map of the Dynamics `incident` schema, including all
  `ts_validationrequest*` / `ts_validationdisposition*` fields the engine reads and writes.

---

## 13. External Integrations

- **Microsoft Dynamics 365 / Dataverse** — cases, accounts, contacts, connections, queues, annotations,
  organization-qualification records (read/write via the Organization Service SDK and FetchXML).
- **CTP (TechSoup trust platform)** — disposition score matrix, organization objects, and external-reference
  provisioning (REST).
- **IRS tax-exempt data** — surfaced through the CTP disposition (subsection, EIN, NTEE, revocation).
- **WHOIS / DNS provider, IP geolocation, USPS address tools** — used by the network/domain fraud signals.
- **Nonprofit Verification Agent** (the companion Azure Function app) — an AI second opinion on intake.
- **Azure Table Storage** — distributed concurrency semaphores.
- **SQL Server stored procedures** — order eligibility, next-ID generation, registration-IP lookup.

---

*This README documents the `validationservices` code path only. Configuration values, environment URLs, API
keys, and credentials are provided at runtime and are intentionally excluded.*
