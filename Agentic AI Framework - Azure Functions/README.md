# Systems Integration & AI Agents — Azure Function App (Python)

A multi-domain **Azure Functions** application (Python v2 programming model) that serves as TechSoup's
integration and automation backbone. It combines three concerns in one deployable app:

- **API gateway / integration layer** — secure, token-validated routing between TechSoup callers,
  **Microsoft Dynamics 365 / Dataverse**, **NetSuite (ERP)**, and a legacy **SOAP/SQL Server** data service.
- **AI agents** — autonomous, tool-using **Claude (Opus) agents** (hosted via **Azure AI Foundry**) that
  verify nonprofit/charity eligibility and resolve global support cases end-to-end.
- **Durable & async processing** — queue workers and Durable Functions orchestrations for long-running,
  reliable, checkpoint-safe workflows.



---

## 1. Architecture at a Glance

- **Runtime:** Azure Functions (Python v2 decorator model), extension bundle v4, 10-minute function timeout.
- **Entry point:** `function_app.py` (~2,300 lines) — registers all HTTP routes, queue workers, and shared services.
- **Auth model:** Per-endpoint (app-level `ANONYMOUS`); JWT signature validation **or** auth-key / master-key bypass.
- **AI runtime:** `foundry_opus` — an in-house agentic framework wrapping Claude via an Azure AI Foundry relay.
- **State & memory:** Azure **Table Storage** for audit archives and long-term agent "memory"; Azure **Queue Storage** for async jobs.
- **External systems:** Dynamics 365 / Dataverse, NetSuite, Azure AI Foundry (Claude), Azure Document Intelligence (OCR), Azure Key Vault, a legacy ESB SOAP service, and the public web (registry lookups / research).

```
Caller ──HTTP──► function_app.py ──► (sync)  Dynamics / NetSuite / SOAP
                      │
                      ├──► (async) Queue ──► Queue Workers ──► AI Agents (foundry_opus)
                      │                                            │
                      └──► Durable Orchestrations              Table Storage (archive + memory)
```

---

## 2. Technology Stack

- **Language / platform:** Python, Azure Functions v2, Azure Durable Functions.
- **AI:** `anthropic` SDK → Claude (Opus) via Azure AI Foundry relay; `pydantic` for structured agent I/O.
- **Azure SDKs:** `azure-functions`, `azure-functions-durable`, `azure-storage-queue`, `azure-data-tables`,
  `azure-ai-documentintelligence`, `azure-keyvault-certificates`, `azure-identity`.
- **Integration / parsing:** `requests`, `httpx`, `zeep` + `lxml` (SOAP), `PyJWT` (token validation),
  `cryptography` (PKCS#12 / mutual TLS), `pypdf` (PDF text extraction).
- **Config:** `python-dotenv` for local `.env` support.

---

## 3. Project Structure

- **`function_app.py`** — Main app: all HTTP endpoints, queue triggers, and service wiring.
- **`durable_functions.py`** — Durable orchestrations + activities (sync, fan-out/fan-in, approval workflows).
- **`host.json` / `.funcignore` / `requirements.txt`** — Functions host config, deploy ignore list, dependencies.
- **`foundry_opus/`** — Reusable agentic framework (client, agent loop, tools, orchestrator, workflow).
- **`nonprofit_verifier/`** — Nonprofit/charity verification agent (tools, prompts, models, PDF/OCR, Dynamics tools).
- **`global_support_case_agent/`** — Autonomous support-case resolution agent (tools, prompts, models, tests).
- **`npv_archive_service.py`** — Table-Storage audit trail for verification requests/results.
- **`npv_memory_service.py` / `gsc_memory_service.py`** — Long-term, learned "memory" stores (Table Storage).
- **`bootstrap_memory.py` / `promote_memory_stage_to_prod.py`** — Seed & promote memory entries across slots.
- **Auth & integration helpers** — `ms_token_validator.py`, `azure_keyvault_helper.py`, `oauth_base.py`,
  `netsuite_token_generator.py`, `soap_data_access_client.py` (+ `DataAccessService.wsdl`),
  `techsoupservices_helper.py`, `validation_request_processing.py`.
- **`scripts/`** — Operational scripts (provision Dynamics fields, seed memory).

---

## 4. HTTP API Surface

All routes support CORS (with OPTIONS preflight). Routes taking an `{auth_key}` accept a **master-key bypass**
or a **PNGO lookup**; others require a **Bearer JWT** validated by signature + client-ID allow-list.

### Integration / Gateway

- **`POST /CRM/{custom_api}`** and **`POST /CRM/{auth_key}/{custom_api}`** — Invoke a Dynamics 365 custom API; returns the Dynamics response.
- **`POST /ERP/{request_name}`** and **`POST /ERP/{auth_key}/{request_name}`** — Route a request to a NetSuite RESTlet (OAuth 1.0a / TBA signed).
- **`GET|POST /ValidateToken`** — Validate a Bearer JWT (signature + client ID) and return token info.
- **`POST /ConvertToDynamicsAPI` / `POST /ConvertFromDynamicsAPI`** — Convert plain JSON ↔ Dynamics OData "expando" format.
- **`GET|POST|PUT|DELETE /SubDomainRouting`** — Route based on subdomain (e.g., NGOsource vs. TechSoup Services).
- **`GET /case/artifacts/{auth_key}/{transaction_id}`** — Retrieve a case with its notes and file attachments (base64) from Dynamics.
- **`POST /Test`** — Diagnostic endpoint (e.g., generate a NetSuite token).

### Nonprofit Verification Agent

- **`GET /agent/nonprofit/health`** — Foundry/Claude deployment health check.
- **`POST /agent/nonprofit/verify`** and **`/verify/{auth_key}`** — Verify a nonprofit from supplied case + email + attachments; optional case-note write-back.
- **`POST /agent/nonprofit/verify-case/{auth_key}`** — Synchronous verification by Dynamics case ID (agent self-discovers evidence).
- **`POST /agent/nonprofit/verify-case-async/{auth_key}`** — Enqueue verification; returns `request_id` immediately.
- **`POST /agent/nonprofit/analyze-document`** — Extract & analyze a single document for authenticity indicators.
- **`POST /agent/nonprofit/archive-query/{auth_key}`** — Query the verification audit archive.
- **`POST /agent/nonprofit/memory-query/{auth_key}`** and **`/memory-admin/{auth_key}`** — Read & manage agent memory (pin, deprecate, feedback, lookup, bootstrap).

### Global Support Case (GSC) Agent

- **`POST /agent/gsc/handle-case-async/{auth_key}`** — Enqueue a support case for autonomous handling (`active_agent` or `simulate` mode).
- **`POST /agent/gsc/memory-query/{auth_key}`** and **`/memory-admin/{auth_key}`** — Read & manage GSC agent memory.

### Durable Orchestrations (from `durable_functions.py`)

- **`POST /orchestration/sync`** — Function-chaining Dynamics→NetSuite sync (fetch → transform → validate → send → update status).
- **`POST /orchestration/parallel-sync`** — Fan-out/fan-in batch processing of many records.
- **`POST /orchestration/approval`** — Human-in-the-loop approval workflow (waits for an external event or timeout).
- **`POST /orchestration/{instance_id}/approve` · `/status` · `/terminate`** — Manage running orchestrations.

### Queue Workers (async)

- **`NonprofitVerifyQueueWorker`** — Drains the nonprofit-verification queue, runs the agent, archives results.
- **`GlobalSupportCaseQueueWorker`** — Drains the GSC queue, runs the agent, applies memory proposals.
- Queue policy (host.json): batch size 3, max dequeue 3, 10-minute visibility timeout.

---

## 5. Authentication & Security Model

- **Bearer JWT validation** (`ms_token_validator.py`): RS256 signature verified against Microsoft's JWKS
  (cached 1 hour); checks `exp`/`nbf`/`iat`, audience, and tenant; extracts `appid`/`azp` (client ID).
- **Client-ID allow-list:** the extracted client ID is checked against `ACCEPTED_CLIENT_IDS` (case-insensitive).
- **Master-key bypass:** routes with `{auth_key}` accept a configured `master_auth_key` to skip per-partner lookups.
- **PNGO lookup:** otherwise `{auth_key}` is resolved to a partner ID via a SOAP stored procedure (`usp_getTsPngoId`).
- **OAuth flows:**
  - **Dynamics / Dataverse** — OAuth 2.0 **client-credentials** to Entra ID; tokens cached with a refresh buffer (thread-safe).
  - **NetSuite** — OAuth 1.0a **Token-Based Authentication** (HMAC-SHA256), built in `oauth_base.py` + `netsuite_token_generator.py`.
  - **Legacy ESB SOAP** — **mutual TLS** with an X.509 certificate pulled from Azure Key Vault (`azure_keyvault_helper.py`).

---

## 6. `foundry_opus` — Agentic Framework

A lightweight, composable framework wrapping Claude (via an Azure AI Foundry relay) so other modules can build agents quickly.

- **`FoundryClient` (client.py)** — Wraps the `anthropic` SDK; methods: `complete()`, `chat()`, `stream()`, `health_check()`.
  Gracefully disables extended "thinking" if the relay rejects it.
- **`FoundryConfig` (config.py)** — Env-driven settings: API key, deployment/model, base URL, max tokens,
  thinking budget, interleaved thinking, temperature, timeout, retries. Loaded via `from_env()`.
- **`Agent` (agent.py)** — Autonomous loop: sends history + system prompt + tools to Claude, executes tool-use
  blocks, feeds results back, repeats up to `max_iterations`. Supports **prompt caching** (ephemeral cache markers)
  and **extended thinking**. Returns an `AgentResult` (output, messages, tool calls, stop reason, token usage).
- **`Tool` / `ToolRegistry` / `@tool` (tools.py)** — Define tools from Python functions with **automatic JSON-Schema
  inference**, serialize to Anthropic's tool format, and look them up by name.
- **`Orchestrator` (orchestrator.py)** — Multi-agent coordination: a coordinator agent routes work to named
  specialist agents (`ROUTE: <agent> :: <instruction>` / `FINAL: <answer>`), or `broadcast()` to all.
- **`Workflow` (workflow.py)** — Linear, conditional step pipeline with context passing and per-step transforms.
- **Exceptions (exceptions.py)** — `FoundryError` base → `FoundryConfigError`, `FoundryAPIError`, `ToolExecutionError`.

---

## 7. Nonprofit Verification Agent (`nonprofit_verifier/`)

**Purpose:** Decide whether an organization legitimately holds nonprofit/charity status, so it can access
TechSoup donation/licensing benefits — verifying documents, representative authority, and official registries.

- **Two modes** (`agent.py`):
  - **Direct** — caller supplies the case + email + attachments; up to 10 iterations.
  - **Case discovery** — caller supplies only a Dynamics **case ID**; the agent autonomously pulls emails, notes,
    and attachments from Dynamics; up to 30 iterations.
- **Tools (`tools.py`)** — `fetch_document_text` (URLs), `decode_attachment_text` (base64 PDFs/images),
  `check_email_domain_match`, `classify_registration_number`, `scan_authenticity_indicators`,
  `web_search` (public registry lookups), and `memory_lookup`.
- **Dynamics tools (`dynamics_tools.py`)** — Read case overview, list/read emails & notes, list/extract entity
  attachments, run generic OData queries, and **write a verification case-note** back to Dynamics.
- **PDF/OCR (`pdf_extract.py`)** — Two-stage extraction: fast `pypdf` first, then **vision OCR fallback**
  (Claude vision / Azure Document Intelligence) for scanned docs, with parallel page processing and per-call timeouts.
- **Strict registry policy (`prompts.py`)** — Every case **must attempt** an online registry check (IRS TEOS, UK
  Charity Commission, Canada CRA, Romania ANAF, Australia ACNC, France JOAFE, etc.) and cite verbatim evidence.
- **Models (`models.py`)** — `VerificationResult` with `status` (Verified / Partially Verified / Not Verified /
  Requires Further Review), `confidence`, `external_registry_checks[]`, `document_based_determination`,
  `recommendation` (Approve / Request Docs / Escalate), token usage, and `memory_proposals[]`.
- **Archive (`npv_archive_service.py`)** — Every request/result is written to Table Storage (date-partitioned),
  with status lifecycle, processing duration, and large-result chunking (Azure's 32 KB property limit).

---

## 8. Global Support Case Agent (`global_support_case_agent/`)

**Purpose:** Autonomously **resolve** Dynamics 365 support cases — research a real answer and reply to the
customer, or escalate with a complete research record — rather than just triaging.

- **Two-phase agent (`agent.py`):**
  1. **Research & decide** — define the inquiry, look up memory + regional site, perform real web research,
     classify the case, and choose **`ResolveAndReply`** or **`EscalateToGlobalSupport`** (up to 40 iterations).
  2. **Adversarial verification** — a second agent fact-checks any draft reply before sending: approve, revise, or block.
- **Acceptable-effort floor (`prompts.py`)** — A reply may only be sent after memory lookup + regional-site
  resolution + real web search/fetch + a substantive (≥200-char) reply citing a real source at Medium/High confidence.
- **Hard safety rules** — **Never** create/update/delete Account or Contact records; special handling for the
  "TechSoup Stock Customer Service" placeholder customer and **Formstack** cases (form-note inquiries, new-email replies).
- **Tools:**
  - **Dynamics (`dynamics_tools.py`)** — case overview, emails & attachments, customer resolution & history,
    hierarchical case classification, send reply, route to queue, stamp AI fields, create case note.
  - **Knowledge (`knowledge_tools.py`)** — `web_search` (DuckDuckGo, with Tavily/Bing/Brave fallbacks),
    `fetch_document_text`, plus internal KB search/get.
  - **Memory (`memory_tools.py`)** — `memory_lookup` and `resolve_techsoup_site` (deterministic regional-site map).
- **Models (`models.py`)** — `GlobalSupportCaseResult` (intent, confidence, recommendation, customer match,
  inquiry definition, research process, draft reply, route decision, classification, memory proposals, action taken).
- **Memory (`gsc_memory_service.py`)** — Learned KB hits, answer templates, validated web sources, routing rules,
  intent signals, and per-customer/per-org context, scoped by region.
- **Scripts** — `create_gsc_incident_fields.py` provisions the custom `ts_ai*` incident fields;
  `seed_gsc_memory.py` seeds memory from KB articles, regional portals, and curated intent/answer templates.

---

## 9. Shared "Memory" Subsystem

Both agents learn over time via Azure Table Storage memory stores (`npv_memory_service.py`, `gsc_memory_service.py`):

- **Structure** — Partitioned by `Category__ScopeKey` (e.g., `Registry__US`, `WebSource__global`,
  `CustomerPreference__customer:<id>`); each entry carries content JSON, confidence, status, and success/failure counts.
- **Self-grading** — Confidence is recomputed from outcomes; entries flip to `needsReview` when failures dominate.
  Agents return **memory proposals** (`record` / `feedback`) that the services apply after each run.
- **Guardrails** — Strict category allow-lists, URL/host validation (prompt-injection resistant), soft-delete,
  and slot isolation (Local / Qa / Stage / Production).
- **Lifecycle tools** — `bootstrap_memory.py` pins canonical registry metadata; `promote_memory_stage_to_prod.py`
  copies high-confidence entries from Stage to Production behind a `--confirm` flag.

---

## 10. Integration & Helper Modules

- **`techsoupservices_helper.py`** — The Dynamics/Dataverse workhorse: cached token acquisition, generic OData CRUD,
  incident & annotation management, file attachment upload/download, expando ↔ JSON conversion, metadata/field
  provisioning, option-set resolution, and SOAP-backed PNGO/nonce lookups.
- **`validation_request_processing.py`** — Orchestrates validation requests: find case by transaction ID, extract
  embedded validation JSON from a case note, download supporting documents, and manage the incident lifecycle.
- **`soap_data_access_client.py` (+ `DataAccessService.wsdl`)** — `zeep`-based SOAP client that executes SQL Server
  stored procedures through the ESB over mutual TLS (e.g., `usp_GetNetSuiteNonce`, `usp_getTsPngoId`).
- **`ms_token_validator.py`** — Entra ID JWT validation & claim extraction.
- **`azure_keyvault_helper.py`** — PKCS#12 certificate retrieval from Key Vault → PEM cert/key for `requests`.
- **`oauth_base.py` / `netsuite_token_generator.py`** — OAuth 1.0a signing and NetSuite SOAP/REST token generation.

---

## 11. Configuration (Environment Variables)

Set these via Azure App Settings or a local `local.settings.json` (never committed):

| Variable | Purpose |
|----------|---------|
| `DYNAMICS_ENVIRONMENT` | Dataverse base URL (e.g., `https://<org>.crm.dynamics.com`) |
| `CLIENT_ID` / `CLIENT_SECRET` / `ts_AzureTenantId` | Dynamics service-principal credentials |
| `TECHSOUPSERVICES_API_RESOURCE` | Expected JWT audience for inbound tokens |
| `ACCEPTED_CLIENT_IDS` | JSON array of allowed caller client IDs |
| `master_auth_key` | Master key to bypass PNGO lookup on `{auth_key}` routes |
| `AzureWebJobsStorage` | Storage connection string (queues, tables, durable state) |
| `NPV_QUEUE_NAME` / `GSC_QUEUE_NAME` | Async queue names |
| `FOUNDRY_API_KEY` / `FOUNDRY_DEPLOYMENT` / `FOUNDRY_BASE_URL` | Azure AI Foundry (Claude) access & model |
| `NetSuite*` (`AccountId`, `ConsumerKey`, `ConsumerSecret`, `TokenId`, `TokenSecret`) | NetSuite TBA credentials |
| `ts_VaultESBSecretUrl`, `ts_DynamicsESBIntegration*`, `ts_ESBUrl` | Key Vault & ESB SOAP settings |
| `DOCINTEL_ENDPOINT` / `DOCINTEL_KEY` | Azure Document Intelligence (image OCR) |

---

## 12. Local Development & Deployment

- **Prerequisites:** Python (matching the Functions runtime), Azure Functions Core Tools, and the Azurite emulator (or a real Storage account).
- **Install:** `pip install -r requirements.txt`
- **Configure:** create `local.settings.json` with the variables above (it is git-ignored).
- **Run locally:** `func start` (serves all HTTP routes; queue/durable features require a Storage connection).
- **Deploy:** publish to an Azure Functions app (e.g., `func azure functionapp publish <app-name>`); set the same
  settings as App Settings, ensuring the managed identity / service principal can read Key Vault and call Dynamics.
- **Provision agent fields:** run `scripts/create_gsc_incident_fields.py` once per Dynamics environment, then
  `bootstrap_memory.py` / `seed_gsc_memory.py` to seed memory.

---

*This README describes functionality only; all credentials and tenant-specific identifiers are supplied at runtime
through environment variables and Azure Key Vault.*
