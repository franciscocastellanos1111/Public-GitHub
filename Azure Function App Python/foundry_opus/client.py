from typing import Any, Iterable, Optional, Sequence

import anthropic

from .config import FoundryConfig
from .exceptions import FoundryAPIError, FoundryConfigError


class FoundryClient:
    def __init__(self, config: Optional[FoundryConfig] = None):
        try:
            self.config = config or FoundryConfig.from_env()
            self.config.validate()
            self._client = anthropic.Anthropic(
                api_key=self.config.api_key,
                base_url=self.config.base_url,
                timeout=self.config.timeout,
                max_retries=self.config.max_retries,
            )
        except FoundryConfigError:
            raise
        except Exception as e:
            raise FoundryAPIError(f"Failed to initialize Foundry client: {e}") from e

    @property
    def raw(self) -> anthropic.Anthropic:
        return self._client

    def complete(
        self,
        prompt: str,
        system: Optional[str] = None,
        max_tokens: Optional[int] = None,
        temperature: Optional[float] = None,
        stop_sequences: Optional[Sequence[str]] = None,
    ) -> str:
        try:
            messages = [{"role": "user", "content": prompt}]
            response = self.chat(
                messages=messages,
                system=system,
                max_tokens=max_tokens,
                temperature=temperature,
                stop_sequences=stop_sequences,
            )
            return self._extract_text(response)
        except FoundryAPIError:
            raise
        except Exception as e:
            raise FoundryAPIError(f"complete() failed: {e}") from e

    def chat(
        self,
        messages: list[dict],
        system: Optional[str] = None,
        max_tokens: Optional[int] = None,
        temperature: Optional[float] = None,
        stop_sequences: Optional[Sequence[str]] = None,
        tools: Optional[list[dict]] = None,
        tool_choice: Optional[dict] = None,
        extra: Optional[dict] = None,
    ):
        try:
            kwargs: dict[str, Any] = {
                "model": self.config.deployment,
                "max_tokens": max_tokens or self.config.max_tokens,
                "messages": messages,
            }
            effective_temp = temperature if temperature is not None else self.config.temperature
            if effective_temp is not None:
                kwargs["temperature"] = effective_temp
            if system:
                kwargs["system"] = system
            if stop_sequences:
                kwargs["stop_sequences"] = list(stop_sequences)
            if tools:
                kwargs["tools"] = tools
            if tool_choice:
                kwargs["tool_choice"] = tool_choice
            if extra:
                kwargs.update(extra)

            return self._client.messages.create(**kwargs)
        except anthropic.APIStatusError as e:
            raise FoundryAPIError(str(e), status_code=e.status_code, payload=getattr(e, "body", None)) from e
        except anthropic.APIError as e:
            raise FoundryAPIError(f"Anthropic API error: {e}") from e
        except Exception as e:
            raise FoundryAPIError(f"chat() failed: {e}") from e

    def stream(
        self,
        messages: list[dict],
        system: Optional[str] = None,
        max_tokens: Optional[int] = None,
        temperature: Optional[float] = None,
    ) -> Iterable[str]:
        try:
            stream_kwargs: dict[str, Any] = {
                "model": self.config.deployment,
                "max_tokens": max_tokens or self.config.max_tokens,
                "system": system or anthropic.NOT_GIVEN,
                "messages": messages,
            }
            effective_temp = temperature if temperature is not None else self.config.temperature
            if effective_temp is not None:
                stream_kwargs["temperature"] = effective_temp
            with self._client.messages.stream(**stream_kwargs) as stream:
                for text in stream.text_stream:
                    yield text
        except anthropic.APIError as e:
            raise FoundryAPIError(f"stream() failed: {e}") from e
        except Exception as e:
            raise FoundryAPIError(f"stream() unexpected error: {e}") from e

    @staticmethod
    def _extract_text(response) -> str:
        try:
            parts = []
            for block in getattr(response, "content", []) or []:
                if getattr(block, "type", None) == "text":
                    parts.append(block.text)
            return "\n".join(parts).strip()
        except Exception as e:
            raise FoundryAPIError(f"Failed to extract text: {e}") from e

    def health_check(self) -> bool:
        try:
            self.complete("ping", max_tokens=8)
            return True
        except Exception:
            return False
