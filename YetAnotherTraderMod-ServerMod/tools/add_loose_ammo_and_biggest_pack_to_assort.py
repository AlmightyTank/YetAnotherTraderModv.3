#!/usr/bin/env python3
r"""
Add a loose ammo offer and its biggest matching ammo pack offer to a Tony/SPT trader assort.json.

Basic PowerShell usage from your mod root:
  python .\tools\add_loose_ammo_and_biggest_pack_to_assort.py --ammo-tpl 56dff026d2720bb8668b4567 --assort .\data\assort.json --db .\db --db .\data --items-db "C:\SPT\SPT_Data\Server\database\templates\items.json" --locale "C:\SPT\SPT_Data\Server\database\locales\global\en.json"

If the pack cannot be auto-found, pass it manually:
  python .\tools\add_loose_ammo_and_biggest_pack_to_assort.py --ammo-tpl <looseAmmoTpl> --pack-tpl <packTpl> --pack-size 120

What it writes to assort.json:
  - root loose ammo offer in assort.items
  - root ammo pack offer in assort.items
  - RUB/USD/EUR cash barter_scheme entries
  - loyal_level_items entries

It never uses StackObjectsCount 100 by default. Default stock is 999.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from typing import Any

SCRIPT_VERSION = "1.0.0"

RUB_TPL = "5449016a4bdc2d6f028b456f"
USD_TPL = "5696686a4bdc2da3298b456a"
EUR_TPL = "569668774bdc2da2298b4568"

TPL_BY_CURRENCY = {
    "RUB": RUB_TPL,
    "USD": USD_TPL,
    "EUR": EUR_TPL,
}

CURRENCY_NAME_BY_TPL = {
    RUB_TPL: "Roubles",
    USD_TPL: "Dollars",
    EUR_TPL: "Euros",
}

HEX_24_RE = re.compile(r"^[a-fA-F0-9]{24}$")
AMMO_PACK_SIZE_RE = re.compile(r"\((\d+)\s*pcs?\)", re.IGNORECASE)
AMMO_PACK_NAME_RE = re.compile(r"\s+ammo\s+pack\s*\(\d+\s*pcs?\).*?$", re.IGNORECASE)

# Fallbacks from Tony's generator. Auto-scan from db/locales still wins when it finds a larger match.
BUILT_IN_AMMO_PACKS: dict[str, list[dict[str, Any]]] = {
    "5f0596629e22f464da6bbdd9": [{"pack_tpl": "657023f81419851aef03e6f1", "pack_name": ".366 TKM AP-M ammo pack (20 pcs)", "pack_size": 20}],
    "59e655cb86f77411dc52a77b": [{"pack_tpl": "657024011419851aef03e6f4", "pack_name": ".366 TKM EKO ammo pack (20 pcs)", "pack_size": 20}],
    "560d5e524bdc2d25448b4571": [{"pack_tpl": "657024361419851aef03e6fa", "pack_name": "12/70 7mm buckshot ammo pack (25 pcs)", "pack_size": 25}],
    "5d6e68a8a4b9360b6c0d54e2": [{"pack_tpl": "64898838d5b4df6140000a20", "pack_name": "12/70 AP-20 ammo pack (25 pcs)", "pack_size": 25}],
    "5d6e6911a4b9361bd5780d52": [{"pack_tpl": "65702474bfc87b3a34093226", "pack_name": "12/70 flechette ammo pack (25 pcs)", "pack_size": 25}],
    "56dfef82d2720bbd668b4567": [{"pack_tpl": "5737292724597765e5728562", "pack_name": "5.45x39mm BP gs ammo pack (120 pcs)", "pack_size": 120}],
    "56dff026d2720bb8668b4567": [{"pack_tpl": "57372b832459776701014e41", "pack_name": "5.45x39mm BS gs ammo pack (120 pcs)", "pack_size": 120}],
    "56dff061d2720bb5668b4567": [{"pack_tpl": "57372c21245977670937c6c2", "pack_name": "5.45x39mm BT gs ammo pack (120 pcs)", "pack_size": 120}],
    "56dff2ced2720bb4668b4567": [{"pack_tpl": "57372d1b2459776862260581", "pack_name": "5.45x39mm PP gs ammo pack (120 pcs)", "pack_size": 120}],
    "5c0d5e4486f77478390952fe": [{"pack_tpl": "657025ebc5d7d4cb4d078588", "pack_name": "5.45x39mm PPBS gs Igolnik ammo pack (120 pcs)", "pack_size": 120}],
    "56dff3afd2720bba668b4567": [{"pack_tpl": "57372e73245977685d4159b4", "pack_name": "5.45x39mm PS gs ammo pack (120 pcs)", "pack_size": 120}],
    "59e0d99486f7744a32234762": [{"pack_tpl": "64acea16c4eda9354b0226b0", "pack_name": "7.62x39mm BP gzh ammo pack (20 pcs)", "pack_size": 20}],
    "601aa3d2b2bcb34913271e6d": [{"pack_tpl": "6489851fc827d4637f01791b", "pack_name": "7.62x39mm MAI AP ammo pack (20 pcs)", "pack_size": 20}],
    "64b7af434b75259c590fa893": [{"pack_tpl": "64ace9f9c4eda9354b0226aa", "pack_name": "7.62x39mm PP gzh ammo pack (20 pcs)", "pack_size": 20}],
    "5656d7c34bdc2d9d198b4587": [{"pack_tpl": "5649ed104bdc2d3d1c8b458b", "pack_name": "7.62x39mm PS gzh ammo pack (20 pcs)", "pack_size": 20}],
    "57372140245977611f70ee91": [{"pack_tpl": "657026341419851aef03e730", "pack_name": "9x18mm PM SP7 gzh ammo pack (50 pcs)", "pack_size": 50}],
    "5c925fa22e221601da359b7b": [{"pack_tpl": "65702591c5d7d4cb4d07857c", "pack_name": "9x19mm AP 6.3 ammo pack (50 pcs)", "pack_size": 50}],
    "5efb0da7a29a85116f6ea05f": [{"pack_tpl": "648987d673c462723909a151", "pack_name": "9x19mm PBP ammo pack (50 pcs)", "pack_size": 50}],
    "56d59d3ad2720bdb418b4577": [{"pack_tpl": "657025a81419851aef03e724", "pack_name": "9x19mm Pst gzh ammo pack (50 pcs)", "pack_size": 50}],
    "5c0d56a986f774449d5de529": [{"pack_tpl": "5c1127bdd174af44217ab8b9", "pack_name": "9x19mm RIP ammo pack (20 pcs)", "pack_size": 20}],
    "5c0d688c86f77413ae3407b2": [{"pack_tpl": "6489854673c462723909a14e", "pack_name": "9x39mm BP ammo pack (20 pcs)", "pack_size": 20}],
    "61962d879bb3d20b0946d385": [{"pack_tpl": "657025cfbfc87b3a34093253", "pack_name": "9x39mm PAB-9 gs ammo pack (20 pcs)", "pack_size": 20}],
    "57a0dfb82459774d3078b56c": [{"pack_tpl": "657025d4c5d7d4cb4d078585", "pack_name": "9x39mm SP-5 gs ammo pack (20 pcs)", "pack_size": 20}],
    "57a0e5022459774d1673f889": [{"pack_tpl": "657025dabfc87b3a34093256", "pack_name": "9x39mm SP-6 gs ammo pack (20 pcs)", "pack_size": 20}],
    "5c0d668f86f7747ccb7f13b2": [{"pack_tpl": "657025dfcfc010a0f5006a3b", "pack_name": "9x39mm SPP gs ammo pack (20 pcs)", "pack_size": 20}],
    "6a427a2e38a6d33bffe9829b": [{"pack_tpl": "65702577cfc010a0f5006a2c", "pack_name": "7.62x54mm R LPS gzh ammo pack (20 pcs)", "pack_size": 20}],
    "6a427a2e38a6d33bffe98292": [{"pack_tpl": "648984b8d5b4df6140000a1a", "pack_name": "7.62x54mm R BS gs ammo pack (20 pcs)", "pack_size": 20}],
    "6a427a2e38a6d33bffe9829d": [{"pack_tpl": "560d75f54bdc2da74d8b4573", "pack_name": "7.62x54mm R SNB gzh ammo pack (20 pcs)", "pack_size": 20}],
}


def strip_json_comments_and_trailing_commas(text: str) -> str:
    text = text.lstrip("\ufeff")
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.DOTALL)
    text = re.sub(r"(^|\s)//.*?$", r"\1", text, flags=re.MULTILINE)
    text = re.sub(r",\s*([}\]])", r"\1", text)
    return text


def load_json(path: Path) -> Any:
    text = path.read_text(encoding="utf-8")
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return json.loads(strip_json_comments_and_trailing_commas(text))


def get_ci(obj: dict[str, Any] | None, *keys: str, default: Any = None) -> Any:
    if not isinstance(obj, dict):
        return default
    for key in keys:
        if key in obj:
            return obj[key]
    lower_map = {str(k).lower(): k for k in obj.keys()}
    for key in keys:
        actual_key = lower_map.get(key.lower())
        if actual_key is not None:
            return obj[actual_key]
    return default


def get_existing_key_ci(obj: dict[str, Any], *keys: str) -> str | None:
    if not isinstance(obj, dict):
        return None
    for key in keys:
        if key in obj:
            return key
    lower_map = {str(k).lower(): str(k) for k in obj.keys()}
    for key in keys:
        actual_key = lower_map.get(key.lower())
        if actual_key is not None:
            return actual_key
    return None


def ensure_assort_section(assort: dict[str, Any], preferred_key: str, *alternate_keys: str, default_value: Any) -> Any:
    existing_key = get_existing_key_ci(assort, preferred_key, *alternate_keys)
    if existing_key is None:
        assort[preferred_key] = default_value
        return assort[preferred_key]
    section = assort.get(existing_key)
    if section is None:
        section = default_value
        assort[existing_key] = section
    return section


def clean_number(value: float) -> int | float:
    return int(value) if float(value).is_integer() else value


def normalize_name(value: str) -> str:
    return re.sub(r"\s+", " ", str(value or "")).strip()


def parse_pack_size_from_name(item_name: str) -> int | None:
    match = AMMO_PACK_SIZE_RE.search(item_name or "")
    if not match:
        return None
    try:
        return max(1, int(match.group(1)))
    except (TypeError, ValueError):
        return None


def normalize_ammo_pack_match_name(item_name: str) -> str:
    name = (item_name or "").lower().strip()
    name = AMMO_PACK_NAME_RE.sub("", name)
    name = AMMO_PACK_SIZE_RE.sub("", name)
    name = name.replace("ammo pack", "")
    name = re.sub(r"\s+", " ", name)
    return name.strip(" -")


def iter_json_files(paths: list[Path]) -> list[Path]:
    files: list[Path] = []
    for path in paths:
        if not path.exists():
            continue
        if path.is_file() and path.suffix.lower() == ".json":
            files.append(path)
        elif path.is_dir():
            files.extend(sorted(p for p in path.rglob("*.json") if p.is_file()))
    return files


def maybe_tpl(value: Any) -> str | None:
    text = str(value or "").strip()
    return text if HEX_24_RE.match(text) else None


def collect_filter_tpls(value: Any) -> set[str]:
    found: set[str] = set()
    if isinstance(value, str):
        tpl = maybe_tpl(value)
        if tpl:
            found.add(tpl)
    elif isinstance(value, list):
        for item in value:
            found.update(collect_filter_tpls(item))
    elif isinstance(value, dict):
        for key, child in value.items():
            if str(key).lower() in {"filter", "filters", "tpl", "_tpl", "template", "templateid"}:
                found.update(collect_filter_tpls(child))
            elif isinstance(child, (dict, list)):
                found.update(collect_filter_tpls(child))
    return found


def extract_stack_slot_pack_size(template: dict[str, Any], loose_ammo_tpl: str) -> int | None:
    props = get_ci(template, "_props", "props", "Properties", default={})
    slots = get_ci(props, "StackSlots", "stackSlots", "Stackslots", default=None)
    if not isinstance(slots, list):
        return None

    best: int | None = None
    for slot in slots:
        if not isinstance(slot, dict):
            continue
        slot_props = get_ci(slot, "_props", "props", default={})
        filter_tpls = collect_filter_tpls(slot_props)
        filter_tpls.update(collect_filter_tpls(slot))
        if loose_ammo_tpl not in filter_tpls:
            continue

        raw_count = get_ci(
            slot,
            "_max_count",
            "max_count",
            "MaxCount",
            "maxCount",
            default=get_ci(slot_props, "_max_count", "max_count", "MaxCount", "maxCount", default=None),
        )
        try:
            count = max(1, int(float(raw_count)))
        except (TypeError, ValueError):
            count = None
        if count is not None:
            best = count if best is None else max(best, count)

    return best


def collect_names_from_locale_shape(data: Any, names: dict[str, str]) -> None:
    if not isinstance(data, dict):
        return

    value_data = get_ci(data, "Value", "value", default=None)
    if isinstance(value_data, dict):
        data = value_data

    for key, value in data.items():
        key_str = str(key)
        if key_str.endswith(" Name"):
            tpl = key_str[:-5]
            if maybe_tpl(tpl) and str(value).strip():
                names[tpl] = normalize_name(str(value))
            continue

        if isinstance(value, dict):
            nested_name = get_ci(value, "Name", "name", "ItemName", "itemName", default=None)
            if maybe_tpl(key_str) and nested_name:
                names[key_str] = normalize_name(str(nested_name))


def collect_templates_and_names_from_object(data: Any, templates: dict[str, dict[str, Any]], names: dict[str, str]) -> None:
    collect_names_from_locale_shape(data, names)

    if isinstance(data, list):
        for entry in data:
            collect_templates_and_names_from_object(entry, templates, names)
        return

    if not isinstance(data, dict):
        return

    # Template object with its own id.
    own_id = maybe_tpl(get_ci(data, "_id", "id", "Id", default=""))
    own_tpl = maybe_tpl(get_ci(data, "_tpl", "tpl", "Tpl", "Template", "TemplateId", default=""))
    if own_id and ("_props" in data or "props" in data or "parentId" in data or "itemTplToClone" in data):
        templates.setdefault(own_id, data)
        direct_name = get_ci(data, "name", "Name", "itemName", "ItemName", "shortName", "ShortName", default=None)
        props = get_ci(data, "_props", "props", default={})
        prop_name = get_ci(props, "Name", "name", "ShortName", "shortName", default=None)
        if direct_name:
            names.setdefault(own_id, normalize_name(str(direct_name)))
        elif prop_name:
            names.setdefault(own_id, normalize_name(str(prop_name)))
    if own_tpl and own_tpl != own_id:
        templates.setdefault(own_tpl, data)

    # Keyed object: { "tplid": { ... } }
    for key, value in data.items():
        tpl = maybe_tpl(key)
        if tpl and isinstance(value, dict):
            templates.setdefault(tpl, value)

            direct_name = get_ci(value, "name", "Name", "itemName", "ItemName", "shortName", "ShortName", default=None)
            if direct_name:
                names.setdefault(tpl, normalize_name(str(direct_name)))

            props = get_ci(value, "_props", "props", default={})
            prop_name = get_ci(props, "Name", "name", "ShortName", "shortName", default=None)
            if prop_name:
                names.setdefault(tpl, normalize_name(str(prop_name)))

            locales = get_ci(value, "locales", "Locales", "locale", "Locale", default=None)
            if isinstance(locales, dict):
                # Handles {locales:{en:{name:"..."}}} and {locales:{Name:"..."}}
                en_locale = get_ci(locales, "en", "en-US", "EN", default=None)
                if isinstance(en_locale, dict):
                    locale_name = get_ci(en_locale, "name", "Name", "itemName", "ItemName", "shortName", "ShortName", default=None)
                    if locale_name:
                        names[tpl] = normalize_name(str(locale_name))
                else:
                    locale_name = get_ci(locales, "name", "Name", "itemName", "ItemName", "shortName", "ShortName", default=None)
                    if locale_name:
                        names[tpl] = normalize_name(str(locale_name))


def load_templates_and_names(json_paths: list[Path]) -> tuple[dict[str, dict[str, Any]], dict[str, str], list[str]]:
    templates: dict[str, dict[str, Any]] = {}
    names: dict[str, str] = {}
    warnings: list[str] = []

    for path in iter_json_files(json_paths):
        try:
            data = load_json(path)
        except Exception as ex:
            warnings.append(f"Skipped unreadable JSON {path}: {ex}")
            continue
        collect_templates_and_names_from_object(data, templates, names)

    return templates, names, warnings


def load_price_catalog(path: Path | None) -> dict[str, float]:
    if path is None or not path.exists():
        return {}
    data = load_json(path)
    prices: dict[str, float] = {}
    if not isinstance(data, list):
        return prices
    for row in data:
        if not isinstance(row, dict):
            continue
        tpl = str(get_ci(row, "TplId", "tplId", "_tpl", "Template", default="") or "")
        if not tpl:
            continue
        price = get_ci(row, "Price", "price", default=None)
        if price is None:
            continue
        try:
            prices[tpl] = float(price)
        except (TypeError, ValueError):
            pass
    return prices


def resolve_name(tpl: str, names: dict[str, str]) -> str:
    if tpl in CURRENCY_NAME_BY_TPL:
        return CURRENCY_NAME_BY_TPL[tpl]
    if tpl in names and names[tpl] and not str(names[tpl]).startswith("UNKNOWN_ITEM_"):
        return names[tpl]
    return f"UNKNOWN_ITEM_{tpl}"


def get_template_price(tpl: str, templates: dict[str, dict[str, Any]], catalog_prices: dict[str, float], fallback: float) -> float:
    if tpl in catalog_prices:
        return catalog_prices[tpl]

    template = templates.get(tpl, {})
    props = get_ci(template, "_props", "props", default={})
    for source in (props, template):
        for key in ("CreditsPrice", "creditsPrice", "Price", "price", "BasePrice", "basePrice"):
            raw = get_ci(source, key, default=None)
            if raw is None:
                continue
            try:
                value = float(raw)
                if value > 0:
                    return value
            except (TypeError, ValueError):
                pass
    return fallback


def find_biggest_pack(
    loose_ammo_tpl: str,
    templates: dict[str, dict[str, Any]],
    names: dict[str, str],
    manual_pack_tpl: str | None,
    manual_pack_size: int | None,
) -> dict[str, Any]:
    if manual_pack_tpl:
        pack_name = resolve_name(manual_pack_tpl, names)
        size = manual_pack_size or parse_pack_size_from_name(pack_name) or 1
        return {"pack_tpl": manual_pack_tpl, "pack_name": pack_name, "pack_size": int(size), "source": "manual"}

    loose_name = resolve_name(loose_ammo_tpl, names)
    loose_base = normalize_ammo_pack_match_name(loose_name)
    candidates: list[dict[str, Any]] = []

    # Built-ins first, but db scan can beat them if it finds a bigger one.
    for candidate in BUILT_IN_AMMO_PACKS.get(loose_ammo_tpl, []):
        candidates.append({**candidate, "source": "built-in"})

    for tpl, template in templates.items():
        if tpl == loose_ammo_tpl:
            continue

        item_name = resolve_name(tpl, names)
        pack_size_from_slot = extract_stack_slot_pack_size(template, loose_ammo_tpl)
        pack_size_from_name = parse_pack_size_from_name(item_name)
        name_says_pack = "ammo pack" in item_name.lower()

        matched_by_slot = pack_size_from_slot is not None
        matched_by_name = False
        if name_says_pack and loose_base and not item_name.startswith("UNKNOWN_ITEM_"):
            pack_base = normalize_ammo_pack_match_name(item_name)
            matched_by_name = pack_base == loose_base or pack_base.startswith(loose_base) or loose_base.startswith(pack_base)

        if not matched_by_slot and not matched_by_name:
            continue

        pack_size = pack_size_from_slot or pack_size_from_name or 1
        candidates.append({
            "pack_tpl": tpl,
            "pack_name": item_name,
            "pack_size": int(pack_size),
            "source": "db-stackslot" if matched_by_slot else "db-name",
        })

    if not candidates:
        raise RuntimeError(
            f"No ammo pack found for loose ammo {loose_ammo_tpl} ({loose_name}). "
            "Pass --pack-tpl and --pack-size, or add --items-db/--locale paths so the script can scan the SPT database."
        )

    candidates.sort(key=lambda row: (int(row.get("pack_size", 0)), str(row.get("pack_name", "")).lower()), reverse=True)
    return candidates[0]


def get_item_id(item: dict[str, Any]) -> str:
    return str(get_ci(item, "_id", "Id", "id", default="") or "")


def get_item_tpl(item: dict[str, Any]) -> str:
    return str(get_ci(item, "_tpl", "Tpl", "Template", "TemplateId", default="") or "")


def get_parent_id(item: dict[str, Any]) -> str:
    return str(get_ci(item, "parentId", "ParentId", "parent_id", default="") or "")


def get_slot_id(item: dict[str, Any]) -> str:
    return str(get_ci(item, "slotId", "SlotId", "slot_id", default="") or "")


def find_root_offer_by_tpl(items: list[Any], tpl: str) -> dict[str, Any] | None:
    for item in items:
        if not isinstance(item, dict):
            continue
        if get_parent_id(item) == "hideout" and get_slot_id(item) in ("", "hideout") and get_item_tpl(item) == tpl:
            return item
    return None


def deterministic_offer_id(existing_ids: set[str], seed: str) -> str:
    for salt in range(1000):
        suffix = "" if salt == 0 else f":{salt}"
        candidate = hashlib.sha1(f"tony-ammo-offer:{seed}{suffix}".encode("utf-8")).hexdigest()[:24]
        if candidate not in existing_ids:
            return candidate
    raise RuntimeError(f"Could not generate a unique offer id for {seed}")


def build_root_offer(offer_id: str, tpl: str, stock: int, unlimited: bool, buy_limit: int | None) -> dict[str, Any]:
    upd: dict[str, Any] = {
        "StackObjectsCount": max(1, int(stock)),
        "UnlimitedCount": bool(unlimited),
    }
    if buy_limit is not None and int(buy_limit) > 0:
        upd["BuyRestrictionMax"] = int(buy_limit)
        upd["BuyRestrictionCurrent"] = 0
    return {
        "_id": offer_id,
        "_tpl": tpl,
        "parentId": "hideout",
        "slotId": "hideout",
        "upd": upd,
    }


def make_cash_scheme(price: float, currency: str) -> list[list[dict[str, Any]]]:
    currency_code = (currency or "RUB").upper()
    currency_tpl = TPL_BY_CURRENCY.get(currency_code, RUB_TPL)
    return [[{"count": clean_number(round(float(price or 0))), "_tpl": currency_tpl}]]


def add_or_update_offer(
    assort: dict[str, Any],
    tpl: str,
    price: float,
    loyalty_level: int,
    currency: str,
    stock: int,
    unlimited: bool,
    buy_limit: int | None,
    update_existing: bool,
    seed: str,
) -> tuple[str, str]:
    items = ensure_assort_section(assort, "items", "Items", default_value=[])
    barter_scheme = ensure_assort_section(assort, "barter_scheme", "BarterScheme", default_value={})
    loyal_level_items = ensure_assort_section(assort, "loyal_level_items", "LoyalLevelItems", "loyalLevelItems", default_value={})

    if not isinstance(items, list):
        raise ValueError("assort items section is not a list")
    if not isinstance(barter_scheme, dict):
        raise ValueError("assort barter_scheme section is not an object")
    if not isinstance(loyal_level_items, dict):
        raise ValueError("assort loyal_level_items section is not an object")

    existing_offer = find_root_offer_by_tpl(items, tpl)
    if existing_offer is not None:
        offer_id = get_item_id(existing_offer)
        if update_existing:
            existing_offer["upd"] = build_root_offer(offer_id, tpl, stock, unlimited, buy_limit)["upd"]
            barter_scheme[offer_id] = make_cash_scheme(price, currency)
            loyal_level_items[offer_id] = int(loyalty_level)
            return offer_id, "updated existing"
        return offer_id, "already existed; skipped update"

    existing_ids = {get_item_id(item) for item in items if isinstance(item, dict) and get_item_id(item)}
    offer_id = deterministic_offer_id(existing_ids, seed)
    items.append(build_root_offer(offer_id, tpl, stock, unlimited, buy_limit))
    barter_scheme[offer_id] = make_cash_scheme(price, currency)
    loyal_level_items[offer_id] = int(loyalty_level)
    return offer_id, "added"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=f"Add loose ammo and its biggest pack to trader assort.json v{SCRIPT_VERSION}")
    parser.add_argument("--version", action="version", version=f"add_loose_ammo_and_biggest_pack_to_assort.py {SCRIPT_VERSION}")
    parser.add_argument("--ammo-tpl", required=True, help="Loose ammo template ID to add")
    parser.add_argument("--pack-tpl", default=None, help="Optional ammo pack template ID override")
    parser.add_argument("--pack-size", type=int, default=None, help="Optional pack size override, required if the pack name does not include (N pcs)")
    parser.add_argument("--assort", default="data/assort.json", help="Trader assort.json path")
    parser.add_argument("--out", default=None, help="Output assort.json path. Defaults to overwrite --assort")
    parser.add_argument("--db", action="append", default=[], help="Folder/file to scan for custom item names/templates. Can be repeated")
    parser.add_argument("--items-db", action="append", default=[], help="SPT templates/items.json path. Can be repeated")
    parser.add_argument("--locale", action="append", default=[], help="Locale JSON path/folder for item names. Can be repeated")
    parser.add_argument("--price-catalog", default="config/items.json", help="Optional config/items.json price catalog. Use empty string to disable")
    parser.add_argument("--loose-price", type=float, default=None, help="Override loose ammo cash price")
    parser.add_argument("--pack-price", type=float, default=None, help="Override ammo pack cash price")
    parser.add_argument("--fallback-loose-price", type=float, default=1000.0, help="Fallback loose ammo price if not found in db/catalog")
    parser.add_argument("--currency", choices=["RUB", "USD", "EUR"], default="RUB")
    parser.add_argument("--loyalty-level", type=int, default=1)
    parser.add_argument("--stock", type=int, default=999, help="StackObjectsCount to write. Default is 999, not 100")
    parser.add_argument("--pack-stock", type=int, default=None, help="Pack StackObjectsCount. Defaults to --stock")
    parser.add_argument("--buy-limit", type=int, default=None, help="Optional BuyRestrictionMax for loose ammo")
    parser.add_argument("--pack-buy-limit", type=int, default=None, help="Optional BuyRestrictionMax for pack offer")
    parser.add_argument("--unlimited", action="store_true", help="Set UnlimitedCount true")
    parser.add_argument("--update-existing", action="store_true", help="Update price/loyalty/stock if the offer already exists")
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    ammo_tpl = str(args.ammo_tpl).strip()
    if not maybe_tpl(ammo_tpl):
        raise ValueError(f"--ammo-tpl does not look like a 24-character template ID: {ammo_tpl}")

    if args.pack_tpl and not maybe_tpl(args.pack_tpl):
        raise ValueError(f"--pack-tpl does not look like a 24-character template ID: {args.pack_tpl}")

    assort_path = Path(args.assort)
    out_path = Path(args.out) if args.out else assort_path
    if not assort_path.exists():
        raise FileNotFoundError(f"assort file not found: {assort_path}")

    db_paths = [Path(p) for p in (args.db or [])]
    item_db_paths = [Path(p) for p in (args.items_db or [])]
    locale_paths = [Path(p) for p in (args.locale or [])]
    scan_paths = db_paths + item_db_paths + locale_paths

    # Also scan common local mod files when they exist.
    for default_path in (Path("db"), Path("data"), Path("config/custom_ammo_packs.json")):
        if default_path.exists() and default_path not in scan_paths:
            scan_paths.append(default_path)

    templates, names, warnings = load_templates_and_names(scan_paths)
    catalog_path = Path(args.price_catalog) if args.price_catalog else None
    catalog_prices = load_price_catalog(catalog_path)

    pack_info = find_biggest_pack(
        loose_ammo_tpl=ammo_tpl,
        templates=templates,
        names=names,
        manual_pack_tpl=args.pack_tpl,
        manual_pack_size=args.pack_size,
    )

    loose_name = resolve_name(ammo_tpl, names)
    pack_tpl = str(pack_info["pack_tpl"])
    pack_name = str(pack_info.get("pack_name") or resolve_name(pack_tpl, names))
    pack_size = max(1, int(pack_info.get("pack_size") or args.pack_size or 1))

    loose_price = float(args.loose_price) if args.loose_price is not None else get_template_price(
        ammo_tpl,
        templates,
        catalog_prices,
        fallback=float(args.fallback_loose_price),
    )
    pack_price = float(args.pack_price) if args.pack_price is not None else loose_price * pack_size

    assort = load_json(assort_path)
    if not isinstance(assort, dict):
        raise ValueError("assort file must be a JSON object")

    loose_offer_id, loose_status = add_or_update_offer(
        assort=assort,
        tpl=ammo_tpl,
        price=loose_price,
        loyalty_level=max(1, int(args.loyalty_level)),
        currency=args.currency,
        stock=max(1, int(args.stock)),
        unlimited=bool(args.unlimited),
        buy_limit=args.buy_limit,
        update_existing=bool(args.update_existing),
        seed=f"loose:{ammo_tpl}",
    )

    pack_offer_id, pack_status = add_or_update_offer(
        assort=assort,
        tpl=pack_tpl,
        price=pack_price,
        loyalty_level=max(1, int(args.loyalty_level)),
        currency=args.currency,
        stock=max(1, int(args.pack_stock if args.pack_stock is not None else args.stock)),
        unlimited=bool(args.unlimited),
        buy_limit=args.pack_buy_limit,
        update_existing=bool(args.update_existing),
        seed=f"pack:{ammo_tpl}:{pack_tpl}",
    )

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(assort, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")

    print("ammo assortment add report")
    print("==========================")
    print(f"Assort: {out_path}")
    print(f"Loose ammo: {loose_name} / {ammo_tpl}")
    print(f"Loose offer: {loose_offer_id} ({loose_status})")
    print(f"Loose price: {clean_number(loose_price)} {args.currency}")
    print(f"Pack: {pack_name} / {pack_tpl}")
    print(f"Pack size: {pack_size} rounds")
    print(f"Pack source: {pack_info.get('source', 'unknown')}")
    print(f"Pack offer: {pack_offer_id} ({pack_status})")
    print(f"Pack price: {clean_number(pack_price)} {args.currency}")
    print(f"Loyalty level: {max(1, int(args.loyalty_level))}")
    print(f"Stock: loose={max(1, int(args.stock))}, pack={max(1, int(args.pack_stock if args.pack_stock is not None else args.stock))}")
    if warnings:
        print("Warnings:")
        for warning in warnings:
            print(f"  - {warning}")
    print("\nNext step: run generate_items_from_assort.py again if you want config/items.json refreshed from assort.json.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
