"""Memory lookup tool for the Global Support Case Agent.

Mirrors nonprofit_verifier.tools.memory_lookup but binds to gsc_memory_service.
"""
from __future__ import annotations

import json
import os
import sys
from typing import Optional

from foundry_opus import tool


@tool(description=(
    "Look up advisory hints learned from prior Global Support Case Agent runs. "
    "Hints are NOT confirmation \u2014 they are leads you must re-verify live. "
    "Use this AFTER `dynamics_get_gsc_case_overview` once you know the region and intent. "
    "Common patterns: "
    "category='KbHit' scope_key=<ISO> for previously useful knowledge articles per region; "
    "category='AnswerTemplate' scope_key=<ISO> for proven reply patterns; "
    "category='WebSource' scope_key='global' for validated official URLs (regional "
    "TechSoup sites live under scope_key='global'; to resolve a country's TechSoup site "
    "use the dedicated resolve_techsoup_site tool instead of guessing scope keys); "
    "category='IntentSignal' scope_key='global' for phrasings that confirm an intent label; "
    "category='RoutingRule' scope_key='global' for escalation triggers; "
    "category='CustomerPreference' scope_key='customer:<contactid>' for per-contact context; "
    "category='OrgIdentity' scope_key='orgdomain:<domain>' for known organization mappings; "
    "category='BlockedSource' scope_key='global' for URLs that previously failed; "
    "category='KnownScam' scope_key='global' for known abusive senders/domains; "
    "category='Heuristic' / 'DocPattern' / 'QueryPattern' as in NPV. "
    "Each result contains a 'Ref' field \u2014 if you used a hint and it worked (or failed), grade it via "
    "memory_proposals[] in your final submit call with action='feedback', "
    "ref=<that Ref>, outcome='success'|'failure'."
))
def memory_lookup(
    category: str,
    scope_key: str,
    subject_contains: Optional[str] = None,
    max_results: int = 8,
) -> str:
    try:
        try:
            import gsc_memory_service as _mem
        except ImportError:
            _here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            if _here not in sys.path:
                sys.path.insert(0, _here)
            import gsc_memory_service as _mem
        rows = _mem.lookup_entries(
            category=category,
            scope_key=scope_key,
            subject_contains=subject_contains,
            max_results=max_results,
        )
        try:
            refs = [r.get("Ref") for r in rows if r.get("Ref")]
            if refs:
                _mem.bump_use_count(refs)
        except Exception:
            pass
        slim = []
        for r in rows:
            slim.append({
                "ref": r.get("Ref"),
                "category": r.get("Category"),
                "scope_key": r.get("ScopeKey"),
                "subject_key": r.get("SubjectKey"),
                "subject": r.get("Subject"),
                "content": r.get("Content"),
                "source": r.get("Source"),
                "confidence": r.get("Confidence"),
                "status": r.get("Status"),
                "success_count": r.get("SuccessCount"),
                "failure_count": r.get("FailureCount"),
                "last_confirmed": r.get("LastConfirmedDateUtc"),
            })
        return json.dumps(
            {"count": len(slim), "results": slim,
             "advisory": "Hints are NOT confirmation \u2014 verify live this run."},
            default=str,
        )
    except Exception as e:
        return f"[memory_lookup error: {e}]"


# Canonical TechSoup regional site map. Used as a resilient fallback when the
# `techsoup_regional_site_directory` memory record cannot be read. Keys are
# uppercase ISO-3166 alpha-2 country codes plus a few named special sites.
_CANONICAL_TS_SITES = {
    "US": "https://www.techsoup.org",
    "CA": "https://www.techsoup.ca",
    "GB": "https://www.techsoup.uk",
    "UK": "https://www.techsoup.uk",
    "IT": "https://www.techsoup.it",
    "DK": "https://www.techsoup.dk",
    "FI": "https://www.techsoup.fi",
    "IE": "https://www.techsoup.ie",
    "SE": "https://www.techsoup.se",
    "CH": "https://www.techsoup.ch",
    "ES": "https://www.techsoup.es",
    "FR": "https://www.techsoup.fr",
    "RO": "https://www.techsoup.ro",
    "DE": "https://www.stifter-helfen.de",
    "AT": "https://www.stifter-helfen.at",
    "JP": "https://www.techsoupjapan.org",
    "TW": "https://www.techsouptaiwan.org",
    "HK": "https://www.techsoup.hk",
    "SG": "https://www.techsoupsingapore.sg",
    "KE": "https://www.techsoupkenya.or.ke",
    "ZA": "https://www.techsoupsouthafrica.org",
    "KN": "https://saintkittsandnevis.techsoup.global",
    "global_support": "https://support.techsoup.org",
    "engage": "https://engage.techsoup.org",
    "tsgn": "https://tsgn.org",
}

# Country-code aliases that should resolve to a shared regional site.
_TS_CC_ALIASES = {
    "GB": "UK",
}


@tool(description=(
    "Resolve the official TechSoup regional website for a given country code. "
    "Call this immediately after you know the case's `ts_countrycode` (or the "
    "customer's country). Pass the ISO-3166 alpha-2 code, e.g. 'IT', 'US', 'GB'. "
    "Returns the country-specific TechSoup site to use as the PRIMARY host for "
    "targeted web_search and fetch_document_text, plus the global site and the "
    "global support site as fallbacks. This is the authoritative, deterministic "
    "way to obtain the regional URL \u2014 do not guess TechSoup URLs."
))
def resolve_techsoup_site(country_code: str) -> str:
    try:
        cc = (country_code or "").strip().upper()
        directory: dict = {}
        try:
            try:
                import gsc_memory_service as _mem
            except ImportError:
                _here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
                if _here not in sys.path:
                    sys.path.insert(0, _here)
                import gsc_memory_service as _mem
            rows = _mem.lookup_entries(
                category="WebSource",
                scope_key="global",
                subject_contains="regional site directory",
                max_results=3,
            )
            for r in rows:
                content = r.get("Content")
                if isinstance(content, str):
                    try:
                        content = json.loads(content)
                    except Exception:
                        content = None
                if isinstance(content, dict):
                    sites = content.get("sites")
                    if isinstance(sites, dict):
                        directory.update({str(k).upper() if len(str(k)) == 2 else str(k): v
                                          for k, v in sites.items()})
        except Exception:
            pass

        merged = {**_CANONICAL_TS_SITES, **directory}
        lookup_cc = _TS_CC_ALIASES.get(cc, cc)
        resolved = merged.get(cc) or merged.get(lookup_cc)

        global_site = merged.get("US") or "https://www.techsoup.org"
        support = merged.get("global_support") or "https://support.techsoup.org"

        return json.dumps({
            "country_code": cc,
            "resolved_site": resolved,
            "matched": bool(resolved),
            "global_site": global_site,
            "global_support": support,
            "all_known_sites": merged,
            "usage": (
                "Use resolved_site as the primary TechSoup host for this country "
                "(targeted web_search and fetch_document_text). If resolved_site is "
                "null, the country has no dedicated site \u2014 use global_site and "
                "open-web research. Always also consider global_support for help "
                "articles. These URLs are leads: confirm them live before citing."
            ),
        }, default=str, ensure_ascii=False)
    except Exception as e:
        return f"[resolve_techsoup_site error: {e}]"
