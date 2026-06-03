"""Knowledge tools for the Global Support Case Agent.

- kb_search / kb_get: Dataverse knowledgearticle access.
- web_search: DuckDuckGo HTML (public, no API key) — same pattern as nonprofit_verifier.tools.
- fetch_document_text: extract text from a public document URL.

The agent must restrict external fetches to ALLOWED official sources
(techsoup.org and country VA portals). This is enforced softly via the
system prompt; this module does not block any host so that the agent can
honestly record `BlockedSource` memory entries when something fails.
"""
from __future__ import annotations

import json
import os
import re
from typing import Any, Dict, List, Optional
from urllib.parse import parse_qs, urlparse

import httpx

from foundry_opus import tool

from . import dynamics_client


_SAFE_TIMEOUT = 20.0
_MAX_BYTES = 5 * 1024 * 1024
_PREVIEW_LIMIT = 20000

# A browser-like User-Agent. Many official sites (techsoup.org / support.techsoup.org,
# microsoft.com) return 403 to non-browser agents; a realistic UA lets us actually read
# the authoritative pages the agent needs to resolve cases.
_BROWSER_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
)

# Marker the agent can detect to distinguish "search infrastructure failed"
# (escalate with that reason) from a genuine "no results were found".
_SEARCH_INFRA_UNAVAILABLE = "SEARCH_INFRA_UNAVAILABLE"


def _json(obj: Any) -> str:
    try:
        return json.dumps(obj, indent=2, default=str, ensure_ascii=False)
    except Exception as e:
        return json.dumps({"error": f"serialization failed: {e}"})


def _truncate(text: str, limit: int = _PREVIEW_LIMIT) -> str:
    try:
        if not text:
            return ""
        if len(text) <= limit:
            return text
        return text[:limit] + f"\n\n[...truncated, original length {len(text)} chars]"
    except Exception:
        return text or ""


def _strip_html(html: str) -> str:
    try:
        text = re.sub(r"<script[\s\S]*?</script>", " ", html or "", flags=re.IGNORECASE)
        text = re.sub(r"<style[\s\S]*?</style>", " ", text, flags=re.IGNORECASE)
        text = re.sub(r"<[^>]+>", " ", text)
        text = re.sub(r"&nbsp;", " ", text, flags=re.IGNORECASE)
        text = re.sub(r"\s+", " ", text)
        return text.strip()
    except Exception:
        return html or ""


@tool(description=(
    "ACCESSORY / OPTIONAL side-check of the internal Dynamics 365 knowledge base "
    "(knowledgearticle entity). The internal KB is largely obsolete and is NOT your "
    "primary or authoritative source. Use it only as a quick secondary check, and only "
    "cite a KB article if it is an EXACT, current match for the customer's specific "
    "question; otherwise ignore the KB entirely and rely on memory_lookup + web research. "
    "Provide free-text keywords; optionally filter by language ('en-US', 'es-ES', 'pt-BR', "
    "etc.) and a state code (3=Published). Returns JSON with knowledgearticleid, article "
    "public number, title, language, and a short excerpt."
))
def kb_search(keywords: str, language: str = "", state_code: int = 3, top: int = 10) -> str:
    try:
        kw = (keywords or "").strip()
        if not kw:
            return _json({"error": "keywords required", "value": []})
        terms = [t for t in re.split(r"\s+", kw) if t]
        clauses: List[str] = []
        for term in terms[:5]:
            esc = term.replace("'", "''")
            clauses.append(
                f"(contains(title,'{esc}') or contains(keywords,'{esc}') or contains(content,'{esc}'))"
            )
        filter_parts: List[str] = []
        if clauses:
            filter_parts.append("(" + " and ".join(clauses) + ")")
        if state_code is not None:
            filter_parts.append(f"statecode eq {int(state_code)}")
        if language:
            esc_lang = language.replace("'", "''")
            filter_parts.append(
                f"(_languagelocaleid_value/languagelocaleid eq null or "
                f"languagelocaleid/localeid eq '{esc_lang}')"
            )
        filter_clause = " and ".join(filter_parts) if filter_parts else None
        rows = dynamics_client.run_async(dynamics_client.query(
            entity_set="knowledgearticles",
            filter_clause=filter_clause,
            select=["knowledgearticleid", "articlepublicnumber", "title", "keywords",
                    "description", "statecode", "statuscode", "modifiedon"],
            top=min(max(1, int(top)), 25),
            orderby="modifiedon desc",
        )) or []
        for r in rows:
            desc = r.get("description") or ""
            r["excerpt"] = _strip_html(desc)[:600]
        return _json({"count": len(rows), "value": rows})
    except Exception as e:
        return _json({"error": str(e)})


@tool(description=(
    "Retrieve the full text of an internal knowledge article by id. Returns JSON with "
    "title, article public number, language, and the article body (HTML stripped). Only "
    "call this if kb_search surfaced an EXACT match worth citing — the internal KB is an "
    "accessory source, not authoritative."
))
def kb_get(knowledgearticleid: str) -> str:
    try:
        if not knowledgearticleid:
            return _json({"error": "knowledgearticleid required"})
        rec = dynamics_client.run_async(dynamics_client.get(
            entity_set="knowledgearticles",
            entity_id=knowledgearticleid,
            select=["knowledgearticleid", "articlepublicnumber", "title",
                    "description", "content", "keywords", "statecode", "modifiedon"],
        ))
        if not rec or rec.get("success") is False:
            return _json({"error": "not_found", "knowledgearticleid": knowledgearticleid})
        rec["content_text"] = _truncate(_strip_html(rec.get("content") or ""))
        rec["description_text"] = _strip_html(rec.get("description") or "")
        rec.pop("content", None)
        return _json(rec)
    except Exception as e:
        return _json({"error": str(e)})


@tool(description=(
    "PRIMARY research tool. Perform a public web search and return the top results as "
    "'TITLE\\nURL\\nSNIPPET' blocks. This is your main instrument for finding real, "
    "current answers. Search iteratively and resourcefully: start with site-restricted "
    "queries against the resolved TechSoup regional site (e.g. `site:techsoup.it csp "
    "registrazione`), then broaden to the global site, the relevant vendor (e.g. Microsoft "
    "for Nonprofits, Google for Nonprofits), and finally the open web. Reformulate queries "
    "with synonyms, the local language, and key concepts until you locate the authoritative "
    "source. Then confirm findings with fetch_document_text. NOTE: if this tool returns a "
    "line beginning with 'SEARCH_INFRA_UNAVAILABLE', the search backend itself failed (not "
    "a genuine empty result) — retry once or twice; if it persists, escalate with "
    "route_decision.reason = 'Research tooling unavailable (web search infrastructure)'."
))
def web_search(query: str, max_results: int = 5) -> str:
    try:
        q = (query or "").strip()
        if not q:
            return "ERROR: empty query"
        try:
            max_n = max(1, min(int(max_results), 10))
        except Exception:
            max_n = 5

        results, infra_errors = _run_search_providers(q, max_n)
        if results:
            lines = [f"Search results for: {q}", ""]
            for i, r in enumerate(results, 1):
                lines.append(f"[{i}] {r['title']}")
                lines.append(f"    URL: {r['url']}")
                if r.get("snippet"):
                    lines.append(f"    {r['snippet']}")
                lines.append("")
            return "\n".join(lines).strip()

        # No results. Distinguish infra failure from a genuine empty result set.
        if infra_errors:
            return (
                f"{_SEARCH_INFRA_UNAVAILABLE}: every search backend failed for query "
                f"'{q}'. Details: {'; '.join(infra_errors)[:500]}"
            )
        return f"No results for query: {q}"
    except Exception as e:
        return f"{_SEARCH_INFRA_UNAVAILABLE}: {e}"


def _run_search_providers(query: str, max_n: int) -> tuple[List[Dict[str, str]], List[str]]:
    """Try the configured provider (if any) first, then fall back to DuckDuckGo.

    Returns (results, infra_errors). `infra_errors` is non-empty only when a backend
    raised/failed — an empty list with no results means the providers genuinely
    returned nothing.

    Provider selection (env): GSC_SEARCH_PROVIDER in {tavily, bing, brave, ddg}.
    Keys: TAVILY_API_KEY, GSC_BING_SEARCH_KEY, GSC_BRAVE_API_KEY.
    """
    provider = (os.getenv("GSC_SEARCH_PROVIDER") or "").strip().lower()
    chain: List[str] = []
    if provider and provider != "ddg":
        chain.append(provider)
    chain.append("ddg")  # always available, no key required

    infra_errors: List[str] = []
    for name in chain:
        try:
            fn = _SEARCH_PROVIDERS.get(name)
            if not fn:
                continue
            results = fn(query, max_n)
            if results:
                return results, []
            # Empty (no error) — try the next backend but don't treat as infra error.
        except Exception as e:
            infra_errors.append(f"{name}: {e}")
            continue
    return [], infra_errors


def _search_ddg(query: str, max_n: int) -> List[Dict[str, str]]:
    url = "https://html.duckduckgo.com/html/"
    with httpx.Client(timeout=_SAFE_TIMEOUT, follow_redirects=True) as client:
        resp = client.post(
            url,
            data={"q": query},
            headers={
                "User-Agent": "Mozilla/5.0 (compatible; GlobalSupportCaseAgent/1.0)",
                "Accept": "text/html",
            },
        )
        resp.raise_for_status()
        html = resp.text
    return _parse_ddg_results(html, max_n)


def _search_tavily(query: str, max_n: int) -> List[Dict[str, str]]:
    key = os.getenv("TAVILY_API_KEY")
    if not key:
        raise RuntimeError("TAVILY_API_KEY not configured")
    with httpx.Client(timeout=_SAFE_TIMEOUT) as client:
        resp = client.post(
            "https://api.tavily.com/search",
            json={
                "api_key": key,
                "query": query,
                "max_results": max_n,
                "search_depth": "basic",
            },
        )
        resp.raise_for_status()
        data = resp.json()
    out: List[Dict[str, str]] = []
    for r in (data.get("results") or [])[:max_n]:
        if r.get("url"):
            out.append({
                "url": r.get("url"),
                "title": r.get("title") or r.get("url"),
                "snippet": (r.get("content") or "")[:400],
            })
    return out


def _search_bing(query: str, max_n: int) -> List[Dict[str, str]]:
    key = os.getenv("GSC_BING_SEARCH_KEY")
    if not key:
        raise RuntimeError("GSC_BING_SEARCH_KEY not configured")
    endpoint = os.getenv("GSC_BING_SEARCH_ENDPOINT", "https://api.bing.microsoft.com/v7.0/search")
    with httpx.Client(timeout=_SAFE_TIMEOUT) as client:
        resp = client.get(
            endpoint,
            params={"q": query, "count": max_n, "responseFilter": "Webpages"},
            headers={"Ocp-Apim-Subscription-Key": key},
        )
        resp.raise_for_status()
        data = resp.json()
    out: List[Dict[str, str]] = []
    for r in ((data.get("webPages") or {}).get("value") or [])[:max_n]:
        if r.get("url"):
            out.append({
                "url": r.get("url"),
                "title": r.get("name") or r.get("url"),
                "snippet": (r.get("snippet") or "")[:400],
            })
    return out


def _search_brave(query: str, max_n: int) -> List[Dict[str, str]]:
    key = os.getenv("GSC_BRAVE_API_KEY")
    if not key:
        raise RuntimeError("GSC_BRAVE_API_KEY not configured")
    with httpx.Client(timeout=_SAFE_TIMEOUT) as client:
        resp = client.get(
            "https://api.search.brave.com/res/v1/web/search",
            params={"q": query, "count": max_n},
            headers={"Accept": "application/json", "X-Subscription-Token": key},
        )
        resp.raise_for_status()
        data = resp.json()
    out: List[Dict[str, str]] = []
    for r in ((data.get("web") or {}).get("results") or [])[:max_n]:
        if r.get("url"):
            out.append({
                "url": r.get("url"),
                "title": r.get("title") or r.get("url"),
                "snippet": (r.get("description") or "")[:400],
            })
    return out


_SEARCH_PROVIDERS = {
    "ddg": _search_ddg,
    "tavily": _search_tavily,
    "bing": _search_bing,
    "brave": _search_brave,
}


def _parse_ddg_results(html: str, max_n: int) -> List[Dict[str, str]]:
    try:
        from html import unescape
        results: List[Dict[str, str]] = []
        pattern = re.compile(
            r'<a[^>]+class="[^"]*result__a[^"]*"[^>]+href="([^"]+)"[^>]*>(.*?)</a>'
            r'(?:.*?<a[^>]+class="[^"]*result__snippet[^"]*"[^>]*>(.*?)</a>)?',
            re.IGNORECASE | re.DOTALL,
        )
        for m in pattern.finditer(html):
            raw_href = unescape(m.group(1))
            title = unescape(re.sub(r"<[^>]+>", "", m.group(2) or "")).strip()
            snippet = unescape(re.sub(r"<[^>]+>", " ", m.group(3) or "")).strip()
            snippet = re.sub(r"\s+", " ", snippet)
            real = raw_href
            try:
                parsed = urlparse(raw_href)
                if "duckduckgo.com" in parsed.netloc and parsed.path.startswith("/l/"):
                    qs = parse_qs(parsed.query)
                    if "uddg" in qs and qs["uddg"]:
                        real = qs["uddg"][0]
                elif raw_href.startswith("//"):
                    real = "https:" + raw_href
            except Exception:
                pass
            if not real or not title:
                continue
            results.append({"url": real, "title": title, "snippet": snippet})
            if len(results) >= max_n:
                break
        return results
    except Exception:
        return []


@tool(description=(
    "Fetch the textual contents of a publicly accessible document URL (PDF/HTML/plain). "
    "Returns at most ~20k chars of extracted text. For long documents, pass the optional "
    "`query` argument (your refined query or key terms) to get the head of the document PLUS "
    "the passages most relevant to that query, instead of a blind first-20k truncation — this "
    "stops the answer being cut off deep in a long page. Use only for URLs returned by "
    "web_search or supplied in the case body. Prefer official .org/.gov/.gob/.gouv/ministry domains."
))
def fetch_document_text(url: str, query: str = "") -> str:
    try:
        parsed = urlparse(url)
        if parsed.scheme not in ("http", "https"):
            return f"ERROR: unsupported URL scheme '{parsed.scheme}'."
        with httpx.Client(timeout=_SAFE_TIMEOUT, follow_redirects=True) as client:
            resp = client.get(url, headers={
                "User-Agent": _BROWSER_UA,
                "Accept": "text/html,application/xhtml+xml,application/pdf,*/*",
                "Accept-Language": "en;q=0.9,*;q=0.5",
            })
            resp.raise_for_status()
            content_type = resp.headers.get("content-type", "").lower()
            data = resp.content[:_MAX_BYTES]
            if "pdf" in content_type or url.lower().endswith(".pdf"):
                text = _extract_pdf_text(data, filename=url)
            elif "html" in content_type or "xml" in content_type:
                text = _strip_html(data.decode("utf-8", errors="replace"))
            else:
                text = data.decode("utf-8", errors="replace")
            if text and len(text) > _PREVIEW_LIMIT and (query or "").strip():
                return _relevant_excerpts(text, query, _PREVIEW_LIMIT)
            return _truncate(text)
    except httpx.HTTPError as e:
        return f"ERROR fetching URL: {e}"
    except Exception as e:
        return f"ERROR: {e}"


def _relevant_excerpts(text: str, query: str, limit: int) -> str:
    """For an over-limit document, return the head plus windows around query-term hits.

    Keeps a head slice for context, then adds ~1200-char windows centred on the
    densest matches of the query terms, merging overlaps, until the budget is spent.
    Falls back to a plain head truncation if no terms match.
    """
    try:
        head_len = min(3000, limit // 3)
        head = text[:head_len]
        lower = text.lower()
        terms = [t for t in re.split(r"\W+", (query or "").lower()) if len(t) > 2]
        if not terms:
            return _truncate(text, limit)

        # Collect match positions.
        positions: List[int] = []
        for term in set(terms):
            start = 0
            while True:
                i = lower.find(term, start)
                if i == -1 or len(positions) > 400:
                    break
                positions.append(i)
                start = i + len(term)
        positions.sort()
        if not positions:
            return (
                head
                + f"\n\n[...no passages matched query terms; showing head only, "
                f"original length {len(text)} chars]"
            )

        win = 1200
        windows: List[tuple[int, int]] = []
        for p in positions:
            s, e = max(0, p - win // 2), min(len(text), p + win // 2)
            if windows and s <= windows[-1][1]:
                windows[-1] = (windows[-1][0], max(windows[-1][1], e))
            else:
                windows.append((s, e))

        budget = limit - head_len
        parts = [head, "\n\n[--- relevant passages (matched query) ---]"]
        for (s, e) in windows:
            if budget <= 0:
                break
            seg = text[s:e]
            seg = seg[:budget]
            budget -= len(seg)
            parts.append(f"\n…{seg}…")
        out = "".join(parts)
        return out + f"\n\n[matched on: {', '.join(sorted(set(terms)))[:200]}; original length {len(text)} chars]"
    except Exception:
        return _truncate(text, limit)


def _extract_pdf_text(raw: bytes, filename: str = "") -> str:
    try:
        try:
            from nonprofit_verifier.pdf_extract import extract_pdf_text  # type: ignore
            text, method = extract_pdf_text(raw, filename=filename)
            if text and not text.startswith("["):
                return f"[extraction-method: {method}]\n{text}"
            return text
        except Exception:
            pass
        try:
            from pypdf import PdfReader
            from io import BytesIO
            reader = PdfReader(BytesIO(raw))
            pages = []
            for page in reader.pages:
                try:
                    pages.append(page.extract_text() or "")
                except Exception:
                    continue
            text = "\n".join(pages).strip()
            return f"[extraction-method: pypdf]\n{text}" if text else "[PDF extraction returned empty text]"
        except Exception as e:
            return f"[PDF extraction unavailable: {e}]"
    except Exception as e:
        return f"[PDF extraction error: {e}]"
