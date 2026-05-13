class FoundryError(Exception):
    pass


class FoundryConfigError(FoundryError):
    pass


class FoundryAPIError(FoundryError):
    def __init__(self, message: str, status_code: int | None = None, payload=None):
        super().__init__(message)
        self.status_code = status_code
        self.payload = payload


class ToolExecutionError(FoundryError):
    def __init__(self, tool_name: str, original: Exception):
        super().__init__(f"Tool '{tool_name}' failed: {original}")
        self.tool_name = tool_name
        self.original = original
