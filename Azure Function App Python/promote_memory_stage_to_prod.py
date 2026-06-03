"""Manual stage -> production promotion CLI for NPV memory.

Reads rows from a source slot (typically 'Stage'), filters those that meet the
promotion bar, and upserts them into a destination slot (typically 'Production').
Promoted rows are re-pinned as Source='manual', Confidence='High' in the destination
and their operational counters are reset.

REQUIRES --confirm to actually write. Without it, only a dry-run report is printed.

Defaults:
    --source-slot Stage
    --target-slot Production

Promotion bar (a row is promoted if ANY of these is true):
    * Source == 'manual'
    * (Confidence == 'High') AND (SuccessCount >= --min-successes, default 10)
    * Explicit allow-list passed via --include-ref (repeatable)

Excluded by default:
    * Status == 'deprecated' (must reactivate first in source slot)
    * Status == 'needsReview' (manual review required)
    * Source == 'agent' with low confidence or insufficient successes

This script intentionally does NOT auto-run on a schedule. It is a manual operator command.

The connection string used is the env var AzureWebJobsStorage (same as the function host).
If your stage and production slots use different storage accounts, set
AZURE_NPV_MEMORY_SOURCE_CONN and AZURE_NPV_MEMORY_TARGET_CONN before invoking.
"""
from __future__ import annotations

import argparse
import json
import logging
import os
import sys
from typing import Iterable


def _resolve_slot_clients(source_slot: str, target_slot: str, logger):
    import npv_memory_service

    src_conn = os.getenv("AZURE_NPV_MEMORY_SOURCE_CONN")
    dst_conn = os.getenv("AZURE_NPV_MEMORY_TARGET_CONN")

    if not src_conn and not dst_conn:
        # Single-account mode: both slots live in the same storage account; the
        # default get_table_client path works.
        return None, None

    if not src_conn or not dst_conn:
        raise RuntimeError(
            "If you set one of AZURE_NPV_MEMORY_SOURCE_CONN / AZURE_NPV_MEMORY_TARGET_CONN, "
            "you must set both."
        )

    from azure.data.tables import TableServiceClient

    def _client(conn: str, slot: str):
        svc = TableServiceClient.from_connection_string(conn_str=conn)
        table_name = npv_memory_service._get_table_name(slot)
        client = svc.get_table_client(table_name=table_name)
        try:
            client.create_table()
        except Exception:
            pass
        return client

    return _client(src_conn, source_slot), _client(dst_conn, target_slot)


def _list_source_rows(source_slot: str, override_client, logger) -> list[dict]:
    import npv_memory_service
    client = override_client or npv_memory_service._get_table_client(source_slot, logger)
    if client is None:
        return []
    rows = []
    try:
        for ent in client.list_entities():
            try:
                rows.append(npv_memory_service._serialize_entity(ent))
            except Exception:
                continue
    except Exception as e:
        logger.error(f"_list_source_rows error: {e}")
    return rows


def _matches_promotion_bar(row: dict, min_successes: int, include_refs: set[str]) -> tuple[bool, str]:
    ref = row.get("Ref")
    if ref and ref in include_refs:
        return True, "explicit_include"
    status = (row.get("Status") or "active")
    if status in {"deprecated", "needsReview"}:
        return False, f"excluded_status={status}"
    source = row.get("Source")
    if source == "manual":
        return True, "source_manual"
    confidence = row.get("Confidence")
    successes = int(row.get("SuccessCount") or 0)
    if confidence == "High" and successes >= min_successes:
        return True, f"high_confidence_successes={successes}"
    return False, f"below_bar(confidence={confidence}, successes={successes})"


def _promote(rows_to_copy: Iterable[dict], source_slot: str, target_slot: str,
             src_client, dst_client, logger) -> dict:
    import npv_memory_service
    if src_client is not None and dst_client is not None:
        # Cross-account explicit clients: do the copy inline.
        from azure.data.tables import UpdateMode
        from datetime import datetime, timezone
        summary = {"copied": 0, "skipped": 0, "errors": []}
        now = datetime.now(timezone.utc)
        for row in rows_to_copy:
            try:
                pk = row["PartitionKey"]
                rk = row["RowKey"]
                clone = {k: v for k, v in row.items() if k not in {"Ref", "Content"}}
                clone["SlotName"] = target_slot
                clone["Source"] = "manual"
                clone["Confidence"] = "High"
                clone["Status"] = "active"
                clone["UseCount"] = 0
                clone["SuccessCount"] = max(1, int(clone.get("SuccessCount") or 0))
                clone["FailureCount"] = 0
                clone["LastUsedDateUtc"] = now
                clone["LastConfirmedDateUtc"] = now
                clone["ReviewNotes"] = ((clone.get("ReviewNotes") or "") + f" | promoted from {source_slot} at {now.isoformat()}")[:31000]
                npv_memory_service._truncate_oversize_string_props(clone, logger)
                dst_client.upsert_entity(entity=clone, mode=UpdateMode.REPLACE)
                summary["copied"] += 1
                logger.info(f"  promoted {pk}/{rk}")
            except Exception as ex:
                summary["errors"].append(f"{row.get('Ref')}: {ex}")
                summary["skipped"] += 1
        return summary

    # Same-account path: reuse npv_memory_service.copy_rows_between_slots.
    refs = [row["Ref"] for row in rows_to_copy if row.get("Ref")]
    return npv_memory_service.copy_rows_between_slots(
        refs=refs,
        source_slot=source_slot,
        target_slot=target_slot,
        set_source_to_manual=True,
        logger=logger,
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Promote NPV memory rows from a source slot to a target slot.")
    parser.add_argument("--source-slot", default="Stage")
    parser.add_argument("--target-slot", default="Production")
    parser.add_argument("--min-successes", type=int, default=10)
    parser.add_argument("--include-ref", action="append", default=[],
                        help="Force-promote a specific Ref (PartitionKey/RowKey). Repeatable.")
    parser.add_argument("--confirm", action="store_true",
                        help="Actually write to the target slot. Without --confirm, this is a dry-run report.")
    parser.add_argument("--output", help="Optional path to write the full report as JSON.")
    args = parser.parse_args(argv)

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    logger = logging.getLogger("promote_memory")

    if args.source_slot == args.target_slot:
        logger.error("source-slot and target-slot must differ")
        return 2

    try:
        src_client, dst_client = _resolve_slot_clients(args.source_slot, args.target_slot, logger)
    except Exception as e:
        logger.error(f"client resolution failed: {e}")
        return 2

    logger.info(f"Listing rows from source slot '{args.source_slot}'...")
    rows = _list_source_rows(args.source_slot, src_client, logger)
    logger.info(f"  found {len(rows)} rows")

    include_refs = set(args.include_ref or [])
    eligible: list[dict] = []
    skipped: list[dict] = []
    for row in rows:
        ok, reason = _matches_promotion_bar(row, args.min_successes, include_refs)
        if ok:
            eligible.append({"ref": row.get("Ref"), "reason": reason, "row": row})
        else:
            skipped.append({"ref": row.get("Ref"), "reason": reason})

    logger.info(f"Promotion-eligible: {len(eligible)}   skipped: {len(skipped)}")
    for e in eligible[:50]:
        logger.info(f"  + {e['ref']}  ({e['reason']})")
    if len(eligible) > 50:
        logger.info(f"  ... +{len(eligible) - 50} more")

    report = {
        "source_slot": args.source_slot,
        "target_slot": args.target_slot,
        "min_successes": args.min_successes,
        "eligible_count": len(eligible),
        "skipped_count": len(skipped),
        "eligible": [{"ref": e["ref"], "reason": e["reason"]} for e in eligible],
        "skipped": skipped,
        "promotion_summary": None,
        "confirmed": bool(args.confirm),
    }

    if not args.confirm:
        logger.warning("Dry-run only. Re-run with --confirm to actually write to the target slot.")
    else:
        logger.info(f"Promoting {len(eligible)} rows to '{args.target_slot}'...")
        summary = _promote(
            rows_to_copy=[e["row"] for e in eligible],
            source_slot=args.source_slot,
            target_slot=args.target_slot,
            src_client=src_client,
            dst_client=dst_client,
            logger=logger,
        )
        report["promotion_summary"] = summary
        logger.info(f"Promotion summary: {summary}")

    if args.output:
        try:
            with open(args.output, "w", encoding="utf-8") as f:
                json.dump(report, f, indent=2, default=str)
            logger.info(f"Wrote report to {args.output}")
        except Exception as ex:
            logger.error(f"Failed to write report: {ex}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
