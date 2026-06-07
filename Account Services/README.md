# Account Services — Dynamics 365 / Dataverse Plugin Suite

This solution is a set of **Microsoft Dynamics 365 / Dataverse plugins** (C#, `IPlugin`) that govern
TechSoup's organization (account) master data inside Dynamics and keep it synchronized with the legacy
**Onyx** back office and TechSoup's **order/fulfillment** platform. The plugins enforce validation, generate
identifiers, manage organization **qualification** workflows, route support cases, protect system notes, and
surface **external (non-Dataverse) data** inside Dynamics queries.

> The code lives under `Account Services/PluginVault/`. All endpoints, SQL servers, certificates, and
> credentials are supplied at runtime through **Dataverse environment variables** and **Azure Key Vault** —
> no secrets are stored in source or in this document.

---

## 1. Solution Overview

- **`AccountServices.sln`** — the Visual Studio solution.
- **`PluginVault/AccountServices.csproj`** — the plugin assembly (.NET Framework, strong-named) containing all
  `IPlugin` classes plus the shared `AccountServicesHelper`.
- **`SlnCrmPackage/SlnCrmPackage.csproj`** — a Dynamics CRM **solution-packaging** project that bundles the
  signed plugin DLL + registration metadata into a deployable solution zip.
- **`PluginVault/Connected Services/`** — auto-generated WCF/SOAP proxies:
  - **DataAccessService** (`http://techsoupglobal.org/DataAccessService/`) — executes SQL Server stored
    procedures through the TechSoup ESB.
  - **orderService** (`http://compumentor.org/orderservice`) — the order/subscription/payment/fulfillment API.

**Tech stack:** Microsoft.CrmSdk.CoreAssemblies (Xrm.Sdk), Microsoft.Identity.Client (MSAL, for Key Vault auth),
Newtonsoft.Json, System.ServiceModel (WCF), and X.509 certificate-secured SOAP.

---

## 2. How These Plugins Are Registered (Primer)

Each class implements `IPlugin.Execute` and is registered in Dynamics against a **message**, a **primary
entity**, and a **pipeline stage**. The naming convention in this suite encodes the stage:

- **`*PreCreateUpdate`** → **Pre-Operation / Pre-Validation** (stages 10/20): validates and mutates the
  `Target` *before* it is committed; blocks invalid saves by throwing `InvalidPluginExecutionException`.
- **`*CreateUpdate`** (no "Pre") → **Post-Operation** (stage 40): runs *after* commit and pushes the saved data
  out to Onyx via SOAP.
- **`OrganizationSave` / `GetOrgData`** → **Custom API / Action** handlers (request/response via `ts_request`
  and `ts_response` parameters).
- **`SearchQuery` / `RetrieveMultipleExternalData`** → a **RetrieveMultiple** pre/post pair that injects
  external order data into query results.

**Recurring safety patterns across the suite:**
- **Loop guards** — post-operation plugins exit early when the modifying user is `TSDynamicsOnyx`
  (the integration account), preventing infinite Dynamics⇄Onyx echo loops.
- **Authorization gates** — pre-operation plugins whitelist system/integration accounts
  (`TSDynamics`, `TSDynamicsOnyx`, `DynamicsClient`, `DynamicsESBIntegration`, `SYSTEM`).
- **Account directives** (`ts_accountdirectives`, a multi-select choice) let data opt out of automation:
  **1 = ExcludeDataIntegration**, **2 = ExcludePostAccountCreateAutoLogic**, **3 = BypassPreAccountValidation**.
- **Consistent tracing/logging** via `AccountServicesHelper.writeToTrace` and integration-failure records in
  `ts_dynamicsonyxintegrationlog`.

---

## 3. Account Plugins (`account` entity)

### AccountPreCreateUpdate — Pre-Operation (Create/Update)
- **Validates** the organization before save: mandatory address block, email, and source; numeric budget;
  organization **designation** (`new_orgdesignation`) must be a `QualOrg` qualification code whose code matches
  the account's country (GB normalized to "uk"); designation requires legal identifier, activity code, budget,
  and phone.
- **Partner (PNGO) rules**: `ts_orgppid` must point to a PNGO account (one carrying `ts_tspngoid`) and requires
  both `ts_pporgid` and a designation; builds `new_platformid` as `<pngoCode>.<pporgid>` or `TechSoup.<TSOrgId>`.
- **Duplicate prevention**: blocks saves matching an existing account on name + legal id + address + postal
  prefix; forbids manual `ts_duplicateofid` changes (must be done via the qualification case).
- **Identity & ownership**: generates/assigns the **TS Org Id** (`accountnumber`) via
  `AccountServicesHelper.getNextTsCustomerId`, and routes ownership to country-specific teams (e.g. DE → German
  validations team, AU → Australian team). Auto-generates an `new_associationcode` for US territories.

### AccountCreateUpdate — Post-Operation (Create/Update)
- **Syncs the account to Onyx** by marshalling ~25 fields (name, address, email, phone, tax id/legal
  identifier, website, budget, activity code, association code, source, designation, partner ids, duplicate-of,
  employee count) into the `usp_dynamicsOrgUpdate` stored procedure over the DataAccessService SOAP/ESB channel.
- **Manages qualification lifecycle**: on create with a designation, opens an organization qualification
  (status *Qualification Pending*) and a legal address; on update it detects **designation or country changes**,
  cancels the prior qualification (status 13), flips the linked case to *OQ – Cancelled* (102074), and logs the
  change to the system integration log.
- Honors `ExcludeDataIntegration` / `ExcludePostAccountCreateAutoLogic` directives and the `TSDynamicsOnyx`
  loop guard.

### AccountRefCreateUpdate — Post-Operation (`ts_accountreference`)
- Forwards external/reference identifiers (e.g. **Cisco** ids) for an account to Onyx via
  `usp_dynamicsOrgCiscoUpdate`, passing the parent TS Org Id, reference type, and reference value.

### PopulateTSOrgId — Post-Operation (Create)
- **Fallback id generator**: if an account was created without an `accountnumber`, it calls `usp_getNextOnyxId`
  through the ESB and stamps the generated TS Org Id back onto the record.

---

## 4. Contact, Connection & Address Plugins

### ContactPreCreateUpdate — Pre-Operation (`contact`)
- **Locks down contact creation** to system/integration accounts (end-user creation throws an exception) and
  **normalizes** `ts_emailvalidationstatus` to one of *Invalid (3)*, *Valid (4)*, or *Not Validated (1)*.

### ContactCreateUpdate — Post-Operation (`contact`)
- Syncs contacts to Onyx on three paths depending on `new_source`:
  - **Validation-request agents** (source `105000`) → `usp_updateOnyxValidationRequestAgent`.
  - **General contacts on create** → `usp_updateOnyxGeneralContact` (full identity + address + portal username).
  - **Updates** → `usp_dynamicsIndividualUpdate` (TS contact id, email-validation status, CTP verification code).

### ConnectionPreCreateUpdate — Pre-Operation (`connection`)
- When an **account-to-something** connection is created without a role, auto-assigns the **"Employer"**
  `connectionrole`.

### ConnectionCreateUpdate — Post-Operation (`connection`)
- Models **agent↔organization** (and org↔org) relationships and syncs them to Onyx via
  `usp_dynamicsContactUpdate`, mapping each side to an owner id + category (account=2, contact=1), the active
  state, and the resolved connection-role names. Respects the `ExcludeDataIntegration` directive on either side.

### AddressCreateUpdate — Post-Operation (`customeraddress`)
- Syncs **legal addresses only** (`addresstypecode = 5`) belonging to **accounts** to Onyx via
  `usp_dynamicsLegalAddressUpdate` (country, region, three address lines, city, postal code, TS Org Id).

---

## 5. Note / Annotation Plugins (`annotation`)

### NotePreCreateUpdate — Pre-Operation (Update/Delete)
- **Protects system notes**: notes carrying a `"systemNote": true` JSON directive (a `NoteSpecialDirectives`
  block appended to the note text) cannot be updated or deleted except by system/integration accounts —
  otherwise it throws `InvalidPluginExecutionException`.

### NoteCreateUpdate — Post-Operation (Create/Update)
- **Formstack case routing**: for cases sourced from Formstack (`caseorigincode = 100003`), it parses the note
  text to extract **country** and **customer email**, resolves the country/region against
  `ts_fieldhierarchyandmapping`, looks up the "GlobalSupport" routing config, finds/creates the appropriate
  **CSP Customer Service** team (assigning it the configured security role), and sets the case's owner,
  `ts_countrycode`, `ts_countryregionglobalsupport`, `ts_emailaddresscustomerprovided`, and resolved `customerid`.
- **System-note ownership**: re-assigns any `"systemNote": true` annotation to the `SYSTEM` user.

---

## 6. Custom API Plugins (Organization Master Data)

### OrganizationSave — Custom API (`ts_request` → `ts_response`)
- The largest plugin and the **write gateway** for organizations. It routes to **create** or **update** based on
  whether a `TSOrgId` is supplied, then orchestrates the full graph:
  - Upserts the `account` with 20+ mapped attributes and resolves **country/region/state** values against the
    `ts_fieldhierarchyandmapping` hierarchy (populating description option-sets).
  - Creates/updates **qualification cases** (`incident`, `casetypecode = 2`, `ts_type = 101996`).
  - Creates **connections** to contacts — hydrating a missing contact from Onyx (`usp_getContactInfo`) under the
    `TSDynamicsOnyx` identity — and assigns connection roles.
  - Persists **organization references** and **legal identifiers** as `ts_accountreference` rows.
  - Handles **PNGO/Batch** partner linkage and flags `duplicateOrg` when a matching account already exists.
- Returns `resultStatus` (success/failure), the `TSOrgId`, and any concatenated `error`.

### GetOrgData — Custom API (`ts_request` → `ts_response`)
- **Read-only** consolidated view of an organization by `TSOrgId`: identity, classification/designation,
  nested address, contact details, activity code (with description/category), employee count, duplicate-of,
  PNGO ids, and current **qualification status** + **qualification case status**. Pure Dataverse reads — no
  external calls.

---

## 7. External-Data Query Plugins (`incident` / `queueitem` RetrieveMultiple)

These two cooperate to make **order totals that live outside Dataverse** queryable and sortable in Dynamics:

### SearchQuery — Pre-Operation RetrieveMultiple
- Inspects the incoming **FetchXML**; if a query for `incident` or `queueitem` requests the
  `ts_tsaggregateordertotal` attribute, it stamps **shared variables** (`MetaReference`, alias names, the
  FetchXML text), ensures the needed link-entities/attributes exist, and raises the page size.

### RetrieveMultipleExternalData — Post-Operation RetrieveMultiple
- Reads the shared-variable flags, collects the result set's TS Org Ids, fetches aggregated order/admin totals
  from SQL via `usp_orgsOrderData` (DataAccessService SOAP), **injects** `ts_tsaggregateordertotal` onto each
  returned entity, and **re-sorts** the collection per the original FetchXML order (type-aware multi-field sort).

---

## 8. Shared Library — `AccountServicesHelper.cs`

A static, stateless helper used by every plugin. Major method groups:

- **Dataverse CRUD/query** — account match/dedup, account references, legal address creation, case retrieval,
  country/region resolution.
- **Qualification management (TechSoup domain)** — create/update `ts_organizationqualification` +
  `ts_organizationqualificationhistory`, read qualification status, sync the linked qualification case.
- **SQL stored-proc access** — `getNextTsCustomerId` (`usp_getNextOnyxId`), `getOnyxIndividualInfo`
  (`usp_getContactInfo`, ~55 mapped fields), order totals.
- **SOAP integration** — builds `ExecuteStoredProcRequestType` calls to **DataAccessService** over a
  certificate-secured binding (`{ts_ESBUrl}/services/TSGDataAccessServiceEBS_V1`).
- **Azure Key Vault & certificates** — `GetVaultCertificate` uses MSAL client-credentials to fetch the X.509
  cert that authenticates the ESB calls.
- **Option-set & hierarchy mapping** — read/insert/update option values; read/write JSON config in
  `ts_fieldhierarchyandmapping`.
- **Teams & RBAC** — find/create teams in the validations business unit and associate security roles.
- **Contact/case creation, customer resolution by email, logging, and regex/date/string utilities.**

---

## 9. TechSoup Domain Concepts

- **TS Org Id** (`accountnumber`) — the canonical organization identifier shared with Onyx.
- **Organization designation** (`new_orgdesignation` → `new_qualificationcode`) — the qualification a country
  grants (e.g. nonprofit category); drives eligibility and qualification cases.
- **Qualification** (`ts_organizationqualification` + history) — per-org status with an audit trail.
- **PNGO** — Partner NGO arrangement linking an org to a partner platform (`ts_orgppid`, `ts_pporgid`,
  `ts_tspngoid`, `ts_tspngocode`).
- **Onyx** — the legacy back-office system kept in sync via ESB stored procedures.
- **System notes** — annotations marked immutable via a `NoteSpecialDirectives` JSON block.
- **Account directives** — per-record automation opt-outs.

---

## 10. Reference Tables

### Stored procedures (via DataAccessService SOAP / ESB)
| Stored proc | Used by | Purpose |
|-------------|---------|---------|
| `usp_getNextOnyxId` | PopulateTSOrgId, helper | Generate next TS Org Id |
| `usp_dynamicsOrgUpdate` | AccountCreateUpdate | Push account master data to Onyx |
| `usp_dynamicsOrgCiscoUpdate` | AccountRefCreateUpdate | Push external/Cisco references |
| `usp_dynamicsIndividualUpdate` / `usp_updateOnyxGeneralContact` / `usp_updateOnyxValidationRequestAgent` | ContactCreateUpdate | Push contact/agent data |
| `usp_dynamicsContactUpdate` | ConnectionCreateUpdate | Push relationship/connection data |
| `usp_dynamicsLegalAddressUpdate` | AddressCreateUpdate | Push legal address |
| `usp_getContactInfo` | OrganizationSave, helper | Hydrate a contact from Onyx |
| `usp_orgsOrderData` | RetrieveMultipleExternalData | Aggregate order totals |

### Key status / option-set codes seen in code
| Code | Meaning |
|------|---------|
| `caseorigincode = 100003` | Formstack-sourced case |
| `casetypecode = 2`, `ts_type = 101996` | Organization Qualification case |
| `ts_casestatus = 102074` | OQ – Cancelled |
| qualification status `13` | Cancelled |
| `new_source = 105000` | Validation-request agent (contact) |
| address `addresstypecode = 5` | Legal address |
| `ts_emailvalidationstatus` 1 / 3 / 4 | Not Validated / Invalid / Valid |
| `ts_accountdirectives` 1 / 2 / 3 | ExcludeDataIntegration / ExcludePostAccountCreateAutoLogic / BypassPreAccountValidation |

### Environment variables (resolved from Dataverse)
- `ts_ESBUrl` — ESB / DataAccessService base URL
- `ts_Sql2kServer` — SQL Server hosting the Onyx databases (`ServiceAdmin`, `DBAdmin`)
- `ts_VaultESBSecretUrl` — Key Vault secret URL for the ESB certificate
- `ts_DynamicsESBIntegrationClientId` / `ts_DynamicsESBIntegrationClientSecret` — Azure AD credentials for Key Vault

---

## 11. End-to-End: a New Organization

1. **`OrganizationSave`** (or a UI/integration create) submits the organization.
2. **`AccountPreCreateUpdate`** validates fields/designation/PNGO, blocks duplicates, assigns the TS Org Id and
   owning team.
3. The account commits; **`AccountCreateUpdate`** opens the qualification + legal address and pushes the org to
   Onyx; **`PopulateTSOrgId`** backstops the id if missing.
4. **Contact/Connection/Address** plugins validate then mirror related records to Onyx, respecting directives
   and loop guards.
5. **`NoteCreateUpdate`** routes any Formstack support cases; **Note/annotation** guards keep system notes
   immutable.
6. **`SearchQuery` + `RetrieveMultipleExternalData`** later enrich incident/queue views with live order totals.

---

*This README documents functionality only. Endpoints, SQL server names, certificates, and credentials are
provided at runtime via Dataverse environment variables and Azure Key Vault and are intentionally excluded.*
