from __future__ import annotations

import os
from typing import Optional

import requests


class TechSoupFunctionsClient:
    def __init__(self, base_url: Optional[str] = None, auth_key: Optional[str] = None, timeout: float = 30.0):
        try:
            self.base_url = (base_url or os.getenv("TS_FUNCTIONS_BASE_URL") or "").rstrip("/")
            self.auth_key = auth_key or os.getenv("TS_FUNCTIONS_AUTH_KEY") or ""
            self.timeout = timeout
        except Exception as e:
            raise RuntimeError(f"Failed to initialize TechSoupFunctionsClient: {e}") from e

    def post_case_note(self, case_id: str, note: str, subject: str = "Nonprofit Verification") -> dict:
        try:
            if not self.base_url or not self.auth_key:
                raise RuntimeError("TS_FUNCTIONS_BASE_URL and TS_FUNCTIONS_AUTH_KEY must be configured.")
            url = f"{self.base_url}/CRM/{self.auth_key}/CreateAnnotation"
            payload = {"objectId": case_id, "subject": subject, "noteText": note}
            resp = requests.post(url, json=payload, timeout=self.timeout)
            resp.raise_for_status()
            try:
                return resp.json()
            except Exception:
                return {"raw": resp.text}
        except Exception as e:
            raise RuntimeError(f"post_case_note failed: {e}") from e
