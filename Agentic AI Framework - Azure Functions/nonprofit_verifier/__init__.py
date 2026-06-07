from .models import (
    Attachment,
    EmailMessage,
    CaseContext,
    VerificationRequest,
    DocumentFinding,
    VerificationResult,
    VerificationStatus,
    ConfidenceLevel,
    Recommendation,
)
from .agent import NonprofitVerificationAgent
from .service import verify_nonprofit_case, verify_nonprofit_case_by_id

__all__ = [
    "Attachment",
    "EmailMessage",
    "CaseContext",
    "VerificationRequest",
    "DocumentFinding",
    "VerificationResult",
    "VerificationStatus",
    "ConfidenceLevel",
    "Recommendation",
    "NonprofitVerificationAgent",
    "verify_nonprofit_case",
    "verify_nonprofit_case_by_id",
]

__version__ = "0.1.0"
