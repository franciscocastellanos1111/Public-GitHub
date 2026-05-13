import json
import logging
import os
import socket
import threading
import traceback
import uuid
from datetime import datetime, timezone

# Lazy-imported on first use so module import never fails at function-app load time
_TABLE_CLIENT_CACHE = {}
_CACHE_LOCK = threading.Lock()

TABLE_NAME_PREFIX = "NonprofitVerificationArchive"


def _utcnow():
    return datetime.now(timezone.utc)


def get_slot_name() -> str:
    try:
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


def _sanitize_table_name_part(value: str) -> str:
    if not value:
        return "Default"
    sanitized = "".join(ch for ch in value if ch.isalnum())
    if not sanitized:
        return "Default"
    return sanitized[0].upper() + sanitized[1:].lower()


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
            logger.error("npv_archive_service - AzureWebJobsStorage connection string not found")
            return None

        service_client = TableServiceClient.from_connection_string(conn_str=connection_string)
        table_client = service_client.get_table_client(table_name=table_name)

        try:
            table_client.create_table()
        except Exception:
            pass

        with _CACHE_LOCK:
            _TABLE_CLIENT_CACHE[table_name] = table_client

        logger.info(f"npv_archive_service - Created table client for: {table_name}")
        return table_client
    except Exception as e:
        logger.error(f"npv_archive_service - Failed to create table client for {table_name}: {e}")
        return None


def _mask_auth_key(auth_key) -> str:
    if not auth_key:
        return ""
    s = str(auth_key)
    if len(s) <= 8:
        return "*" * len(s)
    return f"{s[:4]}...{s[-4:]}"


def archive_message(
    message_json: str,
    queue_name: str,
    function_name: str,
    logger=logging,
    request_id: str = None,
    case_id: str = None,
    auth_key: str = None,
):
    try:
        now = _utcnow()
        slot_name = get_slot_name()

        archive = {
            "PartitionKey": now.strftime("%Y%m%d"),
            "RowKey": f"{now.strftime('%Y%m%d%H%M%S%f')[:-3]}_{uuid.uuid4().hex}",
            "MessageJson": message_json or "",
            "MessageLength": len(message_json or ""),
            "RequestId": request_id or "",
            "CaseId": case_id or "",
            "AuthKeyMasked": _mask_auth_key(auth_key),
            "QueueName": queue_name or "",
            "SlotName": slot_name,
            "FunctionName": function_name or "",
            "ReceivedDateUtc": now,
            "CompletedDateUtc": None,
            "ProcessingDurationMs": None,
            "ProcessingStatus": "Received",
            "IsSuccess": None,
            "ErrorMessage": None,
            "ExceptionDetails": None,
            "RetryCount": 0,
            "ProcessingNotes": None,
            "MachineName": socket.gethostname(),
            "InvocationId": None,
            "ResultJson": None,
            "ResultStatus": None,
            "ResultConfidence": None,
            "AnnotationId": None,
            "WriteBackOk": None,
        }

        table_client = _get_table_client(slot_name, logger)
        if table_client is None:
            logger.error("npv_archive_service - archive_message: no table client")
            return None

        table_client.create_entity(entity=archive)
        logger.info(
            f"npv_archive_service - Archived. Table={_get_table_name(slot_name)}, "
            f"PartitionKey={archive['PartitionKey']}, RowKey={archive['RowKey']}"
        )
        return archive
    except Exception as e:
        logger.error(f"npv_archive_service - archive_message error: {e}")
        return None


# Azure Table Storage limits each string property to 32K UTF-16 chars (~64KB).
# Use a safety margin so the truncation marker still fits.
_MAX_STR_PROP_CHARS = 31000


def _truncate_oversize_string_props(archive: dict, logger=logging) -> None:
    if not archive:
        return
    for k, v in list(archive.items()):
        try:
            if isinstance(v, str) and len(v) > _MAX_STR_PROP_CHARS:
                original_len = len(v)
                archive[k] = v[:_MAX_STR_PROP_CHARS] + f"...[TRUNCATED {original_len - _MAX_STR_PROP_CHARS} chars]"
                logger.warning(
                    f"npv_archive_service - Truncated property '{k}' from {original_len} to {len(archive[k])} chars"
                )
        except Exception:
            pass


def _update(archive: dict, logger=logging) -> None:
    if archive is None:
        return
    try:
        from azure.data.tables import UpdateMode

        table_client = _get_table_client(archive.get("SlotName") or get_slot_name(), logger)
        if table_client is None:
            logger.error("npv_archive_service - _update: no table client")
            return
        _truncate_oversize_string_props(archive, logger)
        try:
            table_client.upsert_entity(entity=archive, mode=UpdateMode.REPLACE)
        except Exception as upsert_ex:
            # Last-resort defense: drop the largest string properties and retry once,
            # so we never leave a row stuck in "Processing" because of payload size.
            logger.error(f"npv_archive_service - upsert failed, retrying with reduced payload: {upsert_ex}")
            for k in ("ResultJson", "MessageJson", "ExceptionDetails", "ProcessingNotes", "ErrorMessage"):
                if isinstance(archive.get(k), str) and len(archive[k]) > 1000:
                    archive[k] = archive[k][:1000] + "...[REDUCED]"
            archive["ProcessingNotes"] = (
                (archive.get("ProcessingNotes") or "") + f" | upsert_retry_after_error: {upsert_ex}"
            )[:_MAX_STR_PROP_CHARS]
            table_client.upsert_entity(entity=archive, mode=UpdateMode.REPLACE)
    except Exception as e:
        logger.error(f"npv_archive_service - _update error: {e}")


def update_with_parsed_details(
    archive: dict,
    request_id: str = None,
    case_id: str = None,
    auth_key: str = None,
    logger=logging,
) -> None:
    if archive is None:
        return
    if request_id:
        archive["RequestId"] = request_id
    if case_id:
        archive["CaseId"] = case_id
    if auth_key:
        archive["AuthKeyMasked"] = _mask_auth_key(auth_key)
    archive["ProcessingStatus"] = "Processing"
    _update(archive, logger)


def _finalize(archive: dict) -> None:
    now = _utcnow()
    archive["CompletedDateUtc"] = now
    received = archive.get("ReceivedDateUtc")
    try:
        if received and isinstance(received, datetime):
            archive["ProcessingDurationMs"] = int((now - received).total_seconds() * 1000)
    except Exception:
        pass


def mark_completed(
    archive: dict,
    result: dict = None,
    notes: str = None,
    annotation_id: str = None,
    write_back_ok: bool = None,
    logger=logging,
) -> None:
    if archive is None:
        return
    _finalize(archive)
    archive["ProcessingStatus"] = "Completed"
    archive["IsSuccess"] = True
    if notes:
        archive["ProcessingNotes"] = notes
    if result is not None:
        try:
            archive["ResultJson"] = json.dumps(result, default=str)
        except Exception as e:
            archive["ResultJson"] = json.dumps({"error": f"Could not serialize result: {e}"})
        archive["ResultStatus"] = str(result.get("status")) if isinstance(result, dict) else None
        archive["ResultConfidence"] = (
            str(result.get("confidence")) if isinstance(result, dict) and result.get("confidence") is not None else None
        )
    if annotation_id is not None:
        archive["AnnotationId"] = annotation_id
    if write_back_ok is not None:
        archive["WriteBackOk"] = bool(write_back_ok)
    _update(archive, logger)


def mark_failed(
    archive: dict,
    error_message: str,
    exception: BaseException = None,
    result: dict = None,
    logger=logging,
) -> None:
    if archive is None:
        return
    _finalize(archive)
    archive["ProcessingStatus"] = "Failed"
    archive["IsSuccess"] = False
    archive["ErrorMessage"] = (error_message or "")[:32000]
    if exception is not None:
        try:
            details = (
                f"Type: {type(exception).__name__}\n"
                f"Message: {exception}\n"
                f"StackTrace:\n{''.join(traceback.format_exception(type(exception), exception, exception.__traceback__))}"
            )
            archive["ExceptionDetails"] = details[:32000]
        except Exception:
            archive["ExceptionDetails"] = str(exception)[:32000]
    if result is not None:
        try:
            archive["ResultJson"] = json.dumps(result, default=str)
        except Exception:
            pass
    _update(archive, logger)


def mark_partially_completed(
    archive: dict,
    notes: str,
    result: dict = None,
    logger=logging,
) -> None:
    if archive is None:
        return
    _finalize(archive)
    archive["ProcessingStatus"] = "PartiallyCompleted"
    archive["IsSuccess"] = False
    archive["ProcessingNotes"] = notes
    if result is not None:
        try:
            archive["ResultJson"] = json.dumps(result, default=str)
        except Exception:
            pass
    _update(archive, logger)


# ---------------------------------------------------------------------------
# Adhoc query support (mirrors QueueMessageArchiveService.BuildAndExecuteAdhocQuery)
# ---------------------------------------------------------------------------
def _escape(value: str) -> str:
    return str(value).replace("'", "''")


def _parse_dt(value: str) -> datetime:
    s = str(value).strip()
    if s.endswith("Z"):
        s = s[:-1] + "+00:00"
    dt = datetime.fromisoformat(s)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def build_and_execute_adhoc_query(query_fields: dict, slot_name: str, logger=logging) -> list:
    results = []
    try:
        table_client = _get_table_client(slot_name, logger)
        if table_client is None:
            return results

        filters = []

        if query_fields.get("startDate"):
            start_date = _parse_dt(query_fields["startDate"])
            filters.append(f"PartitionKey ge '{start_date.strftime('%Y%m%d')}'")

        if query_fields.get("endDate"):
            end_date = _parse_dt(query_fields["endDate"])
            filters.append(f"PartitionKey le '{end_date.strftime('%Y%m%d')}'")

        if query_fields.get("partitionKey"):
            filters.append(f"PartitionKey eq '{_escape(query_fields['partitionKey'])}'")

        if query_fields.get("rowKey"):
            filters.append(f"RowKey eq '{_escape(query_fields['rowKey'])}'")

        if query_fields.get("requestId"):
            filters.append(f"RequestId eq '{_escape(query_fields['requestId'])}'")

        if query_fields.get("caseId"):
            filters.append(f"CaseId eq '{_escape(query_fields['caseId'])}'")

        if query_fields.get("functionName"):
            filters.append(f"FunctionName eq '{_escape(query_fields['functionName'])}'")

        if query_fields.get("queueName"):
            filters.append(f"QueueName eq '{_escape(query_fields['queueName'])}'")

        if query_fields.get("processingStatus"):
            filters.append(f"ProcessingStatus eq '{_escape(query_fields['processingStatus'])}'")

        if query_fields.get("resultStatus"):
            filters.append(f"ResultStatus eq '{_escape(query_fields['resultStatus'])}'")

        if "isSuccess" in query_fields and query_fields["isSuccess"] is not None:
            is_success = bool(query_fields["isSuccess"])
            filters.append(f"IsSuccess eq {str(is_success).lower()}")

        if query_fields.get("receivedDateStart"):
            r_start = _parse_dt(query_fields["receivedDateStart"])
            filters.append(f"ReceivedDateUtc ge datetime'{r_start.strftime('%Y-%m-%dT%H:%M:%SZ')}'")

        if query_fields.get("receivedDateEnd"):
            r_end = _parse_dt(query_fields["receivedDateEnd"])
            filters.append(f"ReceivedDateUtc le datetime'{r_end.strftime('%Y-%m-%dT%H:%M:%SZ')}'")

        filter_string = " and ".join(filters) if filters else None
        logger.info(f"npv_archive_service - adhoc query filter: {filter_string}")

        message_contains = query_fields.get("messageJson")
        result_contains = query_fields.get("resultJson")
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
            if message_contains:
                mj = entity.get("MessageJson") or ""
                if message_contains.lower() not in mj.lower():
                    continue
            if result_contains:
                rj = entity.get("ResultJson") or ""
                if result_contains.lower() not in rj.lower():
                    continue
            results.append(_serialize_entity(entity))
            if max_results and len(results) >= max_results:
                break
    except Exception as e:
        logger.error(f"npv_archive_service - build_and_execute_adhoc_query error: {e}")

    return results


def _serialize_entity(entity) -> dict:
    out = {}
    for k, v in entity.items():
        if isinstance(v, datetime):
            out[k] = v.isoformat()
        elif k == "ResultJson" and v:
            try:
                out[k] = json.loads(v)
            except Exception:
                out[k] = v
        else:
            out[k] = v
    return out
