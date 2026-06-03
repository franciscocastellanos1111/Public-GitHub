"""One-off bootstrap that seeds canonical, manually-pinned memory entries
for the NonprofitVerificationMemory{Slot} table.

Run from the function app directory:

    py bootstrap_memory.py                  # seed the current slot (auto-detected)
    py bootstrap_memory.py --slot Stage     # seed an explicit slot name
    py bootstrap_memory.py --slot Production --dry-run

Safe to re-run: rows are upserted; manual source + High confidence are re-pinned every time.

This script intentionally does NOT auto-promote anything between slots.
Use promote_memory_stage_to_prod.py for stage -> production copies.
"""
from __future__ import annotations

import argparse
import json
import logging
import sys


def _seed_entries() -> list[dict]:
    # Each entry mirrors the npv_memory_service.pin_manual() signature.
    return [
        # ---------------- Brazil ----------------
        {
            "category": "RegistryUrlTemplate",
            "scope_key": "BR",
            "subject_key": "cnpj_receitaws_v1",
            "subject": "ReceitaWS CNPJ JSON mirror (preferred for Brazilian CNPJ lookup)",
            "content": {
                "url_template": "https://www.receitaws.com.br/v1/cnpj/{cnpj_digits}",
                "identifier_field": "cnpj_digits",
                "identifier_normalization": "strip non-digits from CNPJ before substitution",
                "registry_type": "Receita Federal CNPJ (unofficial JSON mirror)",
                "response_format": "json",
                "notes": "Free public mirror of Receita Federal CNPJ. No CAPTCHA. Sometimes rate-limited.",
            },
            "tags": "preferred,no-captcha,json",
        },
        {
            "category": "RegistryUrlTemplate",
            "scope_key": "BR",
            "subject_key": "cnpj_minhareceita_v1",
            "subject": "MinhaReceita CNPJ JSON mirror (backup)",
            "content": {
                "url_template": "https://minhareceita.org/{cnpj_digits}",
                "identifier_field": "cnpj_digits",
                "identifier_normalization": "strip non-digits from CNPJ before substitution",
                "registry_type": "Receita Federal CNPJ (unofficial JSON mirror, backup)",
                "response_format": "json",
            },
            "tags": "backup,no-captcha,json",
        },
        {
            "category": "BlockedSource",
            "scope_key": "global",
            "subject_key": "br_receita_official_captcha",
            "subject": "Brazilian Receita Federal CNPJ official portal is CAPTCHA-walled",
            "content": {
                "host": "solucoes.receita.fazenda.gov.br",
                "reason": "CAPTCHA + JS-only UI; not fetchable by tools",
                "fallback_subject_keys": ["cnpj_receitaws_v1", "cnpj_minhareceita_v1"],
                "scope_country": "BR",
            },
            "tags": "captcha,blocked",
        },
        {
            "category": "Registry",
            "scope_key": "BR",
            "subject_key": "cnpj_receita_federal",
            "subject": "Cadastro Nacional da Pessoa Jur\u00eddica (CNPJ) \u2014 Receita Federal do Brasil",
            "content": {
                "registry_name": "CNPJ \u2014 Receita Federal do Brasil",
                "identifier": "CNPJ (14 digits)",
                "official_host": "solucoes.receita.fazenda.gov.br (CAPTCHA-walled)",
                "preferred_lookup_subject_key": "cnpj_receitaws_v1",
                "notes": "Brazilian federal tax-ID registry. Use the JSON mirrors for tool-based lookups.",
            },
            "tags": "br,federal",
        },
        # ---------------- United States ----------------
        {
            "category": "RegistryUrlTemplate",
            "scope_key": "US",
            "subject_key": "irs_teos_v1",
            "subject": "IRS Tax Exempt Organization Search by EIN",
            "content": {
                "url_template": "https://apps.irs.gov/app/eos/allSearch?ein1={ein_digits}&names=&city=&state=All&country=US&deductibility=all&isDescending=false",
                "identifier_field": "ein_digits",
                "identifier_normalization": "strip non-digits from EIN before substitution",
                "registry_type": "IRS 501(c)(3)",
                "response_format": "html",
            },
            "tags": "irs,501c3",
        },
        # ---------------- United Kingdom ----------------
        {
            "category": "RegistryUrlTemplate",
            "scope_key": "UK",
            "subject_key": "charity_commission_v1",
            "subject": "Charity Commission for England & Wales \u2014 search by registered number",
            "content": {
                "url_template": "https://register-of-charities.charitycommission.gov.uk/charity-search?p_p_id=uk_gov_ccew_portlet_CharitySearchPortlet&p_p_lifecycle=0&query={charity_number}",
                "identifier_field": "charity_number",
                "registry_type": "UK Charity Commission",
                "response_format": "html",
            },
            "tags": "uk,charity",
        },
        # ---------------- Canada ----------------
        {
            "category": "RegistryUrlTemplate",
            "scope_key": "CA",
            "subject_key": "cra_charities_v1",
            "subject": "Canada Revenue Agency \u2014 Charities Listings",
            "content": {
                "url_template": "https://apps.cra-arc.gc.ca/ebci/hacc/srch/pub/dsplyBscSrch?request_locale=en&q.bn={bn_digits}",
                "identifier_field": "bn_digits",
                "identifier_normalization": "Business Number with RR suffix, digits only",
                "registry_type": "CRA Charities Directorate",
                "response_format": "html",
            },
            "tags": "ca,charity",
        },
        # ---------------- Australia ----------------
        {
            "category": "RegistryUrlTemplate",
            "scope_key": "AU",
            "subject_key": "acnc_register_v1",
            "subject": "ACNC Charity Register \u2014 search by ABN",
            "content": {
                "url_template": "https://www.acnc.gov.au/charity?search_api_fulltext={abn_digits}",
                "identifier_field": "abn_digits",
                "registry_type": "ACNC",
                "response_format": "html",
            },
            "tags": "au,charity",
        },
        # ---------------- Romania ----------------
        {
            "category": "RegistryUrlTemplate",
            "scope_key": "RO",
            "subject_key": "anaf_ong_deduceri_v1",
            "subject": "ANAF \u2014 Registrul entit\u0103\u021bilor pentru care se acord\u0103 deduceri fiscale",
            "content": {
                "url_template": "https://static.anaf.ro/static/40/Anaf/AsistentaContribuabili_r/Asociatii_fundatii/Asociatii_fundatii_alocate.htm",
                "identifier_field": "cui",
                "registry_type": "ANAF Romania",
                "response_format": "html",
                "notes": "Large static page; filter results client-side by CIF/CUI.",
            },
            "tags": "ro,nonprofit",
        },
        # ---------------- Spain ----------------
        {
            "category": "Registry",
            "scope_key": "ES",
            "subject_key": "registro_nacional_asociaciones",
            "subject": "Registro Nacional de Asociaciones (Ministerio del Interior)",
            "content": {
                "registry_name": "Registro Nacional de Asociaciones",
                "issuing_authority": "Ministerio del Interior de Espa\u00f1a",
                "url": "https://sede.mir.gob.es/opencms/pae/asociaciones/registro_nacional_asociaciones/",
                "notes": "Public registry; lookup is form-based and partly behind JS.",
            },
            "tags": "es,association",
        },
        # ---------------- Germany ----------------
        {
            "category": "Registry",
            "scope_key": "DE",
            "subject_key": "vereinsregister",
            "subject": "Vereinsregister (German local Amtsgericht registry of associations)",
            "content": {
                "registry_name": "Vereinsregister",
                "url": "https://www.handelsregister.de/rp_web/welcome.xhtml",
                "notes": "Search at handelsregister.de; choose Registerart=VR. Login-walled for full extracts.",
            },
            "tags": "de,verein",
        },
        # ---------------- France ----------------
        {
            "category": "RegistryUrlTemplate",
            "scope_key": "FR",
            "subject_key": "joafe_search_v1",
            "subject": "Journal Officiel des Associations (JOAFE) \u2014 search",
            "content": {
                "url_template": "https://www.journal-officiel.gouv.fr/pages/associations-recherche/?q={query}",
                "identifier_field": "query",
                "registry_type": "JOAFE / RNA",
                "response_format": "html",
            },
            "tags": "fr,association",
        },
        # ---------------- Global heuristics ----------------
        {
            "category": "Heuristic",
            "scope_key": "global",
            "subject_key": "captcha_pivot_rule",
            "subject": "When official registry returns CAPTCHA, record RegistryUnavailable once and pivot",
            "content": {
                "rule": (
                    "If a fetched official registry response indicates CAPTCHA, JS-only UI, "
                    "or repeated 4xx/5xx after one honest attempt, record status='RegistryUnavailable' "
                    "exactly once and immediately pivot to a known JSON mirror or alternate registry. "
                    "Do not loop on the same blocked source."
                ),
            },
            "tags": "rule,pivot",
        },
    ]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Seed canonical NPV memory entries.")
    parser.add_argument("--slot", help="Slot name (e.g. Local, Qa, Stage, Production). Defaults to auto-detected.")
    parser.add_argument("--dry-run", action="store_true", help="Print what would be written; do not call Azure Tables.")
    args = parser.parse_args(argv)

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    logger = logging.getLogger("bootstrap_memory")

    try:
        import npv_memory_service
    except Exception as e:
        logger.error(f"Failed to import npv_memory_service: {e}")
        return 2

    slot = args.slot or npv_memory_service.get_slot_name()
    entries = _seed_entries()
    logger.info(f"Seeding {len(entries)} entries into slot={slot} (dry_run={args.dry_run})")

    written = 0
    rejected = 0
    for ent in entries:
        if args.dry_run:
            logger.info(f"[dry-run] would pin {ent['category']}__{ent['scope_key']}/{ent['subject_key']}")
            continue
        try:
            result = npv_memory_service.pin_manual(
                category=ent["category"],
                scope_key=ent["scope_key"],
                subject_key=ent["subject_key"],
                subject=ent["subject"],
                content=ent["content"],
                tags=ent.get("tags"),
                notes="seeded by bootstrap_memory.py",
                slot_name=slot,
                logger=logger,
            )
            if result is not None:
                written += 1
                logger.info(f"  + {result['PartitionKey']}/{result['RowKey']}")
            else:
                rejected += 1
                logger.warning(f"  - rejected: {ent['category']}/{ent['subject_key']}")
        except Exception as ex:
            rejected += 1
            logger.error(f"  ! error seeding {ent['subject_key']}: {ex}")

    logger.info(f"Done. written={written} rejected={rejected} slot={slot}")
    return 0 if rejected == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
