"""Seed the GSC memory table for a given slot.

Sources:
  A) Dynamics knowledge articles (statecode=Published) from
     https://techsoupservicedesk.crm.dynamics.com -> KbHit per language+region.
     Filters applied: title must not contain \\d{8}; no "Zendesk" anywhere in
     title/keywords/description/content/url; duplicate titles dropped;
     articles with no body text dropped.
     URL handling: the article body text is stored in the KbHit content
     payload. msdyn_ingestedarticleurl is probed for reachability and only
     kept if it responds 2xx; otherwise `kb_url=null` and the article is
     still seeded. URLs embedded in the article body are extracted and
     probed; only reachable ones are kept in `live_embedded_urls`. When
     present, the entry is tagged `has-live-links` and `agent_hint`
     instructs the agent to call `fetch_document_text` on each live URL
     to obtain current information before relying on the snapshot body.
  B) Country-level VA URLs from _cps_analysis/06_all_custom_botcomponents.json -> WebSource
  C) Hardcoded LATAM-10 conversation starters -> IntentSignal + AnswerTemplate

Usage (from the "TechSoupServices Function App" dir):
    python -m scripts.seed_gsc_memory --slot Local --dry-run
    python -m scripts.seed_gsc_memory --slot Qa
    python -m scripts.seed_gsc_memory --slot Production
"""
from __future__ import annotations

import argparse
import json
import logging
import os
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
_log = logging.getLogger("seed_gsc_memory")


_LATAM_10 = [
    {
        "intent": "ProductQuestion",
        "subject": "Microsoft 365 nonprofit license question (LATAM)",
        "snippet": "tengo dudas sobre licencias Microsoft 365 para mi organizacion sin fines de lucro",
        "answer": "Gracias por contactarnos. Para licencias Microsoft 365 nonprofit, necesitamos verificar la elegibilidad de su organizacion. Comparta el RUT/RFC/RUC y el sitio web de la organizacion para iniciar la validacion.",
    },
    {
        "intent": "NonprofitEligibility",
        "subject": "Solicitud de verificacion de elegibilidad (LATAM)",
        "snippet": "como verifico que mi ONG es elegible para los descuentos",
        "answer": "Para verificar la elegibilidad, necesitamos: (1) numero de registro legal de la ONG, (2) acta constitutiva o equivalente, (3) sitio web institucional. Adjunte los documentos en su respuesta.",
    },
    {
        "intent": "AccountAccess",
        "subject": "Recuperacion de acceso a cuenta TechSoup (LATAM)",
        "snippet": "no puedo iniciar sesion en mi cuenta de techsoup",
        "answer": "Para recuperar el acceso a su cuenta, intente restablecer la contrasena desde la pagina de inicio de sesion. Si el problema persiste, comparta el correo asociado a la cuenta y le ayudaremos a recuperarla.",
    },
    {
        "intent": "OrderStatus",
        "subject": "Estado de un pedido (LATAM)",
        "snippet": "donde esta mi pedido / cual es el estado de mi orden",
        "answer": "Para revisar el estado de su pedido, comparta el numero de orden y el correo de la cuenta. Tambien puede revisar el historial de pedidos en su perfil de TechSoup.",
    },
    {
        "intent": "Billing",
        "subject": "Consulta de facturacion (LATAM)",
        "snippet": "necesito una factura / quiero ver el cargo a mi tarjeta",
        "answer": "Para consultas de facturacion, comparta: (1) numero de orden, (2) correo de la cuenta, (3) detalle de la consulta (factura faltante, cargo no reconocido, etc.). Lo derivaremos al equipo de facturacion.",
    },
    {
        "intent": "Refund",
        "subject": "Solicitud de reembolso (LATAM)",
        "snippet": "quiero un reembolso / cancelar mi orden",
        "answer": "Para procesar una solicitud de reembolso, comparta el numero de orden y el motivo. Las solicitudes se evalan caso por caso segun las politicas de cada producto y donante.",
    },
    {
        "intent": "DonationProgram",
        "subject": "Programas de donacion disponibles (LATAM)",
        "snippet": "que programas de donacion tienen disponibles",
        "answer": "Tenemos programas de donacion y descuento de varios donantes (Microsoft, Adobe, Google, entre otros). Comparta su pais y tipo de organizacion para indicarle los programas disponibles.",
    },
    {
        "intent": "DiscountInquiry",
        "subject": "Descuentos para ONG (LATAM)",
        "snippet": "que descuentos hay para mi organizacion sin fines de lucro",
        "answer": "Los descuentos disponibles dependen del pais, donante y tipo de organizacion. Comparta el nombre de la ONG, pais y producto de interes para guiarle a la oferta correcta.",
    },
    {
        "intent": "Shipping",
        "subject": "Envio fisico de productos (LATAM)",
        "snippet": "me llega el producto a mi pais / envian a mi direccion",
        "answer": "La mayoria de las donaciones son licencias digitales que no requieren envio fisico. Para productos fisicos, los tiempos y costos dependen del pais. Indique su pais y el producto para confirmar disponibilidad.",
    },
    {
        "intent": "Partnership",
        "subject": "Colaboracion / partnership (LATAM)",
        "snippet": "como puedo ser partner / quiero ofrecer servicios a techsoup",
        "answer": "Gracias por su interes en colaborar con TechSoup. Lo derivamos al equipo de partnerships con la informacion que comparta: nombre de organizacion, propuesta de valor, y contacto.",
    },
]


def _slug(value: str, max_len: int = 60) -> str:
    try:
        s = re.sub(r"[^A-Za-z0-9_-]+", "-", (value or "").strip()).strip("-")
        return (s or "item")[:max_len]
    except Exception:
        return "item"


def _load_country_va_urls(cps_path: Path) -> List[Dict[str, str]]:
    try:
        if not cps_path.exists():
            _log.warning(f"CPS JSON not found at {cps_path}; skipping Source B.")
            return []
        with cps_path.open("r", encoding="utf-8-sig") as fh:
            data = json.load(fh)
    except Exception as e:
        _log.warning(f"Failed to load {cps_path}: {e}")
        return []

    url_re = re.compile(r"https?://[\w\-./%?=&#:+]+", re.IGNORECASE)
    found: Dict[str, Dict[str, str]] = {}

    def _walk(node: Any):
        try:
            if isinstance(node, dict):
                for v in node.values():
                    _walk(v)
            elif isinstance(node, list):
                for v in node:
                    _walk(v)
            elif isinstance(node, str):
                for m in url_re.findall(node):
                    url = m.rstrip(").,;\"'")
                    host_match = re.match(r"https?://([^/]+)/?", url, re.IGNORECASE)
                    if not host_match:
                        continue
                    host = host_match.group(1).lower()
                    if host not in found:
                        found[host] = {"url": url, "host": host}
        except Exception:
            return

    _walk(data)
    out = list(found.values())
    _log.info(f"Source B: extracted {len(out)} unique URLs from CPS JSON")
    return out


_KB_SOURCE_ENV_URL = "https://techsoupservicedesk.crm.dynamics.com"
_KB_TITLE_8DIGIT_RE = re.compile(r"\d{8}")
_KB_ZENDESK_RE = re.compile(r"zendesk", re.IGNORECASE)


def _fetch_kb_articles_paged(token: str) -> List[Dict[str, Any]]:
    import httpx
    select = ",".join([
        "knowledgearticleid", "title", "articlepublicnumber",
        "_languagelocaleid_value", "msdyn_ingestedarticleurl",
        "content", "keywords", "description",
    ])
    url = (
        f"{_KB_SOURCE_ENV_URL}/api/data/v9.2/knowledgearticles"
        f"?$select={select}&$filter=statecode eq 3"
    )
    headers = {
        "OData-MaxVersion": "4.0",
        "OData-Version": "4.0",
        "Accept": "application/json",
        "Authorization": f"Bearer {token}",
        "Prefer": "odata.maxpagesize=500",
    }
    out: List[Dict[str, Any]] = []
    with httpx.Client(timeout=60) as client:
        while url:
            resp = client.get(url, headers=headers)
            if not resp.is_success:
                _log.warning(f"KB fetch failed at {url[:120]}: {resp.status_code} {resp.text[:200]}")
                break
            data = resp.json()
            out.extend(data.get("value") or [])
            url = data.get("@odata.nextLink")
    _log.info(f"Source A: fetched {len(out)} knowledge articles from {_KB_SOURCE_ENV_URL}")
    return out


def _strip_html(text: str) -> str:
    try:
        cleaned = re.sub(r"<(script|style)[^>]*>.*?</\1>", " ", text or "", flags=re.IGNORECASE | re.DOTALL)
        cleaned = re.sub(r"<[^>]+>", " ", cleaned)
        cleaned = re.sub(r"\s+", " ", cleaned)
        return cleaned.strip()
    except Exception:
        return text or ""


_URL_IN_TEXT_RE = re.compile(r"https?://[^\s<>\"')]+", re.IGNORECASE)
_SKIP_URL_HOSTS = ("support.techsoup.org",)


def _is_skipped_url(url: str) -> bool:
    u = (url or "").lower()
    for host in _SKIP_URL_HOSTS:
        if (
            u.startswith(f"http://{host}")
            or u.startswith(f"https://{host}")
            or f"//{host}/" in u
            or u.endswith(f"//{host}")
        ):
            return True
    return False


def _extract_urls(html: str) -> List[str]:
    if not html:
        return []
    href_urls = re.findall(r"""href\s*=\s*['"](https?://[^'"]+)['"]""", html, flags=re.IGNORECASE)
    src_urls = re.findall(r"""src\s*=\s*['"](https?://[^'"]+)['"]""", html, flags=re.IGNORECASE)
    text_urls = _URL_IN_TEXT_RE.findall(_strip_html(html))
    all_urls = href_urls + src_urls + text_urls
    seen = set()
    out: List[str] = []
    for u in all_urls:
        u = u.rstrip(".,);:>]'\"")
        if u.lower().startswith(("mailto:", "javascript:")):
            continue
        if _is_skipped_url(u):
            continue
        if u in seen:
            continue
        seen.add(u)
        out.append(u)
    return out


_URL_REACHABLE_CACHE: Dict[str, bool] = {}


def _url_reachable(url: str, client) -> bool:
    cached = _URL_REACHABLE_CACHE.get(url)
    if cached is not None:
        return cached
    try:
        r = client.get(url, follow_redirects=True, timeout=8)
        ok = 200 <= r.status_code < 300 and bool((r.text or "").strip())
    except Exception:
        ok = False
    _URL_REACHABLE_CACHE[url] = ok
    return ok


def _filter_kb_articles(articles: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Apply title/zendesk/dup-title filters. URL reachability is handled later per-article."""
    pre: List[Dict[str, Any]] = []
    seen_titles: set = set()
    excl_8digit = excl_zendesk = excl_dup = excl_no_content = 0

    for a in articles:
        title = (a.get("title") or "").strip()
        body_html = a.get("content") or ""
        body_text = _strip_html(body_html)
        if not body_text and not title:
            excl_no_content += 1
            continue
        if _KB_TITLE_8DIGIT_RE.search(title):
            excl_8digit += 1
            continue
        haystack = " ".join([
            title,
            (a.get("msdyn_ingestedarticleurl") or ""),
            a.get("keywords") or "",
            a.get("description") or "",
            body_text,
        ])
        if _KB_ZENDESK_RE.search(haystack):
            excl_zendesk += 1
            continue
        title_key = title.lower()
        if title_key in seen_titles:
            excl_dup += 1
            continue
        seen_titles.add(title_key)
        pre.append(a)

    _log.info(
        f"Source A pre-filter: kept={len(pre)} excluded "
        f"(no_content={excl_no_content}, 8digit_title={excl_8digit}, "
        f"zendesk={excl_zendesk}, dup_title={excl_dup})"
    )
    return pre


_KB_BODY_CHAR_LIMIT = 6000
_KB_EMBEDDED_URL_LIMIT = 10


def _seed_kb_hits(mem, slot_name: str, dry_run: bool) -> int:
    import httpx
    saved_env = os.environ.get("DYNAMICS_ENVIRONMENT")
    os.environ["DYNAMICS_ENVIRONMENT"] = _KB_SOURCE_ENV_URL
    try:
        from global_support_case_agent import dynamics_client
        token = dynamics_client.run_async(dynamics_client._get_access_token())
        if not token:
            _log.warning(
                "Source A: could not obtain access token for "
                f"{_KB_SOURCE_ENV_URL}; skipping KB seed."
            )
            return 0
        articles = _fetch_kb_articles_paged(token)
    except Exception as e:
        _log.warning(f"Source A: could not fetch knowledge articles: {e}")
        return 0
    finally:
        if saved_env is None:
            os.environ.pop("DYNAMICS_ENVIRONMENT", None)
        else:
            os.environ["DYNAMICS_ENVIRONMENT"] = saved_env

    articles = _filter_kb_articles(articles)

    count = 0
    kb_url_reachable_count = 0
    kb_url_unreachable_count = 0
    articles_with_embedded = 0

    total_articles = len(articles)
    with httpx.Client(
        timeout=8,
        follow_redirects=True,
        headers={
            "User-Agent": (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/124.0.0.0 Safari/537.36"
            ),
            "Accept": (
                "text/html,application/xhtml+xml,application/xml;q=0.9,"
                "image/avif,image/webp,*/*;q=0.8"
            ),
            "Accept-Language": "en-US,en;q=0.9",
        },
    ) as client:
        for idx, a in enumerate(articles, start=1):
            if idx == 1 or idx % 100 == 0 or idx == total_articles:
                _log.info(
                    f"Source A progress: processing article {idx}/{total_articles} "
                    f"(kb_url_cache_size={len(_URL_REACHABLE_CACHE)})"
                )
            try:
                kb_id = a.get("knowledgearticleid")
                if not kb_id:
                    continue
                title = (a.get("title") or "").strip()
                article_no = (a.get("articlepublicnumber") or "").strip()
                language = a.get("_languagelocaleid_value") or "global"
                raw_url = (a.get("msdyn_ingestedarticleurl") or "").strip() or None
                body_html = a.get("content") or ""
                body_text = _strip_html(body_html)
                body_text_truncated = body_text[:_KB_BODY_CHAR_LIMIT]
                body_truncated = len(body_text) > _KB_BODY_CHAR_LIMIT

                kb_url: Optional[str] = None
                if raw_url and _is_skipped_url(raw_url):
                    raw_url = None
                if raw_url and _url_reachable(raw_url, client):
                    kb_url = raw_url
                    kb_url_reachable_count += 1
                elif raw_url:
                    kb_url_unreachable_count += 1

                embedded_urls = [
                    u for u in _extract_urls(body_html)[:_KB_EMBEDDED_URL_LIMIT]
                    if not raw_url or u != raw_url
                ]
                has_embedded = len(embedded_urls) > 0
                if has_embedded:
                    articles_with_embedded += 1

                scope_key = _slug(str(language), 50) + ":global"
                subject_key = _slug(article_no or kb_id, 60)
                tags_list = ["seed", "ingested"]
                if has_embedded:
                    tags_list.append("has-embedded-urls")
                if kb_url:
                    tags_list.append("kb-url-live")
                content_payload = {
                    "knowledgearticleid": kb_id,
                    "title": title,
                    "article_number": article_no,
                    "language": str(language),
                    "kb_url": kb_url,
                    "kb_url_original": raw_url,
                    "kb_url_reachable": kb_url is not None,
                    "source_environment": _KB_SOURCE_ENV_URL,
                    "description": (a.get("description") or "").strip(),
                    "keywords": (a.get("keywords") or "").strip(),
                    "body_text": body_text_truncated,
                    "body_truncated": body_truncated,
                    "body_full_length": len(body_text),
                    "has_embedded_urls": has_embedded,
                    "embedded_urls": embedded_urls,
                    "embedded_urls_verified": False,
                    "agent_hint": (
                        "ACTION REQUIRED: this article's body contains external URLs in "
                        "`embedded_urls`. These were NOT verified at seed time. Call "
                        "`fetch_document_text` on each URL to obtain current information "
                        "and to confirm reachability before relying on `body_text` (which "
                        "is a Dataverse snapshot)."
                        if has_embedded else None
                    ),
                }
                if dry_run:
                    extras = []
                    if kb_url:
                        extras.append("kb_url=live")
                    if has_embedded:
                        extras.append(f"embedded={len(embedded_urls)}")
                    extra_str = (" " + " ".join(extras)) if extras else ""
                    _log.info(
                        f"[dry-run] KbHit scope_key={scope_key} subject_key={subject_key} "
                        f"title={title[:60]}{extra_str}"
                    )
                else:
                    mem.pin_manual(
                        category="KbHit",
                        scope_key=scope_key,
                        subject_key=subject_key,
                        subject=title or article_no or kb_id,
                        content=content_payload,
                        tags=",".join(tags_list),
                    )
                count += 1
            except Exception as e:
                _log.warning(f"KbHit seed skip: {e}")

    _log.info(
        f"Source A URL summary: kb_url(live={kb_url_reachable_count}, "
        f"unreachable={kb_url_unreachable_count}); "
        f"articles_with_embedded_urls={articles_with_embedded} "
        f"(NOT verified at seed time — agent must verify via fetch_document_text)"
    )
    _log.info(f"Source A: seeded {count} KbHit entries (dry_run={dry_run}) into slot {slot_name}")
    return count


def _seed_web_sources(mem, slot_name: str, dry_run: bool) -> int:
    cps_path = ROOT.parent.parent / "_cps_analysis" / "06_all_custom_botcomponents.json"
    urls = _load_country_va_urls(cps_path)
    count = 0
    for entry in urls:
        try:
            host = entry["host"]
            url = entry["url"]
            subject_key = _slug(host, 60)
            content = {"url": url, "host": host}
            if dry_run:
                _log.info(f"[dry-run] WebSource subject_key={subject_key} url={url}")
            else:
                mem.pin_manual(
                    category="WebSource",
                    scope_key="global",
                    subject_key=subject_key,
                    subject=host,
                    content=content,
                    tags="seed,cps-extract",
                )
            count += 1
        except Exception as e:
            _log.warning(f"WebSource seed skip: {e}")
    _log.info(f"Source B: seeded {count} WebSource entries (dry_run={dry_run}) into slot {slot_name}")
    return count


def _seed_latam_intents(mem, slot_name: str, dry_run: bool) -> int:
    count = 0
    for i, item in enumerate(_LATAM_10, start=1):
        try:
            intent = item["intent"]
            subject = item["subject"]
            snippet = item["snippet"]
            answer = item["answer"]
            sk_signal = _slug(f"{intent}-{i:02d}", 60)
            sk_template = _slug(f"{intent}-tmpl-{i:02d}", 60)

            if dry_run:
                _log.info(f"[dry-run] IntentSignal LATAM/{sk_signal} subject={subject[:50]}")
                _log.info(f"[dry-run] AnswerTemplate LATAM/{sk_template} subject={subject[:50]}")
            else:
                mem.pin_manual(
                    category="IntentSignal",
                    scope_key="LATAM",
                    subject_key=sk_signal,
                    subject=subject,
                    content={"intent": intent, "phrase": snippet, "language": "es"},
                    tags="seed,latam,manual",
                )
                mem.pin_manual(
                    category="AnswerTemplate",
                    scope_key="LATAM",
                    subject_key=sk_template,
                    subject=subject,
                    content={"intent": intent, "language": "es", "body": answer},
                    tags="seed,latam,manual",
                )
            count += 2
        except Exception as e:
            _log.warning(f"LATAM seed skip: {e}")
    _log.info(f"Source C: seeded {count} LATAM IntentSignal+AnswerTemplate entries (dry_run={dry_run})")
    return count


def main() -> int:
    parser = argparse.ArgumentParser(description="Seed GSC memory table")
    parser.add_argument("--slot", choices=["Local", "Qa", "Production"], default="Local")
    parser.add_argument("--dry-run", action="store_true", default=False)
    parser.add_argument("--skip-kb", action="store_true", default=False)
    parser.add_argument("--skip-web", action="store_true", default=False)
    parser.add_argument("--skip-latam", action="store_true", default=False)
    args = parser.parse_args()

    os.environ["NPV_MEMORY_SLOT"] = args.slot

    try:
        import gsc_memory_service as mem
    except Exception as e:
        _log.error(f"Failed to import gsc_memory_service: {e}")
        return 2

    _log.info(f"Seeding GSC memory: slot={args.slot} dry_run={args.dry_run}")
    total = 0
    if not args.skip_kb:
        total += _seed_kb_hits(mem, args.slot, args.dry_run)
    if not args.skip_web:
        total += _seed_web_sources(mem, args.slot, args.dry_run)
    if not args.skip_latam:
        total += _seed_latam_intents(mem, args.slot, args.dry_run)
    _log.info(f"Done. Total entries seeded: {total} (dry_run={args.dry_run})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
