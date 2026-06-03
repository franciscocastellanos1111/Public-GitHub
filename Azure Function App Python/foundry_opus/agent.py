from dataclasses import dataclass, field
from typing import Any, Callable, Optional

from .client import FoundryClient
from .exceptions import FoundryAPIError, ToolExecutionError
from .tools import Tool, ToolRegistry


@dataclass
class AgentResult:
    output: str
    messages: list[dict]
    tool_calls: list[dict] = field(default_factory=list)
    stop_reason: Optional[str] = None
    iterations: int = 0
    raw_responses: list[Any] = field(default_factory=list)


class Agent:
    def __init__(
        self,
        name: str,
        system_prompt: str,
        client: Optional[FoundryClient] = None,
        tools: Optional[list[Tool]] = None,
        max_iterations: int = 8,
        temperature: Optional[float] = None,
        max_tokens: Optional[int] = None,
        on_tool_call: Optional[Callable[[str, dict], None]] = None,
        enable_prompt_caching: bool = True,
        enable_thinking: bool = False,
    ):
        try:
            self.name = name
            self.enable_thinking = enable_thinking
            self.system_prompt = system_prompt
            self.client = client or FoundryClient()
            self.registry = ToolRegistry()
            for t in tools or []:
                self.registry.register(t)
            self.max_iterations = max_iterations
            self.temperature = temperature
            self.max_tokens = max_tokens
            self.on_tool_call = on_tool_call
            self.enable_prompt_caching = enable_prompt_caching
            self._history: list[dict] = []
        except Exception as e:
            raise FoundryAPIError(f"Agent init failed: {e}") from e

    def add_tool(self, t: Tool) -> None:
        self.registry.register(t)

    def reset(self) -> None:
        self._history = []

    @property
    def history(self) -> list[dict]:
        return list(self._history)

    def run(self, user_input: str, reset: bool = False) -> AgentResult:
        try:
            if reset:
                self.reset()

            self._history.append({"role": "user", "content": user_input})

            tool_calls_log: list[dict] = []
            raw_responses: list[Any] = []
            tools_payload = self.registry.to_anthropic() if len(self.registry) else None

            iteration = 0
            stop_reason: Optional[str] = None

            while iteration < self.max_iterations:
                iteration += 1
                cached_system, cached_tools, cached_messages = self._apply_cache_markers(
                    self.system_prompt, tools_payload, self._history
                )
                response = self.client.chat(
                    messages=cached_messages,
                    system=cached_system,
                    max_tokens=self.max_tokens,
                    temperature=self.temperature,
                    tools=cached_tools,
                    thinking=True if self.enable_thinking else None,
                )
                raw_responses.append(response)
                stop_reason = getattr(response, "stop_reason", None)

                assistant_blocks = []
                tool_uses = []
                for block in getattr(response, "content", []) or []:
                    btype = getattr(block, "type", None)
                    if btype == "text":
                        assistant_blocks.append({"type": "text", "text": block.text})
                    elif btype == "thinking":
                        # Must be preserved (with signature) in history so tool-use
                        # turns remain valid when extended thinking is enabled.
                        assistant_blocks.append({
                            "type": "thinking",
                            "thinking": getattr(block, "thinking", "") or "",
                            "signature": getattr(block, "signature", "") or "",
                        })
                    elif btype == "redacted_thinking":
                        assistant_blocks.append({
                            "type": "redacted_thinking",
                            "data": getattr(block, "data", "") or "",
                        })
                    elif btype == "tool_use":
                        assistant_blocks.append({
                            "type": "tool_use",
                            "id": block.id,
                            "name": block.name,
                            "input": block.input,
                        })
                        tool_uses.append(block)

                self._history.append({"role": "assistant", "content": assistant_blocks})

                if stop_reason != "tool_use" or not tool_uses:
                    break

                tool_results = []
                for tu in tool_uses:
                    if self.on_tool_call:
                        try:
                            self.on_tool_call(tu.name, tu.input)
                        except Exception:
                            pass
                    result_text, is_error = self._execute_tool(tu.name, tu.input)
                    tool_calls_log.append({
                        "name": tu.name,
                        "input": tu.input,
                        "output": result_text,
                        "is_error": is_error,
                    })
                    tool_results.append({
                        "type": "tool_result",
                        "tool_use_id": tu.id,
                        "content": result_text,
                        "is_error": is_error,
                    })

                self._history.append({"role": "user", "content": tool_results})

            output_text = self._collect_text(raw_responses[-1]) if raw_responses else ""
            return AgentResult(
                output=output_text,
                messages=list(self._history),
                tool_calls=tool_calls_log,
                stop_reason=stop_reason,
                iterations=iteration,
                raw_responses=raw_responses,
            )
        except FoundryAPIError:
            raise
        except Exception as e:
            raise FoundryAPIError(f"Agent.run failed: {e}") from e

    def _execute_tool(self, name: str, arguments: dict) -> tuple[str, bool]:
        try:
            t = self.registry.get(name)
            if not t:
                return (f"Tool '{name}' is not registered.", True)
            result = t.invoke(arguments or {})
            return (str(result), False)
        except ToolExecutionError as e:
            return (f"Error: {e}", True)
        except Exception as e:
            return (f"Unexpected tool error: {e}", True)

    def _apply_cache_markers(
        self,
        system_prompt: str,
        tools_payload: Optional[list[dict]],
        history: list[dict],
    ) -> tuple[Any, Optional[list[dict]], list[dict]]:
        try:
            if not self.enable_prompt_caching:
                return system_prompt, tools_payload, history

            cache_marker = {"type": "ephemeral"}

            cached_system = [{
                "type": "text",
                "text": system_prompt,
                "cache_control": cache_marker,
            }] if system_prompt else system_prompt

            cached_tools: Optional[list[dict]] = None
            if tools_payload:
                cached_tools = [dict(t) for t in tools_payload]
                cached_tools[-1] = {**cached_tools[-1], "cache_control": cache_marker}

            cached_messages: list[dict] = [dict(m) for m in history]
            for idx in range(len(cached_messages) - 1, -1, -1):
                msg = cached_messages[idx]
                if msg.get("role") != "user":
                    continue
                content = msg.get("content")
                if isinstance(content, str):
                    msg["content"] = [{
                        "type": "text",
                        "text": content,
                        "cache_control": cache_marker,
                    }]
                elif isinstance(content, list) and content:
                    new_content = [dict(b) if isinstance(b, dict) else b for b in content]
                    last = new_content[-1]
                    if isinstance(last, dict):
                        last["cache_control"] = cache_marker
                        new_content[-1] = last
                    msg["content"] = new_content
                break

            return cached_system, cached_tools, cached_messages
        except Exception:
            return system_prompt, tools_payload, history

    @staticmethod
    def _collect_text(response) -> str:
        try:
            parts = []
            for block in getattr(response, "content", []) or []:
                if getattr(block, "type", None) == "text":
                    parts.append(block.text)
            return "\n".join(parts).strip()
        except Exception:
            return ""
