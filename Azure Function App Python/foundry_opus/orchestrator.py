from dataclasses import dataclass, field
from typing import Any, Optional

from .agent import Agent
from .client import FoundryClient
from .exceptions import FoundryAPIError


@dataclass
class AgentRole:
    name: str
    agent: Agent
    description: str = ""


class Orchestrator:
    def __init__(
        self,
        coordinator_system_prompt: Optional[str] = None,
        roles: Optional[list[AgentRole]] = None,
        client: Optional[FoundryClient] = None,
        max_rounds: int = 6,
    ):
        try:
            self.client = client or FoundryClient()
            self.roles: dict[str, AgentRole] = {}
            for r in roles or []:
                self.roles[r.name] = r
            self.max_rounds = max_rounds
            self.coordinator_system_prompt = coordinator_system_prompt or self._default_coordinator_prompt()
        except Exception as e:
            raise FoundryAPIError(f"Orchestrator init failed: {e}") from e

    def add_role(self, role: AgentRole) -> None:
        self.roles[role.name] = role

    def _default_coordinator_prompt(self) -> str:
        return (
            "You are a coordinator that delegates tasks among specialist agents. "
            "Available agents will be listed in the user message. For each turn, decide either:\n"
            "  ROUTE: <agent_name> :: <instruction>   -- to delegate work, or\n"
            "  FINAL: <answer>                        -- to deliver the final response.\n"
            "Be terse. Do not include explanations outside the directive."
        )

    def route(self, user_request: str) -> dict[str, Any]:
        try:
            history: list[dict[str, str]] = []
            roster = "\n".join(f"- {r.name}: {r.description or 'specialist agent'}" for r in self.roles.values())
            current = (
                f"User request:\n{user_request}\n\n"
                f"Available agents:\n{roster}\n\n"
                "Issue your next directive (ROUTE or FINAL)."
            )

            transcript: list[dict[str, Any]] = []
            final_answer: Optional[str] = None

            for round_idx in range(1, self.max_rounds + 1):
                directive = self.client.complete(
                    prompt=current,
                    system=self.coordinator_system_prompt,
                    temperature=0.2,
                ).strip()
                history.append({"role": "coordinator", "content": directive})

                if directive.upper().startswith("FINAL:"):
                    final_answer = directive[len("FINAL:"):].strip()
                    transcript.append({"round": round_idx, "type": "final", "content": final_answer})
                    break

                if directive.upper().startswith("ROUTE:"):
                    body = directive[len("ROUTE:"):].strip()
                    if "::" not in body:
                        current = "Invalid directive. Use 'ROUTE: <name> :: <instruction>' or 'FINAL: <answer>'."
                        continue
                    target_name, instruction = [p.strip() for p in body.split("::", 1)]
                    role = self.roles.get(target_name)
                    if not role:
                        current = f"Unknown agent '{target_name}'. Choose from: {', '.join(self.roles)}."
                        continue
                    try:
                        agent_output = role.agent.run(instruction, reset=True).output
                    except Exception as e:
                        agent_output = f"[agent error: {e}]"
                    transcript.append({
                        "round": round_idx,
                        "type": "route",
                        "agent": target_name,
                        "instruction": instruction,
                        "output": agent_output,
                    })
                    current = (
                        f"Agent '{target_name}' returned:\n{agent_output}\n\n"
                        "Issue your next directive (ROUTE or FINAL)."
                    )
                    continue

                current = "Unrecognized directive. Use ROUTE or FINAL."

            return {
                "final": final_answer,
                "transcript": transcript,
                "history": history,
            }
        except FoundryAPIError:
            raise
        except Exception as e:
            raise FoundryAPIError(f"Orchestrator.route failed: {e}") from e

    def broadcast(self, prompt: str) -> dict[str, str]:
        try:
            results: dict[str, str] = {}
            for name, role in self.roles.items():
                try:
                    results[name] = role.agent.run(prompt, reset=True).output
                except Exception as e:
                    results[name] = f"[error: {e}]"
            return results
        except Exception as e:
            raise FoundryAPIError(f"Orchestrator.broadcast failed: {e}") from e
