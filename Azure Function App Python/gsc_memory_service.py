"""Living memory store for the Global Support Case Agent.

Mirrors the conventions of npv_memory_service.py:
- per-slot Azure Table named "GlobalSupportCaseMemory{Slot}"
- thread-safe table client cache
- ETag-based optimistic concurrency
- soft-delete via Status
- prompt-injection-resistant content validation

This module is independent from npv_memory_service: the two services have
separate categories, separate scope-key shapes, and separate tables, so they
do not share state.
"""
from __future__ import annotations

import json
import logging
import os
import re
import threading
from datetime import datetime, timezone
from typing import Any, Iterable, Optional


_TABLE_CLIENT_CACHE: dict = {}
_CACHE_LOCK = threading.Lock()

TABLE_NAME_PREFIX = "GlobalSupportCaseMemory"
SCHEMA_VERSION = 1

# Hard allow-list. Any other category is rejected.
ALLOWED_CATEGORIES = {
    "KbHit",
    "AnswerTemplate",
    "WebSource",
    "IntentSignal",
    "RoutingRule",
    "EscalationRubric",
    "OrgIdentity",
    "CustomerPreference",
    "BlockedSource",
    "KnownScam",
    "DocPattern",
    "Heuristic",
    "QueryPattern",
}

ALLOWED_CONFIDENCES = {"High", "Medium", "Low"}
ALLOWED_STATUSES = {"active", "deprecated", "needsReview"}
ALLOWED_SOURCES = {"agent", "manual", "ingested"}

_MAX_STR_PROP_CHARS = 31000

# Wider regex than NPV: GSC needs scope_keys like "customer:<guid>" and
# "orgdomain:example.org".
_SCOPE_KEY_RE = re.compile(r"^[A-Za-z0-9_:\-\.]{1,64}$")
_SUBJECT_KEY_RE = re.compile(r"^[a-z0-9][a-z0-9_\-]{1,62}$")
_URL_HOST_RE = re.compile(r"^[a-z0-9]([a-z0-9\-\.]{0,253}[a-z0-9])?$")


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


def _sanitize_table_name_part(value: str) -> str:
    if not value:
        return "Default"
    sanitized = "".join(ch for ch in value if ch.isalnum())
    if not sanitized:
        return "Default"
    return sanitized[0].upper() + sanitized[1:].lower()


def get_slot_name() -> str:
    try:
        try:
            from npv_archive_service import get_slot_name as _archive_slot
            return _archive_slot()
        except Exception:
            pass
        slot = os.getenv("WEBSITE_SLOT_NAME")
        if slot:
            return slot
        custom = os.getenv("DEPLOYMENT_SLOT")
        if custom:
            return custom
        conn = os.getenv("AzureWebJobsStorage", "")
        if not conn or "UseDevelopmentStorage=true" in conn:
            return "Local"
        return "Production"
    except Exception:
        return "Local"


def _get_table_name(slot_name: str) -> str:
    return f"{TABLE_NAME_PREFIX}{_sanitize_table_name_part(slot_name)}"


def _get_table_client(slot_name: str, logger=logging):
    table_name = _get_table_name(slot_name)
    with _CACHE_LOCK:
        cached = _TABLE_CLIENT_CACHE.get(table_name)
        if cached is not None:
            return cached
    try:
        from azure.data.tables import TableServiceClient

        connection_string = os.getenv("AzureWebJobsStorage")
        if not connection_string:
            logger.error("gsc_memory_service - AzureWebJobsStorage connection string not found")
            return None

        service_client = TableServiceClient.from_connection_string(conn_str=connection_string)
        table_client = service_client.get_table_client(table_name=table_name)
        try:
            table_client.create_table()
        except Exception:
            pass

        with _CACHE_LOCK:
            _TABLE_CLIENT_CACHE[table_name] = table_client

        logger.info(f"gsc_memory_service - Created table client for: {table_name}")
        return table_client
    except Exception as e:
        logger.error(f"gsc_memory_service - Failed to create table client for {table_name}: {e}")
        return None


def _truncate_oversize_string_props(entity: dict, logger=logging) -> None:
    if not entity:
        return
    for k, v in list(entity.items()):
        try:
            if isinstance(v, str) and len(v) > _MAX_STR_PROP_CHARS:
                original_len = len(v)
                entity[k] = v[:_MAX_STR_PROP_CHARS] + f"...[TRUNCATED {original_len - _MAX_STR_PROP_CHARS} chars]"
                logger.warning(
                    f"gsc_memory_service - Truncated property '{k}' from {original_len} to {len(entity[k])} chars"
                )
        except Exception:
            pass


# ---------------------------------------------------------------------------
# Key construction & validation
# ---------------------------------------------------------------------------
def _normalize_scope_key(scope_key: str) -> str:
    s = (scope_key or "").strip()
    if not s:
        raise ValueError("scope_key is required")
    if not _SCOPE_KEY_RE.match(s):
        raise ValueError(f"scope_key '{scope_key}' must match [A-Za-z0-9_:\\-.]{{1,64}}")
    # ISO country codes / 'global' normalization: only when no colon present.
    if ":" not in s and len(s) <= 3 and s.isalpha():
        return s.upper()
    return s


def _slugify_subject_key(subject_key: str) -> str:
    raw = (subject_key or "").strip().lower()
    if not raw:
        raise ValueError("subject_key is required")
    raw = re.sub(r"[^a-z0-9]+", "_", raw).strip("_")
    if not raw:
        raise ValueError("subject_key produced an empty slug")
    if not _SUBJECT_KEY_RE.match(raw):
        raise ValueError(f"subject_key '{subject_key}' -> '{raw}' is not a valid slug")
    return raw


def build_partition_key(category: str, scope_key: str) -> str:
    if category not in ALLOWED_CATEGORIES:
        raise ValueError(f"category '{category}' is not in the allow-list")
    return f"{category}__{_normalize_scope_key(scope_key)}"


def build_row_key(subject_key: str, schema_version: int = SCHEMA_VERSION) -> str:
    return f"{_slugify_subject_key(subject_key)}__v{int(schema_version)}"


def parse_ref(ref: str) -> tuple[str, str]:
    if not ref or "/" not in ref:
        raise ValueError(f"invalid memory ref '{ref}'")
    pk, rk = ref.split("/", 1)
    return pk, rk


def build_ref(partition_key: str, row_key: str) -> str:
    return f"{partition_key}/{row_key}"


# ---------------------------------------------------------------------------
# Content validation
# ---------------------------------------------------------------------------
_FORBIDDEN_URL_PATTERNS = [
    re.compile(r"^(?!https?://)", re.IGNORECASE),
    re.compile(r"@", re.IGNORECASE),
    re.compile(r"://(localhost|127\.|0\.0\.0\.0|10\.|192\.168\.|169\.254\.|172\.(1[6-9]|2\d|3[01])\.)", re.IGNORECASE),
    re.compile(r"://\[?[0-9a-f:]+\]?(/|$)", re.IGNORECASE),
]


def _validate_url(url: str) -> Optional[str]:
    if not isinstance(url, str) or not url:
        return "missing url"
    if len(url) > 2048:
        return "url too long"
    for pat in _FORBIDDEN_URL_PATTERNS:
        if pat.search(url):
            return "url rejected by safety pattern"
    return None


def _validate_content(category: str, content: Any) -> Optional[str]:
    if content is None:
        return None
    if not isinstance(content, dict):
        return "content must be a JSON object"
    try:
        serialized = json.dumps(content, default=str)
    except Exception as e:
        return f"content is not JSON-serializable: {e}"
    if len(serialized) > _MAX_STR_PROP_CHARS:
        return f"content exceeds {_MAX_STR_PROP_CHARS} chars"

    url_fields = ("url", "url_template", "lookup_url", "host_url", "kb_url", "source_url")
    for f in url_fields:
        v = content.get(f)
        if v is None:
            continue
        if "{" in str(v) and "}" in str(v):
            stripped = re.sub(r"\{[^}]+\}", "x", str(v))
            err = _validate_url(stripped)
        else:
            err = _validate_url(str(v))
        if err:
            return f"{f}: {err}"

    host = content.get("host")
    if host is not None:
        h = str(host).strip().lower()
        if not _URL_HOST_RE.match(h):
            return "host is not a valid hostname"
    return None


# ---------------------------------------------------------------------------
# Confidence recompute
# ---------------------------------------------------------------------------
def _recompute_confidence(entity: dict) -> str:
    try:
        if entity.get("Source") == "manual":
            return "High"
        s = int(entity.get("SuccessCount", 0) or 0)
        f = int(entity.get("FailureCount", 0) or 0)
        total = s + f
        if total < 3:
            return "Low"
        ratio = s / max(1, total)
        if ratio >= 0.85 and s >= 5:
            return "High"
        if ratio >= 0.6:
            return "Medium"
        return "Low"
    except Exception:
        return "Low"


# ---------------------------------------------------------------------------
# Core CRUD
# ---------------------------------------------------------------------------
def _new_entity_base(
    category: str,
    scope_key: str,
    subject_key: str,
    subject: str,
    content: Optional[dict],
    source: str,
    origin_case_id: Optional[str],
    slot_name: str,
    tags: Optional[str] = None,
    expires_on_utc: Optional[datetime] = None,
) -> dict:
    pk = build_partition_key(category, scope_key)
    rk = build_row_key(subject_key)
    now = _utcnow()
    return {
        "PartitionKey": pk,
        "RowKey": rk,
        "Category": category,
        "ScopeKey": _normalize_scope_key(scope_key),
        "SubjectKey": _slugify_subject_key(subject_key),
        "Subject": (subject or "")[:512],
        "ContentJson": json.dumps(content or {}, default=str),
        "SchemaVersion": int(SCHEMA_VERSION),
        "Source": source,
        "OriginCaseId": origin_case_id or "",
        "LastUpdatedCaseId": origin_case_id or "",
        "CreatedDateUtc": now,
        "LastUsedDateUtc": now,
        "LastConfirmedDateUtc": now if source == "manual" else None,
        "UseCount": 0,
        "SuccessCount": 1 if source == "manual" else 0,
        "FailureCount": 0,
        "Confidence": "High" if source == "manual" else "Low",
        "Status": "active",
        "SlotName": slot_name,
        "Tags": (tags or "")[:512],
        "ExpiresOnUtc": expires_on_utc,
        "ReviewNotes": "",
    }


def _get_entity(table_client, partition_key: str, row_key: str):
    try:
        from azure.core.exceptions import ResourceNotFoundError
        try:
            return table_client.get_entity(partition_key=partition_key, row_key=row_key)
        except ResourceNotFoundError:
            return None
    except Exception:
        return None


def record_entry(
    category: str,
    scope_key: str,
    subject_key: str,
    subject: str,
    content: Optional[dict] = None,
    source: str = "agent",
    origin_case_id: Optional[str] = None,
    tags: Optional[str] = None,
    slot_name: Optional[str] = None,
    logger=logging,
) -> Optional[dict]:
    try:
        if category not in ALLOWED_CATEGORIES:
            logger.warning(f"gsc_memory_service - record_entry: category '{category}' not allowed")
            return None
        if source not in ALLOWED_SOURCES:
            source = "agent"
        content_err = _validate_content(category, content)
        if content_err:
            logger.warning(f"gsc_memory_service - record_entry: content rejected: {content_err}")
            return None

        slot = slot_name or get_slot_name()
        table_client = _get_table_client(slot, logger)
        if table_client is None:
            return None

        pk = build_partition_key(category, scope_key)
        rk = build_row_key(subject_key)
        existing = _get_entity(table_client, pk, rk)
        if existing is not None:
            now = _utcnow()
            entity = dict(existing)
            entity["LastUpdatedCaseId"] = origin_case_id or entity.get("LastUpdatedCaseId") or ""
            entity["LastConfirmedDateUtc"] = now
            entity["SuccessCount"] = int(entity.get("SuccessCount", 0) or 0) + 1
            if content is not None:
                entity["ContentJson"] = json.dumps(content, default=str)
            if subject:
                entity["Subject"] = subject[:512]
            if tags:
                entity["Tags"] = (tags or "")[:512]
            if entity.get("Source") != "manual" and source == "manual":
                entity["Source"] = "manual"
            entity["Confidence"] = _recompute_confidence(entity)
            if (entity.get("Status") or "active") == "deprecated":
                entity["ReviewNotes"] = (
                    (entity.get("ReviewNotes") or "") + f" | re-record attempt at {now.isoformat()} (kept deprecated)"
                )[:_MAX_STR_PROP_CHARS]
            _upsert_with_truncation(table_client, entity, logger)
            return entity

        entity = _new_entity_base(
            category=category,
            scope_key=scope_key,
            subject_key=subject_key,
            subject=subject,
            content=content,
            source=source,
            origin_case_id=origin_case_id,
            slot_name=slot,
            tags=tags,
        )
        _upsert_with_truncation(table_client, entity, logger)
        return entity
    except Exception as e:
        logger.error(f"gsc_memory_service - record_entry error: {e}")
        return None


def _upsert_with_truncation(table_client, entity: dict, logger=logging) -> None:
    from azure.data.tables import UpdateMode
    _truncate_oversize_string_props(entity, logger)
    try:
        table_client.upsert_entity(entity=entity, mode=UpdateMode.REPLACE)
    except Exception as ex:
        logger.error(f"gsc_memory_service - upsert failed, retrying with reduced payload: {ex}")
        if isinstance(entity.get("ContentJson"), str) and len(entity["ContentJson"]) > 2000:
            entity["ContentJson"] = entity["ContentJson"][:2000] + "...[REDUCED]"
        entity["ReviewNotes"] = ((entity.get("ReviewNotes") or "") + f" | upsert_retry_after_error: {ex}")[:_MAX_STR_PROP_CHARS]
        table_client.upsert_entity(entity=entity, mode=UpdateMode.REPLACE)


def record_feedback(
    ref: str,
    outcome: str,
    notes: Optional[str] = None,
    case_id: Optional[str] = None,
    slot_name: Optional[str] = None,
    logger=logging,
) -> Optional[dict]:
    try:
        if outcome not in {"success", "failure"}:
            logger.warning(f"gsc_memory_service - record_feedback: invalid outcome '{outcome}'")
            return None
        pk, rk = parse_ref(ref)
        slot = slot_name or get_slot_name()
        table_client = _get_table_client(slot, logger)
        if table_client is None:
            return None

        from azure.data.tables import UpdateMode
        from azure.core.exceptions import ResourceModifiedError

        for attempt in range(3):
            entity = _get_entity(table_client, pk, rk)
            if entity is None:
                logger.warning(f"gsc_memory_service - record_feedback: ref not found: {ref}")
                return None
            entity = dict(entity)
            now = _utcnow()
            if outcome == "success":
                entity["SuccessCount"] = int(entity.get("SuccessCount", 0) or 0) + 1
                entity["LastConfirmedDateUtc"] = now
            else:
                entity["FailureCount"] = int(entity.get("FailureCount", 0) or 0) + 1
            entity["LastUpdatedCaseId"] = case_id or entity.get("LastUpdatedCaseId") or ""
            if notes:
                entity["ReviewNotes"] = ((entity.get("ReviewNotes") or "") + f" | {now.isoformat()} {outcome}: {notes}")[:_MAX_STR_PROP_CHARS]
            entity["Confidence"] = _recompute_confidence(entity)

            try:
                s = int(entity.get("SuccessCount", 0) or 0)
                f = int(entity.get("FailureCount", 0) or 0)
                if entity.get("Source") != "manual" and (s + f) >= 3 and f / max(1, s + f) >= 0.5:
                    if (entity.get("Status") or "active") == "active":
                        entity["Status"] = "needsReview"
            except Exception:
                pass

            _truncate_oversize_string_props(entity, logger)
            try:
                table_client.update_entity(entity=entity, mode=UpdateMode.REPLACE)
                return entity
            except ResourceModifiedError:
                if attempt == 2:
                    logger.warning(f"gsc_memory_service - record_feedback: concurrency conflict after 3 attempts for {ref}")
                    return None
                continue
        return None
    except Exception as e:
        logger.error(f"gsc_memory_service - record_feedback error: {e}")
        return None


def bump_use_count(refs: Iterable[str], slot_name: Optional[str] = None, logger=logging) -> int:
    bumped = 0
    try:
        from azure.data.tables import UpdateMode
        from azure.core.exceptions import ResourceModifiedError
        slot = slot_name or get_slot_name()
        table_client = _get_table_client(slot, logger)
        if table_client is None:
            return 0
        for ref in refs:
            try:
                pk, rk = parse_ref(ref)
            except Exception:
                continue
            for attempt in range(2):
                entity = _get_entity(table_client, pk, rk)
                if entity is None:
                    break
                entity = dict(entity)
                entity["UseCount"] = int(entity.get("UseCount", 0) or 0) + 1
                entity["LastUsedDateUtc"] = _utcnow()
                try:
                    table_client.update_entity(entity=entity, mode=UpdateMode.REPLACE)
                    bumped += 1
                    break
                except ResourceModifiedError:
                    if attempt == 1:
                        break
                    continue
    except Exception as e:
        logger.error(f"gsc_memory_service - bump_use_count error: {e}")
    return bumped


def lookup_entries(
    category: str,
    scope_key: str,
    subject_contains: Optional[str] = None,
    min_confidence: str = "Low",
    include_statuses: Optional[Iterable[str]] = None,
    max_results: int = 10,
    slot_name: Optional[str] = None,
    logger=logging,
) -> list:
    out: list = []
    try:
        if category not in ALLOWED_CATEGORIES:
            logger.warning(f"gsc_memory_service - lookup: category '{category}' not allowed")
            return out
        slot = slot_name or get_slot_name()
        table_client = _get_table_client(slot, logger)
        if table_client is None:
            return out

        pk = build_partition_key(category, scope_key)
        statuses = set(include_statuses) if include_statuses else {"active", "needsReview"}
        conf_rank = {"Low": 0, "Medium": 1, "High": 2}
        min_rank = conf_rank.get(min_confidence, 0)

        entities = table_client.query_entities(query_filter=f"PartitionKey eq '{pk}'")
        for entity in entities:
            try:
                if (entity.get("Status") or "active") not in statuses:
                    continue
                if conf_rank.get(entity.get("Confidence") or "Low", 0) < min_rank:
                    continue
                if subject_contains:
                    blob = ((entity.get("Subject") or "") + " " + (entity.get("ContentJson") or ""))
                    if subject_contains.lower() not in blob.lower():
                        continue
                out.append(_serialize_entity(entity))
                if len(out) >= max(1, int(max_results)):
                    break
            except Exception:
                continue
    except Exception as e:
        logger.error(f"gsc_memory_service - lookup error: {e}")
    return out


def apply_proposals(
    proposals: Iterable[dict],
    case_id: Optional[str] = None,
    slot_name: Optional[str] = None,
    logger=logging,
) -> dict:
    summary = {"recorded": 0, "feedback": 0, "rejected": 0, "errors": []}
    if not proposals:
        return summary
    slot = slot_name or get_slot_name()
    for p in proposals:
        try:
            if not isinstance(p, dict):
                summary["rejected"] += 1
                continue
            action = (p.get("action") or "").lower()
            if action == "record":
                ent = record_entry(
                    category=p.get("category"),
                    scope_key=p.get("scope_key"),
                    subject_key=p.get("subject_key") or p.get("subject"),
                    subject=p.get("subject") or "",
                    content=p.get("content"),
                    source="agent",
                    origin_case_id=case_id,
                    tags=p.get("tags"),
                    slot_name=slot,
                    logger=logger,
                )
                if ent is not None:
                    summary["recorded"] += 1
                else:
                    summary["rejected"] += 1
            elif action == "feedback":
                ent = record_feedback(
                    ref=p.get("ref"),
                    outcome=(p.get("outcome") or "").lower(),
                    notes=p.get("notes"),
                    case_id=case_id,
                    slot_name=slot,
                    logger=logger,
                )
                if ent is not None:
                    summary["feedback"] += 1
                else:
                    summary["rejected"] += 1
            else:
                summary["rejected"] += 1
        except Exception as e:
            summary["errors"].append(str(e)[:500])
            summary["rejected"] += 1
    return summary


def set_status(ref: str, new_status: str, notes: Optional[str] = None,
               slot_name: Optional[str] = None, logger=logging) -> Optional[dict]:
    try:
        if new_status not in ALLOWED_STATUSES:
            logger.warning(f"gsc_memory_service - set_status: invalid status '{new_status}'")
            return None
        pk, rk = parse_ref(ref)
        slot = slot_name or get_slot_name()
        table_client = _get_table_client(slot, logger)
        if table_client is None:
            return None
        entity = _get_entity(table_client, pk, rk)
        if entity is None:
            return None
        entity = dict(entity)
        entity["Status"] = new_status
        if notes:
            entity["ReviewNotes"] = ((entity.get("ReviewNotes") or "") + f" | {_utcnow().isoformat()} status->{new_status}: {notes}")[:_MAX_STR_PROP_CHARS]
        _upsert_with_truncation(table_client, entity, logger)
        return entity
    except Exception as e:
        logger.error(f"gsc_memory_service - set_status error: {e}")
        return None


def pin_manual(
    category: str,
    scope_key: str,
    subject_key: str,
    subject: str,
    content: dict,
    tags: Optional[str] = None,
    notes: Optional[str] = None,
    slot_name: Optional[str] = None,
    logger=logging,
) -> Optional[dict]:
    try:
        ent = record_entry(
            category=category,
            scope_key=scope_key,
            subject_key=subject_key,
            subject=subject,
            content=content,
            source="manual",
            origin_case_id=None,
            tags=tags,
            slot_name=slot_name,
            logger=logger,
        )
        if ent is None:
            return None
        ref = build_ref(ent["PartitionKey"], ent["RowKey"])
        return set_status(ref, "active", notes=notes, slot_name=slot_name, logger=logger)
    except Exception as e:
        logger.error(f"gsc_memory_service - pin_manual error: {e}")
        return None


def _escape(value: str) -> str:
    return str(value).replace("'", "''")


def build_and_execute_adhoc_query(query_fields: dict, slot_name: str, logger=logging) -> list:
    results: list = []
    try:
        table_client = _get_table_client(slot_name, logger)
        if table_client is None:
            return results

        filters: list[str] = []
        if query_fields.get("partitionKey"):
            filters.append(f"PartitionKey eq '{_escape(query_fields['partitionKey'])}'")
        if query_fields.get("rowKey"):
            filters.append(f"RowKey eq '{_escape(query_fields['rowKey'])}'")
        if query_fields.get("category"):
            filters.append(f"Category eq '{_escape(query_fields['category'])}'")
        if query_fields.get("scopeKey"):
            filters.append(f"ScopeKey eq '{_escape(query_fields['scopeKey'])}'")
        if query_fields.get("status"):
            filters.append(f"Status eq '{_escape(query_fields['status'])}'")
        if query_fields.get("source"):
            filters.append(f"Source eq '{_escape(query_fields['source'])}'")
        if query_fields.get("confidence"):
            filters.append(f"Confidence eq '{_escape(query_fields['confidence'])}'")
        if query_fields.get("subjectKey"):
            filters.append(f"SubjectKey eq '{_escape(query_fields['subjectKey'])}'")

        if query_fields.get("category") and query_fields.get("scopeKey"):
            try:
                pk = build_partition_key(query_fields["category"], query_fields["scopeKey"])
                filters.append(f"PartitionKey eq '{_escape(pk)}'")
            except Exception:
                pass

        filter_string = " and ".join(filters) if filters else None
        logger.info(f"gsc_memory_service - adhoc query filter: {filter_string}")

        subject_contains = query_fields.get("subjectContains")
        content_contains = query_fields.get("contentContains")
        max_results = query_fields.get("maxResults")
        try:
            max_results = int(max_results) if max_results is not None else None
        except Exception:
            max_results = None

        if filter_string:
            entities = table_client.query_entities(query_filter=filter_string)
        else:
            entities = table_client.list_entities()

        for entity in entities:
            try:
                if subject_contains:
                    sj = (entity.get("Subject") or "")
                    if subject_contains.lower() not in sj.lower():
                        continue
                if content_contains:
                    cj = (entity.get("ContentJson") or "")
                    if content_contains.lower() not in cj.lower():
                        continue
                results.append(_serialize_entity(entity))
                if max_results and len(results) >= max_results:
                    break
            except Exception:
                continue
    except Exception as e:
        logger.error(f"gsc_memory_service - build_and_execute_adhoc_query error: {e}")
    return results


def _serialize_entity(entity) -> dict:
    out = {}
    try:
        for k, v in entity.items():
            if isinstance(v, datetime):
                out[k] = v.isoformat()
            elif k == "ContentJson" and v:
                try:
                    out["Content"] = json.loads(v)
                    out[k] = v
                except Exception:
                    out[k] = v
            else:
                out[k] = v
        out["Ref"] = build_ref(out.get("PartitionKey", ""), out.get("RowKey", ""))
    except Exception:
        pass
    return out


def copy_rows_between_slots(
    refs: Iterable[str],
    source_slot: str,
    target_slot: str,
    set_source_to_manual: bool = True,
    logger=logging,
) -> dict:
    summary = {"copied": 0, "skipped": 0, "errors": []}
    try:
        from azure.data.tables import UpdateMode
        src_client = _get_table_client(source_slot, logger)
        dst_client = _get_table_client(target_slot, logger)
        if src_client is None or dst_client is None:
            summary["errors"].append("missing table client")
            return summary
        now = _utcnow()
        for ref in refs:
            try:
                pk, rk = parse_ref(ref)
                src = _get_entity(src_client, pk, rk)
                if src is None:
                    summary["skipped"] += 1
                    continue
                clone = dict(src)
                clone["SlotName"] = target_slot
                if set_source_to_manual:
                    clone["Source"] = "manual"
                    clone["Confidence"] = "High"
                clone["UseCount"] = 0
                clone["LastUsedDateUtc"] = now
                clone["LastConfirmedDateUtc"] = now
                clone["ReviewNotes"] = ((clone.get("ReviewNotes") or "") + f" | promoted from {source_slot} at {now.isoformat()}")[:_MAX_STR_PROP_CHARS]
                _truncate_oversize_string_props(clone, logger)
                dst_client.upsert_entity(entity=clone, mode=UpdateMode.REPLACE)
                summary["copied"] += 1
            except Exception as e:
                summary["errors"].append(f"{ref}: {e}")
    except Exception as e:
        summary["errors"].append(str(e))
    return summary
