"""MCP baglanti denemesi (yonetim ekrani "Baglantiyi dene").

Durumsuz: sunucu acilir, araclari listelenir, kapatilir. Is kurali yoktur -- hangi sunucunun kime verildigini
.NET bilir; burasi yalniz "bu baglanti calisiyor mu, ne sunuyor" sorusunu cevaplar. Hata sonuctur (ok=false + neden),
istisna olarak disari tasinmaz: yanlis komut ya da kapali adres kullanicinin duzeltecegi bir veridir.
"""

from __future__ import annotations

import asyncio
import contextlib

import httpx2
from mcp import Client, StdioServerParameters
from mcp.client.sse import sse_client
from mcp.client.streamable_http import streamable_http_client

from .contracts import McpProbeResult, McpServerConfig, McpToolInfo

#: Ilk acilis yavas olabilir (npx paketi indirir); daha uzun bekleyen deneme ekrani kilitler.
PROBE_TIMEOUT_S = 45.0
#: Arac aciklamasi ekranda ozet; tam sema ajana SDK'dan gider.
DESCRIPTION_MAX = 300


def _target(server: McpServerConfig, stack: contextlib.AsyncExitStack):
    if server.type == "stdio":
        if not server.command:
            raise ValueError("stdio sunucusu icin komut yok")
        return StdioServerParameters(command=server.command, args=list(server.args), env=dict(server.env) or None)
    if not server.url:
        raise ValueError(f"{server.type} sunucusu icin adres yok")
    if server.type == "sse":
        return sse_client(server.url, headers=dict(server.headers) or None)
    if server.headers:
        # Verilen istemciyi tasima kapatmaz: yigin kapatir.
        http = httpx2.AsyncClient(headers=dict(server.headers))
        stack.push_async_callback(http.aclose)
        return streamable_http_client(server.url, http_client=http)
    return server.url


def _describe(exc: BaseException) -> str:
    """anyio gorev gruplari hatayi ExceptionGroup'a sarar; kullaniciya en icteki gercek neden gosterilir."""
    while isinstance(exc, BaseExceptionGroup) and exc.exceptions:
        exc = exc.exceptions[0]
    text = str(exc).strip() or type(exc).__name__
    return f"{type(exc).__name__}: {text}"[:500]


async def probe(server: McpServerConfig, timeout_s: float = PROBE_TIMEOUT_S) -> McpProbeResult:
    try:
        async with asyncio.timeout(timeout_s), contextlib.AsyncExitStack() as stack:
            async with Client(_target(server, stack), read_timeout_seconds=timeout_s) as client:
                listing = await client.list_tools()
                info = client.server_info
                tools = [
                    McpToolInfo(name=t.name, description=(t.description or "")[:DESCRIPTION_MAX] or None)
                    for t in listing.tools
                ]
                more = " (ilk sayfa; sunucu daha fazlasini sunuyor)" if getattr(listing, "next_cursor", None) else ""
                return McpProbeResult(
                    ok=True,
                    detail=f"{len(tools)} arac{more}",
                    tools=tools,
                    server_name=getattr(info, "name", None),
                    server_version=getattr(info, "version", None),
                )
    except TimeoutError:
        return McpProbeResult(ok=False, detail=f"{timeout_s:.0f} s icinde yanit yok (sunucu acilmadi ya da el sikismadi)")
    except Exception as exc:  # noqa: BLE001 -- baglanti hatasi veridir, sonuca yazilir
        return McpProbeResult(ok=False, detail=_describe(exc))
