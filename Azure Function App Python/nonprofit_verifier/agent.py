from __future__ import annotations

import json
import logging
from typing import Any, Optional

from foundry_opus import Agent, AgentResult, FoundryClient, Tool

from .models import (
    CaseContext,
    ConfidenceLevel,
    DocumentFinding,
    EmailMessage,
    ExternalRegistryCheck,
    MemoryProposal,
    Recommendation,
    RepresentativeAuthority,
    TokenUsage,
    VerificationRequest,
    VerificationResult,
    VerificationStatus,
)
from .prompts import SYSTEM_PROMPT, CASE_DISCOVERY_INSTRUCTIONS
from .tools import ALL_TOOLS

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


def _log_usage(operation: str, case_id: Optional[str], usage: TokenUsage) -> None:
    try:
        _logger.info(
            "NonprofitVerificationAgent.%s case_id=%s iterations=%d "
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
        "status": {"type": "string", "enum": [s.value for s in VerificationStatus]},
        "confidence": {"type": "string", "enum": [c.value for c in ConfidenceLevel]},
        "recommendation": {"type": "string", "enum": [r.value for r in Recommendation]},
        "organization_name": {"type": "string"},
        "jurisdiction": {"type": "string"},
        "classification": {"type": "string"},
        "representative": {
            "type": "object",
            "properties": {
                "representative_name": {"type": "string"},
                "title": {"type": "string"},
                "email_domain_matches_org": {"type": "boolean"},
                "evidence": {"type": "array", "items": {"type": "string"}},
                "concerns": {"type": "array", "items": {"type": "string"}},
                "is_authorized": {"type": "boolean"},
            },
        },
        "documents": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "document_name": {"type": "string"},
                    "document_type": {"type": "string"},
                    "issuer": {"type": "string"},
                    "registration_number": {"type": "string"},
                    "issue_date": {"type": "string"},
                    "expiration_date": {"type": "string"},
                    "jurisdiction": {"type": "string"},
                    "classification": {"type": "string"},
                    "authentic_signals": {"type": "array", "items": {"type": "string"}},
                    "concerns": {"type": "array", "items": {"type": "string"}},
                },
                "required": ["document_name"],
            },
        },
        "concerns": {"type": "array", "items": {"type": "string"}},
        "missing_information": {"type": "array", "items": {"type": "string"}},
        "external_verification_recommended": {"type": "array", "items": {"type": "string"}},
        "external_registry_checks": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "registry_name": {"type": "string"},
                    "jurisdiction": {"type": "string"},
                    "issuing_authority": {"type": "string"},
                    "lookup_url": {"type": "string"},
                    "query_used": {"type": "string"},
                    "access_method": {
                        "type": "string",
                        "description": (
                            "REQUIRED for any honest registry check. The actual online tool you used to access the registry: "
                            "'web_search' | 'fetch_document_text' | 'api' | 'browser'. "
                            "NEVER use values like 'document review' or 'analysis of attached document' — entries describing "
                            "customer-supplied document analysis are FORBIDDEN here; put those in the top-level "
                            "'document_based_determination' field and in 'documents[]' instead."
                        ),
                    },
                    "status": {"type": "string", "description": "Confirmed | NotFound | Inconclusive | RegistryUnavailable | Mismatch | NotAttempted"},
                    "matched_fields": {"type": "array", "items": {"type": "string"}},
                    "mismatched_fields": {"type": "array", "items": {"type": "string"}},
                    "evidence_quotes": {"type": "array", "items": {"type": "string"}},
                    "notes": {"type": "string"},
                },
                "required": ["registry_name", "status"],
            },
        },
        "next_steps": {"type": "array", "items": {"type": "string"}},
        "reasoning": {"type": "string"},
        "requires_human_review": {"type": "boolean"},
        "document_based_determination": {
            "type": "string",
            "description": (
                "REQUIRED whenever no entry in 'external_registry_checks[]' has status='Confirmed' via real online "
                "access. An evidence-cited narrative explaining whether the customer-supplied documents are sufficient "
                "to determine the organization's nonprofit status, and why. Cite specific documents by name, their "
                "issuing authorities, and short verbatim phrases. State plainly whether the documents alone justify "
                "the chosen recommendation and what residual risk remains because no live registry confirmation was "
                "obtained. Leave null/omit only when at least one registry check is genuinely 'Confirmed' online."
            ),
        },
        "memory_proposals": {
            "type": "array",
            "description": (
                "OPTIONAL. Long-term memory updates the orchestrator should persist after this case completes. "
                "Each item is either action='record' (teach a new fact: working registry URL, blocked source, doc "
                "pattern, etc.) or action='feedback' (grade a memory entry returned earlier by `memory_lookup` "
                "as success/failure). Do NOT propose to write personal data, customer-specific identifiers, or "
                "unverified claims. Only propose facts you actually validated this run. Categories allowed: "
                "Registry, RegistryUrlTemplate, BlockedSource, DocPattern, IssuingAuthority, QueryPattern, "
                "OrgIdentity, Heuristic, KnownScam."
            ),
            "items": {
                "type": "object",
                "properties": {
                    "action": {"type": "string", "enum": ["record", "feedback"]},
                    "category": {"type": "string"},
                    "scope_key": {"type": "string", "description": "ISO country code (e.g. 'BR') or 'global'"},
                    "subject_key": {"type": "string", "description": "stable lower_snake identifier, e.g. 'cnpj_receitaws_v1'"},
                    "subject": {"type": "string"},
                    "content": {"type": "object"},
                    "tags": {"type": "string"},
                    "ref": {"type": "string", "description": "PartitionKey/RowKey returned by memory_lookup; required for action='feedback'"},
                    "outcome": {"type": "string", "enum": ["success", "failure"]},
                    "notes": {"type": "string"},
                },
                "required": ["action"],
            },
        },
    },
    "required": ["status", "confidence", "recommendation", "reasoning", "external_registry_checks"],
}


class NonprofitVerificationAgent:
    def __init__(
        self,
        client: Optional[FoundryClient] = None,
        max_iterations: int = 10,
        on_tool_call=None,
    ):
        try:
            self.client = client or FoundryClient()
            self.max_iterations = max_iterations
            self.on_tool_call = on_tool_call
            self._captured: Optional[dict] = None
        except Exception as e:
            raise RuntimeError(f"Failed to initialize NonprofitVerificationAgent: {e}") from e

    def verify(self, request: VerificationRequest) -> VerificationResult:
        try:
            self._captured = None

            submit_tool = Tool(
                name="submit_verification_result",
                description=(
                    "Submit the FINAL structured verification result. Call this exactly once "
                    "at the end of your analysis. After calling this, do not produce any further text."
                ),
                input_schema=_SUBMIT_SCHEMA,
                handler=self._capture_result,
            )

            agent = Agent(
                name="NonprofitVerificationAgent",
                system_prompt=SYSTEM_PROMPT,
                client=self.client,
                tools=[*ALL_TOOLS, submit_tool],
                max_iterations=self.max_iterations,
                max_tokens=32000,
                on_tool_call=self.on_tool_call,
            )

            user_payload = self._build_user_payload(request)
            agent_result = agent.run(user_payload, reset=True)
            usage = _summarize_token_usage(agent_result)
            _log_usage("verify", request.case.case_id, usage)

            if not self._captured:
                return self._fallback_result(
                    case_id=request.case.case_id,
                    reason=(
                        "Model did not call submit_verification_result. "
                        f"Last output:\n{agent_result.output[:1000]}"
                    ),
                    token_usage=usage,
                )

            return self._build_result(request, self._captured, token_usage=usage)
        except Exception as e:
            return self._fallback_result(
                case_id=request.case.case_id,
                reason=f"Verification failed with error: {e}",
            )

    def verify_by_case_id(
        self,
        case_id: str,
        max_iterations: Optional[int] = None,
        memory_hints: Optional[list[dict]] = None,
    ) -> VerificationResult:
        try:
            from .dynamics_tools import DYNAMICS_TOOLS

            self._captured = None

            submit_tool = Tool(
                name="submit_verification_result",
                description=(
                    "Submit the FINAL structured verification result. Call this exactly once "
                    "at the end of your analysis. After calling this, do not produce any further text."
                ),
                input_schema=_SUBMIT_SCHEMA,
                handler=self._capture_result,
            )

            full_prompt = SYSTEM_PROMPT + "\n\n" + CASE_DISCOVERY_INSTRUCTIONS

            iters = max_iterations if max_iterations is not None else max(self.max_iterations, 30)

            agent = Agent(
                name="NonprofitVerificationAgent.CaseDiscovery",
                system_prompt=full_prompt,
                client=self.client,
                tools=[*ALL_TOOLS, *DYNAMICS_TOOLS, submit_tool],
                max_iterations=iters,
                max_tokens=32000,
                on_tool_call=self.on_tool_call,
            )

            hints_block = ""
            if memory_hints:
                try:
                    hints_block = (
                        "\n\nMEMORY HINTS (advisory only — NOT confirmation, MUST be re-verified live this run):\n"
                        + json.dumps(memory_hints, indent=2, default=str)[:8000]
                        + "\n"
                    )
                except Exception:
                    hints_block = ""

            user_payload = (
                "You are given ONLY a Dynamics 365 case id. Autonomously gather all relevant "
                "evidence from the case (overview, customer reply email, notes, attachments) "
                "and produce a verification verdict by calling `submit_verification_result` "
                "exactly once at the end.\n\n"
                f"case_id: {case_id}\n\n"
                "Begin by calling `dynamics_get_case_overview` with this case_id."
                + hints_block
            )
            agent_result = agent.run(user_payload, reset=True)
            usage = _summarize_token_usage(agent_result)
            _log_usage("verify_by_case_id", case_id, usage)

            if not self._captured:
                return self._fallback_result(
                    case_id=case_id,
                    reason=(
                        "Model did not call submit_verification_result. "
                        f"Last output:\n{agent_result.output[:1000]}"
                    ),
                    token_usage=usage,
                )

            request = VerificationRequest(
                case=CaseContext(case_id=case_id),
                email=EmailMessage(sender_email="unknown@unknown.invalid"),
            )
            return self._build_result(request, self._captured, token_usage=usage)
        except Exception as e:
            return self._fallback_result(
                case_id=case_id,
                reason=f"Case-by-id verification failed with error: {e}",
            )

    def _capture_result(self, **kwargs) -> str:
        try:
            self._captured = kwargs
            return "Verification result captured."
        except Exception as e:
            return f"Error capturing result: {e}"

    def _build_user_payload(self, request: VerificationRequest) -> str:
        try:
            attachment_summaries = []
            for a in request.email.attachments:
                attachment_summaries.append({
                    "filename": a.filename,
                    "content_type": a.content_type,
                    "url": a.url,
                    "has_inline_base64": bool(a.content_base64),
                    "extracted_text_preview": (a.extracted_text or "")[:1500] if a.extracted_text else None,
                    "size_bytes": a.size_bytes,
                })

            payload = {
                "case": request.case.model_dump(exclude_none=True),
                "email": {
                    "sender_name": request.email.sender_name,
                    "sender_email": request.email.sender_email,
                    "sender_title": request.email.sender_title,
                    "subject": request.email.subject,
                    "body": request.email.body,
                    "received_at": request.email.received_at.isoformat() if request.email.received_at else None,
                    "attachments": attachment_summaries,
                },
            }

            return (
                "Analyze the following case and produce a verification verdict by calling "
                "`submit_verification_result` exactly once at the end.\n\n"
                "CASE PAYLOAD (JSON):\n"
                f"{json.dumps(payload, indent=2, default=str)}\n\n"
                "Use the available tools to fetch URL contents, decode base64 attachments, "
                "check domain match, scan for authenticity indicators, and validate registration numbers. "
                "Cross-reference all evidence. When done, submit the structured verdict."
            )
        except Exception as e:
            return f"[Failed to build payload: {e}]"

    def _build_result(
        self,
        request: VerificationRequest,
        data: dict,
        token_usage: Optional[TokenUsage] = None,
    ) -> VerificationResult:
        try:
            extra_concerns: list[str] = []

            rep_data = data.get("representative") or None
            rep: Optional[RepresentativeAuthority] = None
            if isinstance(rep_data, dict):
                try:
                    rep = RepresentativeAuthority(**rep_data)
                except Exception as rep_e:
                    extra_concerns.append(f"Representative authority parse failed: {rep_e}")
            elif rep_data is not None:
                extra_concerns.append(
                    "Model returned 'representative' as a non-object value; field discarded."
                )

            documents: list[DocumentFinding] = []
            for d in (data.get("documents") or []):
                if isinstance(d, dict):
                    try:
                        documents.append(DocumentFinding(**d))
                    except Exception as doc_e:
                        extra_concerns.append(f"Document finding parse failed: {doc_e}")

            registry_checks: list[ExternalRegistryCheck] = []
            for r in (data.get("external_registry_checks") or []):
                if isinstance(r, dict):
                    try:
                        registry_checks.append(ExternalRegistryCheck(**r))
                    except Exception as reg_e:
                        extra_concerns.append(f"External registry check parse failed: {reg_e}")

            memory_proposals: list[MemoryProposal] = []
            for mp in (data.get("memory_proposals") or []):
                if isinstance(mp, dict):
                    try:
                        memory_proposals.append(MemoryProposal(**mp))
                    except Exception as mp_e:
                        extra_concerns.append(f"Memory proposal parse failed: {mp_e}")

            confidence_value = ConfidenceLevel(data["confidence"])
            requires_human = bool(data.get("requires_human_review", True))
            doc_determination = data.get("document_based_determination") or None

            def _is_real_online_attempt(rc: ExternalRegistryCheck) -> bool:
                am = (rc.access_method or "").strip().lower()
                if am not in {"web_search", "fetch_document_text", "api", "browser"}:
                    return False
                status_l = (rc.status or "").lower()
                # NotAttempted means no live call was made; it does not count as an attempt.
                return status_l != "notattempted"

            has_confirmed_online = any(
                (rc.status or "").lower() == "confirmed" and _is_real_online_attempt(rc)
                for rc in registry_checks
            )
            has_any_real_attempt = any(_is_real_online_attempt(rc) for rc in registry_checks)

            if not registry_checks:
                extra_concerns.append(
                    "No external registry verification was recorded \u2014 mandatory online lookup step "
                    "was skipped. Verdict downgraded."
                )
                if confidence_value == ConfidenceLevel.HIGH:
                    confidence_value = ConfidenceLevel.MEDIUM
                requires_human = True
            elif not has_any_real_attempt:
                extra_concerns.append(
                    "No genuine online registry attempt was made (all entries are 'NotAttempted'). "
                    "Per policy, an online lookup MUST be attempted whenever an official registry exists "
                    "for the jurisdiction. Verdict downgraded and flagged for human review."
                )
                if confidence_value == ConfidenceLevel.HIGH:
                    confidence_value = ConfidenceLevel.MEDIUM
                requires_human = True
            elif not has_confirmed_online and not doc_determination:
                extra_concerns.append(
                    "No 'Confirmed' online registry check AND no 'document_based_determination' was provided \u2014 "
                    "the basis for the verdict is unclear."
                )
                if confidence_value == ConfidenceLevel.HIGH:
                    confidence_value = ConfidenceLevel.MEDIUM
                requires_human = True

            result = VerificationResult(
                case_id=request.case.case_id,
                status=VerificationStatus(data["status"]),
                confidence=confidence_value,
                jurisdiction=data.get("jurisdiction"),
                classification=data.get("classification"),
                organization_name=data.get("organization_name") or request.case.organization_name,
                representative=rep,
                documents=documents,
                concerns=(data.get("concerns") or []) + extra_concerns,
                missing_information=data.get("missing_information") or [],
                external_verification_recommended=data.get("external_verification_recommended") or [],
                external_registry_checks=registry_checks,
                document_based_determination=doc_determination,
                recommendation=Recommendation(data["recommendation"]),
                next_steps=data.get("next_steps") or [],
                reasoning=data.get("reasoning") or "",
                requires_human_review=requires_human,
                analyzed_documents=[a.filename for a in request.email.attachments],
                token_usage=token_usage,
                memory_proposals=memory_proposals,
            )
            result.case_note = result.to_case_note()
            return result
        except Exception as e:
            return self._fallback_result(
                case_id=request.case.case_id,
                reason=f"Failed to parse model output: {e}. Raw: {data}",
                token_usage=token_usage,
            )

    @staticmethod
    def _fallback_result(
        case_id: Optional[str],
        reason: str,
        token_usage: Optional[TokenUsage] = None,
    ) -> VerificationResult:
        try:
            res = VerificationResult(
                case_id=case_id,
                status=VerificationStatus.REQUIRES_FURTHER_REVIEW,
                confidence=ConfidenceLevel.LOW,
                recommendation=Recommendation.ESCALATE_FOR_MANUAL_REVIEW,
                reasoning=reason,
                requires_human_review=True,
                concerns=["Automated verification did not complete cleanly."],
                token_usage=token_usage,
            )
            res.case_note = res.to_case_note()
            return res
        except Exception as e:
            raise RuntimeError(f"Failed to build fallback result: {e}") from e
