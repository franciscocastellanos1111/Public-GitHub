"""Focused unit tests for the changes made to the Global Support Case Agent.

These are offline tests: every Dynamics / Foundry / network boundary is monkeypatched.
Run from the function app root:  .venv/Scripts/python.exe -m pytest global_support_case_agent/tests -v
"""
from __future__ import annotations

import json

import pytest


# ---------------------------------------------------------------------------
# A.3 — HTML reply body safety net
# ---------------------------------------------------------------------------
def test_ensure_html_body_wraps_plaintext():
    from global_support_case_agent.dynamics_tools import _ensure_html_body
    out = _ensure_html_body("Hello there\n\nSecond paragraph\nwith a line break")
    assert "<p>Hello there</p>" in out
    assert "Second paragraph<br>with a line break" in out


def test_ensure_html_body_keeps_existing_html():
    from global_support_case_agent.dynamics_tools import _ensure_html_body
    html = "<p>Already <a href='https://x'>linked</a></p>"
    assert _ensure_html_body(html) == html


def test_ensure_html_body_strips_markdown_fence():
    from global_support_case_agent.dynamics_tools import _ensure_html_body
    out = _ensure_html_body("```html\n<p>x</p>\n```")
    assert out == "<p>x</p>"


# ---------------------------------------------------------------------------
# C.11 — relevance-aware long-document retrieval
# ---------------------------------------------------------------------------
def test_relevant_excerpts_surfaces_buried_answer():
    from global_support_case_agent.knowledge_tools import _relevant_excerpts
    doc = ("alpha " * 600) + "THE ANSWER IS CSP REGISTRATION IN ITALY " + ("beta " * 6000)
    out = _relevant_excerpts(doc, "CSP registration Italy", 20000)
    assert "THE ANSWER IS CSP REGISTRATION IN ITALY" in out
    assert len(out) <= 20000 + 500


def test_relevant_excerpts_head_only_when_no_match():
    from global_support_case_agent.knowledge_tools import _relevant_excerpts
    doc = "zzzz " * 8000
    out = _relevant_excerpts(doc, "nonexistentterm", 9000)
    assert "no passages matched" in out


# ---------------------------------------------------------------------------
# B.5 — search provider chain + infra-failure signal
# ---------------------------------------------------------------------------
def test_search_provider_prefers_configured_then_falls_back(monkeypatch):
    import global_support_case_agent.knowledge_tools as kt

    calls = []

    def fake_tavily(q, n):
        calls.append("tavily")
        raise RuntimeError("tavily down")

    def fake_ddg(q, n):
        calls.append("ddg")
        return [{"url": "https://techsoup.it/x", "title": "T", "snippet": "s"}]

    monkeypatch.setitem(kt._SEARCH_PROVIDERS, "tavily", fake_tavily)
    monkeypatch.setitem(kt._SEARCH_PROVIDERS, "ddg", fake_ddg)
    monkeypatch.setenv("GSC_SEARCH_PROVIDER", "tavily")

    results, errors = kt._run_search_providers("q", 5)
    assert calls == ["tavily", "ddg"]      # tried provider first, then fell back
    assert results and results[0]["url"].endswith("/x")
    # Fallback recovered, so no infra error is surfaced (errors only matter when
    # there are zero results across the whole chain).
    assert errors == []


def test_web_search_reports_infra_unavailable_when_all_fail(monkeypatch):
    import global_support_case_agent.knowledge_tools as kt

    def boom(q, n):
        raise RuntimeError("network blocked")

    monkeypatch.setitem(kt._SEARCH_PROVIDERS, "ddg", boom)
    monkeypatch.delenv("GSC_SEARCH_PROVIDER", raising=False)

    out = kt.web_search.invoke({"query": "anything"})
    assert out.startswith("SEARCH_INFRA_UNAVAILABLE")


def test_web_search_genuine_no_results(monkeypatch):
    import global_support_case_agent.knowledge_tools as kt
    monkeypatch.setitem(kt._SEARCH_PROVIDERS, "ddg", lambda q, n: [])
    monkeypatch.delenv("GSC_SEARCH_PROVIDER", raising=False)
    out = kt.web_search.invoke({"query": "obscure"})
    assert out.startswith("No results")


# ---------------------------------------------------------------------------
# A.1 — case overview surfaces the new fields
# ---------------------------------------------------------------------------
def test_overview_surfaces_country_origin_and_customer(monkeypatch):
    import global_support_case_agent.dynamics_tools as dt

    incident = {
        "incidentid": "case-1",
        "caseorigincode": 100003,
        "caseorigincode@OData.Community.Display.V1.FormattedValue": "Formstack",
        "casetypecode@OData.Community.Display.V1.FormattedValue": "Question",
        "ts_countrycode": "IT",
        "ts_emailaddresscustomerprovided": "user@example.org",
        "customerid_account": {"accountid": "acc-1", "name": "TechSoup Stock Customer Service"},
    }
    monkeypatch.setattr(dt.dynamics_client, "request", lambda *a, **k: incident)
    monkeypatch.setattr(dt.dynamics_client, "query", lambda *a, **k: [])
    monkeypatch.setattr(dt.dynamics_client, "run_async", lambda coro: coro if isinstance(coro, (dict, list)) else coro)

    # run_async normally executes a coroutine; here request/query already return values.
    monkeypatch.setattr(dt.dynamics_client, "run_async", lambda v: v)

    out = json.loads(dt.dynamics_get_gsc_case_overview.invoke({"case_id": "case-1"}))
    assert out["caseorigincode"] == 100003
    assert out["caseorigincode_label"] == "Formstack"
    assert out["casetypecode_label"] == "Question"
    assert out["ts_countrycode"] == "IT"
    assert out["ts_emailaddresscustomerprovided"] == "user@example.org"
    assert out["customerid_name"] == "TechSoup Stock Customer Service"
    assert out["customerid_type"] == "account"


def test_overview_request_excludes_invalid_account_field(monkeypatch):
    # Guards the pre-existing bug: address1_country_code is NOT a valid property on
    # account and 400s the whole GET (reported as 'case not found'). Lock it out.
    import global_support_case_agent.dynamics_tools as dt
    seen = {}

    def fake_request(method, endpoint, additional_headers=None):
        seen["endpoint"] = endpoint
        return {"incidentid": "c1"}

    monkeypatch.setattr(dt.dynamics_client, "request", fake_request)
    monkeypatch.setattr(dt.dynamics_client, "query", lambda *a, **k: [])
    monkeypatch.setattr(dt.dynamics_client, "run_async", lambda v: v)

    dt.dynamics_get_gsc_case_overview.invoke({"case_id": "c1"})
    ep = seen["endpoint"]
    assert "address1_country_code" not in ep
    assert "ts_countrycode" in ep and "caseorigincode" in ep
    assert "ts_emailaddresscustomerprovided" in ep
    assert "ts_region" not in ep


# ---------------------------------------------------------------------------
# C.8 — one-call classification subtree builder
# ---------------------------------------------------------------------------
def test_classification_tree_builds_nested(monkeypatch):
    import global_support_case_agent.dynamics_tools as dt

    rows_by_field = {
        "Type_GlobalSupport": [
            {"ts_value": "Eligibility", "ts_valuecode": "1", "ts_valueseq": "1",
             "ts_parentfieldvalue": "Question"},
        ],
        "SubType_GlobalSupport": [
            {"ts_value": "Nonprofit", "ts_valuecode": "10", "ts_valueseq": "1",
             "ts_parentfieldvalue": "Eligibility"},
        ],
        "SubType_2_GlobalSupport": [
            {"ts_value": "Docs", "ts_valuecode": "100", "ts_valueseq": "1",
             "ts_parentfieldvalue": "Nonprofit"},
        ],
        "SubType_3_GlobalSupport": [
            {"ts_value": "Statute", "ts_valuecode": "1000", "ts_valueseq": "1",
             "ts_parentfieldvalue": "Docs"},
        ],
    }
    monkeypatch.setattr(dt, "_fetch_all_level_rows", lambda fieldname: rows_by_field.get(fieldname, []))

    out = json.loads(dt.dynamics_gsc_get_classification_tree.invoke({"parent_type_label": "Question"}))
    assert out["type_count"] == 1
    t = out["tree"][0]
    assert t["label"] == "Eligibility" and t["code"] == "1"
    st = t["subtypes"][0]
    assert st["label"] == "Nonprofit"
    assert st["details"][0]["label"] == "Docs"
    assert st["details"][0]["subtype_3"][0]["label"] == "Statute"


# ---------------------------------------------------------------------------
# C.10 — per-tool error tallying
# ---------------------------------------------------------------------------
def test_tally_tool_errors_counts_flags_and_error_strings():
    from global_support_case_agent.agent import GlobalSupportCaseAgent
    acc: dict = {}
    GlobalSupportCaseAgent._tally_tool_errors(
        [
            {"name": "web_search", "output": "SEARCH_INFRA_UNAVAILABLE: x", "is_error": False},
            {"name": "fetch_document_text", "output": "ERROR fetching URL: boom", "is_error": False},
            {"name": "dynamics_gsc_query", "output": '{"error": "bad"}', "is_error": False},
            {"name": "memory_lookup", "output": "ok normal result", "is_error": False},
            {"name": "kb_search", "output": "x", "is_error": True},
        ],
        acc,
    )
    assert acc == {
        "web_search": 1,
        "fetch_document_text": 1,
        "dynamics_gsc_query": 1,
        "kb_search": 1,
    }


# ---------------------------------------------------------------------------
# A.2 — idempotency guard short-circuits redelivery
# ---------------------------------------------------------------------------
def test_idempotency_guard_skips_on_matching_operation_id(monkeypatch):
    import global_support_case_agent.service as svc
    from global_support_case_agent.models import ActionTaken, AgentMode, CaseRequest

    # Case already stamped with this exact operation_id => redelivery.
    monkeypatch.setattr(svc.dynamics_tools, "get_case_ai_operation_id", lambda case_id: "op-123")

    def _fail_agent(*a, **k):
        raise AssertionError("agent should NOT run on idempotent redelivery")

    monkeypatch.setattr(svc, "GlobalSupportCaseAgent", type(
        "Stub", (), {
            "__init__": _fail_agent,
            "_fallback_result": staticmethod(svc.GlobalSupportCaseAgent._fallback_result),
        }
    ))

    req = CaseRequest(case_id="case-1", agent_mode=AgentMode.ACTIVE_AGENT)
    result = svc.handle_case_by_id(case_id="case-1", operation_id="op-123", request=req)
    assert result.action_taken == ActionTaken.SKIPPED
    assert "Idempotent skip" in result.reasoning


def test_idempotency_guard_runs_when_operation_id_differs(monkeypatch):
    import global_support_case_agent.service as svc
    from global_support_case_agent.models import (
        AgentMode, CaseRequest, GlobalSupportCaseResult, Recommendation,
    )

    monkeypatch.setattr(svc.dynamics_tools, "get_case_ai_operation_id", lambda case_id: "OLD-op")

    ran = {"called": False}

    class StubAgent:
        def __init__(self, *a, **k):
            pass

        def handle_by_case_id(self, **k):
            ran["called"] = True
            return GlobalSupportCaseResult(
                case_id="case-1", recommendation=Recommendation.ESCALATE_TO_GLOBAL_SUPPORT,
            )

    monkeypatch.setattr(svc, "GlobalSupportCaseAgent", StubAgent)
    # Don't actually write to Dynamics in persistence.
    monkeypatch.setattr(svc, "_persist_result", lambda **k: None)

    req = CaseRequest(case_id="case-1", agent_mode=AgentMode.ACTIVE_AGENT)
    svc.handle_case_by_id(case_id="case-1", operation_id="NEW-op", request=req)
    assert ran["called"] is True


# ---------------------------------------------------------------------------
# B.7 — corrective verification: block / approve / revise
# ---------------------------------------------------------------------------
def _stub_verifier(monkeypatch, agent, verdict_dict):
    import global_support_case_agent.agent as ag

    class StubVerifier:
        def __init__(self, *a, **k):
            agent._verification = dict(verdict_dict)

        def run(self, *a, **k):
            class R:
                iterations = 1
                raw_responses = []
            return R()

    monkeypatch.setattr(ag, "Agent", StubVerifier)
    monkeypatch.setenv("GSC_ENABLE_VERIFICATION", "1")


def _fresh_agent():
    import global_support_case_agent.agent as ag
    agent = ag.GlobalSupportCaseAgent.__new__(ag.GlobalSupportCaseAgent)
    agent.client = object()
    agent._verification = None
    agent.on_tool_call = None
    return agent


def test_verification_blocks_only_on_proven_incorrect(monkeypatch):
    from global_support_case_agent.models import Recommendation
    agent = _fresh_agent()
    _stub_verifier(monkeypatch, agent, {
        "verdict": "block",
        "block_reason": "Central claim 'TechSoup gives free Office to anyone' is provably false.",
    })
    captured = {
        "recommendation": Recommendation.RESOLVE_AND_REPLY.value,
        "draft_reply": {"body": "A confident but provably wrong answer.", "language": "en"},
        "external_sources": [{"url": "https://x", "excerpt": "unrelated"}],
        "case_status_code": 104,
    }
    agent._run_verification("case-1", captured)
    assert captured["recommendation"] == Recommendation.ESCALATE_TO_GLOBAL_SUPPORT.value
    assert captured["requires_human_review"] is True
    assert captured["case_status_code"] is None
    assert any("proven incorrect" in c.lower() for c in captured["concerns"])


def test_verification_approve_keeps_reply(monkeypatch):
    from global_support_case_agent.models import Recommendation
    agent = _fresh_agent()
    _stub_verifier(monkeypatch, agent, {"verdict": "approve"})
    captured = {
        "recommendation": Recommendation.RESOLVE_AND_REPLY.value,
        "draft_reply": {"body": "A well-supported answer.", "language": "en"},
        "external_sources": [{"url": "https://x", "excerpt": "supports the answer"}],
    }
    agent._run_verification("case-1", captured)
    assert captured["recommendation"] == Recommendation.RESOLVE_AND_REPLY.value
    assert captured["draft_reply"]["body"] == "A well-supported answer."


def test_verification_revise_corrects_and_keeps_reply(monkeypatch):
    from global_support_case_agent.models import Recommendation
    agent = _fresh_agent()
    _stub_verifier(monkeypatch, agent, {
        "verdict": "revise",
        "corrected_subject": "Corrected subject",
        "corrected_body": "<p>Corrected, researched answer with confirmed link.</p>",
        "changes": ["confirmed microsoft.com/en-us/nonprofits resolves", "removed unverified timeframe"],
        "verified_sources": [{"url": "https://www.microsoft.com/en-us/nonprofits", "title": "MS Nonprofits"}],
        "confidence_after": "High",
    })
    captured = {
        "recommendation": Recommendation.RESOLVE_AND_REPLY.value,
        "confidence": "Medium",
        "draft_reply": {"body": "Original with an unverified detail.", "subject": "Old", "language": "no"},
        "external_sources": [{"url": "https://support.techsoup.org/x"}],
        "research_process": ["step 1"],
    }
    agent._run_verification("case-1", captured)
    # Still resolves, with the corrected content applied.
    assert captured["recommendation"] == Recommendation.RESOLVE_AND_REPLY.value
    assert captured["draft_reply"]["body"].startswith("<p>Corrected")
    assert captured["draft_reply"]["subject"] == "Corrected subject"
    assert captured["confidence"] == "High"
    urls = [s["url"] for s in captured["external_sources"]]
    assert "https://www.microsoft.com/en-us/nonprofits" in urls
    assert any("verification" in s.lower() for s in captured["research_process"])


# ---------------------------------------------------------------------------
# Archive: ResultJson chunking round-trip (split on write, reassemble on read)
# ---------------------------------------------------------------------------
def test_result_json_chunk_roundtrip():
    import npv_archive_service as nas
    payload = json.dumps({"body": "x" * 70000, "n": 7})  # ~70KB JSON, > 2 chunks
    archive = {"RowKey": "r1", "ResultJson": payload}

    nas._split_result_json(archive)
    # Primary chunk fits a single property; overflow lives in ResultJson_2/_3.
    assert len(archive["ResultJson"]) <= nas._MAX_STR_PROP_CHARS
    assert "ResultJson_2" in archive and "ResultJson_3" in archive
    assert archive["ResultJsonChunks"] == 3

    # Reassembly reconstructs the exact original string.
    assert nas._reassemble_result_json(archive) == payload

    # Serialization folds chunks back into one parsed ResultJson and hides bookkeeping.
    out = nas._serialize_entity(archive)
    assert out["ResultJson"] == {"body": "x" * 70000, "n": 7}
    assert "ResultJson_2" not in out and "ResultJsonChunks" not in out


def test_split_clears_stale_chunks_when_small():
    import npv_archive_service as nas
    # A prior large version left chunk props; a new small ResultJson must purge them.
    archive = {"ResultJson": "small", "ResultJson_2": "stale", "ResultJson_3": "stale",
               "ResultJsonChunks": 3}
    nas._split_result_json(archive)
    assert archive["ResultJson"] == "small"
    assert "ResultJson_2" not in archive and "ResultJson_3" not in archive
    assert "ResultJsonChunks" not in archive


def test_chunk_cap_truncates_extreme_payload():
    import npv_archive_service as nas
    huge = "y" * (nas._MAX_STR_PROP_CHARS * (nas._RESULTJSON_MAX_CHUNKS + 5))
    archive = {"ResultJson": huge}
    nas._split_result_json(archive)
    assert archive["ResultJsonChunks"] == nas._RESULTJSON_MAX_CHUNKS
    assert "[TRUNCATED" in nas._reassemble_result_json(archive)


# ---------------------------------------------------------------------------
# B.6 — client attempts thinking only when opted in, and degrades gracefully
# ---------------------------------------------------------------------------
def test_client_thinking_opt_in_and_fallback(monkeypatch):
    import foundry_opus.client as fc

    client = fc.FoundryClient.__new__(fc.FoundryClient)
    client.config = type("Cfg", (), {
        "deployment": "claude-opus-4-8", "max_tokens": 32000,
        "temperature": None, "thinking_budget": 10000, "interleaved_thinking": False,
    })()
    client._thinking_supported = True

    seen = {"calls": []}

    import httpx

    def _thinking_rejected_error():
        resp = httpx.Response(status_code=400, request=httpx.Request("POST", "https://x"))
        return fc.anthropic.APIStatusError(
            "unknown field: thinking",
            response=resp,
            body={"error": {"message": "unknown field thinking / budget_tokens"}},
        )

    class FakeMessages:
        def create(self, **kwargs):
            seen["calls"].append(kwargs)
            if "thinking" in kwargs:
                # Simulate a relay that rejects the thinking parameter.
                raise _thinking_rejected_error()
            return "OK"

    client._client = type("C", (), {"messages": FakeMessages()})()

    # thinking=None (default for OCR/complete) => never attempts thinking.
    seen["calls"].clear()
    client.chat(messages=[{"role": "user", "content": "hi"}], thinking=None)
    assert all("thinking" not in c for c in seen["calls"])

    # thinking=True => attempts once, gets rejected, disables, retries without.
    seen["calls"].clear()
    out = client.chat(messages=[{"role": "user", "content": "hi"}], thinking=True)
    assert out == "OK"
    assert len(seen["calls"]) == 2
    assert "thinking" in seen["calls"][0] and "thinking" not in seen["calls"][1]
    assert client._thinking_supported is False
