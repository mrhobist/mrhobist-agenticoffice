"""Testlerin baglanti denemesi icin en kucuk stdio MCP sunucusu: tek arac (echo)."""

from mcp.server.mcpserver import MCPServer

server = MCPServer("echo-test")


@server.tool()
def echo(text: str) -> str:
    """Verilen metni aynen dondurur."""
    return text


if __name__ == "__main__":
    server.run()
