from __future__ import annotations

import base64
import os
import re
from typing import Optional
from urllib.parse import urlparse

import httpx

from foundry_opus import tool


_SAFE_TIMEOUT = 20.0
_MAX_BYTES = 5 * 1024 * 1024  # 5 MB cap for fetched documents

_IMAGE_EXTS = (".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".heif", ".heic")


def _truncate(text: str, limit: int = 20000) -> str:
    try:
        if len(text) <= limit:
            return text
        return text[:limit] + f"\n\n[...truncated, original length {len(text)} chars]"
    except Exception:
        return text


@tool(description="Fetch the textual contents of a publicly accessible document URL (PDF/HTML/plain). Returns at most ~20k chars of extracted text. Use only for URLs supplied in the case payload.")
def fetch_document_text(url: str) -> str:
    try:
        parsed = urlparse(url)
        if parsed.scheme not in ("http", "https"):
            return f"ERROR: unsupported URL scheme '{parsed.scheme}'."
        with httpx.Client(timeout=_SAFE_TIMEOUT, follow_redirects=True) as client:
            resp = client.get(url, headers={"User-Agent": "NonprofitVerificationAgent/1.0"})
            resp.raise_for_status()
            content_type = resp.headers.get("content-type", "").lower()
            data = resp.content[:_MAX_BYTES]

            if "pdf" in content_type or url.lower().endswith(".pdf"):
                text = _extract_pdf_text(data, filename=url)
            elif _is_image(content_type, url):
                text = _extract_image_text(data, filename=url, content_type=content_type)
            elif "html" in content_type or "xml" in content_type:
                text = _strip_html(data.decode("utf-8", errors="replace"))
            else:
                text = data.decode("utf-8", errors="replace")
            return _truncate(text)
    except httpx.HTTPError as e:
        return f"ERROR fetching URL: {e}"
    except Exception as e:
        return f"ERROR: {e}"


@tool(description="Decode a base64-encoded attachment and return its extracted text. content_type may be e.g. 'application/pdf', 'image/png', 'image/jpeg', or 'text/plain'. Image attachments (screenshots/photos of certificates) are OCR'd via Azure AI Document Intelligence. Returns truncated text suitable for analysis.")
def decode_attachment_text(filename: str, content_base64: str, content_type: str = "") -> str:
    try:
        raw = base64.b64decode(content_base64, validate=False)
        ct = (content_type or "").lower()
        if "pdf" in ct or filename.lower().endswith(".pdf"):
            text = _extract_pdf_text(raw, filename=filename)
        elif _is_image(ct, filename):
            text = _extract_image_text(raw, filename=filename, content_type=ct)
        elif "html" in ct:
            text = _strip_html(raw.decode("utf-8", errors="replace"))
        else:
            text = raw.decode("utf-8", errors="replace")
        return _truncate(text)
    except Exception as e:
        return f"ERROR decoding attachment '{filename}': {e}"


@tool(description="Check whether an email sender domain matches the organization's known domain. Returns 'MATCH', 'MISMATCH', 'FREE_PROVIDER', or 'UNKNOWN'.")
def check_email_domain_match(sender_email: str, organization_domain: str = "") -> str:
    try:
        if "@" not in sender_email:
            return "UNKNOWN: invalid sender email"
        sender_domain = sender_email.split("@", 1)[1].strip().lower()
        free_providers = {
            "gmail.com", "yahoo.com", "outlook.com", "hotmail.com",
            "aol.com", "icloud.com", "proton.me", "protonmail.com", "live.com",
        }
        if sender_domain in free_providers:
            return f"FREE_PROVIDER: sender uses {sender_domain} (free email provider — cannot confirm authority by domain)"
        if not organization_domain:
            return f"UNKNOWN: no organization domain on file. Sender domain is {sender_domain}"
        org_domain = organization_domain.strip().lower().lstrip("@")
        if sender_domain == org_domain or sender_domain.endswith("." + org_domain):
            return f"MATCH: {sender_domain} matches organization domain {org_domain}"
        return f"MISMATCH: sender {sender_domain} vs organization {org_domain}"
    except Exception as e:
        return f"ERROR: {e}"


@tool(description="Inspect a candidate registration number using offline heuristics. Returns the likely jurisdiction/format if it matches a known pattern. Does NOT contact any external registry.")
def classify_registration_number(registration_number: str) -> str:
    try:
        rn = (registration_number or "").strip()
        if not rn:
            return "UNKNOWN: empty"
        # US EIN: NN-NNNNNNN
        if re.fullmatch(r"\d{2}-\d{7}", rn):
            return "Likely US EIN (Employer Identification Number) — used for IRS 501(c)(3) verification."
        # UK Charity Commission: 6-7 digits, optional /N suffix
        if re.fullmatch(r"\d{6,7}(-\d{1,2})?", rn):
            return "Possible UK Charity Commission registration number."
        # Canada Registered Charity: 9 digits + RR + 4 digits
        if re.fullmatch(r"\d{9}RR\d{4}", rn, re.IGNORECASE):
            return "Likely Canada CRA Registered Charity number (BN/RR format)."
        # Australia ABN
        if re.fullmatch(r"\d{2}\s?\d{3}\s?\d{3}\s?\d{3}", rn):
            return "Possible Australian Business Number (ABN)."
        # Romania CIF/CUI: 2-10 digits, often prefixed RO for VAT-registered entities
        if re.fullmatch(r"(RO)?\d{2,10}", rn, re.IGNORECASE):
            return "Possible Romanian CIF/CUI (Cod de Identificare Fiscal\u0103) issued by ANAF."
        # Generic European VAT pattern (country code + 8-12 digits)
        if re.fullmatch(r"[A-Z]{2}\d{8,12}", rn):
            return "Possible European VAT/registration number (country-prefixed)."
        return f"UNRECOGNIZED format: {rn} — may still be valid in another jurisdiction."

    except Exception as e:
        return f"ERROR: {e}"


@tool(description="Search the provided document text for keyword indicators of authenticity (e.g. 'IRS', 'Charity Commission', 'seal', registration markers). Returns a list of indicators found.")
def scan_authenticity_indicators(text: str) -> str:
    try:
        if not text:
            return "No text provided."
        indicators = {
            # English / US / UK / CA / AU
            "IRS": r"\b(IRS|Internal Revenue Service)\b",
            "501(c)(3) language": r"501\s*\(\s*c\s*\)\s*\(\s*3\s*\)",
            "EIN reference": r"\bE\.?I\.?N\.?\b|\bEmployer Identification Number\b",
            "UK Charity Commission": r"Charity\s+Commission",
            "Canada CRA": r"Canada Revenue Agency|CRA",
            "Official seal mention": r"\b(seal|stamp)\b",
            "Letterhead clue": r"letterhead|official letter",
            "Signature line": r"\bsignature\b|/s/|signed by",
            "Issue date": r"\b(issued|date of issue)\b",
            "Expiration": r"\b(expires|expiration|valid through|valid until)\b",
            "Tax exempt": r"tax[- ]exempt",
            "Determination letter": r"determination letter",
            # Romanian nonprofit / fiscal authority indicators
            "Romania ANAF": r"\bANAF\b|Agen[\u0163t]ia\s+Na[\u0163t]ional[a\u0103]\s+de\s+Administrare\s+Fiscal[a\u0103]",
            "Romania Ministerul Finantelor": r"Ministerul\s+Finan[\u0163t]elor",
            "Romania CIF/CUI label": r"\bC\.?I\.?F\.?\b|\bC\.?U\.?I\.?\b|Cod\s+de\s+[\u00ceI]nregistrare\s+Fiscal[a\u0103]",
            "Romania Asociatie/Fundatie": r"\bAsocia[\u0163t]i[ae]\b|\bFunda[\u0163t]i[ae]\b",
            "Romania OG 26/2000": r"O\.?G\.?\s*(nr\.?\s*)?26\s*[\\/]?\s*2000",
            "Romania Judecatorie/Incheiere": r"Judec[a\u0103]torie|[\u00ceI]ncheiere|Sentin[\u0163t][a\u0103]",
            "Romania Registrul Asociatiilor": r"Registrul\s+(special|na[\u0163t]ional)\s+al\s+(Asocia[\u0163t]iilor|Funda[\u0163t]iilor)",
            "Romania Act constitutiv/Statut": r"Act\s+constitutiv|\bStatut(ul)?\b",
            "Romania Stampila/Semnatura": r"[\u015fs]tampil[a\u0103]|semn[a\u0103]tur[a\u0103]",
            "Romania Certificat": r"Certificat\s+de\s+[\u00ceI]nregistrare",
            "Romania Direct date": r"Direc[\u0163t]ia\s+General[a\u0103]\s+Regional[a\u0103]\s+a\s+Finan[\u0163t]elor\s+Publice",
        }
        found = []
        for label, pattern in indicators.items():
            if re.search(pattern, text, re.IGNORECASE):
                found.append(label)
        if not found:
            return "No common authenticity indicators detected."
        return "Indicators found: " + ", ".join(found)
    except Exception as e:
        return f"ERROR: {e}"


def _strip_html(html: str) -> str:
    try:
        text = re.sub(r"<script[\s\S]*?</script>", " ", html, flags=re.IGNORECASE)
        text = re.sub(r"<style[\s\S]*?</style>", " ", text, flags=re.IGNORECASE)
        text = re.sub(r"<[^>]+>", " ", text)
        text = re.sub(r"\s+", " ", text)
        return text.strip()
    except Exception:
        return html


def _extract_pdf_text(data: bytes, filename: str = "") -> str:
    try:
        from .pdf_extract import extract_pdf_text
        text, method = extract_pdf_text(data, filename=filename)
        if text and not text.startswith("["):
            return f"[extraction-method: {method}]\n{text}"
        return text
    except Exception as e:
        return f"[PDF extraction error: {e}]"


def _is_image(content_type: str, filename_or_url: str = "") -> bool:
    try:
        ct = (content_type or "").lower()
        if ct.startswith("image/"):
            return True
        name = (filename_or_url or "").lower()
        return name.endswith(_IMAGE_EXTS)
    except Exception:
        return False


def _extract_image_text(data: bytes, filename: str = "", content_type: str = "") -> str:
    try:
        endpoint = os.getenv("DOCINTEL_ENDPOINT")
        key = os.getenv("DOCINTEL_KEY")
        if not endpoint or not key:
            return (
                "[image OCR unavailable: DOCINTEL_ENDPOINT / DOCINTEL_KEY are not configured. "
                f"filename={filename}, content_type={content_type}, bytes={len(data)}]"
            )
        try:
            from azure.ai.documentintelligence import DocumentIntelligenceClient
            from azure.ai.documentintelligence.models import AnalyzeDocumentRequest
            from azure.core.credentials import AzureKeyCredential
        except Exception as imp_ex:
            return f"[image OCR unavailable: azure-ai-documentintelligence not installed: {imp_ex}]"

        client = DocumentIntelligenceClient(
            endpoint=endpoint.rstrip("/"),
            credential=AzureKeyCredential(key),
        )
        poller = client.begin_analyze_document(
            "prebuilt-read",
            AnalyzeDocumentRequest(bytes_source=data),
        )
        result = poller.result()
        text = (getattr(result, "content", None) or "").strip()
        if not text:
            return f"[image OCR returned no text. filename={filename}, content_type={content_type}, bytes={len(data)}]"
        return f"[extraction-method: docintel-prebuilt-read]\n{text}"
    except Exception as e:
        return f"[image OCR error for '{filename}': {e}]"


@tool(description=(
    "Perform a public web search (DuckDuckGo HTML, no API key) and return the top results "
    "as a list of 'TITLE\\nURL\\nSNIPPET' blocks. Use this to locate official non-profit/charity "
    "registries, government agency lookup pages, or registry public-search portals for any country. "
    "Then call fetch_document_text(url) on the most promising official .gov/.gob/.gouv/ministry "
    "domain result. Combine multiple queries (organization name, registration number, registry name) "
    "for cross-verification."
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
        url = "https://html.duckduckgo.com/html/"
        with httpx.Client(timeout=_SAFE_TIMEOUT, follow_redirects=True) as client:
            resp = client.post(
                url,
                data={"q": q},
                headers={
                    "User-Agent": "Mozilla/5.0 (compatible; NonprofitVerificationAgent/1.0)",
                    "Accept": "text/html",
                },
            )
            resp.raise_for_status()
            html = resp.text
        results = _parse_ddg_results(html, max_n)
        if not results:
            return f"No results for query: {q}"
        lines = [f"Search results for: {q}", ""]
        for i, r in enumerate(results, 1):
            lines.append(f"[{i}] {r['title']}")
            lines.append(f"    URL: {r['url']}")
            if r.get("snippet"):
                lines.append(f"    {r['snippet']}")
            lines.append("")
        return "\n".join(lines).strip()
    except httpx.HTTPError as e:
        return f"ERROR running web search: {e}"
    except Exception as e:
        return f"ERROR: {e}"


def _parse_ddg_results(html: str, max_n: int) -> list[dict]:
    try:
        from html import unescape
        from urllib.parse import parse_qs, urlparse as _urlparse
        results: list[dict] = []
        # DuckDuckGo HTML lite: each result has class "result__a" anchor and "result__snippet"
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
            # DDG HTML wraps real URL in /l/?uddg=<url>&...
            real = raw_href
            try:
                parsed = _urlparse(raw_href)
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
    "Look up advisory hints learned from prior verification runs. "
    "Hints are NOT confirmation \u2014 they are leads you must re-verify live. "
    "Use this after `dynamics_get_case_overview` once you know the organization's country. "
    "Common patterns to try: "
    "category='RegistryUrlTemplate' scope_key=<ISO country code> to find known working registry URL patterns; "
    "category='BlockedSource' scope_key='global' to find sources that previously failed (CAPTCHA, geo-block); "
    "category='Registry' scope_key=<ISO> for known registry names; "
    "category='QueryPattern' scope_key=<ISO> for previously successful web_search queries; "
    "category='DocPattern' scope_key=<ISO> for document authenticity patterns. "
    "Each result contains a 'Ref' field \u2014 if you use a hint and it works (or fails), grade it via "
    "memory_proposals[] in your final submit_verification_result call with action='feedback', "
    "ref=<that Ref>, outcome='success'|'failure'."
))
def memory_lookup(
    category: str,
    scope_key: str,
    subject_contains: Optional[str] = None,
    max_results: int = 8,
) -> str:
    try:
        import json as _json
        import sys as _sys
        # Service module lives one directory up from the nonprofit_verifier package.
        try:
            import npv_memory_service as _mem
        except ImportError:
            _here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            if _here not in _sys.path:
                _sys.path.insert(0, _here)
            import npv_memory_service as _mem
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
        return _json.dumps({"count": len(slim), "results": slim, "advisory": "Hints are NOT confirmation \u2014 verify live this run."}, default=str)
    except Exception as e:
        return f"[memory_lookup error: {e}]"


ALL_TOOLS = [
    fetch_document_text,
    decode_attachment_text,
    check_email_domain_match,
    classify_registration_number,
    scan_authenticity_indicators,
    web_search,
    memory_lookup,
]
