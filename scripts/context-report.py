"""Context budget report: how full the carried history got, what compaction dropped, and the calibration used.

Usage:
    python scripts/context-report.py                 # last 5 runs
    python scripts/context-report.py --runs 20
    python scripts/context-report.py --run 20260923-101500-slug

Reads data/aiteam.db read-only (the Api may keep running; SQLite is in WAL mode). Numbers come from the
turn record written by the .NET side (Turn.context, Turn.toolsOffered) -- the calibration is NOT recomputed
here, the report shows the value the compactor actually used when it decided. docs/DOMAIN.md -> Baglam butcesi.

"resent" is an estimate: dropped tokens x internal turns of that call = history the model would have been
sent again on every internal turn had it not been dropped (most of it would have been cache reads).

Second section (2026-09-24): context INSIDE one agent call. With the single-agent team no history is carried, so the
.NET compactor never fires; the context grows within the CLI session instead (tool results, written code, thinking).
"peak" = the largest single API call of the turn (Turn.peakContextTokens), "5m" = share of cache writes made with the
5-minute TTL (Settings -> prompt cache), "ttl" = the TTL the turn asked for. Rows written before 2026-09-24 show "-".
"""
from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--db", default=str(ROOT / "data" / "aiteam.db"))
    ap.add_argument("--runs", type=int, default=5, help="newest N runs (ignored with --run)")
    ap.add_argument("--run", help="a single run id")
    args = ap.parse_args()

    db = Path(args.db)
    if not db.exists():
        print(f"no database: {db}", file=sys.stderr)
        return 1
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)

    if args.run:
        run_ids = [args.run]
    else:
        run_ids = [r[0] for r in con.execute("SELECT id FROM run ORDER BY started_at DESC, id DESC LIMIT ?", (args.runs,))]
    if not run_ids:
        print("no runs")
        return 0

    marks = ",".join("?" * len(run_ids))
    rows = con.execute(
        f"SELECT run_id, agent, stage, task, round, provider, model, input_tokens, cache_read_tokens, data"
        f" FROM run_turn WHERE run_id IN ({marks}) ORDER BY id",
        run_ids,
    ).fetchall()

    print("== calibration samples (tool-less turns, all runs) ==")
    for provider, model, n in con.execute(
        "SELECT provider, model, COUNT(*) FROM run_turn"
        " WHERE input_tokens > 0 AND json_extract(data, '$.toolsOffered') = 0 GROUP BY provider, model ORDER BY 3 DESC"
    ):
        print(f"  {provider}/{model}: {n} samples (fit needs 8 with spread)")

    print("\n== carried history per turn ==")
    print(f"  {'run':<28} {'agent':<16} {'task':<6} {'stage':<12} {'rnd':>3}  {'carried':>14} -> {'kept':>14}  {'c/t':>5} {'n':>3}  {'in_tok':>9} {'turns':>5}  resent~")
    total_dropped = total_resent = compacted = carried_turns = 0
    for run_id, agent, stage, task, rnd, provider, model, in_tok, cache_read, data in rows:
        d = json.loads(data) if data else {}
        ctx = d.get("context")
        if not ctx:
            continue
        carried_turns += 1
        cpt = ctx["charsPerToken"] or 4.0
        dropped_tok = (ctx["carriedChars"] - ctx["keptChars"]) / cpt
        turns = d.get("turns") or 1
        resent = dropped_tok * turns
        if ctx["keptMessages"] < ctx["carriedMessages"] or ctx["keptChars"] < ctx["carriedChars"]:
            compacted += 1
            total_dropped += dropped_tok
            total_resent += resent
        print(
            f"  {run_id:<28} {agent:<16} {task or '-':<6} {stage or '-':<12} {rnd or '-':>3}"
            f"  {ctx['carriedMessages']:>3}m {ctx['carriedChars']:>8}c -> {ctx['keptMessages']:>3}m {ctx['keptChars']:>8}c"
            f"  {cpt:>5.2f} {ctx['calibrationSamples']:>3}  {in_tok or 0:>9} {turns:>5}  {int(resent):>7}"
        )

    print(
        f"\n{carried_turns} turns carried history, {compacted} compacted;"
        f" dropped ~{int(total_dropped)} tokens, ~{int(total_resent)} tokens not resent across internal turns."
    )

    print("\n== context inside the call (per turn) ==")
    print(f"  {'run':<28} {'task':<6} {'stage':<12} {'turns':>5} {'peak':>8} {'avg/turn':>9} {'write':>8} {'5m':>5} {'ttl':>4} {'cost$':>7}")
    for run_id, agent, stage, task, rnd, provider, model, in_tok, cache_read, data in rows:
        d = json.loads(data) if data else {}
        turns = d.get("turns") or 1
        peak = d.get("peakContextTokens")
        write = d.get("cacheWriteTokens") or 0
        short = d.get("cacheWrite5mTokens") or 0
        share = f"{short / write:.0%}" if write and peak is not None else "-"
        cost = d.get("costUsd")
        print(
            f"  {run_id:<28} {task or '-':<6} {stage or '-':<12} {turns:>5} {peak if peak is not None else '-':>8}"
            f" {int((in_tok or 0) / turns):>9} {write:>8} {share:>5} {d.get('cacheTtl') or '-':>4} {cost if cost is not None else 0:>7.2f}"
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
