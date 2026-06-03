"""Dynamics 365 tools for the Global Support Case Agent.

All operations route through `global_support_case_agent.dynamics_client` which
honours `DYNAMICS_ENVIRONMENT` (full URL) — independent of the QA-pinned
`validation_request_processing` module used by the NPV agent.
"""
from __future__ import annotations

import base64
import json
import re
from typing import Any, Dict, List, Optional

import httpx

from foundry_opus import tool

from . import dynamics_client


# Global Support Case incident type code (verified during the audit phase).
GLOBAL_SUPPORT_CASE_TYPECODE = 7

_PREVIEW_LIMIT = 20000


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


_IMAGE_MEDIA_TYPES = {
    "image/jpeg": "image/jpeg",
    "image/jpg": "image/jpeg",
    "image/png": "image/png",
    "image/gif": "image/gif",
    "image/webp": "image/webp",
}

_IMAGE_OCR_PROMPT = (
    "You are a high-fidelity OCR transcriber. Extract ALL text from the attached image "
    "VERBATIM — including handwriting, stamps, seals, headers/footers, registration "
    "numbers, dates, and annotations. Preserve line breaks. Do not translate or "
    "summarize. Output ONLY the raw extracted text."
)


def _extract_image_text(raw: bytes, mimetype: str = "", filename: str = "") -> str:
    """OCR an image attachment via Claude vision (same Foundry deployment the agent uses)."""
    try:
        ct = (mimetype or "").lower().strip()
        fn = (filename or "").lower()
        media_type = _IMAGE_MEDIA_TYPES.get(ct)
        if not media_type:
            for ext, mt in ((".jpg", "image/jpeg"), (".jpeg", "image/jpeg"),
                            (".png", "image/png"), (".gif", "image/gif"), (".webp", "image/webp")):
                if fn.endswith(ext):
                    media_type = mt
                    break
        if not media_type:
            return f"[image OCR skipped: unsupported image type '{mimetype or filename}']"

        from foundry_opus import FoundryClient  # local import to avoid import cycles
        client = FoundryClient()
        b64 = base64.b64encode(raw).decode("ascii")
        resp = client.chat(
            messages=[{
                "role": "user",
                "content": [
                    {"type": "image", "source": {"type": "base64", "media_type": media_type, "data": b64}},
                    {"type": "text", "text": _IMAGE_OCR_PROMPT},
                ],
            }],
            max_tokens=4000,
        )
        parts = []
        for block in (getattr(resp, "content", []) or []):
            if getattr(block, "type", None) == "text":
                parts.append(getattr(block, "text", "") or "")
        text = "\n".join(parts).strip()
        return f"[extraction-method: vision-ocr]\n{text}" if text else "[image OCR returned empty text]"
    except Exception as e:
        return f"[image OCR error: {e}]"


def _decode_base64_to_text(b64: str, mimetype: str = "", filename: str = "") -> str:
    try:
        if not b64:
            return ""
        raw = base64.b64decode(b64, validate=False)
        ct = (mimetype or "").lower()
        fn = (filename or "").lower()
        if ct.startswith("image/") or fn.endswith((".jpg", ".jpeg", ".png", ".gif", ".webp")):
            return _extract_image_text(raw, mimetype=mimetype, filename=filename)
        if "pdf" in ct or fn.endswith(".pdf"):
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
                pages = [(p.extract_text() or "") for p in reader.pages]
                text = "\n".join(pages).strip()
                return f"[extraction-method: pypdf]\n{text}" if text else "[PDF extraction returned empty text]"
            except Exception as e:
                return f"[PDF extraction unavailable: {e}]"
        if "html" in ct or fn.endswith((".html", ".htm")):
            return _strip_html(raw.decode("utf-8", errors="replace"))
        return raw.decode("utf-8", errors="replace")
    except Exception as e:
        return f"[decode error: {e}]"


# ---------------------------------------------------------------------------
# Case overview
# ---------------------------------------------------------------------------
@tool(description=(
    "Retrieve the Dynamics 365 case (incident) record by id, including title, "
    "description, customer (with resolved customerid_name / customerid_type), "
    "ts_countrycode, ts_emailaddresscustomerprovided, caseorigincode (+ its label, "
    "used to detect Formstack cases: caseorigincode == 100003), and the originating "
    "email id. Returns JSON with related-record counts. Use this FIRST when given only a caseId."
))
def dynamics_get_gsc_case_overview(case_id: str) -> str:
    try:
        select_columns = [
            "incidentid", "ticketnumber", "title", "description",
            "casetypecode", "caseorigincode", "createdon", "modifiedon",
            "statecode", "statuscode",
            "_customerid_value", "ts_countrycode", "ts_emailaddresscustomerprovided",
            "ts_aiintent", "ts_aiconfidence", "ts_airecommendation",
            "ts_aioperationid", "_ts_originemailid_value", "_ts_aisuggestedkb_value",
        ]
        # NOTE: `address1_country_code` is NOT a valid property on the account entity —
        # selecting it 400s the whole GET (0x80060888), which previously made this tool
        # report "case not found" for every account-customer case. Use address1_country.
        expand = (
            "customerid_account($select=accountid,name,websiteurl,emailaddress1,"
            "telephone1,address1_country),"
            "customerid_contact($select=contactid,fullname,emailaddress1,telephone1,"
            "address1_country)"
        )
        endpoint = (
            f"incidents({case_id})?$select={','.join(select_columns)}&$expand={expand}"
        )
        # Request FormattedValue annotations so caseorigincode / casetypecode resolve
        # to their display labels (needed for the Formstack branch and classification).
        headers = {"Prefer": 'odata.include-annotations="OData.Community.Display.V1.FormattedValue"'}
        record = dynamics_client.run_async(
            dynamics_client.request("GET", endpoint, additional_headers=headers)
        )
        if not record or record.get("success") is False:
            return _json({"error": "case not found", "case_id": case_id})

        # Surface formatted labels for the picklists the agent reasons about.
        def _fv(field: str) -> Optional[str]:
            return record.get(f"{field}@OData.Community.Display.V1.FormattedValue")

        record["caseorigincode_label"] = _fv("caseorigincode")
        record["casetypecode_label"] = _fv("casetypecode")

        # Resolve the customer (account or contact) into a flat name/type so the
        # agent can apply the "TechSoup Stock Customer Service" placeholder rule
        # without inferring which expand populated.
        acct = record.get("customerid_account") or {}
        cont = record.get("customerid_contact") or {}
        if isinstance(acct, dict) and acct.get("name"):
            record["customerid_name"] = acct.get("name")
            record["customerid_type"] = "account"
        elif isinstance(cont, dict) and cont.get("fullname"):
            record["customerid_name"] = cont.get("fullname")
            record["customerid_type"] = "contact"
        else:
            record["customerid_name"] = None
            record["customerid_type"] = None

        emails = dynamics_client.run_async(dynamics_client.query(
            entity_set="emails",
            filter_clause=f"_regardingobjectid_value eq {case_id}",
            select=["activityid"],
            top=200,
        )) or []
        notes = dynamics_client.run_async(dynamics_client.query(
            entity_set="annotations",
            filter_clause=f"_objectid_value eq {case_id}",
            select=["annotationid"],
            top=200,
        )) or []
        record["_related_counts"] = {
            "emails": len(emails),
            "notes": len(notes),
        }
        return _json(record)
    except Exception as e:
        return _json({"error": str(e)})


# ---------------------------------------------------------------------------
# Emails on the case
# ---------------------------------------------------------------------------
@tool(description=(
    "List email activities regarding a case in descending order by createdon. "
    "Returns activityid, subject, direction (incoming/outgoing), from, to, "
    "createdon and a short body snippet. Use to find the inbound customer email."
))
def dynamics_list_gsc_case_emails(case_id: str) -> str:
    try:
        emails = dynamics_client.run_async(dynamics_client.query(
            entity_set="emails",
            filter_clause=f"_regardingobjectid_value eq {case_id}",
            select=[
                "activityid", "subject", "description",
                "directioncode", "statecode", "statuscode",
                "createdon", "actualend", "sender",
            ],
            expand="email_activity_parties($select=participationtypemask,addressused,_partyid_value)",
            orderby="createdon desc",
            top=50,
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
                "snippet": snippet,
            })
        return _json(out)
    except Exception as e:
        return _json({"error": str(e)})


@tool(description=(
    "Retrieve a single email activity by id with the full body (HTML stripped) and "
    "an attachment list (id, filename, mimetype, size). Does NOT decode attachment bytes."
))
def dynamics_get_gsc_email(email_id: str) -> str:
    try:
        record = dynamics_client.run_async(dynamics_client.get(
            entity_set="emails",
            entity_id=email_id,
            select=[
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

        attachments_raw = dynamics_client.run_async(dynamics_client.query(
            entity_set="activitymimeattachments",
            filter_clause=f"_objectid_value eq {email_id}",
            select=["activitymimeattachmentid", "filename", "mimetype", "filesize"],
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
            "body_text": _truncate(body_text),
            "attachments": attachments,
        })
    except Exception as e:
        return _json({"error": str(e)})


@tool(description=(
    "Download an email attachment (activitymimeattachment) by id, decode its "
    "base64 body, and return extracted text (PDF/HTML/plain)."
))
def dynamics_get_gsc_email_attachment_text(attachment_id: str) -> str:
    try:
        record = dynamics_client.run_async(dynamics_client.get(
            entity_set="activitymimeattachments",
            entity_id=attachment_id,
            select=["activitymimeattachmentid", "filename", "mimetype", "filesize", "body"],
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
# Customer resolution & history
# ---------------------------------------------------------------------------
@tool(description=(
    "Resolve a sender email address to a Dynamics contact and/or account. "
    "Tries (1) exact email match on contact, (2) domain match on account "
    "(emailaddress1 ends with @<domain>), (3) returns nothing if no match. "
    "Returns JSON {method, contact, account}."
))
def dynamics_resolve_gsc_contact(sender_email: str) -> str:
    try:
        s = (sender_email or "").strip().lower()
        if not s or "@" not in s:
            return _json({"method": "None", "contact": None, "account": None})
        esc = s.replace("'", "''")
        contacts = dynamics_client.run_async(dynamics_client.query(
            entity_set="contacts",
            filter_clause=f"emailaddress1 eq '{esc}'",
            select=["contactid", "fullname", "emailaddress1", "_parentcustomerid_value"],
            top=1,
        )) or []
        if contacts:
            c = contacts[0]
            return _json({"method": "ExactEmail", "contact": c, "account": None})

        domain = s.split("@", 1)[1]
        esc_d = domain.replace("'", "''")
        accounts = dynamics_client.run_async(dynamics_client.query(
            entity_set="accounts",
            filter_clause=f"endswith(emailaddress1,'@{esc_d}') or endswith(websiteurl,'{esc_d}')",
            select=["accountid", "name", "websiteurl", "emailaddress1"],
            top=3,
        )) or []
        if accounts:
            return _json({"method": "DomainMatch", "contact": None, "account": accounts[0]})

        return _json({"method": "None", "contact": None, "account": None})
    except Exception as e:
        return _json({"error": str(e)})


@tool(description=(
    "Retrieve recent prior cases (incidents) for a given customer id (contactid or "
    "accountid). Returns the top N most recent with ticketnumber, title, statecode, "
    "createdon. Use to provide brief context — do NOT echo their full contents."
))
def dynamics_gsc_customer_history(customer_id: str, top: int = 10) -> str:
    try:
        if not customer_id:
            return _json({"error": "customer_id required"})
        try:
            top = max(1, min(int(top), 25))
        except Exception:
            top = 10
        rows = dynamics_client.run_async(dynamics_client.query(
            entity_set="incidents",
            filter_clause=f"_customerid_value eq {customer_id}",
            select=["incidentid", "ticketnumber", "title", "casetypecode",
                    "statecode", "statuscode", "createdon", "modifiedon"],
            orderby="createdon desc",
            top=top,
        )) or []
        return _json({"count": len(rows), "value": rows})
    except Exception as e:
        return _json({"error": str(e)})


# ---------------------------------------------------------------------------
# Classification (ts_type / ts_subtype / ts_detail / ts_subtype_3) hierarchy
# ---------------------------------------------------------------------------
_CLASSIFICATION_FIELDNAME_BY_LEVEL = {
    "type": "Type_GlobalSupport",
    "subtype": "SubType_GlobalSupport",
    "detail": "SubType_2_GlobalSupport",
    "subtype_3": "SubType_3_GlobalSupport",
}

_CLASSIFICATION_PARENT_LEVEL = {
    "type": "casetypecode",
    "subtype": "ts_type",
    "detail": "ts_subtype",
    "subtype_3": "ts_detail",
}


def _xml_escape_value(v: str) -> str:
    try:
        return (
            (v or "")
            .replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace("'", "&apos;")
            .replace('"', "&quot;")
        )
    except Exception:
        return v or ""


def _fetch_classification_state(case_id: str) -> Dict[str, Any]:
    """Read the case picklist labels (formatted values) needed for hierarchical lookup."""
    try:
        select = (
            "casetypecode,ts_type,ts_subtype,ts_detail,ts_subtype_3,"
            "ts_casestatus,statecode,statuscode"
        )
        endpoint = f"incidents({case_id})?$select={select}"
        headers = {"Prefer": 'odata.include-annotations="OData.Community.Display.V1.FormattedValue"'}
        resp = dynamics_client.run_async(
            dynamics_client.request("GET", endpoint, additional_headers=headers)
        ) or {}
        if resp.get("success") is False:
            return {"error": resp.get("error"), "case_id": case_id}

        def fv(field: str) -> Optional[str]:
            return resp.get(f"{field}@OData.Community.Display.V1.FormattedValue")

        return {
            "case_id": case_id,
            "casetypecode": resp.get("casetypecode"),
            "casetypecode_label": fv("casetypecode"),
            "ts_type": resp.get("ts_type"),
            "ts_type_label": fv("ts_type"),
            "ts_subtype": resp.get("ts_subtype"),
            "ts_subtype_label": fv("ts_subtype"),
            "ts_detail": resp.get("ts_detail"),
            "ts_detail_label": fv("ts_detail"),
            "ts_subtype_3": resp.get("ts_subtype_3"),
            "ts_subtype_3_label": fv("ts_subtype_3"),
            "ts_casestatus": resp.get("ts_casestatus"),
            "ts_casestatus_label": fv("ts_casestatus"),
            "statecode": resp.get("statecode"),
            "statuscode": resp.get("statuscode"),
        }
    except Exception as e:
        return {"error": str(e), "case_id": case_id}


@tool(description=(
    "Read the case's current classification picklist state. Returns the formatted text "
    "labels for casetypecode, ts_type, ts_subtype, ts_detail, ts_subtype_3, and ts_casestatus. "
    "Use this BEFORE choosing classification values so you know (1) the casetypecode label "
    "(needed as the parent_value for level 'type') and (2) what is already set."
))
def dynamics_gsc_get_case_classification_state(case_id: str) -> str:
    try:
        return _json(_fetch_classification_state(case_id))
    except Exception as e:
        return _json({"error": str(e)})


@tool(description=(
    "Return the hierarchical classification options available at a given level for the "
    "Global Support case classification tree (ts_type / ts_subtype / ts_detail / ts_subtype_3). "
    "Args: level in ('type','subtype','detail','subtype_3'); parent_value is the TEXT label of the "
    "parent level — for 'type' use casetypecode label (e.g. 'Question'), for 'subtype' use the chosen "
    "ts_type label, for 'detail' use the chosen ts_subtype label, for 'subtype_3' use the chosen ts_detail label. "
    "Returns a JSON list of {label, code, seq}. Use the returned 'code' as the picklist value to set."
))
def dynamics_gsc_get_classification_options(level: str, parent_value: str) -> str:
    try:
        key = (level or "").strip().lower()
        fieldname = _CLASSIFICATION_FIELDNAME_BY_LEVEL.get(key)
        if not fieldname:
            return _json({
                "error": f"invalid level '{level}'",
                "valid_levels": list(_CLASSIFICATION_FIELDNAME_BY_LEVEL.keys()),
            })
        parent_clean = (parent_value or "").strip()
        if not parent_clean:
            return _json({"error": "parent_value is required", "level": key})

        fetch = (
            "<fetch version='1.0' mapping='logical' distinct='true'>"
            "<entity name='ts_fieldhierarchyandmapping'>"
            "<attribute name='ts_value'/>"
            "<attribute name='ts_valueseq'/>"
            "<attribute name='ts_valuecode'/>"
            "<order attribute='ts_valueseq'/>"
            "<filter type='and'>"
            f"<condition attribute='ts_fieldname' operator='eq' value='{fieldname}'/>"
            f"<condition attribute='ts_parentfieldvalue' operator='eq' value='{_xml_escape_value(parent_clean)}'/>"
            "</filter>"
            "</entity></fetch>"
        )
        rows = dynamics_client.run_async(
            dynamics_client.fetch_xml("ts_fieldhierarchyandmappings", fetch)
        ) or []
        options = []
        for r in rows:
            options.append({
                "label": r.get("ts_value"),
                "code": r.get("ts_valuecode"),
                "seq": r.get("ts_valueseq"),
            })
        return _json({
            "level": key,
            "fieldname": fieldname,
            "parent_value": parent_clean,
            "count": len(options),
            "options": options,
        })
    except Exception as e:
        return _json({"error": str(e)})


def _fetch_all_level_rows(fieldname: str) -> List[Dict[str, Any]]:
    """All option rows for one classification fieldname (no parent filter)."""
    fetch = (
        "<fetch version='1.0' mapping='logical' distinct='true'>"
        "<entity name='ts_fieldhierarchyandmapping'>"
        "<attribute name='ts_value'/>"
        "<attribute name='ts_valueseq'/>"
        "<attribute name='ts_valuecode'/>"
        "<attribute name='ts_parentfieldvalue'/>"
        "<order attribute='ts_valueseq'/>"
        "<filter type='and'>"
        f"<condition attribute='ts_fieldname' operator='eq' value='{fieldname}'/>"
        "</filter>"
        "</entity></fetch>"
    )
    return dynamics_client.run_async(
        dynamics_client.fetch_xml("ts_fieldhierarchyandmappings", fetch)
    ) or []


@tool(description=(
    "Return the ENTIRE Global Support classification subtree under a given case type in a "
    "single call — type -> subtype -> detail -> subtype_3, nested, each node carrying its "
    "{label, code}. Pass parent_type_label = the casetypecode label from "
    "dynamics_gsc_get_case_classification_state (e.g. 'Question'). PREFER this over calling "
    "dynamics_gsc_get_classification_options level-by-level: it lets you pick the full path in "
    "one shot and saves iterations. Use the exact 'code' values when filling `classification`."
))
def dynamics_gsc_get_classification_tree(parent_type_label: str) -> str:
    try:
        parent_clean = (parent_type_label or "").strip()
        if not parent_clean:
            return _json({"error": "parent_type_label is required"})

        # Index each child level by its parent value (case-insensitive).
        def _index(fieldname: str) -> Dict[str, List[Dict[str, Any]]]:
            idx: Dict[str, List[Dict[str, Any]]] = {}
            for r in _fetch_all_level_rows(fieldname):
                parent = (r.get("ts_parentfieldvalue") or "").strip().lower()
                idx.setdefault(parent, []).append({
                    "label": r.get("ts_value"),
                    "code": r.get("ts_valuecode"),
                    "seq": r.get("ts_valueseq"),
                })
            return idx

        subtype_idx = _index(_CLASSIFICATION_FIELDNAME_BY_LEVEL["subtype"])
        detail_idx = _index(_CLASSIFICATION_FIELDNAME_BY_LEVEL["detail"])
        subtype3_idx = _index(_CLASSIFICATION_FIELDNAME_BY_LEVEL["subtype_3"])

        def _children(idx: Dict[str, List[Dict[str, Any]]], label: Optional[str]) -> List[Dict[str, Any]]:
            return idx.get((label or "").strip().lower(), [])

        type_rows = [
            r for r in _fetch_all_level_rows(_CLASSIFICATION_FIELDNAME_BY_LEVEL["type"])
            if (r.get("ts_parentfieldvalue") or "").strip().lower() == parent_clean.lower()
        ]
        tree: List[Dict[str, Any]] = []
        for t in type_rows:
            t_label = t.get("ts_value")
            t_node: Dict[str, Any] = {
                "label": t_label, "code": t.get("ts_valuecode"), "subtypes": [],
            }
            for st in _children(subtype_idx, t_label):
                st_node: Dict[str, Any] = {
                    "label": st["label"], "code": st["code"], "details": [],
                }
                for d in _children(detail_idx, st["label"]):
                    d_node: Dict[str, Any] = {
                        "label": d["label"], "code": d["code"], "subtype_3": [],
                    }
                    for s3 in _children(subtype3_idx, d["label"]):
                        d_node["subtype_3"].append({"label": s3["label"], "code": s3["code"]})
                    st_node["details"].append(d_node)
                t_node["subtypes"].append(st_node)
            tree.append(t_node)

        return _json({
            "parent_type_label": parent_clean,
            "type_count": len(tree),
            "tree": tree,
        })
    except Exception as e:
        return _json({"error": str(e)})


# ---------------------------------------------------------------------------
# Generic query (escape hatch)
# ---------------------------------------------------------------------------
@tool(description=(
    "Generic Dataverse OData query. Provide entity_set (e.g. 'emails', 'annotations', "
    "'accounts'), an OData $filter expression, optional comma-separated select_columns, "
    "optional expand, and optional top. Use only when dedicated tools are insufficient."
))
def dynamics_gsc_query(entity_set: str, filter_clause: str, select_columns: str = "",
                       expand: str = "", top: int = 25) -> str:
    try:
        sel: Optional[List[str]] = None
        if select_columns:
            sel = [c.strip() for c in select_columns.split(",") if c.strip()]
        records = dynamics_client.run_async(dynamics_client.query(
            entity_set=entity_set,
            filter_clause=filter_clause,
            select=sel,
            expand=(expand or None),
            top=int(top) if top else None,
        )) or []
        return _json({"count": len(records), "value": records})
    except Exception as e:
        return _json({"error": str(e)})


# ---------------------------------------------------------------------------
# Writers used by service.py (NOT registered as agent tools).
# ---------------------------------------------------------------------------
def create_case_note(case_id: str, subject: str, notetext: str) -> Dict[str, Any]:
    try:
        return dynamics_client.run_async(dynamics_client.create_annotation(
            incident_id=case_id, subject=subject, notetext=notetext,
        )) or {"success": False, "error": "no result"}
    except Exception as e:
        return {"success": False, "error": str(e)}


def get_case_ai_operation_id(case_id: str) -> Optional[str]:
    """Read the case's currently-recorded ts_aioperationid (the agent's idempotency key).

    Used to detect queue redelivery: if a prior run already stamped this exact
    operation_id, re-processing would double-send/route. Returns the stored value
    or None. Never raises.
    """
    try:
        if not case_id:
            return None
        rec = dynamics_client.run_async(dynamics_client.get(
            entity_set="incidents",
            entity_id=case_id,
            select=["incidentid", "ts_aioperationid"],
        ))
        if not rec or rec.get("success") is False:
            return None
        val = rec.get("ts_aioperationid")
        return str(val) if val else None
    except Exception as e:
        print(f"gsc dynamics_tools.get_case_ai_operation_id error: {e}")
        return None


def update_case_ai_fields(
    case_id: str,
    intent: Optional[str] = None,
    confidence_optionvalue: Optional[int] = None,
    recommendation_optionvalue: Optional[int] = None,
    suggested_reply: Optional[str] = None,
    suggested_kb_id: Optional[str] = None,
    origin_email_id: Optional[str] = None,
    operation_id: Optional[str] = None,
) -> Dict[str, Any]:
    try:
        fields: Dict[str, Any] = {}
        if intent:
            fields["ts_aiintent"] = intent[:100]
        if confidence_optionvalue is not None:
            fields["ts_aiconfidence"] = int(confidence_optionvalue)
        if recommendation_optionvalue is not None:
            fields["ts_airecommendation"] = int(recommendation_optionvalue)
        if suggested_reply:
            fields["ts_aisuggestedreply"] = suggested_reply[:100000]
        if operation_id:
            fields["ts_aioperationid"] = operation_id[:100]
        if suggested_kb_id:
            fields["ts_aisuggestedkb@odata.bind"] = f"/knowledgearticles({suggested_kb_id})"
        if origin_email_id:
            fields["ts_originemailid@odata.bind"] = f"/emails({origin_email_id})"
        if not fields:
            return {"success": True, "skipped": True}
        return dynamics_client.run_async(dynamics_client.update(
            entity_set="incidents",
            entity_id=case_id,
            fields=fields,
        )) or {"success": False, "error": "no result"}
    except Exception as e:
        return {"success": False, "error": str(e)}


DYNAMICS_TOOLS = [
    dynamics_get_gsc_case_overview,
    dynamics_list_gsc_case_emails,
    dynamics_get_gsc_email,
    dynamics_get_gsc_email_attachment_text,
    dynamics_resolve_gsc_contact,
    dynamics_gsc_customer_history,
    dynamics_gsc_get_case_classification_state,
    dynamics_gsc_get_classification_tree,
    dynamics_gsc_get_classification_options,
    dynamics_gsc_query,
]


# ---------------------------------------------------------------------------
# Queue + reply writers used by service.py (NOT registered as agent tools).
# ---------------------------------------------------------------------------
# activityparty.participationtypemask:
#   1=From, 2=To, 3=Cc, 4=Bcc
# activityparty.partytype (partyobjecttypecode): 8=systemuser, 9=team, 10=account,
#   2=contact, 2010=queue, 0=unresolved (use addressused only)
_PARTY_KIND_BIND = {
    "contact": "/contacts",
    "account": "/accounts",
    "systemuser": "/systemusers",
    "queue": "/queues",
}

_QUEUE_ID_CACHE: Dict[str, Optional[str]] = {}


def resolve_queue_by_name(queue_name: str) -> Optional[Dict[str, Any]]:
    """Look up a Dynamics queue by name. Cached for the lifetime of the process."""
    try:
        if not queue_name:
            return None
        key = queue_name.strip().lower()
        if key in _QUEUE_ID_CACHE:
            cached_id = _QUEUE_ID_CACHE[key]
            return {"queueid": cached_id, "name": queue_name} if cached_id else None
        esc = queue_name.replace("'", "''")
        rows = dynamics_client.run_async(dynamics_client.query(
            entity_set="queues",
            filter_clause=f"name eq '{esc}'",
            select=["queueid", "name", "emailaddress", "queuetypecode"],
            top=1,
        )) or []
        if not rows:
            _QUEUE_ID_CACHE[key] = None
            return None
        _QUEUE_ID_CACHE[key] = rows[0].get("queueid")
        return rows[0]
    except Exception as e:
        print(f"gsc dynamics_tools.resolve_queue_by_name error: {e}")
        return None


def get_inbound_email_to_queue(email_id: str) -> Optional[Dict[str, Any]]:
    """Return the queue (id + name) the inbound email was addressed to, if any."""
    try:
        if not email_id:
            return None
        record = dynamics_client.run_async(dynamics_client.get(
            entity_set="emails",
            entity_id=email_id,
            select=["activityid", "directioncode"],
            expand="email_activity_parties($select=participationtypemask,addressused,_partyid_value,partyobjecttypecode)",
        ))
        if not record:
            return None
        parties = record.get("email_activity_parties") or []
        # TO recipients on the inbound (directioncode=false) message.
        to_parties = [
            p for p in parties
            if p.get("participationtypemask") == 2
            and (p.get("partyobjecttypecode") == "queue" or p.get("partyobjecttypecode") == 2010)
        ]
        if not to_parties:
            return None
        q = to_parties[0]
        queue_id = q.get("_partyid_value")
        if not queue_id:
            return None
        # Hydrate queue name.
        info = dynamics_client.run_async(dynamics_client.get(
            entity_set="queues", entity_id=queue_id,
            select=["queueid", "name", "emailaddress"],
        ))
        return info or {"queueid": queue_id, "name": None}
    except Exception as e:
        print(f"gsc dynamics_tools.get_inbound_email_to_queue error: {e}")
        return None


def _ensure_html_body(body: str) -> str:
    """Defense-in-depth: the agent is instructed to emit HTML, but if a plain-text
    body slips through (no HTML tags), convert newlines to <br>/paragraphs so the
    reply does not collapse into a single line in the customer's mail client.
    Also strips a leading/trailing markdown code fence if present.
    """
    try:
        if not body:
            return ""
        text = body.strip()
        # Strip accidental ```html ... ``` fences.
        if text.startswith("```"):
            text = re.sub(r"^```[a-zA-Z]*\s*", "", text)
            text = re.sub(r"\s*```$", "", text)
        # If it already contains HTML element tags, trust the model's markup.
        if re.search(r"<\s*(p|br|div|ul|ol|li|a|table|h[1-6]|span|strong|em)\b", text, re.IGNORECASE):
            return text
        # Plain text fallback: paragraphs on blank lines, <br> on single newlines.
        paragraphs = [p.strip() for p in re.split(r"\n\s*\n", text) if p.strip()]
        if not paragraphs:
            return text
        return "".join(
            "<p>" + p.replace("\n", "<br>") + "</p>" for p in paragraphs
        )
    except Exception:
        return body or ""


def _build_to_party(
    kind: str,
    party_id: Optional[str],
    address_used: Optional[str],
) -> Dict[str, Any]:
    kind = (kind or "").lower()
    bind = _PARTY_KIND_BIND.get(kind)
    party: Dict[str, Any] = {"participationtypemask": 2}
    if bind and party_id:
        party[f"partyid_{kind}@odata.bind"] = f"{bind}({party_id})"
    if address_used:
        party["addressused"] = address_used
    return party


def send_case_reply(
    *,
    case_id: str,
    from_queue_id: str,
    to_kind: str,
    to_party_id: Optional[str],
    to_address_used: Optional[str],
    subject: str,
    body_html: str,
    in_reply_to_email_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Create an outbound email regarding the case + invoke SendEmail. Returns
    {success, emailid, sent}."""
    try:
        if not case_id or not from_queue_id:
            return {"success": False, "error": "case_id and from_queue_id are required"}
        if not to_address_used and not to_party_id:
            return {"success": False, "error": "either to_party_id or to_address_used must be supplied"}

        from_party = {
            "participationtypemask": 1,
            "partyid_queue@odata.bind": f"/queues({from_queue_id})",
        }
        to_party = _build_to_party(to_kind, to_party_id, to_address_used)

        email_fields: Dict[str, Any] = {
            "subject": (subject or "")[:200],
            "description": _ensure_html_body(body_html or ""),
            "directioncode": True,  # outbound
            "regardingobjectid_incident@odata.bind": f"/incidents({case_id})",
            "email_activity_parties": [from_party, to_party],
        }
        if in_reply_to_email_id:
            email_fields["ParentActivityId@odata.bind"] = f"/emails({in_reply_to_email_id})"

        create_resp = dynamics_client.run_async(dynamics_client.create(
            entity_set="emails", fields=email_fields, select=["activityid"],
        )) or {}
        if create_resp.get("success") is False:
            return {"success": False, "error": f"create email failed: {create_resp.get('error')}"}
        email_id = create_resp.get("activityid") or create_resp.get("entityId")
        if not email_id:
            return {"success": False, "error": "create email did not return activityid"}

        send_resp = dynamics_client.run_async(dynamics_client.action(
            f"emails({email_id})/Microsoft.Dynamics.CRM.SendEmail",
            {"IssueSend": True},
        )) or {}
        sent = send_resp.get("success") is not False
        return {
            "success": True,
            "emailid": email_id,
            "sent": sent,
            "send_response": send_resp,
        }
    except Exception as e:
        return {"success": False, "error": str(e)}


def route_case_to_queue(case_id: str, queue_id: str, comment: Optional[str] = None) -> Dict[str, Any]:
    """Invoke AddToQueue to move the incident into the target queue."""
    try:
        if not case_id or not queue_id:
            return {"success": False, "error": "case_id and queue_id are required"}
        body = {
            "Target": {
                "incidentid": case_id,
                "@odata.type": "Microsoft.Dynamics.CRM.incident",
            },
            "DestinationQueueId": {
                "queueid": queue_id,
                "@odata.type": "Microsoft.Dynamics.CRM.queue",
            },
        }
        if comment:
            body["QueueItemProperties"] = {
                "@odata.type": "Microsoft.Dynamics.CRM.queueitem",
                "title": comment[:200],
            }
        resp = dynamics_client.run_async(dynamics_client.action("AddToQueue", body)) or {}
        return {
            "success": resp.get("success") is not False,
            "response": resp,
        }
    except Exception as e:
        return {"success": False, "error": str(e)}
