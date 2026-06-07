from dataclasses import dataclass, field
from typing import Any, Callable, Optional, Union

from .agent import Agent, AgentResult
from .client import FoundryClient
from .exceptions import FoundryAPIError


StepInputBuilder = Callable[[dict[str, Any]], str]


@dataclass
class WorkflowStep:
    name: str
    agent: Optional[Agent] = None
    prompt: Optional[Union[str, StepInputBuilder]] = None
    system: Optional[str] = None
    transform: Optional[Callable[[str, dict[str, Any]], Any]] = None
    condition: Optional[Callable[[dict[str, Any]], bool]] = None


@dataclass
class WorkflowResult:
    outputs: dict[str, Any] = field(default_factory=dict)
    final: Any = None
    skipped: list[str] = field(default_factory=list)


class Workflow:
    def __init__(
        self,
        name: str,
        steps: list[WorkflowStep],
        client: Optional[FoundryClient] = None,
    ):
        try:
            self.name = name
            self.steps = steps
            self.client = client or FoundryClient()
        except Exception as e:
            raise FoundryAPIError(f"Workflow init failed: {e}") from e

    def run(self, initial_input: Optional[dict[str, Any]] = None) -> WorkflowResult:
        try:
            context: dict[str, Any] = dict(initial_input or {})
            result = WorkflowResult()

            for step in self.steps:
                if step.condition:
                    try:
                        if not step.condition(context):
                            result.skipped.append(step.name)
                            continue
                    except Exception:
                        result.skipped.append(step.name)
                        continue

                prompt_text = self._resolve_prompt(step, context)
                output_text = self._execute_step(step, prompt_text)

                value: Any = output_text
                if step.transform:
                    try:
                        value = step.transform(output_text, context)
                    except Exception as e:
                        value = {"error": str(e), "raw": output_text}

                context[step.name] = value
                result.outputs[step.name] = value
                result.final = value

            return result
        except FoundryAPIError:
            raise
        except Exception as e:
            raise FoundryAPIError(f"Workflow.run failed: {e}") from e

    def _resolve_prompt(self, step: WorkflowStep, context: dict[str, Any]) -> str:
        try:
            if callable(step.prompt):
                return step.prompt(context)
            if isinstance(step.prompt, str):
                return step.prompt.format(**{k: v for k, v in context.items() if isinstance(v, (str, int, float))})
            return ""
        except Exception:
            return step.prompt if isinstance(step.prompt, str) else ""

    def _execute_step(self, step: WorkflowStep, prompt_text: str) -> str:
        try:
            if step.agent:
                agent_result: AgentResult = step.agent.run(prompt_text, reset=True)
                return agent_result.output
            return self.client.complete(prompt=prompt_text, system=step.system)
        except FoundryAPIError:
            raise
        except Exception as e:
            raise FoundryAPIError(f"Step '{step.name}' failed: {e}") from e
