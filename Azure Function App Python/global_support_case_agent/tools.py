"""Tool registry for the Global Support Case Agent."""
from __future__ import annotations

from .dynamics_tools import DYNAMICS_TOOLS
from .knowledge_tools import kb_search, kb_get, web_search, fetch_document_text
from .memory_tools import memory_lookup, resolve_techsoup_site


ALL_TOOLS = [
    web_search,
    fetch_document_text,
    memory_lookup,
    resolve_techsoup_site,
    kb_search,
    kb_get,
    *DYNAMICS_TOOLS,
]

__all__ = ["ALL_TOOLS"]
