from __future__ import annotations

import logging
import os
import uuid
from typing import Optional

from .agent import GlobalSupportCaseAgent
from .models import (
    ActionTaken,
    AgentMode,
    CaseRequest,
    ConfidenceLevel,
    GlobalSupportCaseResult,
    IntentLabel,
    Recommendation,
    RouteDecision,
)
from . import dynamics_tools


_logger = logging.getLogger(__name__)


_CONFIDENCE_OPTIONVALUES = {
    ConfidenceLevel.HIGH.value: 10001,
    ConfidenceLevel.MEDIUM.value: 10002,
    ConfidenceLevel.LOW.value: 10003,
}

# ResolveAndReply and EscalateToGlobalSupport are mapped onto pre-existing
# picklist values so we don't have to create new option-set entries in Dataverse.
_RECOMMENDATION_OPTIONVALUES = {
    Recommendation.RESOLVE_AND_REPLY.value: 10001,         # AutoResolve slot
    Recommendation.ESCALATE_TO_GLOBAL_SUPPORT.value: 10003,  # EscalateToTier2 slot
    Recommendation.AUTO_RESOLVE.value: 10001,
    Recommendation.DRAFT_REPLY_FOR_REVIEW.value: 10002,
    Recommendation.ESCALATE_TO_TIER2.value: 10003,
    Recommendation.ESCALATE_TO_BILLING.value: 10004,
    Recommendation.REQUEST_MORE_INFO.value: 10005,
    Recommendation.ROUTE_TO_NONPROFIT_VERIFIER.value: 10006,
    Recommendation.REJECT.value: 10007,
}


_GLOBAL_SUPPORT_QUEUE_NAME = os.getenv("GSC_FALLBACK_QUEUE_NAME", "Global Support")


def _evaluate_effort_floor(result: GlobalSupportCaseResult) -> tuple[bool, list[str]]:
    """Objective acceptable-effort floor (system prompt mirror).

    Returns (acceptable, list_of_failed_floor_items).
    """
    reasons: list[str] = []
    try:
        tool_counts = (result.effort.by_tool if result.effort else {}) or {}
        if tool_counts.get("memory_lookup", 0) < 1:
            reasons.append("memory_lookup not called")
        if tool_counts.get("web_search", 0) < 1:
            reasons.append("web_search not called (primary research tool)")
        evidence_calls = tool_counts.get("fetch_document_text", 0)
        if evidence_calls < 1:
            reasons.append("no web evidence retrieved (fetch_document_text)")

        idf = result.inquiry_definition
        has_inquiry = bool(idf and (idf.key_concepts or idf.refined_query))
        if not has_inquiry:
            reasons.append("no inquiry_definition (key_concepts/refined_query)")

        draft = result.draft_reply
        body = (draft.body if draft else "") or ""
        if len(body.strip()) < 200:
            reasons.append("draft body shorter than 200 chars")

        cited = bool(result.external_sources) or bool(result.suggested_kb)
        if not cited:
            reasons.append("no external (or exact-match KB) citation on the result")

        if result.confidence == ConfidenceLevel.LOW:
            reasons.append("confidence is Low")

        if result.intent == IntentLabel.SPAM:
            reasons.append("intent is Spam (do not auto-reply)")
    except Exception as e:
        reasons.append(f"effort evaluator error: {e}")
    return (len(reasons) == 0, reasons)


def _update_ai_fields(case_id: str, result: GlobalSupportCaseResult, request: CaseRequest) -> None:
    try:
        suggested_kb_id = result.suggested_kb[0].knowledgearticleid if result.suggested_kb else None
        suggested_reply = (result.draft_reply.body if result.draft_reply else "") or ""
        dynamics_tools.update_case_ai_fields(
            case_id=case_id,
            intent=result.intent.value,
            confidence_optionvalue=_CONFIDENCE_OPTIONVALUES.get(result.confidence.value),
            recommendation_optionvalue=_RECOMMENDATION_OPTIONVALUES.get(result.recommendation.value),
            suggested_reply=suggested_reply,
            suggested_kb_id=suggested_kb_id,
            origin_email_id=request.email_id,
            operation_id=result.operation_id,
        )
    except Exception as e:
        _logger.warning(f"GSC service - update_case_ai_fields failed for case {case_id}: {e}")


def _update_case_classification_and_status(case_id: str, result: GlobalSupportCaseResult) -> None:
    """Write ts_type/ts_subtype/ts_detail/ts_subtype_3 + ts_casestatus on the incident.

    Only the levels the agent populated are written. ts_casestatus is only written when set
    (which by policy is 104/Closed when recommendation == ResolveAndReply and a reply was sent).
    """
    try:
        fields: dict = {}
        cls = result.classification
        if cls:
            if cls.type and cls.type.code is not None:
                fields["ts_type"] = int(cls.type.code)
            if cls.subtype and cls.subtype.code is not None:
                fields["ts_subtype"] = int(cls.subtype.code)
            if cls.detail and cls.detail.code is not None:
                fields["ts_detail"] = int(cls.detail.code)
            if cls.subtype_3 and cls.subtype_3.code is not None:
                fields["ts_subtype_3"] = int(cls.subtype_3.code)

        if result.case_status_code is not None and result.action_taken == ActionTaken.REPLY_SENT:
            fields["ts_casestatus"] = int(result.case_status_code)

        if not fields:
            return

        from . import dynamics_client
        resp = dynamics_client.run_async(dynamics_client.update(
            entity_set="incidents",
            entity_id=case_id,
            fields=fields,
        )) or {}
        if resp.get("success") is False:
            _logger.warning(
                f"GSC service - classification/status update failed for case {case_id}: {resp}"
            )
    except Exception as e:
        _logger.warning(f"GSC service - _update_case_classification_and_status error for case {case_id}: {e}")


def _write_case_note(case_id: str, result: GlobalSupportCaseResult) -> None:
    try:
        note_subject = f"GSC Agent [{result.action_taken.value}]: {result.intent.value} ({result.confidence.value})"
        dynamics_tools.create_case_note(
            case_id=case_id,
            subject=note_subject[:200],
            notetext=result.case_note or "",
        )
    except Exception as e:
        _logger.warning(f"GSC service - create_case_note failed for case {case_id}: {e}")


def _apply_memory_proposals(case_id: str, result: GlobalSupportCaseResult) -> None:
    try:
        if not result.memory_proposals:
            return
        try:
            import gsc_memory_service as _mem  # type: ignore
        except ImportError:
            import sys
            here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            if here not in sys.path:
                sys.path.insert(0, here)
            import gsc_memory_service as _mem  # type: ignore
        proposals = [p.model_dump(exclude_none=True) for p in result.memory_proposals]
        summary = _mem.apply_proposals(proposals, case_id=case_id)
        _logger.info(f"GSC service - memory apply_proposals for case {case_id}: {summary}")
    except Exception as e:
        _logger.warning(f"GSC service - apply_proposals failed for case {case_id}: {e}")


def _resolve_global_support_queue() -> Optional[dict]:
    try:
        return dynamics_tools.resolve_queue_by_name(_GLOBAL_SUPPORT_QUEUE_NAME)
    except Exception as e:
        _logger.warning(f"GSC service - resolve queue '{_GLOBAL_SUPPORT_QUEUE_NAME}' failed: {e}")
        return None


def _dispatch_actions(case_id: str, result: GlobalSupportCaseResult, request: CaseRequest) -> None:
    """Materialise the agent's verdict into Dynamics actions.

    In SIMULATE mode, NO direct Dynamics writes happen (no case note, no AI
    fields, no email send, no queue route). Only memory_proposals are applied,
    and the simulated payload is archived on `result.simulated_actions`.

    In ACTIVE_AGENT mode: AI fields + case note are written, then either the
    reply is sent OR the case is routed to the Global Support queue based on
    the agent's chosen recommendation AND the objective effort floor.
    """
    simulate = result.agent_mode == AgentMode.SIMULATE

    acceptable, failed_items = _evaluate_effort_floor(result)
    if result.effort is not None:
        result.effort.acceptable = acceptable
        if failed_items:
            result.effort.reasons = list({*result.effort.reasons, *failed_items})

    chose_reply = result.recommendation == Recommendation.RESOLVE_AND_REPLY
    draft = result.draft_reply

    can_reply = (
        chose_reply
        and acceptable
        and draft is not None
        and bool(draft.body)
        and bool(draft.reply_to)
        and bool(draft.from_queue_id)
        and not draft.no_reply_reason
    )

    if can_reply:
        action_payload = {
            "action": "send_email",
            "case_id": case_id,
            "from_queue_id": draft.from_queue_id,
            "from_queue_name": draft.from_queue_name,
            "to_kind": draft.reply_to_kind,
            "to_party_id": draft.reply_to_party_id,
            "to_address_used": draft.reply_to,
            "reply_recipient_source": draft.reply_recipient_source,
            "subject": draft.subject,
            "body": draft.body,
            "in_reply_to_email_id": request.email_id,
        }
        if simulate:
            result.action_taken = ActionTaken.SIMULATED_REPLY
            result.simulated_actions.append(action_payload)
            result.action_detail = {"simulated": True, "kind": "reply"}
        else:
            try:
                send = dynamics_tools.send_case_reply(
                    case_id=case_id,
                    from_queue_id=draft.from_queue_id,
                    to_kind=draft.reply_to_kind or "unresolved_email",
                    to_party_id=draft.reply_to_party_id,
                    to_address_used=draft.reply_to,
                    subject=draft.subject or "",
                    body_html=draft.body,
                    in_reply_to_email_id=request.email_id,
                )
                if send.get("success") and send.get("sent"):
                    result.action_taken = ActionTaken.REPLY_SENT
                    result.action_detail = send
                else:
                    result.action_taken = ActionTaken.FAILED
                    result.action_detail = send
                    result.concerns.append(
                        f"send_case_reply failed: {send.get('error') or send.get('send_response')}"
                    )
            except Exception as e:
                result.action_taken = ActionTaken.FAILED
                result.action_detail = {"error": str(e)}
                result.concerns.append(f"send_case_reply exception: {e}")
    else:
        # Escalate to Global Support queue.
        queue = _resolve_global_support_queue()
        queue_id = (queue or {}).get("queueid")
        queue_name = (queue or {}).get("name") or _GLOBAL_SUPPORT_QUEUE_NAME

        if result.route_decision is None:
            result.route_decision = RouteDecision()
        result.route_decision.queue_name = queue_name
        result.route_decision.queue_id = queue_id
        if not result.route_decision.reason:
            if chose_reply and not acceptable:
                result.route_decision.reason = "Effort floor not met: " + "; ".join(failed_items)
            elif not chose_reply:
                result.route_decision.reason = "Agent chose to escalate to Global Support"
            else:
                result.route_decision.reason = "Reply preconditions not met"

        comment = (result.route_decision.reason or "GSC Agent escalation")[:200]
        action_payload = {
            "action": "add_to_queue",
            "case_id": case_id,
            "queue_id": queue_id,
            "queue_name": queue_name,
            "comment": comment,
            "reason": result.route_decision.reason,
        }
        if simulate:
            result.action_taken = ActionTaken.SIMULATED_ROUTE
            result.simulated_actions.append(action_payload)
            result.action_detail = {"simulated": True, "kind": "route"}
        else:
            if not queue_id:
                result.action_taken = ActionTaken.FAILED
                result.action_detail = {"error": f"queue '{queue_name}' not found"}
                result.concerns.append(f"Global Support queue not found: {queue_name}")
            else:
                try:
                    route = dynamics_tools.route_case_to_queue(case_id, queue_id, comment)
                    if route.get("success"):
                        result.action_taken = ActionTaken.ROUTED_TO_QUEUE
                        result.action_detail = route
                    else:
                        result.action_taken = ActionTaken.FAILED
                        result.action_detail = route
                        result.concerns.append(
                            f"route_case_to_queue failed: {route.get('error') or route.get('response')}"
                        )
                except Exception as e:
                    result.action_taken = ActionTaken.FAILED
                    result.action_detail = {"error": str(e)}
                    result.concerns.append(f"route_case_to_queue exception: {e}")

    # Refresh case_note with the (now final) action_taken + route_decision baked in.
    try:
        result.case_note = result.to_case_note()
    except Exception:
        pass


def _persist_result(case_id: str, result: GlobalSupportCaseResult, request: CaseRequest) -> None:
    """Side-effect entrypoint. NEVER raises into the caller."""
    result.agent_mode = request.agent_mode
    try:
        _dispatch_actions(case_id, result, request)
    except Exception as e:
        _logger.warning(f"GSC service - _dispatch_actions failed for case {case_id}: {e}")

    # Default ts_casestatus = 104 (Closed) when a reply was sent and the agent didn't pick a value.
    if (
        result.action_taken == ActionTaken.REPLY_SENT
        and result.case_status_code is None
        and result.recommendation == Recommendation.RESOLVE_AND_REPLY
    ):
        result.case_status_code = 104

    if request.agent_mode != AgentMode.SIMULATE:
        _update_ai_fields(case_id, result, request)
        _update_case_classification_and_status(case_id, result)
        _write_case_note(case_id, result)
    else:
        try:
            sim_fields: dict = {}
            cls = result.classification
            if cls:
                if cls.type and cls.type.code is not None:
                    sim_fields["ts_type"] = int(cls.type.code)
                if cls.subtype and cls.subtype.code is not None:
                    sim_fields["ts_subtype"] = int(cls.subtype.code)
                if cls.detail and cls.detail.code is not None:
                    sim_fields["ts_detail"] = int(cls.detail.code)
                if cls.subtype_3 and cls.subtype_3.code is not None:
                    sim_fields["ts_subtype_3"] = int(cls.subtype_3.code)
            # In SIMULATE, REPLY_SENT never happens; use SIMULATED_REPLY as the trigger for 104.
            if (
                result.case_status_code is None
                and result.recommendation == Recommendation.RESOLVE_AND_REPLY
                and result.action_taken == ActionTaken.SIMULATED_REPLY
            ):
                result.case_status_code = 104
            if result.case_status_code is not None and result.recommendation == Recommendation.RESOLVE_AND_REPLY:
                sim_fields["ts_casestatus"] = int(result.case_status_code)
            if sim_fields:
                result.simulated_actions.append({
                    "action": "update_classification_and_status",
                    "fields": sim_fields,
                })
        except Exception as e:
            _logger.warning(
                f"GSC service - simulated classification payload build failed for case {case_id}: {e}"
            )

    _apply_memory_proposals(case_id, result)


def handle_case_by_id(
    case_id: str,
    agent: Optional[GlobalSupportCaseAgent] = None,
    max_iterations: Optional[int] = None,
    operation_id: Optional[str] = None,
    request: Optional[CaseRequest] = None,
) -> GlobalSupportCaseResult:
    """Run the Global Support Case Agent over a single case id.

    NEVER raises into the queue worker — returns a fallback result on any failure.
    """
    op_id = operation_id or str(uuid.uuid4())
    try:
        req = request or CaseRequest(case_id=case_id)

        # Idempotency guard: Azure Storage queues deliver at-least-once. If a prior
        # run already stamped THIS operation_id on the case, re-processing would
        # double-send a reply or double-route. Short-circuit on redelivery.
        # (SIMULATE mode never writes ts_aioperationid, so it is never skipped.)
        if req.agent_mode != AgentMode.SIMULATE and operation_id:
            try:
                existing_op = dynamics_tools.get_case_ai_operation_id(case_id)
            except Exception:
                existing_op = None
            if existing_op and existing_op == operation_id:
                _logger.info(
                    "GSC service - skipping case %s: operation_id %s already processed "
                    "(queue redelivery).",
                    case_id, operation_id,
                )
                skipped = GlobalSupportCaseAgent._fallback_result(
                    case_id=case_id,
                    reason=(
                        f"Idempotent skip: operation_id {operation_id} was already "
                        "processed on this case (at-least-once queue redelivery)."
                    ),
                )
                skipped.operation_id = op_id
                skipped.action_taken = ActionTaken.SKIPPED
                skipped.requires_human_review = False
                return skipped

        runner = agent or GlobalSupportCaseAgent(
            max_iterations=max(int(os.getenv("GSC_MAX_ITERATIONS", "40")), 40),
        )
        result = runner.handle_by_case_id(
            case_id=case_id,
            max_iterations=max_iterations,
            agent_mode=req.agent_mode,
        )
        result.case_id = case_id
        result.operation_id = op_id
        _persist_result(case_id=case_id, result=result, request=req)
        return result
    except Exception as e:
        _logger.exception(f"GSC service - handle_case_by_id failed for case {case_id}")
        fallback = GlobalSupportCaseAgent._fallback_result(
            case_id=case_id,
            reason=f"Top-level handler failed: {e}",
        )
        fallback.operation_id = op_id
        try:
            req = request or CaseRequest(case_id=case_id)
            _persist_result(case_id=case_id, result=fallback, request=req)
        except Exception:
            pass
        return fallback


def handle_case(
    request: CaseRequest,
    agent: Optional[GlobalSupportCaseAgent] = None,
    max_iterations: Optional[int] = None,
) -> GlobalSupportCaseResult:
    if not request or not request.case_id:
        return GlobalSupportCaseAgent._fallback_result(
            case_id=None,
            reason="handle_case called without a caseId.",
        )
    return handle_case_by_id(
        case_id=request.case_id,
        agent=agent,
        max_iterations=max_iterations,
        request=request,
    )
