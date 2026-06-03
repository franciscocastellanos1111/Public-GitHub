"""Environment-aware Dynamics 365 Web API client for the Global Support Case Agent.

Why a new module instead of reusing validation_request_processing?
- That module hard-codes the QA Dataverse URL.
- The Global Support Case Agent is expected to run against three environments
  (qa / stage / prod) gated by an env var, so we need a small wrapper that
  picks the right tenant URL at request time.
"""
from __future__ import annotations

import asyncio
import json
import os
import re
import threading
from datetime import datetime, timezone, timedelta
from typing import Any, Dict, List, Optional

import httpx


def _get_env_url() -> str:
    return os.getenv("DYNAMICS_ENVIRONMENT") or "https://tsdynamicsqa.crm.dynamics.com"


def _get_tenant_id() -> str:
    return os.getenv("TENANT_ID") or "d8ba2331-6b05-4303-9a60-36c58c3e272d"


def _get_client_id() -> str:
    return os.getenv("CLIENT_ID") or "2b325da3-cbd1-4dfa-9363-e4fdb20b605c"


def _get_client_secret() -> Optional[str]:
    return os.getenv("CLIENT_SECRET")


_TOKEN_CACHE: Dict[str, Dict[str, Any]] = {}
_TOKEN_LOCK = threading.Lock()


async def _get_access_token() -> Optional[str]:
    try:
        env_url = _get_env_url()
        with _TOKEN_LOCK:
            cached = _TOKEN_CACHE.get(env_url)
            if cached and datetime.now(timezone.utc) < cached["expires"]:
                return cached["token"]

        tenant_id = _get_tenant_id()
        client_id = _get_client_id()
        client_secret = _get_client_secret()
        if not client_secret:
            print("gsc dynamics_client - CLIENT_SECRET not configured")
            return None

        url = f"https://login.microsoftonline.com/{tenant_id}/oauth2/v2.0/token"
        params = {
            "client_id": client_id,
            "scope": f"{env_url}/.default",
            "client_secret": client_secret,
            "grant_type": "client_credentials",
        }
        headers = {"Accept": "application/json", "Content-Type": "application/x-www-form-urlencoded"}

        async with httpx.AsyncClient() as client:
            resp = await client.post(url, data=params, headers=headers, timeout=30)
        if resp.status_code != 200:
            print(f"gsc dynamics_client - token request failed: {resp.status_code} {resp.text[:200]}")
            return None
        body = resp.json()
        token = body.get("access_token")
        expires_in = int(body.get("expires_in") or 3599)
        if not token:
            return None

        with _TOKEN_LOCK:
            _TOKEN_CACHE[env_url] = {
                "token": token,
                "expires": datetime.now(timezone.utc) + timedelta(seconds=max(60, expires_in - 200)),
            }
        return token
    except Exception as e:
        print(f"gsc dynamics_client - _get_access_token error: {e}")
        return None


async def request(method: str, endpoint_path: str, req_obj: Optional[Dict] = None,
                  additional_headers: Optional[Dict[str, str]] = None) -> Dict[str, Any]:
    try:
        token = await _get_access_token()
        if not token:
            return {"success": False, "error": "no_access_token"}

        env_url = _get_env_url()
        url = f"{env_url}/api/data/v9.2/{endpoint_path}"
        headers = {
            "OData-MaxVersion": "4.0",
            "OData-Version": "4.0",
            "Accept": "application/json",
            "Content-Type": "application/json; charset=utf-8",
            "Authorization": f"Bearer {token}",
        }
        if additional_headers:
            headers.update(additional_headers)

        async with httpx.AsyncClient() as client:
            if method in ("GET", "DELETE"):
                resp = await client.request(method, url, headers=headers, timeout=60)
            else:
                resp = await client.request(method, url, headers=headers, json=req_obj, timeout=60)

        if not resp.is_success:
            return {"success": False, "status": resp.status_code, "error": resp.text[:1000]}

        if resp.status_code == 204:
            entity_id = None
            odata_entity_id = resp.headers.get("OData-EntityId")
            if odata_entity_id:
                m = re.search(r"\(([0-9a-fA-F-]+)\)", odata_entity_id)
                if m:
                    entity_id = m.group(1)
            return {"success": True, "status": 204, "entityId": entity_id}

        try:
            return resp.json()
        except Exception:
            return {"success": True, "raw": resp.text}
    except Exception as e:
        return {"success": False, "error": str(e)}


def run_async(coro):
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


async def query(entity_set: str, filter_clause: Optional[str] = None,
                select: Optional[List[str]] = None, expand: Optional[str] = None,
                top: Optional[int] = None, orderby: Optional[str] = None) -> List[Dict]:
    try:
        params = []
        if filter_clause:
            params.append(f"$filter={filter_clause}")
        if select:
            params.append(f"$select={','.join(select)}")
        if expand:
            params.append(f"$expand={expand}")
        if top:
            params.append(f"$top={top}")
        if orderby:
            params.append(f"$orderby={orderby}")
        endpoint = entity_set + (("?" + "&".join(params)) if params else "")
        result = await request("GET", endpoint)
        if result.get("success") is False:
            return []
        return result.get("value", [])
    except Exception as e:
        print(f"gsc dynamics_client - query error: {e}")
        return []


async def fetch_xml(entity_set: str, fetch_xml_str: str) -> List[Dict]:
    try:
        from urllib.parse import quote
        endpoint = f"{entity_set}?fetchXml={quote(fetch_xml_str)}"
        result = await request("GET", endpoint)
        if result.get("success") is False:
            return []
        return result.get("value", []) or []
    except Exception as e:
        print(f"gsc dynamics_client - fetch_xml error: {e}")
        return []


async def get(entity_set: str, entity_id: str, select: Optional[List[str]] = None,
              expand: Optional[str] = None) -> Optional[Dict]:
    try:
        params = []
        if select:
            params.append(f"$select={','.join(select)}")
        if expand:
            params.append(f"$expand={expand}")
        endpoint = f"{entity_set}({entity_id})" + (("?" + "&".join(params)) if params else "")
        result = await request("GET", endpoint)
        if result.get("success") is False:
            return None
        return result
    except Exception as e:
        print(f"gsc dynamics_client - get error: {e}")
        return None


async def create(entity_set: str, fields: Dict[str, Any],
                 select: Optional[List[str]] = None) -> Dict[str, Any]:
    try:
        endpoint = entity_set
        headers: Dict[str, str] = {}
        if select:
            endpoint += "?$select=" + ",".join(select)
            headers["Prefer"] = "return=representation"
        return await request("POST", endpoint, fields, headers)
    except Exception as e:
        return {"success": False, "error": str(e)}


async def update(entity_set: str, entity_id: str, fields: Dict[str, Any]) -> Dict[str, Any]:
    try:
        endpoint = f"{entity_set}({entity_id})"
        return await request("PATCH", endpoint, fields)
    except Exception as e:
        return {"success": False, "error": str(e)}


async def create_annotation(incident_id: str, subject: str, notetext: str) -> Dict[str, Any]:
    try:
        annotation = {
            "objectid_incident@odata.bind": f"/incidents({incident_id})",
            "subject": subject,
            "notetext": notetext,
        }
        headers = {"Prefer": "return=representation"}
        return await request("POST", "annotations?$select=annotationid,subject", annotation, headers)
    except Exception as e:
        return {"success": False, "error": str(e)}


async def action(action_path: str, body: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    """POST a Dataverse action. action_path is e.g. 'SendEmail' (unbound) or
    'emails(GUID)/Microsoft.Dynamics.CRM.SendEmail' (bound)."""
    try:
        return await request("POST", action_path, body or {})
    except Exception as e:
        return {"success": False, "error": str(e)}
