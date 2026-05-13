"""Shared PDF text extraction with OCR fallback.

Strategy:
  1. Try pypdf first (fast, cheap, works for digitally generated PDFs).
  2. If pypdf returns essentially no text (< MIN_TEXT_THRESHOLD chars), the PDF
     is image-only / scanned. Fall back to Claude vision via Foundry.
  3. Cache results in-process to avoid repeated OCR of the same bytes within
     a single agent run.

The OCR fallback uses Anthropic's native PDF document content blocks, which
have visual/OCR understanding built in. No external Tesseract/Poppler needed.
"""
from __future__ import annotations

import base64
import hashlib
import logging
import os
import sys
import threading
from io import BytesIO
from typing import Optional

_log = logging.getLogger("npv.ocr")

def _dbg(msg: str) -> None:
    try:
        _log.warning(msg)
    except Exception:
        pass
    try:
        print(msg, flush=True)
        sys.stdout.flush()
    except Exception:
        pass

MIN_TEXT_THRESHOLD = 100
MAX_OCR_BYTES = 30 * 1024 * 1024  # Anthropic PDF limit ~32 MB
OCR_MAX_TOKENS = 8000
# Above these thresholds we split the PDF page-by-page before OCR — sending an
# 8MB / 16-page PDF as a single request times out the upstream Foundry-Anthropic
# relay. Per-page calls finish in 10-30 s each.
OCR_CHUNK_SIZE_BYTES = 4 * 1024 * 1024
OCR_CHUNK_PAGE_COUNT = 4
OCR_PAGES_PER_CHUNK = 1
OCR_PER_CALL_TIMEOUT = float(os.getenv("NPV_OCR_PER_CALL_TIMEOUT") or "180")
OCR_PER_CALL_RETRIES = 0
OCR_PARALLEL_PAGES = max(1, int(os.getenv("NPV_OCR_PARALLEL_PAGES") or "6"))
OCR_DEBUG = (os.getenv("NPV_OCR_DEBUG") or "").strip().lower() in ("1", "true", "yes")
OCR_PROMPT = (
    "You are a high-fidelity OCR transcriber. Extract ALL text from the "
    "attached PDF VERBATIM, including handwritten notes, stamps, seals, "
    "header/footer text, signatures captions, registration numbers, dates, "
    "and any annotations. Preserve original line breaks. Do not translate. "
    "Do not summarize. Output ONLY the raw extracted text — no commentary, "
    "no markdown fences."
)

_cache_lock = threading.Lock()
_ocr_cache: dict[str, str] = {}


def _hash_bytes(data: bytes) -> str:
    try:
        return hashlib.sha256(data).hexdigest()
    except Exception:
        return ""


def pypdf_extract(data: bytes, max_pages: int = 50) -> str:
    try:
        try:
            from pypdf import PdfReader  # type: ignore
        except ImportError:
            from PyPDF2 import PdfReader  # type: ignore
        reader = PdfReader(BytesIO(data))
        chunks = []
        for page in reader.pages[:max_pages]:
            try:
                chunks.append(page.extract_text() or "")
            except Exception:
                continue
        return "\n".join(chunks).strip()
    except Exception as e:
        return f"[pypdf error: {e}]"


def _ocr_disabled() -> bool:
    return (os.getenv("NPV_DISABLE_PDF_OCR") or "").strip().lower() in ("1", "true", "yes")


def _build_ocr_client():
    from foundry_opus import FoundryClient  # type: ignore
    from foundry_opus.config import FoundryConfig  # type: ignore
    cfg = FoundryConfig.from_env()
    cfg.timeout = OCR_PER_CALL_TIMEOUT
    cfg.max_retries = OCR_PER_CALL_RETRIES
    return FoundryClient(cfg)


def _ocr_single_pdf_with_timeout(client, data: bytes, timeout: float) -> str:
    """Run _ocr_single_pdf in a daemon thread with a hard wall-clock timeout.

    The Anthropic SDK timeout is unreliable through the Foundry relay (HTTPS
    socket can stall indefinitely without firing the configured read timeout).
    A daemon thread can be safely abandoned if it never returns; the process
    will reclaim it on exit.
    """
    import threading as _t
    result_box: dict = {}

    def _worker():
        try:
            result_box["text"] = _ocr_single_pdf(client, data)
        except BaseException as e:  # noqa: BLE001
            result_box["error"] = e

    th = _t.Thread(target=_worker, name="pdf-ocr", daemon=True)
    th.start()
    th.join(timeout=timeout)
    if th.is_alive():
        # Thread is wedged on socket. Abandon it; daemon=True means it dies on
        # process exit. Surface a clean exception to the caller so the per-page
        # loop records it and proceeds to the next page.
        raise TimeoutError(f"OCR call exceeded {timeout}s wall-clock timeout")
    if "error" in result_box:
        raise result_box["error"]
    return result_box.get("text", "")


def _ocr_single_pdf(client, data: bytes) -> str:
    b64 = base64.b64encode(data).decode("ascii")
    resp = client.chat(
        messages=[
            {
                "role": "user",
                "content": [
                    {
                        "type": "document",
                        "source": {
                            "type": "base64",
                            "media_type": "application/pdf",
                            "data": b64,
                        },
                    },
                    {"type": "text", "text": OCR_PROMPT},
                ],
            }
        ],
        max_tokens=OCR_MAX_TOKENS,
    )
    parts = []
    for block in (getattr(resp, "content", []) or []):
        if getattr(block, "type", None) == "text":
            parts.append(getattr(block, "text", "") or "")
    return "\n".join(parts).strip()


def _split_pdf_pages(data: bytes, pages_per_chunk: int = OCR_PAGES_PER_CHUNK) -> list[bytes]:
    """Split a PDF into smaller PDFs of `pages_per_chunk` pages each."""
    try:
        try:
            from pypdf import PdfReader, PdfWriter  # type: ignore
        except ImportError:
            from PyPDF2 import PdfReader, PdfWriter  # type: ignore
        reader = PdfReader(BytesIO(data))
        chunks: list[bytes] = []
        total = len(reader.pages)
        for start in range(0, total, pages_per_chunk):
            writer = PdfWriter()
            for i in range(start, min(start + pages_per_chunk, total)):
                writer.add_page(reader.pages[i])
            buf = BytesIO()
            writer.write(buf)
            chunks.append(buf.getvalue())
        return chunks
    except Exception:
        return []


def _needs_chunking(data: bytes) -> bool:
    if len(data) > OCR_CHUNK_SIZE_BYTES:
        return True
    try:
        try:
            from pypdf import PdfReader  # type: ignore
        except ImportError:
            from PyPDF2 import PdfReader  # type: ignore
        reader = PdfReader(BytesIO(data))
        return len(reader.pages) > OCR_CHUNK_PAGE_COUNT
    except Exception:
        return False


def vision_ocr(data: bytes, filename: str = "") -> str:
    if _ocr_disabled():
        return "[OCR disabled by NPV_DISABLE_PDF_OCR]"
    if len(data) > MAX_OCR_BYTES:
        return f"[OCR skipped: PDF size {len(data)} exceeds {MAX_OCR_BYTES} byte limit]"
    key = _hash_bytes(data)
    if key:
        with _cache_lock:
            cached = _ocr_cache.get(key)
            if cached is not None:
                return cached
    try:
        client = _build_ocr_client()
        if _needs_chunking(data):
            chunks = _split_pdf_pages(data, pages_per_chunk=OCR_PAGES_PER_CHUNK)
            if not chunks:
                # Fallback: try single shot anyway
                text = _ocr_single_pdf_with_timeout(client, data, OCR_PER_CALL_TIMEOUT)
            else:
                if OCR_DEBUG:
                    _dbg(f"[OCR] {filename or 'pdf'}: {len(chunks)} pages, {len(data)} bytes, parallel={OCR_PARALLEL_PAGES}")

                from concurrent.futures import ThreadPoolExecutor

                def _ocr_one(idx_chunk):
                    idx, chunk = idx_chunk
                    import time as _time
                    _t0 = _time.time()
                    if OCR_DEBUG:
                        _dbg(f"[OCR] page {idx}/{len(chunks)} starting...")
                    try:
                        # Fresh client per page so a wedged httpx connection
                        # pool from a previous page can't poison subsequent ones.
                        per_page_client = _build_ocr_client()
                        page_text = _ocr_single_pdf_with_timeout(per_page_client, chunk, OCR_PER_CALL_TIMEOUT)
                        if OCR_DEBUG:
                            _dbg(f"[OCR] page {idx}/{len(chunks)} OK in {_time.time()-_t0:.1f}s ({len(page_text)} chars)")
                        return idx, page_text
                    except Exception as pe:
                        if OCR_DEBUG:
                            _dbg(f"[OCR] page {idx}/{len(chunks)} FAILED in {_time.time()-_t0:.1f}s: {pe}")
                        return idx, f"[OCR page-chunk {idx} error: {pe}]"

                results: dict = {}
                with ThreadPoolExecutor(max_workers=min(OCR_PARALLEL_PAGES, len(chunks))) as ex:
                    for idx, page_text in ex.map(_ocr_one, list(enumerate(chunks, start=1))):
                        results[idx] = page_text
                page_texts = [f"[--- page {i} ---]\n{results[i]}" for i in sorted(results.keys())]
                text = "\n".join(page_texts).strip()
        else:
            text = _ocr_single_pdf_with_timeout(client, data, OCR_PER_CALL_TIMEOUT)
        if not text:
            text = "[OCR returned empty result]"
        # Only cache successful, complete results. Avoid poisoning the cache
        # with transient errors (per-page timeouts, empty, generic OCR error)
        # so retries on the same bytes can recover.
        is_error = text.startswith("[OCR error") or text.startswith("[OCR returned empty") or text.startswith("[OCR skipped") or text.startswith("[OCR disabled")
        partial_failure = "[OCR page-chunk" in text and "error:" in text
        if key and not is_error and not partial_failure:
            with _cache_lock:
                _ocr_cache[key] = text
        return text
    except Exception as e:
        return f"[OCR error: {e}]"


def extract_pdf_text(data: bytes, filename: str = "", min_chars: int = MIN_TEXT_THRESHOLD) -> tuple[str, str]:
    """Return (text, method) where method is 'pypdf', 'ocr', 'pypdf+ocr', or 'error'."""
    try:
        primary = pypdf_extract(data)
    except Exception as e:
        primary = f"[pypdf error: {e}]"
    primary_clean = primary if primary and not primary.startswith("[") else ""
    if len(primary_clean) >= min_chars:
        return primary_clean, "pypdf"
    ocr_text = vision_ocr(data, filename=filename)
    # OCR errors are flagged via "[OCR error:" prefix; chunked output starts with
    # "[--- page N ---]" which is valid content.
    ocr_failed = (not ocr_text) or ocr_text.startswith("[OCR error")
    if not ocr_failed:
        if primary_clean:
            combined = (
                "[--- pypdf extraction ---]\n"
                + primary_clean
                + "\n[--- vision OCR (scanned content) ---]\n"
                + ocr_text
            )
            return combined, "pypdf+ocr"
        return ocr_text, "ocr"
    if primary_clean:
        return primary_clean + f"\n[ocr-fallback-failed: {ocr_text}]", "pypdf"
    return f"[extraction failed — pypdf:{primary or 'empty'} ocr:{ocr_text}]", "error"
