from __future__ import annotations

from datetime import datetime
from enum import Enum
from typing import Any, List, Optional

from pydantic import BaseModel, Field


# Intent labels recognized by the Global Support Case Agent (design §3.2).
class IntentLabel(str, Enum):
    NONPROFIT_ELIGIBILITY = "NonprofitEligibility"
    PRODUCT_QUESTION = "ProductQuestion"
    DONATION_PROGRAM = "DonationProgram"
    DISCOUNT_INQUIRY = "DiscountInquiry"
    ACCOUNT_ACCESS = "AccountAccess"
    ORDER_STATUS = "OrderStatus"
    BILLING = "Billing"
    REFUND = "Refund"
    SHIPPING = "Shipping"
    PARTNERSHIP = "Partnership"
    SPAM = "Spam"
    OTHER = "Other"


class ConfidenceLevel(str, Enum):
    HIGH = "High"
    MEDIUM = "Medium"
    LOW = "Low"


class Recommendation(str, Enum):
    RESOLVE_AND_REPLY = "ResolveAndReply"
    ESCALATE_TO_GLOBAL_SUPPORT = "EscalateToGlobalSupport"
    AUTO_RESOLVE = "AutoResolve"
    DRAFT_REPLY_FOR_REVIEW = "DraftReplyForReview"
    ESCALATE_TO_TIER2 = "EscalateToTier2"
    ESCALATE_TO_BILLING = "EscalateToBilling"
    REQUEST_MORE_INFO = "RequestMoreInfo"
    ROUTE_TO_NONPROFIT_VERIFIER = "RouteToNonprofitVerifier"
    REJECT = "Reject"


class AgentMode(str, Enum):
    ACTIVE_AGENT = "active_agent"
    SIMULATE = "simulate"


class ActionTaken(str, Enum):
    REPLY_SENT = "ReplySent"
    ROUTED_TO_QUEUE = "RoutedToQueue"
    SIMULATED_REPLY = "SimulatedReply"
    SIMULATED_ROUTE = "SimulatedRoute"
    SKIPPED = "Skipped"
    FAILED = "Failed"


class CustomerMatchMethod(str, Enum):
    EXACT_EMAIL = "ExactEmail"
    DOMAIN_MATCH = "DomainMatch"
    NAME_FUZZY = "NameFuzzy"
    NONE = "None"


class CaseRequest(BaseModel):
    case_id: str = Field(alias="caseId")
    email_id: Optional[str] = Field(default=None, alias="emailId")
    trigger: Optional[str] = None
    correlation_id: Optional[str] = Field(default=None, alias="correlationId")
    agent_mode: AgentMode = Field(default=AgentMode.ACTIVE_AGENT, alias="agentMode")

    class Config:
        populate_by_name = True


class EmailMessage(BaseModel):
    activity_id: Optional[str] = None
    direction: Optional[str] = None
    sender_name: Optional[str] = None
    sender_email: Optional[str] = None
    subject: Optional[str] = None
    body: str = ""
    received_at: Optional[datetime] = None


class CaseContext(BaseModel):
    case_id: Optional[str] = None
    case_number: Optional[str] = None
    title: Optional[str] = None
    description: Optional[str] = None
    customer_id: Optional[str] = None
    customer_name: Optional[str] = None
    customer_email: Optional[str] = None
    region: Optional[str] = None
    language: Optional[str] = None
    case_type_code: Optional[int] = None
    extra: dict = Field(default_factory=dict)


class KbCitation(BaseModel):
    knowledgearticleid: str
    title: Optional[str] = None
    article_number: Optional[str] = None
    language: Optional[str] = None
    excerpt: Optional[str] = None
    relevance: Optional[float] = None


class ExternalSourceCitation(BaseModel):
    url: str
    title: Optional[str] = None
    excerpt: Optional[str] = None
    fetched_at: Optional[datetime] = None


class CustomerMatch(BaseModel):
    method: CustomerMatchMethod = CustomerMatchMethod.NONE
    contactid: Optional[str] = None
    accountid: Optional[str] = None
    display_name: Optional[str] = None
    domain: Optional[str] = None
    confidence: Optional[float] = None
    notes: Optional[str] = None


class ResolutionDraft(BaseModel):
    language: Optional[str] = None
    subject: Optional[str] = None
    body: str = ""
    tone: Optional[str] = None
    # Reply routing — agent decides recipient, service materialises send.
    reply_to: Optional[str] = None  # canonical recipient email address
    reply_to_kind: Optional[str] = None  # contact | account | unresolved_email | none
    reply_to_party_id: Optional[str] = None  # contactid or accountid if resolved
    reply_recipient_source: Optional[str] = None  # customerid | ts_emailaddresscustomerprovided | inbound_sender | none
    from_queue_id: Optional[str] = None  # Dynamics queueid the inbound email arrived at
    from_queue_name: Optional[str] = None
    no_reply_reason: Optional[str] = None  # populated if agent intentionally declines to reply


class MemoryProposal(BaseModel):
    action: str = Field(
        description=(
            "Either 'record' (create/update a memory entry) or 'feedback' "
            "(report success/failure for an entry returned by memory_lookup)."
        )
    )
    category: Optional[str] = Field(
        default=None,
        description=(
            "For action='record'. One of: KbHit, AnswerTemplate, WebSource, "
            "IntentSignal, RoutingRule, EscalationRubric, OrgIdentity, "
            "CustomerPreference, BlockedSource, KnownScam, DocPattern, "
            "Heuristic, QueryPattern."
        ),
    )
    scope_key: Optional[str] = Field(
        default=None,
        description=(
            "For action='record'. Examples: ISO country code ('US', 'BR'), "
            "'global', 'customer:<contactid>', 'orgdomain:example.org'. "
            "Must match [A-Za-z0-9_:\\-.]{1,64}."
        ),
    )
    subject_key: Optional[str] = None
    subject: Optional[str] = None
    content: Optional[dict] = None
    tags: Optional[str] = None
    ref: Optional[str] = None
    outcome: Optional[str] = None
    notes: Optional[str] = None


class TokenUsage(BaseModel):
    input_tokens: int = 0
    output_tokens: int = 0
    cache_read_input_tokens: int = 0
    cache_creation_input_tokens: int = 0
    total_tokens: int = 0
    iterations: int = 0
    per_iteration: List[dict] = Field(default_factory=list)


class EffortMetrics(BaseModel):
    iterations: int = 0
    total_tool_calls: int = 0
    by_tool: dict = Field(default_factory=dict)
    errors_by_tool: dict = Field(default_factory=dict)
    acceptable: bool = False
    reasons: List[str] = Field(default_factory=list)


class RouteDecision(BaseModel):
    queue_name: Optional[str] = None
    queue_id: Optional[str] = None
    reason: Optional[str] = None
    notes: Optional[str] = None


class ClassificationLevel(BaseModel):
    code: Optional[int] = None
    label: Optional[str] = None


class CaseClassification(BaseModel):
    """Hierarchical classification picked from ts_fieldhierarchyandmapping."""
    type: Optional[ClassificationLevel] = None
    subtype: Optional[ClassificationLevel] = None
    detail: Optional[ClassificationLevel] = None
    subtype_3: Optional[ClassificationLevel] = None
    notes: Optional[str] = None


class InquiryDefinition(BaseModel):
    """The agent's conceptualization of the customer's request (Phase 1)."""
    raw_request: Optional[str] = None
    key_concepts: List[str] = Field(default_factory=list)
    refined_query: Optional[str] = None
    contextual_elements: List[str] = Field(default_factory=list)


class GlobalSupportCaseResult(BaseModel):
    case_id: Optional[str] = None
    operation_id: Optional[str] = None
    agent_mode: AgentMode = AgentMode.ACTIVE_AGENT
    intent: IntentLabel = IntentLabel.OTHER
    confidence: ConfidenceLevel = ConfidenceLevel.LOW
    recommendation: Recommendation = Recommendation.ESCALATE_TO_GLOBAL_SUPPORT
    customer_match: Optional[CustomerMatch] = None
    inquiry_definition: Optional[InquiryDefinition] = None
    research_process: List[str] = Field(default_factory=list)
    suggested_kb: List[KbCitation] = Field(default_factory=list)
    external_sources: List[ExternalSourceCitation] = Field(default_factory=list)
    draft_reply: Optional[ResolutionDraft] = None
    route_decision: Optional[RouteDecision] = None
    classification: Optional[CaseClassification] = None
    case_status_code: Optional[int] = None  # ts_casestatus picklist value. 104 = Closed (set on ResolveAndReply).
    summary: str = ""
    reasoning: str = ""
    concerns: List[str] = Field(default_factory=list)
    next_steps: List[str] = Field(default_factory=list)
    requires_human_review: bool = True
    case_note: str = ""
    analyzed_at: datetime = Field(default_factory=datetime.utcnow)
    token_usage: Optional[TokenUsage] = None
    memory_proposals: List[MemoryProposal] = Field(default_factory=list)
    effort: Optional[EffortMetrics] = None
    action_taken: ActionTaken = ActionTaken.SKIPPED
    action_detail: Optional[dict] = None
    simulated_actions: List[dict] = Field(default_factory=list)

    def to_case_note(self) -> str:
        try:
            lines: List[str] = []
            lines.append(
                f"GLOBAL SUPPORT CASE AGENT — intent: {self.intent.value} "
                f"(confidence: {self.confidence.value})"
            )
            lines.append(f"Recommendation: {self.recommendation.value}")
            lines.append(f"Agent mode: {self.agent_mode.value}")
            lines.append(f"Action taken: {self.action_taken.value}")
            lines.append(f"Analyzed at (UTC): {self.analyzed_at.isoformat()}")
            if self.effort:
                lines.append(
                    f"Effort: iterations={self.effort.iterations} "
                    f"total_tool_calls={self.effort.total_tool_calls} "
                    f"acceptable={self.effort.acceptable}"
                )
                if self.effort.errors_by_tool:
                    lines.append(
                        "  Tool errors: "
                        + ", ".join(f"{k}={v}" for k, v in self.effort.errors_by_tool.items())
                    )
                if self.effort.reasons:
                    lines.append("  Effort reasons:")
                    for r in self.effort.reasons:
                        lines.append(f"    - {r}")
            if self.route_decision:
                lines.append(
                    f"Route decision: queue={self.route_decision.queue_name or '-'} "
                    f"reason={self.route_decision.reason or '-'}"
                )
            if self.classification:
                c = self.classification
                def _lvl(x):
                    return f"{(x.label if x else None) or '-'} (code={x.code if x else None})"
                lines.append(
                    "Classification: "
                    f"type={_lvl(c.type)} | subtype={_lvl(c.subtype)} | "
                    f"detail={_lvl(c.detail)} | subtype_3={_lvl(c.subtype_3)}"
                )
                if c.notes:
                    lines.append(f"  Classification notes: {c.notes}")
            if self.case_status_code is not None:
                lines.append(f"Case status to set (ts_casestatus): {self.case_status_code}")
            if self.customer_match and self.customer_match.method != CustomerMatchMethod.NONE:
                cm = self.customer_match
                lines.append(
                    f"Customer match: {cm.method.value} — "
                    f"{cm.display_name or '?'} (contactid={cm.contactid or '-'}, accountid={cm.accountid or '-'})"
                )
            if self.inquiry_definition:
                idf = self.inquiry_definition
                if idf.raw_request or idf.key_concepts or idf.refined_query:
                    lines.append("Inquiry definition:")
                    if idf.raw_request:
                        lines.append(f"  Raw request: {idf.raw_request}")
                    if idf.key_concepts:
                        lines.append(f"  Key concepts: {', '.join(idf.key_concepts)}")
                    if idf.refined_query:
                        lines.append(f"  Refined query: {idf.refined_query}")
                    if idf.contextual_elements:
                        lines.append(f"  Contextual elements: {', '.join(idf.contextual_elements)}")
            if self.research_process:
                lines.append("Research process:")
                for i, step in enumerate(self.research_process, 1):
                    lines.append(f"  {i}. {step}")
            if self.summary:
                lines.append("Summary:")
                lines.append(self.summary)
            if self.suggested_kb:
                lines.append("Suggested KB articles:")
                for k in self.suggested_kb:
                    lines.append(f"  - {k.title or k.knowledgearticleid} (id={k.knowledgearticleid})")
            if self.external_sources:
                lines.append("External sources cited:")
                for s in self.external_sources:
                    lines.append(f"  - {s.title or s.url}: {s.url}")
            if self.draft_reply and (self.draft_reply.body or self.draft_reply.no_reply_reason):
                lines.append("Draft reply:")
                if self.draft_reply.subject:
                    lines.append(f"  Subject: {self.draft_reply.subject}")
                if self.draft_reply.language:
                    lines.append(f"  Language: {self.draft_reply.language}")
                if self.draft_reply.reply_to:
                    lines.append(
                        f"  To: {self.draft_reply.reply_to} "
                        f"(kind={self.draft_reply.reply_to_kind or '-'}, "
                        f"source={self.draft_reply.reply_recipient_source or '-'})"
                    )
                if self.draft_reply.from_queue_name or self.draft_reply.from_queue_id:
                    lines.append(
                        f"  From queue: {self.draft_reply.from_queue_name or '-'} "
                        f"({self.draft_reply.from_queue_id or '-'})"
                    )
                if self.draft_reply.no_reply_reason:
                    lines.append(f"  No-reply reason: {self.draft_reply.no_reply_reason}")
                if self.draft_reply.body:
                    lines.append(self.draft_reply.body)
            if self.concerns:
                lines.append("Concerns:")
                for c in self.concerns:
                    lines.append(f"  - {c}")
            if self.next_steps:
                lines.append("Next steps:")
                for s in self.next_steps:
                    lines.append(f"  - {s}")
            if self.reasoning:
                lines.append("Reasoning:")
                lines.append(self.reasoning)
            return "\n".join(lines)
        except Exception as e:
            return f"[Error formatting case note: {e}]"
