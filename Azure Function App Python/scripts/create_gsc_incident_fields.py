"""Create the 7 custom Dynamics 365 incident fields used by the
Global Support Case Agent.

Idempotent. Safe to re-run. Skips fields whose LogicalName already exists.

Targets one of three environments per run:
    qa    -> https://tsdynamicsqa.crm.dynamics.com
    stage -> https://tsdynamicsstage.crm.dynamics.com
    prod  -> https://techsoup.crm.dynamics.com

Credentials are read from environment variables:
    TENANT_ID       (default: d8ba2331-6b05-4303-9a60-36c58c3e272d)
    CLIENT_ID       (default: 2b325da3-cbd1-4dfa-9363-e4fdb20b605c)
    CLIENT_SECRET   (REQUIRED; never hard-coded)

Usage:
    set CLIENT_SECRET=<the secret>
    py scripts/create_gsc_incident_fields.py --env qa
    py scripts/create_gsc_incident_fields.py --env qa --dry-run
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any, Dict, List, Optional

import requests


ENV_URLS: Dict[str, str] = {
    "qa": "https://tsdynamicsqa.crm.dynamics.com",
    "stage": "https://tsdynamicsstage.crm.dynamics.com",
    "prod": "https://techsoup.crm.dynamics.com",
}

DEFAULT_TENANT_ID = "d8ba2331-6b05-4303-9a60-36c58c3e272d"
DEFAULT_CLIENT_ID = "2b325da3-cbd1-4dfa-9363-e4fdb20b605c"

CUSTOMIZATION_PREFIX = "ts"
PUBLISHER_OPTIONVALUE_PREFIX = 10000


def _get_token(dataverse_url: str, tenant_id: str, client_id: str, client_secret: str) -> str:
    try:
        url = f"https://login.microsoftonline.com/{tenant_id}/oauth2/v2.0/token"
        data = {
            "client_id": client_id,
            "client_secret": client_secret,
            "scope": f"{dataverse_url}/.default",
            "grant_type": "client_credentials",
        }
        resp = requests.post(url, data=data, timeout=30)
        resp.raise_for_status()
        return resp.json()["access_token"]
    except Exception as e:
        raise RuntimeError(f"Failed to acquire access token: {e}") from e


def _api_base(dataverse_url: str) -> str:
    return f"{dataverse_url.rstrip('/')}/api/data/v9.2"


def _headers(token: str) -> Dict[str, str]:
    return {
        "OData-MaxVersion": "4.0",
        "OData-Version": "4.0",
        "Accept": "application/json",
        "Content-Type": "application/json; charset=utf-8",
        "Authorization": f"Bearer {token}",
        "MSCRM.SolutionUniqueName": os.getenv("GSC_SOLUTION_UNIQUE_NAME", "TSDataArchitecture"),
    }


def _attribute_exists(dataverse_url: str, token: str, logical_name: str) -> bool:
    try:
        url = (
            f"{_api_base(dataverse_url)}/EntityDefinitions(LogicalName='incident')"
            f"/Attributes(LogicalName='{logical_name}')?$select=LogicalName"
        )
        resp = requests.get(url, headers=_headers(token), timeout=30)
        return resp.status_code == 200
    except Exception:
        return False


def _post_attribute(dataverse_url: str, token: str, body: Dict[str, Any]) -> Dict[str, Any]:
    url = f"{_api_base(dataverse_url)}/EntityDefinitions(LogicalName='incident')/Attributes"
    resp = requests.post(url, headers=_headers(token), data=json.dumps(body), timeout=60)
    if resp.status_code not in (200, 201, 204):
        raise RuntimeError(f"POST attribute failed ({resp.status_code}): {resp.text}")
    return {"status": resp.status_code, "headers": dict(resp.headers)}


def _create_lookup_relationship(
    dataverse_url: str,
    token: str,
    referenced_entity: str,
    referencing_attribute_logical: str,
    referencing_attribute_schema: str,
    schema_name: str,
    display_label: str,
) -> Dict[str, Any]:
    url = f"{_api_base(dataverse_url)}/RelationshipDefinitions"
    body = {
        "@odata.type": "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata",
        "SchemaName": schema_name,
        "ReferencedEntity": referenced_entity,
        "ReferencingEntity": "incident",
        "Lookup": {
            "AttributeType": "Lookup",
            "AttributeTypeName": {"Value": "LookupType"},
            "Description": {
                "@odata.type": "Microsoft.Dynamics.CRM.Label",
                "LocalizedLabels": [
                    {"Label": display_label, "LanguageCode": 1033}
                ],
            },
            "DisplayName": {
                "@odata.type": "Microsoft.Dynamics.CRM.Label",
                "LocalizedLabels": [
                    {"Label": display_label, "LanguageCode": 1033}
                ],
            },
            "RequiredLevel": {"Value": "None"},
            "SchemaName": referencing_attribute_schema,
            "LogicalName": referencing_attribute_logical,
        },
        "CascadeConfiguration": {
            "Assign": "NoCascade",
            "Delete": "RemoveLink",
            "Merge": "NoCascade",
            "Reparent": "NoCascade",
            "Share": "NoCascade",
            "Unshare": "NoCascade",
        },
    }
    resp = requests.post(url, headers=_headers(token), data=json.dumps(body), timeout=60)
    if resp.status_code not in (200, 201, 204):
        raise RuntimeError(f"POST relationship failed ({resp.status_code}): {resp.text}")
    return {"status": resp.status_code}


def _label(value: str) -> Dict[str, Any]:
    return {
        "@odata.type": "Microsoft.Dynamics.CRM.Label",
        "LocalizedLabels": [{"Label": value, "LanguageCode": 1033}],
    }


def _option(value: int, label: str) -> Dict[str, Any]:
    return {
        "Value": value,
        "Label": _label(label),
    }


def build_field_specs() -> List[Dict[str, Any]]:
    return [
        {
            "logical": "ts_aiintent",
            "kind": "string",
            "schema": "ts_AIIntent",
            "display": "AI Intent",
            "description": "Intent label classified by the Global Support Case Agent.",
            "max_length": 100,
        },
        {
            "logical": "ts_aioperationid",
            "kind": "string",
            "schema": "ts_AIOperationId",
            "display": "AI Operation Id",
            "description": "Async operation id correlating the queued Global Support Case Agent run.",
            "max_length": 100,
        },
        {
            "logical": "ts_aiconfidence",
            "kind": "picklist",
            "schema": "ts_AIConfidence",
            "display": "AI Confidence",
            "description": "Confidence level of the Global Support Case Agent verdict.",
            "options": [
                _option(PUBLISHER_OPTIONVALUE_PREFIX + 1, "High"),
                _option(PUBLISHER_OPTIONVALUE_PREFIX + 2, "Medium"),
                _option(PUBLISHER_OPTIONVALUE_PREFIX + 3, "Low"),
            ],
        },
        {
            "logical": "ts_airecommendation",
            "kind": "picklist",
            "schema": "ts_AIRecommendation",
            "display": "AI Recommendation",
            "description": "Action recommendation produced by the Global Support Case Agent.",
            "options": [
                _option(PUBLISHER_OPTIONVALUE_PREFIX + 1, "AutoResolve"),
                _option(PUBLISHER_OPTIONVALUE_PREFIX + 2, "DraftReplyForReview"),
                _option(PUBLISHER_OPTIONVALUE_PREFIX + 3, "EscalateToTier2"),
                _option(PUBLISHER_OPTIONVALUE_PREFIX + 4, "EscalateToBilling"),
                _option(PUBLISHER_OPTIONVALUE_PREFIX + 5, "RequestMoreInfo"),
                _option(PUBLISHER_OPTIONVALUE_PREFIX + 6, "RouteToNonprofitVerifier"),
                _option(PUBLISHER_OPTIONVALUE_PREFIX + 7, "Reject"),
            ],
        },
        {
            "logical": "ts_aisuggestedreply",
            "kind": "memo",
            "schema": "ts_AISuggestedReply",
            "display": "AI Suggested Reply",
            "description": "Draft reply produced by the Global Support Case Agent.",
            "max_length": 100000,
        },
        {
            "logical": "ts_aisuggestedkb",
            "kind": "lookup",
            "referenced": "knowledgearticle",
            "schema": "ts_AISuggestedKB",
            "relationship_schema": "ts_incident_ts_aisuggestedkb_knowledgearticle",
            "display": "AI Suggested KB",
            "description": "Knowledge article suggested by the Global Support Case Agent.",
        },
        {
            "logical": "ts_originemailid",
            "kind": "lookup",
            "referenced": "email",
            "schema": "ts_OriginEmailId",
            "relationship_schema": "ts_incident_ts_originemailid_email",
            "display": "Origin Email",
            "description": "Inbound email activity that triggered creation of this Global Support Case.",
        },
        # {
        #     "logical": "ts_region",
        #     "kind": "string",
        #     "schema": "ts_Region",
        #     "display": "Region",
        #     "description": "Region or country tag used by the Global Support Case Agent for memory scoping (e.g. LATAM, US, MX, BR).",
        #     "max_length": 50,
        # },
    ]


def _make_string_body(spec: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata",
        "AttributeType": "String",
        "AttributeTypeName": {"Value": "StringType"},
        "SchemaName": spec["schema"],
        "LogicalName": spec["logical"],
        "DisplayName": _label(spec["display"]),
        "Description": _label(spec["description"]),
        "RequiredLevel": {"Value": "None"},
        "MaxLength": int(spec.get("max_length", 100)),
        "FormatName": {"Value": "Text"},
    }


def _make_memo_body(spec: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "@odata.type": "Microsoft.Dynamics.CRM.MemoAttributeMetadata",
        "AttributeType": "Memo",
        "AttributeTypeName": {"Value": "MemoType"},
        "SchemaName": spec["schema"],
        "LogicalName": spec["logical"],
        "DisplayName": _label(spec["display"]),
        "Description": _label(spec["description"]),
        "RequiredLevel": {"Value": "None"},
        "MaxLength": int(spec.get("max_length", 100000)),
        "Format": "TextArea",
    }


def _make_picklist_body(spec: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "@odata.type": "Microsoft.Dynamics.CRM.PicklistAttributeMetadata",
        "AttributeType": "Picklist",
        "AttributeTypeName": {"Value": "PicklistType"},
        "SchemaName": spec["schema"],
        "LogicalName": spec["logical"],
        "DisplayName": _label(spec["display"]),
        "Description": _label(spec["description"]),
        "RequiredLevel": {"Value": "None"},
        "OptionSet": {
            "@odata.type": "Microsoft.Dynamics.CRM.OptionSetMetadata",
            "OptionSetType": "Picklist",
            "IsGlobal": False,
            "Options": spec["options"],
        },
    }


def _ensure_field(dataverse_url: str, token: str, spec: Dict[str, Any], dry_run: bool, logger=print) -> str:
    try:
        logical = spec["logical"]
        if _attribute_exists(dataverse_url, token, logical):
            logger(f"  [skip] {logical} already exists")
            return "skipped"

        kind = spec["kind"]
        if dry_run:
            logger(f"  [dry-run] would create {kind} attribute {logical}")
            return "dry-run"

        if kind == "string":
            _post_attribute(dataverse_url, token, _make_string_body(spec))
        elif kind == "memo":
            _post_attribute(dataverse_url, token, _make_memo_body(spec))
        elif kind == "picklist":
            _post_attribute(dataverse_url, token, _make_picklist_body(spec))
        elif kind == "lookup":
            _create_lookup_relationship(
                dataverse_url=dataverse_url,
                token=token,
                referenced_entity=spec["referenced"],
                referencing_attribute_logical=spec["logical"],
                referencing_attribute_schema=spec["schema"],
                schema_name=spec["relationship_schema"],
                display_label=spec["display"],
            )
        else:
            raise ValueError(f"Unsupported kind: {kind}")

        logger(f"  [ok] created {kind} attribute {logical}")
        return "created"
    except Exception as e:
        logger(f"  [error] {spec.get('logical')}: {e}")
        return f"error: {e}"


def main(argv: Optional[List[str]] = None) -> int:
    try:
        # parser = argparse.ArgumentParser(description="Create Global Support Case Agent custom fields on incident.")
        # parser.add_argument("--env", required=True, choices=list(ENV_URLS.keys()), help="Target Dynamics environment")
        # parser.add_argument("--dry-run", action="store_true", help="Show what would be created without making changes")
        # args = parser.parse_args(argv)

        dataverse_url = ENV_URLS["stage"]
        tenant_id = os.getenv("TENANT_ID") or DEFAULT_TENANT_ID
        client_id = os.getenv("CLIENT_ID") or DEFAULT_CLIENT_ID
        client_secret = os.getenv("CLIENT_SECRET")
        if not client_secret:
            print("ERROR: CLIENT_SECRET environment variable is required.", file=sys.stderr)
            return 2

        print(f"Target: {dataverse_url} (env=stage)")
        print(f"Tenant: {tenant_id}")
        print(f"Client: {client_id}")
        print(f"Dry-run: False")

        token = _get_token(dataverse_url, tenant_id, client_id, client_secret)
        print("Authenticated. Processing fields...")

        summary = {"created": 0, "skipped": 0, "errors": 0, "dry_run": 0}
        for spec in build_field_specs():
            print(f"- {spec['logical']} ({spec['kind']})")
            outcome = _ensure_field(dataverse_url, token, spec, False)
            if outcome == "created":
                summary["created"] += 1
            elif outcome == "skipped":
                summary["skipped"] += 1
            elif outcome == "dry-run":
                summary["dry_run"] += 1
            else:
                summary["errors"] += 1

        print("\nSummary:")
        for k, v in summary.items():
            print(f"  {k}: {v}")

        return 0 if summary["errors"] == 0 else 1
    except Exception as e:
        print(f"FATAL: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
