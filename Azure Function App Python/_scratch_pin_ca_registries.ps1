# Ad-hoc one-shot: pin verified Canadian provincial-registry findings to Qa + Stage
# via the live /agent/nonprofit/memory-admin/noauth endpoint.
$ErrorActionPreference = "Stop"

$endpoints = @(
    @{ Name = "Qa";    Url = "https://qa.techsoupservices.org/agent/nonprofit/memory-admin/noauth" },
    @{ Name = "Stage"; Url = "https://stage.techsoupservices.org/agent/nonprofit/memory-admin/noauth" }
)

$today = "2026-05-16"

$entries = @(
    # ===== Verified, programmatic-API capable =====
    @{
        category    = "Registry"
        scope_key   = "ca-bc"
        subject_key = "bc_orgbook_api_v1"
        subject     = "British Columbia - OrgBook BC public JSON API (legal entities incl. societies/nonprofits)"
        content     = @{
            jurisdiction         = "BC, Canada"
            issuer               = "BC Corporate Registry / Province of BC Ministry of Citizens' Services"
            url                  = "https://orgbook.gov.bc.ca/"
            url_template         = "https://orgbook.gov.bc.ca/api/v4/search/topic?q={query}"
            api_base             = "https://orgbook.gov.bc.ca/api/v4/"
            response_format      = "application/json"
            auth_required        = $false
            covers               = "All legal entities registered in BC: corporations, sole proprietorships, general partnerships, societies (including non-profit societies), cooperatives. Returns entity name, source_id, registration_date, entity_status (ACT=active), entity_type, home_jurisdiction."
            verification_method  = "GET request; parse JSON results[].names[].text and attributes (entity_status, entity_type, registration_date)."
            entity_type_codes    = "SP=sole prop, GP=general partnership, BC/C/CC=BC corp, S=society, CP=cooperative"
            example              = "https://orgbook.gov.bc.ca/api/v4/search/topic?q=foundation"
            license              = "Access Only License - British Columbia (https://bcgov.github.io/data-publication/pages/faq.html)"
            limitations          = "Returns entities registered with BC Corporate Registry only; charitable-status determination still requires CRA Charities Listings lookup."
            verified_on          = $today
        }
        tags  = @("canada","bc","provincial","api","json","societies","nonprofit")
        notes = "Verified 2026-05-16 via GET to /api/v4/search/topic?q=foundation returning 4,477 results. Best-in-class open API for BC nonprofit verification."
    },
    @{
        category    = "RegistryUrlTemplate"
        scope_key   = "ca-bc"
        subject_key = "bc_orgbook_topic_detail_v1"
        subject     = "British Columbia - OrgBook BC topic detail endpoint"
        content     = @{
            jurisdiction = "BC, Canada"
            url_template = "https://orgbook.gov.bc.ca/api/v4/topic/{topic_id}/formatted"
            response_format = "application/json"
            covers       = "Full credential set for a single BC entity given its OrgBook topic id (obtained from the /search/topic endpoint as results[].id)."
            verified_on  = $today
        }
        tags  = @("canada","bc","provincial","api","json","detail")
        notes = "Companion to bc_orgbook_api_v1 for fetching full details of a single entity."
    },
    @{
        category    = "Registry"
        scope_key   = "ca-federal"
        subject_key = "corporations_canada_federal_search_v1"
        subject     = "Canada (federal) - Corporations Canada search for federal NFP-Act corporations"
        content     = @{
            jurisdiction        = "Canada (federal)"
            issuer              = "Innovation, Science and Economic Development Canada - Corporations Canada"
            url                 = "https://ised-isde.canada.ca/cc/lgcy/fdrlCrpSrch.html"
            url_template        = "https://ised-isde.canada.ca/cc/lgcy/fdrlCrpSrch.html?V_SEARCH.command=navigate&V_TOKEN={query}"
            response_format     = "text/html"
            auth_required       = $false
            covers              = "All federally-incorporated entities including Not-for-Profit Corporations Act (NFP Act) corporations. Distinct from CRA Charities Listings: a federal NFP corp is not automatically a registered charity."
            verification_method = "HTML form search by corporate name, corporation number, or business number. Result page lists corporate name, status (Active/Dissolved/etc.), registered office, directors history."
            limitations         = "HTML scrape only - no JSON API. Excludes financial institutions (banks, insurance, loan/trust)."
            verified_on         = $today
        }
        tags  = @("canada","federal","nfp","html","corporations-canada")
        notes = "Use jointly with cra_charities_v1: federal NFP-Act registration confirms legal existence; CRA Charities lookup confirms charitable/tax-receiptable status."
    },
    @{
        category    = "Registry"
        scope_key   = "ca-ns"
        subject_key = "ns_rjsc_search_v1"
        subject     = "Nova Scotia - Registry of Joint Stock Companies public search (businesses and non-profits)"
        content     = @{
            jurisdiction        = "NS, Canada"
            issuer              = "Service Nova Scotia - Registry of Joint Stock Companies"
            url                 = "https://www.novascotia.ca/search-business-or-non-profit-information-filed-registry-joint-stock-companies"
            response_format     = "text/html"
            auth_required       = $false
            covers              = "Nova Scotia businesses AND non-profits registered with RJSC. Returns name, addresses, registration date, status."
            verification_method = "HTML form. No documented JSON API."
            verified_on         = $today
        }
        tags  = @("canada","ns","provincial","html","nonprofit")
        notes = "Confirmed accessible 2026-05-16 (no bot challenge). Manual / HTML-scrape lookup only."
    },
    @{
        category    = "Heuristic"
        scope_key   = "CA"
        subject_key = "cbr_unified_search_no_automation_v1"
        subject     = "Canada-wide - CBR (Canada's Business Registries) unified search - MANUAL ONLY, automation forbidden"
        content     = @{
            jurisdiction        = "Canada (federal + AB, BC, MB, NS, ON, QC, SK)"
            url                 = "https://ised-isde.canada.ca/cbr-rec/en/search"
            response_format     = "text/html"
            covers              = "Streamlined cross-jurisdiction search covering official registries of AB, BC, MB, NS, ON, QC, SK, and Corporations Canada. NL/NB/PE/YT/NT/NU not yet integrated."
            automation_status   = "FORBIDDEN"
            terms_quote         = "Automated tools that copy, search or scrape search results are forbidden. Anyone using these automated tools will be denied access to this service without notice."
            agent_guidance      = "Do NOT issue automated HTTP requests to this URL. Use it only as a human-verification reference link in case notes. For programmatic lookups, fall back to per-jurisdiction registries (e.g. OrgBook BC for BC, Corporations Canada for federal, NS RJSC for NS)."
            verified_on         = $today
        }
        tags  = @("canada","federal","provincial","manual-only","blocked-automation","reference")
        notes = "TOS explicitly prohibits scraping. Pin as reference for case notes only; do not invoke from agent HTTP tools."
    },

    # ===== Blocked / bot-protected sources (warnings) =====
    @{
        category    = "BlockedSource"
        scope_key   = "ca-on"
        subject_key = "on_obr_queue_it_blocked_v1"
        subject     = "Ontario - OBR public search is queue-it.net protected (programmatic access blocked)"
        content     = @{
            jurisdiction         = "ON, Canada"
            url                  = "https://www.appmybizaccount.gov.on.ca/onbis/master/entry.pub?applicationCode=onbis-master&businessService=registerItemSearch"
            blocking_mechanism   = "queue-it.net wait-room redirect"
            observed_redirect    = "https://ontario.queue-it.net/?c=ontario&e=onbis&..."
            agent_guidance       = "Do NOT attempt direct HTTP fetch - it returns a queue-it landing page, not the OBR search form. For ON entities, fall back to the CBR unified search (manual link only) or request the user provide an OBR profile-report PDF."
            paid_search_products = "OBR offers profile reports (CAD 8), document copies (CAD 3), certificate of status (CAD 26) - not free."
            verified_on          = $today
        }
        tags  = @("canada","on","provincial","blocked","queue-it")
        notes = "Confirmed 2026-05-16: HTTP GET redirects to ontario.queue-it.net."
    },
    @{
        category    = "BlockedSource"
        scope_key   = "ca-qc"
        subject_key = "qc_req_bot_protected_v1"
        subject     = "Quebec - Registraire des entreprises (REQ) blocks programmatic access (HTTP 403)"
        content     = @{
            jurisdiction       = "QC, Canada"
            url                = "https://www.registreentreprises.gouv.qc.ca/en/consulter/rechercher.aspx"
            blocking_mechanism = "Server-side bot detection returning HTTP 403 Forbidden"
            agent_guidance     = "Do NOT attempt programmatic fetch - returns 403. For QC entities, use the CBR unified search (manual link only) or request the user provide a REQ statement of information (etat de renseignements / state of information document, NEQ number)."
            verified_on        = $today
        }
        tags  = @("canada","qc","provincial","blocked","bot-detection")
        notes = "Confirmed 2026-05-16: fetch returned 'Forbidden'."
    },
    @{
        category    = "BlockedSource"
        scope_key   = "ca-pe"
        subject_key = "pe_corp_registry_bot_challenge_v1"
        subject     = "Prince Edward Island - corporate registry pages gated by browser-verification challenge"
        content     = @{
            jurisdiction       = "PE, Canada"
            url                = "https://www.princeedwardisland.ca/en/topic/corporate-services-registry"
            blocking_mechanism = "JavaScript-based browser verification interstitial ('Verifying your browser before proceeding...')"
            agent_guidance     = "Do NOT attempt programmatic fetch. For PE entities, request user to provide a Corporate Registry profile/extract directly."
            verified_on        = $today
        }
        tags  = @("canada","pe","provincial","blocked","js-challenge")
        notes = "Confirmed 2026-05-16: incident-id challenge page returned."
    },

    # ===== Strategy heuristic =====
    @{
        category    = "Heuristic"
        scope_key   = "CA"
        subject_key = "ca_provincial_lookup_strategy_v1"
        subject     = "Strategy - ordered lookup approach for Canadian nonprofit verification"
        content     = @{
            jurisdiction = "Canada (all)"
            strategy_steps = @(
                "1. CRA Charities Listings (cra_charities_v1) - confirms registered charity status + BN/9-digit registration number. Works for ALL provinces.",
                "2. If federally incorporated (NFP Act): Corporations Canada search (corporations_canada_federal_search_v1).",
                "3. Provincial fallback by jurisdiction:",
                "   - BC: OrgBook BC JSON API (bc_orgbook_api_v1) - automate freely.",
                "   - NS: RJSC HTML search (ns_rjsc_search_v1) - manual / scrape.",
                "   - ON: BLOCKED programmatically (queue-it); use CBR link as manual reference or ask user for OBR profile report.",
                "   - QC: BLOCKED programmatically (403); ask user for REQ statement / NEQ.",
                "   - PE: BLOCKED (JS challenge); ask user for registry extract.",
                "   - AB / SK / MB / NB / NL / YT / NT / NU: paid or partly-public; request documentation from applicant.",
                "4. Cross-check: a CRA-registered charity is the strongest signal. A provincial-only nonprofit registration without CRA charitable status is NOT automatically tax-receiptable."
            )
            do_not_automate = @("CBR unified search","Ontario OBR","Quebec REQ","PEI Corporate Registry")
            free_apis       = @("CRA Charities Listings","OrgBook BC")
            verified_on     = $today
        }
        tags  = @("canada","strategy","heuristic","provincial","nonprofit","lookup-order")
        notes = "Master playbook for CA-jurisdiction nonprofit verification. Consult before issuing any provincial lookup."
    }
)

$summary = New-Object System.Collections.Generic.List[object]

foreach ($ep in $endpoints) {
    Write-Host "`n=== Pinning to slot: $($ep.Name) ===" -ForegroundColor Cyan
    foreach ($e in $entries) {
        $tagsStr = if ($e.tags -is [array]) { ($e.tags -join ",") } else { [string]$e.tags }
        $body = @{
            action      = "pin"
            slot        = $ep.Name
            category    = $e.category
            scope_key   = $e.scope_key
            subject_key = $e.subject_key
            subject     = $e.subject
            content     = $e.content
            tags        = $tagsStr
            notes       = $e.notes
        } | ConvertTo-Json -Depth 12 -Compress

        try {
            $resp = Invoke-RestMethod -Uri $ep.Url -Method Post -ContentType "application/json" -Body $body -TimeoutSec 60
            $resultPreview = if ($resp.result) { ($resp.result | ConvertTo-Json -Depth 4 -Compress).Substring(0, [Math]::Min(160, ($resp.result | ConvertTo-Json -Depth 4 -Compress).Length)) } else { "<no result>" }
            Write-Host ("  OK  [{0}] {1}  -> {2}" -f $e.category, $e.subject_key, $resultPreview) -ForegroundColor Green
            $summary.Add([pscustomobject]@{ Slot=$ep.Name; Subject=$e.subject_key; Status="OK" })
        } catch {
            $errBody = ""
            try { $errBody = $_.ErrorDetails.Message } catch {}
            Write-Host ("  FAIL [{0}] {1}  -> {2} | {3}" -f $e.category, $e.subject_key, $_.Exception.Message, $errBody) -ForegroundColor Red
            $summary.Add([pscustomobject]@{ Slot=$ep.Name; Subject=$e.subject_key; Status="FAIL: $($_.Exception.Message)" })
        }
    }
}

Write-Host "`n=== Summary ===" -ForegroundColor Yellow
$summary | Format-Table -AutoSize
