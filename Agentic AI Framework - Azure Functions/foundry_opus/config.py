import os
from dataclasses import dataclass, field
from typing import Optional

try:
    from dotenv import load_dotenv
    load_dotenv()
except Exception:
    pass

from .exceptions import FoundryConfigError


@dataclass
class FoundryConfig:
    # No default: the key MUST come from the FOUNDRY_API_KEY env var / App Setting.
    # (A hardcoded key previously lived here — it has been removed and should be rotated.)
    api_key: str = field(default_factory=lambda: os.getenv("FOUNDRY_API_KEY", ""))
    deployment: str = field(default_factory=lambda: os.getenv("FOUNDRY_DEPLOYMENT", "claude-opus-4-8"))
    base_url: str = field(default_factory=lambda: os.getenv(
        "FOUNDRY_BASE_URL",
        "https://techsoupaiservices.services.ai.azure.com/anthropic",
    ))
    max_tokens: int = field(default_factory=lambda: int(os.getenv("FOUNDRY_MAX_TOKENS", "4096")))
    # Extended thinking. budget_tokens > 0 enables it (must be < the request's
    # max_tokens). 0 disables. The client degrades gracefully: if the Foundry relay
    # rejects the `thinking` parameter, it disables thinking and retries the request.
    thinking_budget: int = field(default_factory=lambda: int(os.getenv("FOUNDRY_THINKING_BUDGET", "10000") or "0"))
    # Interleaved thinking (reasoning *between* tool calls) requires a beta header the
    # relay may not support — gated separately and OFF by default.
    interleaved_thinking: bool = field(default_factory=lambda: (
        (os.getenv("FOUNDRY_INTERLEAVED_THINKING") or "").strip().lower() in ("1", "true", "yes")
    ))
    # Some Foundry-hosted Anthropic models (e.g. Opus 4.7) reject the `temperature` parameter.
    # Leave as None to omit it from requests; set FOUNDRY_TEMPERATURE to opt in.
    temperature: Optional[float] = field(default_factory=lambda: (
        float(os.getenv("FOUNDRY_TEMPERATURE")) if os.getenv("FOUNDRY_TEMPERATURE") else None
    ))
    timeout: float = 120.0
    max_retries: int = 3
    api_version: Optional[str] = None

    def validate(self) -> None:
        try:
            if not self.api_key:
                raise FoundryConfigError("FOUNDRY_API_KEY is missing.")
            if not self.deployment:
                raise FoundryConfigError("FOUNDRY_DEPLOYMENT is missing.")
            if not self.base_url:
                raise FoundryConfigError("FOUNDRY_BASE_URL is missing.")
            if not self.base_url.startswith("http"):
                raise FoundryConfigError("FOUNDRY_BASE_URL must be an http(s) URL.")
        except FoundryConfigError:
            raise
        except Exception as e:
            raise FoundryConfigError(f"Configuration error: {e}") from e

    @classmethod
    def from_env(cls) -> "FoundryConfig":
        try:
            cfg = cls()
            cfg.validate()
            return cfg
        except FoundryConfigError:
            raise
        except Exception as e:
            raise FoundryConfigError(f"Failed to load config from env: {e}") from e
