from __future__ import annotations

from datetime import datetime
from enum import Enum
from typing import Any, List, Optional

from pydantic import BaseModel, Field


class VerificationStatus(str, Enum):
    VERIFIED = "Verified"
    PARTIALLY_VERIFIED = "Partially Verified"
    NOT_VERIFIED = "Not Verified"
    REQUIRES_FURTHER_REVIEW = "Requires Further Review"


class ConfidenceLevel(str, Enum):
    HIGH = "High"
    MEDIUM = "Medium"
    LOW = "Low"


class Recommendation(str, Enum):
    APPROVE = "Approve"
    REQUEST_ADDITIONAL_DOCUMENTATION = "Request Additional Documentation"
    ESCALATE_FOR_MANUAL_REVIEW = "Escalate For Manual Review"


class Attachment(BaseModel):
    filename: str
    content_type: Optional[str] = None
    url: Optional[str] = None
    content_base64: Optional[str] = None
    extracted_text: Optional[str] = None
    size_bytes: Optional[int] = None


class EmailMessage(BaseModel):
    sender_name: Optional[str] = None
    sender_email: str
    sender_title: Optional[str] = None
    subject: Optional[str] = None
    body: str = ""
    received_at: Optional[datetime] = None
    attachments: List[Attachment] = Field(default_factory=list)


class CaseContext(BaseModel):
    case_id: Optional[str] = None
    case_number: Optional[str] = None
    organization_name: Optional[str] = None
    organization_domain: Optional[str] = None
    organization_country: Optional[str] = None
    requested_classification: Optional[str] = None
    notes: Optional[str] = None
    extra: dict = Field(default_factory=dict)


class VerificationRequest(BaseModel):
    case: CaseContext
    email: EmailMessage


class DocumentFinding(BaseModel):
    document_name: str
    document_type: Optional[str] = None
    issuer: Optional[str] = None
    registration_number: Optional[str] = None
    issue_date: Optional[str] = None
    expiration_date: Optional[str] = None
    jurisdiction: Optional[str] = None
    classification: Optional[str] = None
    authentic_signals: List[str] = Field(default_factory=list)
    concerns: List[str] = Field(default_factory=list)


class ExternalRegistryCheck(BaseModel):
    registry_name: str
    jurisdiction: Optional[str] = None
    issuing_authority: Optional[str] = None
    lookup_url: Optional[str] = None
    query_used: Optional[str] = None
    status: str = "NotAttempted"
    access_method: Optional[str] = Field(
        default=None,
        description=(
            "How the registry was actually accessed online. Allowed values: "
            "'web_search', 'fetch_document_text', 'api', 'browser', or null if no "
            "online access was performed. This field MUST describe a real online "
            "interaction. It must NEVER be set to values like 'document review' — "
            "review of customer-supplied documents is NOT registry access."
        ),
    )
    matched_fields: List[str] = Field(default_factory=list)
    mismatched_fields: List[str] = Field(default_factory=list)
    evidence_quotes: List[str] = Field(default_factory=list)
    notes: Optional[str] = None


class RepresentativeAuthority(BaseModel):
    representative_name: Optional[str] = None
    title: Optional[str] = None
    email_domain_matches_org: Optional[bool] = None
    evidence: List[str] = Field(default_factory=list)
    concerns: List[str] = Field(default_factory=list)
    is_authorized: Optional[bool] = None


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
            "For action='record'. One of: Registry, RegistryUrlTemplate, BlockedSource, "
            "DocPattern, IssuingAuthority, QueryPattern, OrgIdentity, Heuristic, KnownScam."
        ),
    )
    scope_key: Optional[str] = Field(
        default=None,
        description=(
            "For action='record'. ISO country code (e.g., 'BR', 'US') or 'global'. "
            "Must match [A-Za-z0-9_-]{1,32}."
        ),
    )
    subject_key: Optional[str] = Field(
        default=None,
        description=(
            "For action='record'. Short stable identifier (lower_snake), e.g. "
            "'cnpj_receitaws_v1'. Same subject_key in same (category, scope) = update."
        ),
    )
    subject: Optional[str] = Field(default=None, description="Human-readable label (<=512 chars).")
    content: Optional[dict] = Field(
        default=None,
        description=(
            "Category-specific JSON. For Registry/RegistryUrlTemplate include "
            "'url_template' (with {placeholders}); for BlockedSource include 'host' "
            "and 'reason'; for DocPattern include 'pattern' and 'meaning'."
        ),
    )
    tags: Optional[str] = None
    ref: Optional[str] = Field(
        default=None,
        description="For action='feedback'. PartitionKey/RowKey of the entry being graded.",
    )
    outcome: Optional[str] = Field(
        default=None,
        description="For action='feedback'. Either 'success' or 'failure'.",
    )
    notes: Optional[str] = None


class TokenUsage(BaseModel):
    input_tokens: int = 0
    output_tokens: int = 0
    cache_read_input_tokens: int = 0
    cache_creation_input_tokens: int = 0
    total_tokens: int = 0
    iterations: int = 0
    per_iteration: List[dict] = Field(default_factory=list)


class VerificationResult(BaseModel):
    case_id: Optional[str] = None
    status: VerificationStatus
    confidence: ConfidenceLevel
    jurisdiction: Optional[str] = None
    classification: Optional[str] = None
    organization_name: Optional[str] = None
    representative: Optional[RepresentativeAuthority] = None
    documents: List[DocumentFinding] = Field(default_factory=list)
    concerns: List[str] = Field(default_factory=list)
    missing_information: List[str] = Field(default_factory=list)
    external_verification_recommended: List[str] = Field(default_factory=list)
    external_registry_checks: List[ExternalRegistryCheck] = Field(default_factory=list)
    document_based_determination: Optional[str] = Field(
        default=None,
        description=(
            "When no online registry confirmation is available (status NotAttempted, "
            "RegistryUnavailable, NotFound, or Inconclusive on every entry of "
            "external_registry_checks), this field MUST contain an explicit, evidence-"
            "cited explanation of why the customer-supplied documents alone are (or are "
            "not) sufficient to determine the organization's nonprofit status. Cite "
            "specific documents, issuing authorities, and verbatim phrases. Leave null "
            "ONLY when at least one external_registry_checks entry has status "
            "'Confirmed' via real online access."
        ),
    )
    recommendation: Recommendation
    next_steps: List[str] = Field(default_factory=list)
    case_note: str = ""
    reasoning: str = ""
    requires_human_review: bool = False
    analyzed_at: datetime = Field(default_factory=datetime.utcnow)
    analyzed_documents: List[str] = Field(default_factory=list)
    token_usage: Optional[TokenUsage] = None
    memory_proposals: List[MemoryProposal] = Field(default_factory=list)

    def to_case_note(self) -> str:
        try:
            lines: List[str] = []
            lines.append(f"NONPROFIT VERIFICATION — {self.status.value} (confidence: {self.confidence.value})")
            lines.append(f"Analyzed at (UTC): {self.analyzed_at.isoformat()}")
            if self.organization_name:
                lines.append(f"Organization: {self.organization_name}")
            if self.jurisdiction:
                lines.append(f"Jurisdiction: {self.jurisdiction}")
            if self.classification:
                lines.append(f"Classification: {self.classification}")
            if self.representative:
                rep = self.representative
                lines.append(
                    f"Representative: {rep.representative_name or 'unknown'} "
                    f"({rep.title or 'no title'}) — authorized: {rep.is_authorized}"
                )
            if self.documents:
                lines.append("Documents reviewed:")
                for d in self.documents:
                    lines.append(f"  - {d.document_name} [{d.document_type or 'unknown type'}]")
            if self.concerns:
                lines.append("Concerns:")
                for c in self.concerns:
                    lines.append(f"  - {c}")
            if self.missing_information:
                lines.append("Missing information:")
                for m in self.missing_information:
                    lines.append(f"  - {m}")
            if self.external_registry_checks:
                lines.append("External registry checks:")
                for chk in self.external_registry_checks:
                    parts = [chk.registry_name]
                    if chk.jurisdiction:
                        parts.append(chk.jurisdiction)
                    if chk.status:
                        parts.append(f"status={chk.status}")
                    lines.append("  - " + " | ".join(parts))
                    if chk.lookup_url:
                        lines.append(f"      url: {chk.lookup_url}")
                    if chk.matched_fields:
                        lines.append(f"      matched: {', '.join(chk.matched_fields)}")
                    if chk.mismatched_fields:
                        lines.append(f"      mismatched: {', '.join(chk.mismatched_fields)}")
                    if chk.notes:
                        lines.append(f"      notes: {chk.notes}")
            if self.document_based_determination:
                lines.append("Document-based determination (no online registry confirmation):")
                lines.append(self.document_based_determination)
            if self.external_verification_recommended:
                lines.append("External verification recommended:")
                for e in self.external_verification_recommended:
                    lines.append(f"  - {e}")
            lines.append(f"Recommendation: {self.recommendation.value}")
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
