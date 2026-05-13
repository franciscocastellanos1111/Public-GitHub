from __future__ import annotations

from typing import Optional

from .agent import NonprofitVerificationAgent
from .models import VerificationRequest, VerificationResult


def verify_nonprofit_case(
    request: VerificationRequest,
    agent: Optional[NonprofitVerificationAgent] = None,
) -> VerificationResult:
    try:
        runner = agent or NonprofitVerificationAgent()
        return runner.verify(request)
    except Exception as e:
        raise RuntimeError(f"verify_nonprofit_case failed: {e}") from e


def verify_nonprofit_case_by_id(
    case_id: str,
    agent: Optional[NonprofitVerificationAgent] = None,
    max_iterations: Optional[int] = None,
) -> VerificationResult:
    try:
        runner = agent or NonprofitVerificationAgent()
        return runner.verify_by_case_id(case_id, max_iterations=max_iterations)
    except Exception as e:
        raise RuntimeError(f"verify_nonprofit_case_by_id failed: {e}") from e
