from __future__ import annotations

import asyncio
import base64
import json
import os
import re
import sys
from typing import Any, Dict, List, Optional


def _ensure_validation_lib_on_path() -> None:
    try:
        here = os.path.dirname(os.path.abspath(__file__))
        # nonprofit_verifier/ -> Nonprofit Verification Agent/ -> Python/
        python_root = os.path.normpath(os.path.join(here, "..", ".."))
        if os.path.isdir(python_root) and python_root not in sys.path:
            sys.path.insert(0, python_root)
    except Exception as e:
        print(f"Failed to extend sys.path for validation_request_processing: {e}")


_ensure_validation_lib_on_path()

import validation_request_processing as vrp  # noqa: E402

from foundry_opus import tool  # noqa: E402


_PREVIEW_LIMIT = 20000


def _run(coro):
    try:
        try:
            loop = asyncio.get_event_loop()
            if loop.is_running():
                new_loop = asyncio.new_event_loop()
                try:
                    return new_loop.run_until_complete(coro)
                finally:
                    new_loop.close()
        except RuntimeError:
            pass
        return asyncio.run(coro)
    except Exception as e:
        return {"success": False, "error": f"async execution failed: {e}"}


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


def _decode_base64_to_text(b64: str, mimetype: str = "", filename: str = "") -> str:
    try:
        if not b64:
            return ""
        raw = base64.b64decode(b64, validate=False)
        ct = (mimetype or "").lower()
        fn = (filename or "").lower()
        if "pdf" in ct or fn.endswith(".pdf"):
            try:
                from .pdf_extract import extract_pdf_text
                text, method = extract_pdf_text(raw, filename=filename)
                if text and not text.startswith("["):
                    return f"[extraction-method: {method}]\n{text}"
                return text
            except Exception as e:
                return f"[PDF extraction unavailable: {e}]"
        if "html" in ct or fn.endswith((".html", ".htm")):
            return _strip_html(raw.decode("utf-8", errors="replace"))
        return raw.decode("utf-8", errors="replace")
    except Exception as e:
        return f"[decode error: {e}]"


def _json(obj: Any) -> str:
    try:
        return json.dumps(obj, indent=2, default=str, ensure_ascii=False)
    except Exception as e:
        return json.dumps({"error": f"serialization failed: {e}"})


# ---------------------------------------------------------------------------
# Case overview
# ---------------------------------------------------------------------------
@tool(description="Retrieve the Dynamics 365 case (incident) record by id, including selected validation-request fields and the related customer account. Returns JSON. Use this FIRST when given only a caseId.")
def dynamics_get_case_overview(case_id: str) -> str:
    try:
        select_columns = [
            "incidentid", "ticketnumber", "title", "description",
            "createdon", "modifiedon", "statecode", "statuscode",
            "ts_validationrequestlegalname",
            "ts_validationrequestlegalidentifier",
            "ts_validationrequestorgtype",
            "ts_validationrequestmissionstatement",
            "ts_validationrequestaddressline1",
            "ts_validationrequestaddresscity",
            "ts_validationrequestaddressstateregion",
            "ts_validationrequestaddresspostalcode",
            "ts_validationrequestaddresscountryid",
            "ts_validationrequestemail",
            "ts_validationrequestphone",
            "ts_validationrequestwebsite",
            "ts_validationrequestagentfirstname",
            "ts_validationrequestagentlastname",
            "ts_validationrequestagentemail",
            "ts_validationrequesttransactionid",
            "_customerid_value",
        ]
        record = _run(vrp.get_incident(
            incident_id=case_id,
            select_columns=select_columns,
            expand="customerid_account($select=accountid,name,websiteurl,emailaddress1,telephone1,address1_country)",
        ))
        if not record or record.get("success") is False:
            return _json({"error": "case not found", "case_id": case_id})
        # Get summary counts of related records.
        emails = _run(vrp.query_entity(
            "emails",
            f"_regardingobjectid_value eq {case_id}",
            select_columns=["activityid"],
            top=200,
        )) or []
        notes = _run(vrp.get_annotations(case_id)) or []
        attachments = _run(vrp.query_entity(
            "msdyn_entityattachments",
            f"_msdyn_relatedentity_value eq {case_id}",
            select_columns=["msdyn_entityattachmentid"],
            top=200,
        )) or []
        record["_related_counts"] = {
            "emails": len(emails),
            "notes": len(notes),
            "entity_attachments": len(attachments),
        }
        return _json(record)
    except Exception as e:
        return _json({"error": str(e)})


# ---------------------------------------------------------------------------
# Emails
# ---------------------------------------------------------------------------
@tool(description="List all email activities (emails) regarding a case. Returns JSON array with activityid, subject, direction (incoming/outgoing), sender, to, createdon, statecode, and a body snippet. Use to locate the customer's reply email containing the documentation.")
def dynamics_list_case_emails(case_id: str) -> str:
    try:
        emails = _run(vrp.query_entity(
            "emails",
            f"_regardingobjectid_value eq {case_id}",
            select_columns=[
                "activityid", "subject", "description",
                "directioncode", "statecode", "statuscode",
                "createdon", "actualend", "sender",
            ],
            expand="email_activity_parties($select=participationtypemask,addressused,_partyid_value)",
            orderby="createdon desc",
            top=100,
        )) or []
        out: List[Dict[str, Any]] = []
        for e in emails:
            parties = e.get("email_activity_parties") or []
            from_addrs = [p.get("addressused") for p in parties if p.get("participationtypemask") == 1]
            to_addrs = [p.get("addressused") for p in parties if p.get("participationtypemask") == 2]
            description = e.get("description") or ""
            snippet = _strip_html(description)[:600]
            out.append({
                "activityid": e.get("activityid"),
                "subject": e.get("subject"),
                "direction": "outgoing" if e.get("directioncode") else "incoming",
                "from": from_addrs or ([e.get("sender")] if e.get("sender") else []),
                "to": to_addrs,
                "createdon": e.get("createdon"),
                "actualend": e.get("actualend"),
                "statecode": e.get("statecode"),
                "snippet": snippet,
            })
        return _json(out)
    except Exception as e:
        return _json({"error": str(e)})


@tool(description="Retrieve a single email activity by id, including the full body (HTML stripped) and a list of its attachments (id, filename, mimetype, size). Does NOT decode attachment contents — use dynamics_get_email_attachment_text for that.")
def dynamics_get_email(email_id: str) -> str:
    try:
        record = _run(vrp.get_entity(
            "emails", email_id,
            select_columns=[
                "activityid", "subject", "description",
                "directioncode", "statecode", "statuscode",
                "createdon", "actualend", "sender",
                "_regardingobjectid_value",
            ],
            expand="email_activity_parties($select=participationtypemask,addressused,_partyid_value)",
        ))
        if not record or record.get("success") is False:
            return _json({"error": "email not found", "email_id": email_id})

        parties = record.get("email_activity_parties") or []
        from_addrs = [p.get("addressused") for p in parties if p.get("participationtypemask") == 1]
        to_addrs = [p.get("addressused") for p in parties if p.get("participationtypemask") == 2]
        cc_addrs = [p.get("addressused") for p in parties if p.get("participationtypemask") == 3]

        attachments_raw = _run(vrp.query_entity(
            "activitymimeattachments",
            f"_objectid_value eq {email_id}",
            select_columns=["activitymimeattachmentid", "filename", "mimetype", "filesize"],
            top=50,
        )) or []
        attachments = [{
            "attachmentid": a.get("activitymimeattachmentid"),
            "filename": a.get("filename"),
            "mimetype": a.get("mimetype"),
            "filesize": a.get("filesize"),
        } for a in attachments_raw]

        body_text = _strip_html(record.get("description") or "")
        return _json({
            "activityid": record.get("activityid"),
            "subject": record.get("subject"),
            "direction": "outgoing" if record.get("directioncode") else "incoming",
            "from": from_addrs or ([record.get("sender")] if record.get("sender") else []),
            "to": to_addrs,
            "cc": cc_addrs,
            "createdon": record.get("createdon"),
            "actualend": record.get("actualend"),
            "body_text": _truncate(body_text),
            "attachments": attachments,
        })
    except Exception as e:
        return _json({"error": str(e)})


@tool(description="Download an email attachment (activitymimeattachment) by id, decode its base64 body, and return extracted text (PDF/HTML/plain). Returns truncated text suitable for analysis.")
def dynamics_get_email_attachment_text(attachment_id: str) -> str:
    try:
        record = _run(vrp.get_entity(
            "activitymimeattachments", attachment_id,
            select_columns=["activitymimeattachmentid", "filename", "mimetype", "filesize", "body"],
        ))
        if not record or record.get("success") is False:
            return _json({"error": "attachment not found", "attachment_id": attachment_id})
        text = _decode_base64_to_text(
            record.get("body") or "",
            mimetype=record.get("mimetype") or "",
            filename=record.get("filename") or "",
        )
        return _json({
            "attachmentid": record.get("activitymimeattachmentid"),
            "filename": record.get("filename"),
            "mimetype": record.get("mimetype"),
            "filesize": record.get("filesize"),
            "extracted_text": _truncate(text),
        })
    except Exception as e:
        return _json({"error": str(e)})


# ---------------------------------------------------------------------------
# Notes (annotations)
# ---------------------------------------------------------------------------
@tool(description="List all notes (annotations) on a case. Returns JSON array with annotationid, subject, notetext snippet, and whether each note has a file attachment (filename, mimetype).")
def dynamics_list_case_notes(case_id: str) -> str:
    try:
        notes = _run(vrp.get_annotations(
            case_id,
            select_columns=[
                "annotationid", "subject", "notetext",
                "isdocument", "filename", "mimetype", "filesize",
                "createdon", "createdby",
            ],
        )) or []
        out = []
        for n in notes:
            txt = n.get("notetext") or ""
            out.append({
                "annotationid": n.get("annotationid"),
                "subject": n.get("subject"),
                "notetext_snippet": _strip_html(txt)[:600],
                "has_file": bool(n.get("isdocument")),
                "filename": n.get("filename"),
                "mimetype": n.get("mimetype"),
                "filesize": n.get("filesize"),
                "createdon": n.get("createdon"),
            })
        return _json(out)
    except Exception as e:
        return _json({"error": str(e)})


@tool(description="Retrieve a single note (annotation) by id, including the full notetext and (if present) the decoded text of its file attachment.")
def dynamics_get_note(annotation_id: str) -> str:
    try:
        record = _run(vrp.get_entity(
            "annotations", annotation_id,
            select_columns=[
                "annotationid", "subject", "notetext",
                "isdocument", "filename", "mimetype", "filesize",
                "documentbody", "createdon",
            ],
        ))
        if not record or record.get("success") is False:
            return _json({"error": "annotation not found", "annotation_id": annotation_id})
        file_text = ""
        if record.get("isdocument") and record.get("documentbody"):
            file_text = _decode_base64_to_text(
                record.get("documentbody") or "",
                mimetype=record.get("mimetype") or "",
                filename=record.get("filename") or "",
            )
        return _json({
            "annotationid": record.get("annotationid"),
            "subject": record.get("subject"),
            "notetext": _truncate(record.get("notetext") or ""),
            "filename": record.get("filename"),
            "mimetype": record.get("mimetype"),
            "filesize": record.get("filesize"),
            "extracted_file_text": _truncate(file_text) if file_text else None,
        })
    except Exception as e:
        return _json({"error": str(e)})


# ---------------------------------------------------------------------------
# Entity attachments (msdyn_entityattachment + msdyn_fileblob)
# ---------------------------------------------------------------------------
@tool(description="List msdyn_entityattachment records related to a case. Returns array with attachmentid, name, and reference value. Use dynamics_get_entity_attachment_text to download and extract.")
def dynamics_list_case_entity_attachments(case_id: str) -> str:
    try:
        records = _run(vrp.query_entity(
            "msdyn_entityattachments",
            f"_msdyn_relatedentity_value eq {case_id}",
            select_columns=["msdyn_entityattachmentid", "msdyn_name", "ts_referencevalue"],
            top=100,
        )) or []
        out = [{
            "attachmentid": r.get("msdyn_entityattachmentid"),
            "name": r.get("msdyn_name"),
            "reference": r.get("ts_referencevalue"),
        } for r in records]
        return _json(out)
    except Exception as e:
        return _json({"error": str(e)})


@tool(description="Download an msdyn_entityattachment file blob by id and return extracted text (PDF/HTML/plain). Truncated to a safe length for analysis.")
def dynamics_get_entity_attachment_text(attachment_id: str) -> str:
    try:
        info = _run(vrp.retrieve_file_info(attachment_id)) or {}
        filename = info.get("filename") or f"{attachment_id}.bin"
        mimetype = info.get("mimetype") or ""
        # Direct blob download via async helper.
        async def _download() -> Optional[bytes]:
            try:
                token = await vrp.get_cached_token()
                if not token:
                    return None
                import httpx
                url = f"{vrp.DYNAMICS_ENVIRONMENT}/api/data/v9.2/msdyn_entityattachments({attachment_id})/msdyn_fileblob/$value"
                async with httpx.AsyncClient(timeout=30.0) as client:
                    resp = await client.get(url, headers={"Authorization": f"Bearer {token}"})
                    if resp.status_code != 200:
                        return None
                    return resp.content
            except Exception:
                return None
        data = _run(_download())
        if not data:
            return _json({"error": "download failed", "attachment_id": attachment_id})
        b64 = base64.b64encode(data).decode("ascii")
        text = _decode_base64_to_text(b64, mimetype=mimetype, filename=filename)
        return _json({
            "attachmentid": attachment_id,
            "filename": filename,
            "mimetype": mimetype,
            "filesize": len(data),
            "extracted_text": _truncate(text),
        })
    except Exception as e:
        return _json({"error": str(e)})


# ---------------------------------------------------------------------------
# Create a note (annotation) on a case
# ---------------------------------------------------------------------------
def create_case_note(case_id: str, subject: str, notetext: str) -> Dict[str, Any]:
    try:
        return _run(vrp.create_annotation(case_id, subject, notetext)) or {"success": False, "error": "no result"}
    except Exception as e:
        return {"success": False, "error": str(e)}


@tool(description="Create a note (annotation) on a Dynamics 365 case. Parameters: case_id (incident id), subject (short title), notetext (full note body). Returns JSON with the created annotationid. Call this AFTER submit_verification_result to persist the verification verdict back to the case.")
def dynamics_create_case_note(case_id: str, subject: str, notetext: str) -> str:
    try:
        result = create_case_note(case_id, subject, notetext)
        return _json({
            "annotationid": result.get("annotationid") or result.get("entityId"),
            "success": result.get("success") is not False,
            "subject": result.get("subject", subject),
        })
    except Exception as e:
        return _json({"error": str(e)})


# ---------------------------------------------------------------------------
# Generic escape hatch
# ---------------------------------------------------------------------------
@tool(description="Generic Dataverse OData query. Provide entity_set (e.g. 'emails', 'annotations', 'accounts'), an OData $filter expression, optional comma-separated select_columns, optional expand, and optional top. Use only when the dedicated tools are insufficient.")
def dynamics_query(entity_set: str, filter_clause: str, select_columns: str = "", expand: str = "", top: int = 25) -> str:
    try:
        sel: Optional[List[str]] = None
        if select_columns:
            sel = [c.strip() for c in select_columns.split(",") if c.strip()]
        records = _run(vrp.query_entity(
            entity_set,
            filter_clause,
            select_columns=sel,
            expand=(expand or None),
            top=int(top) if top else None,
        )) or []
        return _json({"count": len(records), "value": records})
    except Exception as e:
        return _json({"error": str(e)})


DYNAMICS_TOOLS = [
    dynamics_get_case_overview,
    dynamics_list_case_emails,
    dynamics_get_email,
    dynamics_get_email_attachment_text,
    dynamics_list_case_notes,
    dynamics_get_note,
    dynamics_list_case_entity_attachments,
    dynamics_get_entity_attachment_text,
    dynamics_create_case_note,
    dynamics_query,
]
