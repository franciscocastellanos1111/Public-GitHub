from __future__ import annotations

import json
import logging
import os
from typing import Optional

from foundry_opus import Agent, AgentResult, FoundryClient, Tool

from .models import (
    AgentMode,
    CaseClassification,
    CaseContext,
    CaseRequest,
    ClassificationLevel,
    ConfidenceLevel,
    CustomerMatch,
    CustomerMatchMethod,
    EffortMetrics,
    ExternalSourceCitation,
    GlobalSupportCaseResult,
    InquiryDefinition,
    IntentLabel,
    KbCitation,
    MemoryProposal,
    Recommendation,
    ResolutionDraft,
    RouteDecision,
    TokenUsage,
)
from .prompts import SYSTEM_PROMPT, CASE_DISCOVERY_INSTRUCTIONS
from .tools import ALL_TOOLS
from .knowledge_tools import web_search, fetch_document_text, kb_search, kb_get
from .memory_tools import memory_lookup, resolve_techsoup_site


_logger = logging.getLogger(__name__)


def _summarize_token_usage(agent_result: AgentResult) -> TokenUsage:
    try:
        usage = TokenUsage(iterations=getattr(agent_result, "iterations", 0) or 0)
        for idx, resp in enumerate(getattr(agent_result, "raw_responses", []) or [], start=1):
            u = getattr(resp, "usage", None)
            if u is None:
                continue
            in_t = int(getattr(u, "input_tokens", 0) or 0)
            out_t = int(getattr(u, "output_tokens", 0) or 0)
            cr_t = int(getattr(u, "cache_read_input_tokens", 0) or 0)
            cc_t = int(getattr(u, "cache_creation_input_tokens", 0) or 0)
            usage.input_tokens += in_t
            usage.output_tokens += out_t
            usage.cache_read_input_tokens += cr_t
            usage.cache_creation_input_tokens += cc_t
            usage.per_iteration.append({
                "iteration": idx,
                "input_tokens": in_t,
                "output_tokens": out_t,
                "cache_read_input_tokens": cr_t,
                "cache_creation_input_tokens": cc_t,
            })
        usage.total_tokens = usage.input_tokens + usage.output_tokens
        return usage
    except Exception as e:
        _logger.warning(f"_summarize_token_usage failed: {e}")
        return TokenUsage()


def _merge_token_usage(base: TokenUsage, extra: TokenUsage) -> TokenUsage:
    try:
        base.iterations += getattr(extra, "iterations", 0) or 0
        base.input_tokens += getattr(extra, "input_tokens", 0) or 0
        base.output_tokens += getattr(extra, "output_tokens", 0) or 0
        base.cache_read_input_tokens += getattr(extra, "cache_read_input_tokens", 0) or 0
        base.cache_creation_input_tokens += getattr(extra, "cache_creation_input_tokens", 0) or 0
        base.total_tokens = base.input_tokens + base.output_tokens
        try:
            base.per_iteration.extend(getattr(extra, "per_iteration", []) or [])
        except Exception:
            pass
        return base
    except Exception as e:
        _logger.warning(f"_merge_token_usage failed: {e}")
        return base


def _log_usage(operation: str, case_id: Optional[str], usage: TokenUsage) -> None:
    try:
        _logger.info(
            "GlobalSupportCaseAgent.%s case_id=%s iterations=%d "
            "input_tokens=%d output_tokens=%d cache_read=%d cache_creation=%d total=%d",
            operation,
            case_id or "<none>",
            usage.iterations,
            usage.input_tokens,
            usage.output_tokens,
            usage.cache_read_input_tokens,
            usage.cache_creation_input_tokens,
            usage.total_tokens,
        )
    except Exception:
        pass


_SUBMIT_SCHEMA = {
    "type": "object",
    "properties": {
        "intent": {"type": "string", "enum": [i.value for i in IntentLabel]},
        "confidence": {"type": "string", "enum": [c.value for c in ConfidenceLevel]},
        "recommendation": {"type": "string", "enum": [r.value for r in Recommendation]},
        "summary": {"type": "string"},
        "reasoning": {"type": "string"},
        "inquiry_definition": {
            "type": "object",
            "description": (
                "Your Phase-1 conceptualization of the customer's request. Decompose what "
                "the customer is actually asking, using all available context, BEFORE researching."
            ),
            "properties": {
                "raw_request": {"type": "string"},
                "key_concepts": {"type": "array", "items": {"type": "string"}},
                "refined_query": {"type": "string"},
                "contextual_elements": {"type": "array", "items": {"type": "string"}},
            },
        },
        "research_process": {
            "type": "array",
            "items": {"type": "string"},
            "description": (
                "Ordered list of the meaningful research and reasoning steps you took: what you "
                "searched, why, what you found, how it shaped your plan, and the key sources. "
                "Required for both ResolveAndReply and EscalateToGlobalSupport."
            ),
        },
        "requires_human_review": {"type": "boolean"},
        "concerns": {"type": "array", "items": {"type": "string"}},
        "next_steps": {"type": "array", "items": {"type": "string"}},
        "customer_match": {
            "type": "object",
            "properties": {
                "method": {"type": "string",
                           "enum": [m.value for m in CustomerMatchMethod]},
                "contactid": {"type": "string"},
                "accountid": {"type": "string"},
                "display_name": {"type": "string"},
                "domain": {"type": "string"},
                "confidence": {"type": "number"},
                "notes": {"type": "string"},
            },
        },
        "suggested_kb": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "knowledgearticleid": {"type": "string"},
                    "title": {"type": "string"},
                    "article_number": {"type": "string"},
                    "language": {"type": "string"},
                    "excerpt": {"type": "string"},
                    "relevance": {"type": "number"},
                },
                "required": ["knowledgearticleid"],
            },
        },
        "external_sources": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "url": {"type": "string"},
                    "title": {"type": "string"},
                    "excerpt": {"type": "string"},
                },
                "required": ["url"],
            },
        },
        "draft_reply": {
            "type": "object",
            "properties": {
                "language": {"type": "string"},
                "subject": {"type": "string"},
                "body": {
                    "type": "string",
                    "description": (
                        "The customer-facing reply, written as a well-formed HTML fragment "
                        "(<p>, <br>, <a href>, <ul>/<li>). NOT plain text with bare newlines, "
                        "and NOT wrapped in markdown fences or <html>/<body> tags."
                    ),
                },
                "tone": {"type": "string"},
                "reply_to": {"type": "string"},
                "reply_to_kind": {"type": "string",
                                   "enum": ["contact", "account", "unresolved_email", "none"]},
                "reply_to_party_id": {"type": "string"},
                "reply_recipient_source": {"type": "string",
                                            "enum": ["customerid", "ts_emailaddresscustomerprovided",
                                                     "inbound_sender", "none"]},
                "from_queue_id": {"type": "string"},
                "from_queue_name": {"type": "string"},
                "no_reply_reason": {"type": "string"},
            },
        },
        "route_decision": {
            "type": "object",
            "properties": {
                "queue_name": {"type": "string"},
                "queue_id": {"type": "string"},
                "reason": {"type": "string"},
                "notes": {"type": "string"},
            },
        },
        "effort_self_assessment": {
            "type": "object",
            "properties": {
                "meets_floor": {"type": "boolean"},
                "failed_floor_items": {"type": "array", "items": {"type": "string"}},
                "notes": {"type": "string"},
            },
        },
        "classification": {
            "type": "object",
            "description": (
                "Hierarchical case classification picked from ts_fieldhierarchyandmapping. "
                "Fill as deeply as you can (type -> subtype -> detail -> subtype_3). "
                "Each level has a numeric 'code' (the picklist option value returned by "
                "dynamics_gsc_get_classification_options) and a 'label' (the display text)."
            ),
            "properties": {
                "type": {"type": "object", "properties": {
                    "code": {"type": "integer"},
                    "label": {"type": "string"},
                }},
                "subtype": {"type": "object", "properties": {
                    "code": {"type": "integer"},
                    "label": {"type": "string"},
                }},
                "detail": {"type": "object", "properties": {
                    "code": {"type": "integer"},
                    "label": {"type": "string"},
                }},
                "subtype_3": {"type": "object", "properties": {
                    "code": {"type": "integer"},
                    "label": {"type": "string"},
                }},
                "notes": {"type": "string"},
            },
        },
        "case_status_code": {
            "type": "integer",
            "description": (
                "ts_casestatus picklist value to set on the case. Use 104 (Closed) ONLY when "
                "recommendation == 'ResolveAndReply'. Leave null/omit when escalating."
            ),
        },
        "memory_proposals": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "action": {"type": "string", "enum": ["record", "feedback"]},
                    "category": {"type": "string"},
                    "scope_key": {"type": "string"},
                    "subject_key": {"type": "string"},
                    "subject": {"type": "string"},
                    "content": {"type": "object"},
                    "tags": {"type": "string"},
                    "ref": {"type": "string"},
                    "outcome": {"type": "string", "enum": ["success", "failure"]},
                    "notes": {"type": "string"},
                },
                "required": ["action"],
            },
        },
    },
    "required": ["intent", "confidence", "recommendation"],
}


_VERIFY_SCHEMA = {
    "type": "object",
    "properties": {
        "verdict": {
            "type": "string",
            "enum": ["approve", "revise", "block"],
            "description": (
                "approve = the reply is correct and valuable as-is; "
                "revise = you researched and produced a corrected/improved reply (still send it); "
                "block = ONLY if, after research, you PROVED a CENTRAL claim is absolutely false "
                "AND the reply cannot be corrected into something valuable. Blocking causes the "
                "case to be escalated to a human, which is a failure outcome — avoid it unless truly proven."
            ),
        },
        "corrected_subject": {
            "type": "string",
            "description": "Final reply subject. Provide for 'revise'; optional for 'approve'.",
        },
        "corrected_body": {
            "type": "string",
            "description": (
                "REQUIRED for verdict='revise': the FINAL reply body as a well-formed HTML fragment "
                "(<p>, <br>, <a href>, <ul>/<li>), in the customer's language, still substantive. "
                "This REPLACES the agent's draft. Correct or soften any unverified claim rather than "
                "deleting the whole reply."
            ),
        },
        "changes": {
            "type": "array",
            "items": {"type": "string"},
            "description": (
                "What you verified or corrected (e.g. 'confirmed microsoft.com/en-us/nonprofits "
                "resolves 200', 'removed unverified 3-day timeframe', 'added confirmed support URL')."
            ),
        },
        "verified_sources": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "url": {"type": "string"},
                    "title": {"type": "string"},
                    "excerpt": {"type": "string"},
                },
                "required": ["url"],
            },
            "description": "Sources you actually confirmed during verification; merged into the result's citations.",
        },
        "block_reason": {
            "type": "string",
            "description": (
                "REQUIRED only for verdict='block'. State the specific CENTRAL claim you PROVED is "
                "absolutely false and why the reply cannot be corrected. A missing citation or an "
                "unconfirmed minor detail is NOT a valid block reason."
            ),
        },
        "confidence_after": {"type": "string", "enum": ["High", "Medium", "Low"]},
    },
    "required": ["verdict"],
}

_VERIFIER_SYSTEM_PROMPT = (
    "You are a senior TechSoup support reviewer. The agent has drafted a reply to a customer, and "
    "your job is to make sure the customer receives a CORRECT, VALUABLE reply — NOT to block it. "
    "The agent is expected to resolve these cases (especially simple Formstack Microsoft-CSP "
    "inquiries); escalating to a human is a FAILURE outcome, so you must work to let a good reply "
    "ship.\n\n"
    "PROCESS:\n"
    "1. Read the inquiry and the draft. Identify any claim that is weak, unsupported, or possibly incorrect.\n"
    "2. For EACH such claim, USE your research tools (web_search, fetch_document_text, memory_lookup, "
    "resolve_techsoup_site, kb_search/kb_get) to CONFIRM or CORRECT it. A claim merely being absent "
    "from the agent's cited list is NOT proof it is wrong — verify it. Well-known canonical URLs (e.g. "
    "https://www.microsoft.com/en-us/nonprofits) and facts drawn from search snippets are acceptable "
    "once you confirm them; check that links resolve.\n"
    "3. Produce the FINAL reply. If everything checks out, verdict='approve'. If you corrected or "
    "strengthened anything — fixed a fact, removed/softened a detail you could not confirm, added a "
    "confirmed source/link — return verdict='revise' WITH a complete corrected_body (well-formed HTML, "
    "customer's language, still substantive and helpful).\n"
    "4. Use verdict='block' ONLY when you can PROVE a CENTRAL claim is absolutely false AND the reply "
    "cannot be corrected into something valuable. NEVER block over a missing citation, a plausible "
    "detail you simply could not fully confirm, or minor wording — soften or remove that detail and keep "
    "the reply.\n\n"
    "Default strongly to delivering a correct reply (approve or revise). Call `submit_verification` exactly once."
)


class GlobalSupportCaseAgent:
    def __init__(
        self,
        client: Optional[FoundryClient] = None,
        max_iterations: int = 40,
        on_tool_call=None,
    ):
        try:
            self.client = client or FoundryClient()
            self.max_iterations = max_iterations
            self._external_on_tool_call = on_tool_call
            self.on_tool_call = self._wrap_on_tool_call(on_tool_call)
            self._captured: Optional[dict] = None
            self._verification: Optional[dict] = None
            self._tool_calls: dict = {}
            self._tool_call_total: int = 0
        except Exception as e:
            raise RuntimeError(f"Failed to initialize GlobalSupportCaseAgent: {e}") from e

    def _wrap_on_tool_call(self, external):
        def _handler(tool_name, *args, **kwargs):
            try:
                self._tool_calls[tool_name] = self._tool_calls.get(tool_name, 0) + 1
                self._tool_call_total += 1
            except Exception:
                pass
            if external is not None:
                try:
                    return external(tool_name, *args, **kwargs)
                except Exception:
                    pass
            return None
        return _handler

    def handle_by_case_id(
        self,
        case_id: str,
        max_iterations: Optional[int] = None,
        memory_hints: Optional[list] = None,
        agent_mode: AgentMode = AgentMode.ACTIVE_AGENT,
    ) -> GlobalSupportCaseResult:
        try:
            self._captured = None
            self._tool_calls = {}
            self._tool_call_total = 0

            submit_tool = Tool(
                name="submit_global_support_case_result",
                description=(
                    "Submit the FINAL structured Global Support Case result. Call this "
                    "exactly once at the end of your analysis. After calling this, do "
                    "not produce any further text."
                ),
                input_schema=_SUBMIT_SCHEMA,
                handler=self._capture_result,
            )

            full_prompt = SYSTEM_PROMPT + "\n\n" + CASE_DISCOVERY_INSTRUCTIONS
            iters = max_iterations if max_iterations is not None else self.max_iterations
            iters = max(int(iters), 40)

            agent = Agent(
                name="GlobalSupportCaseAgent.CaseDiscovery",
                system_prompt=full_prompt,
                client=self.client,
                tools=[*ALL_TOOLS, submit_tool],
                max_iterations=iters,
                max_tokens=32000,
                on_tool_call=self.on_tool_call,
                enable_thinking=True,
            )

            hints_block = ""
            if memory_hints:
                try:
                    hints_block = (
                        "\n\nMEMORY HINTS (advisory only \u2014 NOT confirmation, MUST be re-verified live this run):\n"
                        + json.dumps(memory_hints, indent=2, default=str)[:8000]
                        + "\n"
                    )
                except Exception:
                    hints_block = ""

            user_payload = (
                "You are given ONLY a Dynamics 365 case id. Autonomously gather all "
                "relevant evidence (case overview, inbound email + the queue that received it, "
                "attachments, customer history), find an answer the customer can actually use, "
                "and EITHER (A) compose a complete reply and choose `ResolveAndReply`, OR "
                "(B) choose `EscalateToGlobalSupport` with notes. Call "
                "`submit_global_support_case_result` EXACTLY ONCE at the end.\n\n"
                f"case_id: {case_id}\n"
                f"agent_mode: {agent_mode.value}\n\n"
                "Begin by calling `dynamics_get_gsc_case_overview` with this case_id."
                + hints_block
            )

            agent_result = agent.run(user_payload, reset=True)
            usage = _summarize_token_usage(agent_result)
            _log_usage("handle_by_case_id", case_id, usage)

            errors_by_tool: dict = {}
            self._tally_tool_errors(getattr(agent_result, "tool_calls", None), errors_by_tool)

            if not self._captured:
                try:
                    forcing_message = (
                        "You have reached the end of your research budget WITHOUT calling "
                        "`submit_global_support_case_result`. STOP researching now. Based on "
                        "everything you have gathered so far, call "
                        "`submit_global_support_case_result` IMMEDIATELY and exactly once. "
                        "Do NOT call any other tool. If you do not have a verified, "
                        "substantive answer, choose `EscalateToGlobalSupport`, set "
                        "`requires_human_review = true`, populate `route_decision.reason`, "
                        "and record your `inquiry_definition` and `research_process` so a "
                        "human can continue. This is your final required action."
                    )
                    forced_result = agent.run(forcing_message, reset=False)
                    forced_usage = _summarize_token_usage(forced_result)
                    usage = _merge_token_usage(usage, forced_usage)
                    self._tally_tool_errors(getattr(forced_result, "tool_calls", None), errors_by_tool)
                except Exception as e:
                    _logger.warning(f"GSC agent - forced-submit pass failed for case {case_id}: {e}")

            # Adversarial pre-send verification (may downgrade ResolveAndReply → escalate).
            if self._captured:
                try:
                    v_usage = self._run_verification(case_id, self._captured)
                    if v_usage is not None:
                        usage = _merge_token_usage(usage, v_usage)
                except Exception as e:
                    _logger.warning(f"GSC verification wiring failed for case {case_id}: {e}")

            effort = EffortMetrics(
                iterations=usage.iterations,
                total_tool_calls=self._tool_call_total,
                by_tool=dict(self._tool_calls),
                errors_by_tool=errors_by_tool,
            )

            if not self._captured:
                fb = self._fallback_result(
                    case_id=case_id,
                    reason=(
                        "Model did not call submit_global_support_case_result. "
                        f"Last output:\n{agent_result.output[:1000]}"
                    ),
                    token_usage=usage,
                )
                fb.effort = effort
                return fb

            return self._build_result(case_id, self._captured, token_usage=usage, effort=effort)
        except Exception as e:
            return self._fallback_result(
                case_id=case_id,
                reason=f"Case-by-id handling failed with error: {e}",
            )

    def _capture_result(self, **kwargs) -> str:
        try:
            self._captured = kwargs
            return "Global support case result captured."
        except Exception as e:
            return f"Error capturing result: {e}"

    def _run_verification(self, case_id: Optional[str], captured: dict) -> Optional[TokenUsage]:
        """Corrective pre-send review (NOT a veto).

        Runs ONLY when the agent chose ResolveAndReply. The reviewer is a
        research-capable reviser: it confirms/corrects weak or unsupported claims
        using the same research tools as the main agent and returns an IMPROVED
        reply (approve/revise) so the case still resolves. It downgrades to
        EscalateToGlobalSupport ONLY when it can PROVE a central claim is
        absolutely false and uncorrectable (verdict='block'). Mutates `captured`
        in place. Returns the verifier's token usage (or None). Never raises.
        """
        try:
            if (captured.get("recommendation") or "") != Recommendation.RESOLVE_AND_REPLY.value:
                return None
            if (os.getenv("GSC_ENABLE_VERIFICATION", "1") or "1").strip().lower() in ("0", "false", "no"):
                return None

            draft = captured.get("draft_reply") or {}
            if not isinstance(draft, dict):
                return None
            body = (draft.get("body") or "")
            if not body.strip():
                return None  # nothing to verify; effort floor will handle it

            payload = {
                "inquiry_definition": captured.get("inquiry_definition"),
                "draft_reply": {
                    "language": draft.get("language"),
                    "subject": draft.get("subject"),
                    "body": body,
                },
                "external_sources": captured.get("external_sources") or [],
                "suggested_kb": captured.get("suggested_kb") or [],
                "research_process": captured.get("research_process") or [],
                "stated_confidence": captured.get("confidence"),
            }

            verify_tool = Tool(
                name="submit_verification",
                description=(
                    "Submit your FINAL verification verdict and (for 'revise') the corrected reply. "
                    "Call exactly once, after you have finished any research."
                ),
                input_schema=_VERIFY_SCHEMA,
                handler=self._capture_verification,
            )
            self._verification = None
            verifier = Agent(
                name="GlobalSupportCaseAgent.Verifier",
                system_prompt=_VERIFIER_SYSTEM_PROMPT,
                client=self.client,
                # Research-capable: the verifier can confirm/correct claims, not just judge.
                tools=[
                    web_search, fetch_document_text, memory_lookup,
                    resolve_techsoup_site, kb_search, kb_get, verify_tool,
                ],
                max_iterations=12,
                max_tokens=16000,
                on_tool_call=self.on_tool_call,
                enable_thinking=True,
            )
            user_msg = (
                "Review the drafted customer reply below. Research and CORRECT any weak or "
                "unsupported claim so a high-quality reply can be sent — do NOT block a resolvable "
                "reply. Only block if you can prove a central claim is absolutely false and "
                "uncorrectable. Call `submit_verification` exactly once when done.\n\n"
                + json.dumps(payload, indent=2, default=str)[:24000]
            )
            vres = verifier.run(user_msg, reset=True)
            usage = _summarize_token_usage(vres)

            verdict = self._verification or {}
            v = (verdict.get("verdict") or "").strip().lower()
            changes = [str(c) for c in (verdict.get("changes") or []) if c]

            if v == "block":
                reason = (verdict.get("block_reason") or "").strip() or (
                    "verifier reported a proven, uncorrectable incorrect claim"
                )
                _logger.info("GSC verifier BLOCKED case %s (proven incorrect): %s", case_id, reason)
                captured["recommendation"] = Recommendation.ESCALATE_TO_GLOBAL_SUPPORT.value
                captured["requires_human_review"] = True
                captured["case_status_code"] = None
                rd = captured.get("route_decision")
                if not isinstance(rd, dict):
                    rd = {}
                if not rd.get("reason"):
                    rd["reason"] = "Verification proved a central claim incorrect: " + reason
                captured["route_decision"] = rd
                concerns = captured.get("concerns")
                if not isinstance(concerns, list):
                    concerns = []
                concerns.append("Pre-send verification blocked (proven incorrect): " + reason)
                captured["concerns"] = concerns
                return usage

            # approve / revise (or empty verdict) — KEEP ResolveAndReply, apply improvements.
            if v == "revise":
                cb = (verdict.get("corrected_body") or "").strip()
                if cb:
                    draft["body"] = cb
                cs = (verdict.get("corrected_subject") or "").strip()
                if cs:
                    draft["subject"] = cs
                captured["draft_reply"] = draft

            verified_sources = verdict.get("verified_sources") or []
            if verified_sources:
                ext = captured.get("external_sources")
                if not isinstance(ext, list):
                    ext = []
                seen_urls = {e.get("url") for e in ext if isinstance(e, dict)}
                for s in verified_sources:
                    if isinstance(s, dict) and s.get("url") and s.get("url") not in seen_urls:
                        ext.append(s)
                        seen_urls.add(s.get("url"))
                captured["external_sources"] = ext

            ca = (verdict.get("confidence_after") or "").strip()
            if ca in ("High", "Medium", "Low"):
                captured["confidence"] = ca

            # Record what verification did as an informational research step (not a concern).
            if changes:
                rp = captured.get("research_process")
                if not isinstance(rp, list):
                    rp = []
                rp.append("Pre-send verification (" + (v or "approve") + "): " + "; ".join(changes))
                captured["research_process"] = rp

            _logger.info("GSC verifier %s case %s (changes=%d).", v or "approve", case_id, len(changes))
            return usage
        except Exception as e:
            _logger.warning(f"GSC verification pass failed (continuing) for case {case_id}: {e}")
            return None

    @staticmethod
    def _tally_tool_errors(tool_calls, acc: dict) -> None:
        """Count is_error tool results per tool name so infra failures are visible in
        EffortMetrics (distinct from 'tool found nothing'). Never raises."""
        try:
            for tc in (tool_calls or []):
                if not isinstance(tc, dict):
                    continue
                if tc.get("is_error"):
                    name = tc.get("name") or "<unknown>"
                    acc[name] = acc.get(name, 0) + 1
                else:
                    # Tools that return a structured error string (not flagged is_error).
                    out = tc.get("output")
                    if isinstance(out, str) and (
                        out.startswith("ERROR") or out.startswith("SEARCH_INFRA_UNAVAILABLE")
                        or '"error"' in out[:120]
                    ):
                        name = tc.get("name") or "<unknown>"
                        acc[name] = acc.get(name, 0) + 1
        except Exception:
            pass

    def _capture_verification(self, **kwargs) -> str:
        try:
            self._verification = kwargs
            return "Verification captured."
        except Exception as e:
            return f"Error capturing verification: {e}"

    def _build_result(
        self,
        case_id: Optional[str],
        data: dict,
        token_usage: Optional[TokenUsage] = None,
        effort: Optional[EffortMetrics] = None,
    ) -> GlobalSupportCaseResult:
        try:
            extra_concerns: list[str] = []

            kb_list: list[KbCitation] = []
            for k in (data.get("suggested_kb") or []):
                if isinstance(k, dict):
                    try:
                        kb_list.append(KbCitation(**k))
                    except Exception as e:
                        extra_concerns.append(f"KB citation parse failed: {e}")

            ext_list: list[ExternalSourceCitation] = []
            for s in (data.get("external_sources") or []):
                if isinstance(s, dict):
                    try:
                        ext_list.append(ExternalSourceCitation(**s))
                    except Exception as e:
                        extra_concerns.append(f"External source parse failed: {e}")

            customer_match: Optional[CustomerMatch] = None
            cm = data.get("customer_match")
            if isinstance(cm, dict):
                try:
                    customer_match = CustomerMatch(**cm)
                except Exception as e:
                    extra_concerns.append(f"Customer match parse failed: {e}")

            draft: Optional[ResolutionDraft] = None
            dr = data.get("draft_reply")
            if isinstance(dr, dict):
                try:
                    draft = ResolutionDraft(**dr)
                except Exception as e:
                    extra_concerns.append(f"Draft reply parse failed: {e}")

            memory_proposals: list[MemoryProposal] = []
            for mp in (data.get("memory_proposals") or []):
                if isinstance(mp, dict):
                    try:
                        memory_proposals.append(MemoryProposal(**mp))
                    except Exception as e:
                        extra_concerns.append(f"Memory proposal parse failed: {e}")

            route_decision: Optional[RouteDecision] = None
            rd = data.get("route_decision")
            if isinstance(rd, dict):
                try:
                    route_decision = RouteDecision(**rd)
                except Exception as e:
                    extra_concerns.append(f"Route decision parse failed: {e}")

            classification: Optional[CaseClassification] = None
            cls = data.get("classification")
            if isinstance(cls, dict):
                def _level(d):
                    if not isinstance(d, dict):
                        return None
                    if d.get("code") is None and not d.get("label"):
                        return None
                    try:
                        code = d.get("code")
                        code_int = int(code) if code is not None and str(code).strip() != "" else None
                        return ClassificationLevel(code=code_int, label=d.get("label"))
                    except Exception:
                        return None
                try:
                    classification = CaseClassification(
                        type=_level(cls.get("type")),
                        subtype=_level(cls.get("subtype")),
                        detail=_level(cls.get("detail")),
                        subtype_3=_level(cls.get("subtype_3")),
                        notes=cls.get("notes"),
                    )
                except Exception as e:
                    extra_concerns.append(f"Classification parse failed: {e}")

            case_status_code: Optional[int] = None
            csc_raw = data.get("case_status_code")
            if csc_raw is not None:
                try:
                    case_status_code = int(csc_raw)
                except Exception:
                    extra_concerns.append(f"case_status_code not int: {csc_raw}")

            inquiry_definition: Optional[InquiryDefinition] = None
            idf = data.get("inquiry_definition")
            if isinstance(idf, dict):
                try:
                    inquiry_definition = InquiryDefinition(
                        raw_request=idf.get("raw_request"),
                        key_concepts=[str(x) for x in (idf.get("key_concepts") or []) if x],
                        refined_query=idf.get("refined_query"),
                        contextual_elements=[str(x) for x in (idf.get("contextual_elements") or []) if x],
                    )
                except Exception as e:
                    extra_concerns.append(f"Inquiry definition parse failed: {e}")

            research_process: list[str] = []
            for step in (data.get("research_process") or []):
                if step is None:
                    continue
                research_process.append(step if isinstance(step, str) else str(step))

            # Effort self-assessment from the model, merged into effort metrics.
            esa = data.get("effort_self_assessment") or {}
            if effort is not None and isinstance(esa, dict):
                try:
                    if esa.get("failed_floor_items"):
                        effort.reasons.extend(
                            [str(x) for x in esa.get("failed_floor_items") if x]
                        )
                    if esa.get("notes"):
                        effort.reasons.append(f"agent_notes: {esa.get('notes')}")
                except Exception:
                    pass

            result = GlobalSupportCaseResult(
                case_id=case_id,
                intent=IntentLabel(data.get("intent") or IntentLabel.OTHER.value),
                confidence=ConfidenceLevel(data.get("confidence") or ConfidenceLevel.LOW.value),
                recommendation=Recommendation(
                    data.get("recommendation") or Recommendation.ESCALATE_TO_GLOBAL_SUPPORT.value
                ),
                customer_match=customer_match,
                inquiry_definition=inquiry_definition,
                research_process=research_process,
                suggested_kb=kb_list,
                external_sources=ext_list,
                draft_reply=draft,
                route_decision=route_decision,
                classification=classification,
                case_status_code=case_status_code,
                summary=data.get("summary") or "",
                reasoning=data.get("reasoning") or "",
                concerns=(data.get("concerns") or []) + extra_concerns,
                next_steps=data.get("next_steps") or [],
                requires_human_review=bool(data.get("requires_human_review", True)),
                token_usage=token_usage,
                memory_proposals=memory_proposals,
                effort=effort,
            )
            result.case_note = result.to_case_note()
            return result
        except Exception as e:
            return self._fallback_result(
                case_id=case_id,
                reason=f"Failed to parse model output: {e}. Raw: {data}",
                token_usage=token_usage,
            )

    @staticmethod
    def _fallback_result(
        case_id: Optional[str],
        reason: str,
        token_usage: Optional[TokenUsage] = None,
    ) -> GlobalSupportCaseResult:
        try:
            res = GlobalSupportCaseResult(
                case_id=case_id,
                intent=IntentLabel.OTHER,
                confidence=ConfidenceLevel.LOW,
                recommendation=Recommendation.ESCALATE_TO_GLOBAL_SUPPORT,
                reasoning=reason,
                requires_human_review=True,
                concerns=["Agent did not complete cleanly."],
                token_usage=token_usage,
            )
            res.case_note = res.to_case_note()
            return res
        except Exception as e:
            raise RuntimeError(f"Failed to build fallback result: {e}") from e
