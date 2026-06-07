import inspect
from dataclasses import dataclass, field
from typing import Any, Callable, Optional

from .exceptions import ToolExecutionError


@dataclass
class Tool:
    name: str
    description: str
    input_schema: dict
    handler: Callable[..., Any]

    def to_anthropic(self) -> dict:
        return {
            "name": self.name,
            "description": self.description,
            "input_schema": self.input_schema,
        }

    def invoke(self, arguments: dict) -> Any:
        try:
            return self.handler(**(arguments or {}))
        except Exception as e:
            raise ToolExecutionError(self.name, e) from e


@dataclass
class ToolRegistry:
    _tools: dict[str, Tool] = field(default_factory=dict)

    def register(self, tool_obj: Tool) -> None:
        try:
            self._tools[tool_obj.name] = tool_obj
        except Exception as e:
            raise ToolExecutionError(tool_obj.name, e) from e

    def get(self, name: str) -> Optional[Tool]:
        return self._tools.get(name)

    def all(self) -> list[Tool]:
        return list(self._tools.values())

    def to_anthropic(self) -> list[dict]:
        return [t.to_anthropic() for t in self._tools.values()]

    def __len__(self) -> int:
        return len(self._tools)

    def __contains__(self, name: str) -> bool:
        return name in self._tools


def tool(
    name: Optional[str] = None,
    description: Optional[str] = None,
    input_schema: Optional[dict] = None,
):
    def decorator(fn: Callable[..., Any]) -> Tool:
        try:
            tool_name = name or fn.__name__
            tool_desc = description or (fn.__doc__ or "").strip() or tool_name
            schema = input_schema or _infer_schema(fn)
            return Tool(name=tool_name, description=tool_desc, input_schema=schema, handler=fn)
        except Exception as e:
            raise ToolExecutionError(name or fn.__name__, e) from e

    return decorator


def _infer_schema(fn: Callable[..., Any]) -> dict:
    try:
        sig = inspect.signature(fn)
        properties: dict[str, dict] = {}
        required: list[str] = []
        type_map = {
            str: "string",
            int: "integer",
            float: "number",
            bool: "boolean",
            list: "array",
            dict: "object",
        }
        for pname, param in sig.parameters.items():
            ptype = type_map.get(param.annotation, "string")
            properties[pname] = {"type": ptype}
            if param.default is inspect.Parameter.empty:
                required.append(pname)
        schema: dict = {"type": "object", "properties": properties}
        if required:
            schema["required"] = required
        return schema
    except Exception:
        return {"type": "object", "properties": {}}
