#!/usr/bin/env python3
"""
Generate Tony config/items.json from a trader data/assort.json file.

This version can use the tarkov.dev GraphQL API to resolve readable item names
from template IDs, then falls back to locale/catalog/built-in/raw ID names.

Outputs the barter-aware schema used by PriceConfigItem.cs:
[
  {
    "OfferId": "root assort item id",
    "TplId": "sold item tpl",
    "ItemName": "readable name if available",
    "Price": 42000,
    "Currency": "RUB",
    "CashOnly": true,
    "BarterScheme": [[{"TplId": "5449016a4bdc2d6f028b456f", "ItemName": "Roubles", "Count": 42000}]],
    "AlwaysBarter": false,
    "AlwaysInStock": false,
    "AmmoBarterPackTplId": "57372e73245977685d4159b4",
    "PackOfferId": "root ammo pack offer id when found"
  }
]

Basic usage from your mod root:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json

Use tarkov.dev names (default):
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --tarkov-dev

Offline only / do not call tarkov.dev:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --no-tarkov-dev

Optional readable names / fallback prices:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --catalog config/items.old.json
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --locale path/to/en.json

Load custom item/locales names from your mod db/data folders so custom templates do not become UNKNOWN_ITEM_*:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --db db --db data

Generate missing real barter recipes for cash-only rows, using Price as the target value:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --catalog config/items.json --generate-barter-schemes cash-only

Fill only empty/missing non-currency barter recipes without touching existing real barters:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --catalog config/items.json --fill-empty-barters

Ammo rows generate a real item BarterScheme valued as a whole ammo pack and, when possible,
write the matching ammo pack template so the runtime loader can sell the pack when the offer rolls barter:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --catalog config/items.json --generate-barter-schemes cash-only --ammo-barter-pack-size 30

Force every row to cash-only while still preserving the original/generated barter recipe in BarterScheme:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --cash-only --catalog config/items.old.json

Keep your current config/items.json tuning and only fill rows/settings that are missing:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --keep-current-settings --generate-barter-schemes cash-only

Overwrite/regenerate every field except existing BarterScheme values:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --overwrite-except-barter --current-settings config/items.json

Limit repeated barter ingredients so one item does not dominate generated recipes:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --generate-barter-schemes cash-only --barter-max-uses-per-item 5

When an ammo row has an AmmoBarterPackTplId target but no matching pack root offer
in assort.json, the script now creates the missing pack offer in assort.json, adds
barter_scheme and loyal_level_items entries, and writes PackOfferId into items.json.
Use --assort-out to write the updated assort somewhere else, or --no-update-assort
to keep the old warning-only behavior.

Custom ammo helpers:
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --custom-ammo-tpl <looseTpl>
  python tools/generate_items_from_assort.py --assort data/assort.json --out config/items.json --ammo-pack-map config/custom_ammo_packs.json

Custom ammo packs can also be auto-detected from local db/data item JSON. If a pack item
has StackSlots -> cartridges -> _props.filters[].Filter, the Filter value is treated as
the loose ammo tpl to link to that pack item. The containing pack tpl, or the slot _parent
when present, is used as AmmoBarterPackTplId, and _max_count is used as pack size.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import random
import re
import time
import urllib.error
import urllib.request

SCRIPT_VERSION = "2.13.6"
from collections import Counter
from pathlib import Path
from typing import Any

RUB_TPL = "5449016a4bdc2d6f028b456f"
USD_TPL = "5696686a4bdc2da3298b456a"
EUR_TPL = "569668774bdc2da2298b4568"

TARKOV_DEV_GRAPHQL_URL = "https://api.tarkov.dev/graphql"
TARKOV_DEV_CACHE_MAX_AGE_SECONDS = 7 * 24 * 60 * 60

CURRENCY_BY_TPL = {
    RUB_TPL: "RUB",
    USD_TPL: "USD",
    EUR_TPL: "EUR",
}

CURRENCY_NAME_BY_TPL = {
    RUB_TPL: "Roubles",
    USD_TPL: "Dollars",
    EUR_TPL: "Euros",
}

TPL_BY_CURRENCY = {value: key for key, value in CURRENCY_BY_TPL.items()}

# Loaded at runtime from --db folders/files. This prevents custom templates from
# falling back to UNKNOWN_ITEM_<tpl> when their names are stored in db/customItems,
# db/customLocales, data/customItems, or similar local JSON files.
CUSTOM_DB_NAMES: dict[str, str] = {}

# items.json row flag support. Toolset should always stay barter and still count
# toward the random barter target. Every other row is generated with false.
ALWAYS_BARTER_TPL_IDS: set[str] = {
    "590c2e1186f77425357b6124",  # Toolset
}

# These rows should never be zeroed by RandomizeStockAvailable.
# They are generated with AlwaysInStock=true so the stock randomizer skips them.
ALWAYS_IN_STOCK_TPL_IDS: set[str] = {
    "5b4391a586f7745321235ab2",  # WI-FI Camera
    "5991b51486f77447b112d44f",  # MS2000 Marker
}

# Small built-in name map for common Tony assortment entries. Names are only for readability;
# TplId, OfferId, Price, Currency, CashOnly, and BarterScheme are what the mod actually uses.
BUILT_IN_NAMES = {
    "5449016a4bdc2d6f028b456f": "Roubles",
    "5696686a4bdc2da3298b456a": "Dollars",
    "569668774bdc2da2298b4568": "Euros",
    "56d59d3ad2720bdb418b4577": "9x19mm Pst gzh",
    "56dff4a2d2720bbd668b456a": "5.45x39mm T gs",
    "56dff3afd2720bba668b4567": "5.45x39mm PS gs",
    "5656d7c34bdc2d9d198b4587": "7.62x39mm PS gzh",
    "59e6542b86f77411dc52a77a": ".366 TKM FMJ",
    "560d5e524bdc2d25448b4571": "12/70 7mm buckshot",
    "5448bd6b4bdc2dfc2f8b4569": "Makarov PM 9x18PM pistol",
    "57dc2fa62459775949412633": "Kalashnikov AKS-74U 5.45x39 assault rifle",
    "54491c4f4bdc2db1078b4568": "MP-133 12ga pump-action shotgun",
    "60339954d62c9b14ed777c06": "Soyuz-TM STM-9 Gen.2 9x19 carbine",
    "5e4abfed86f77406a2713cf7": "Splav Tarzan M22 chest rig (Smog)",
    "5648a7494bdc2d9d488b4583": "PACA Soft Armor",
    "5c0e5bab86f77461f55ed1f3": "6B23-1 body armor (EMR)",
    "5c06c6a80db834001b735491": "SSh-68 steel helmet (Olive Drab)",
    "544fb25a4bdc2dfb738b4567": "Aseptic bandage",
    "5755356824597772cb798962": "AI-2 medkit",
    "544fb3364bdc2d34748b456a": "Immobilizing splint",
    "544fb37f4bdc2dee738b4567": "Analgin painkillers",
    "590c2e1186f77425357b6124": "Toolset",
    "56dff421d2720b5f5a8b4567": "5.45x39mm SP",
    "56dfef82d2720bbd668b4567": "5.45x39mm BP gs",
    "56dff061d2720bb5668b4567": "5.45x39mm BT gs",
    "59e4cf5286f7741778269d8a": "7.62x39mm T-45M1 gzh",
    "57a0dfb82459774d3078b56c": "9x39mm SP-5 gs",
    "5644bd2b4bdc2d3b4c8b4572": "Kalashnikov AK-74N 5.45x39 assault rifle",
    "5ac66d2e5acfc43b321d4b53": "Kalashnikov AK-103 7.62x39 assault rifle",
    "59d6088586f774275f37482f": "Kalashnikov AKM 7.62x39 assault rifle",
    "5ac66d9b5acfc4001633997a": "Kalashnikov AK-105 5.45x39 assault rifle",
    "59984ab886f7743e98271174": "PP-19-01 Vityaz 9x19 submachine gun",
    "59e7643b86f7742cbf2c109a": "WARTECH TV-109 + TV-106 chest rig (A-TACS FG)",
    "5d5d646386f7742797261fd9": "6B3TM-01 armored rig (Khaki)",
    "5a7c4850e899ef00150be885": "6B47 Ratnik-BSh helmet (Olive Drab)",
    "5aa7cfc0e5b5b00015693143": "6B47 Ratnik-BSh helmet (Digital Flora cover)",
    "544fb45d4bdc2dee738b4568": "Salewa first aid kit",
    "590c661e86f7741e566b646a": "Car first aid kit",
    "60098af40accd37ef2175f27": "CAT hemostatic tourniquet",
    "5e831507ea0a7c419c2f9bd9": "Esmarch tourniquet",
    "5e8488fa988a8701445df1e4": "CALOK-B hemostatic applicator",
    "5d02778e86f774203e7dedbe": "CMS surgical kit",
    "5991b51486f77447b112d44f": "MS2000 Marker",
    "57838ad32459774a17445cd2": "VSS Vintorez 9x39 special sniper rifle",
    "59fb023c86f7746d0d4b423c": "Weapon case",
    "59fb042886f7746c5005a7b2": "Item case",
}

# Extra embedded fallbacks for Tony's curated assortment. The tarkov.dev lookup is still preferred;
# these only prevent UNKNOWN_ITEM_* names when running fully offline or when the API schema/network fails.
BUILT_IN_NAMES.update({
    "57372140245977611f70ee91": "9x18mm PM SP7 gzh",
    "56d59d3ad2720bdb418b4577": "9x19mm Pst gzh",
    "56dff4a2d2720bbd668b456a": "5.45x39mm T gs",
    "56dff3afd2720bba668b4567": "5.45x39mm PS gs",
    "5656d7c34bdc2d9d198b4587": "7.62x39mm PS gzh",
    "59e6542b86f77411dc52a77a": ".366 TKM FMJ",
    "59e655cb86f77411dc52a77b": ".366 TKM EKO",
    "560d5e524bdc2d25448b4571": "12/70 7mm buckshot",
    "5448bd6b4bdc2dfc2f8b4569": "Makarov PM 9x18PM pistol",
    "57dc2fa62459775949412633": "Kalashnikov AKS-74U 5.45x39 assault rifle",
    "59e6152586f77473dc057aa1": "Molot Arms VPO-136 Vepr-KM 7.62x39 carbine",
    "59e6687d86f77411d949b251": "Molot Arms VPO-209 .366 TKM carbine",
    "54491c4f4bdc2db1078b4568": "MP-133 12ga pump-action shotgun",
    "60339954d62c9b14ed777c06": "Soyuz-TM STM-9 Gen.2 9x19 carbine",
    "5e4abfed86f77406a2713cf7": "Splav Tarzan M22 chest rig (Smog)",
    "5648a69d4bdc2ded0b8b457b": "BlackRock chest rig (Gray)",
    "5648a7494bdc2d9d488b4583": "PACA Soft Armor",
    "5c0e5bab86f77461f55ed1f3": "6B23-1 body armor (EMR)",
    "5c06c6a80db834001b735491": "SSh-68 steel helmet (Olive Drab)",
    "544fb25a4bdc2dfb738b4567": "Aseptic bandage",
    "5751a25924597722c463c472": "Army bandage",
    "5755356824597772cb798962": "AI-2 medkit",
    "544fb3364bdc2d34748b456a": "Immobilizing splint",
    "544fb37f4bdc2dee738b4567": "Analgin painkillers",
    "590c2e1186f77425357b6124": "Toolset",
    "56dff421d2720b5f5a8b4567": "5.45x39mm SP",
    "56dfef82d2720bbd668b4567": "5.45x39mm BP gs",
    "56dff061d2720bb5668b4567": "5.45x39mm BT gs",
    "59e4cf5286f7741778269d8a": "7.62x39mm T-45M1 gzh",
    "5c925fa22e221601da359b7b": "9x19mm AP 6.3",
    "57a0dfb82459774d3078b56c": "9x39mm SP-5 gs",
    "5d6e6911a4b9361bd5780d52": "12/70 flechette",
    "5644bd2b4bdc2d3b4c8b4572": "Kalashnikov AK-74N 5.45x39 assault rifle",
    "5ac66d2e5acfc43b321d4b53": "Kalashnikov AK-103 7.62x39 assault rifle",
    "59d6088586f774275f37482f": "Kalashnikov AKM 7.62x39 assault rifle",
    "5ac66d9b5acfc4001633997a": "Kalashnikov AK-105 5.45x39 assault rifle",
    "59984ab886f7743e98271174": "PP-19-01 Vityaz 9x19 submachine gun",
    "576165642459773c7a400233": "Saiga-12K ver.10 12ga semi-automatic shotgun",
    "59e7643b86f7742cbf2c109a": "WARTECH TV-109 + TV-106 chest rig (A-TACS FG)",
    "5d5d646386f7742797261fd9": "6B3TM-01 armored rig (Khaki)",
    "5a7c4850e899ef00150be885": "6B47 Ratnik-BSh helmet (Olive Drab)",
    "5aa7cfc0e5b5b00015693143": "6B47 Ratnik-BSh helmet (Digital Flora cover)",
    "544fb45d4bdc2dee738b4568": "Salewa first aid kit",
    "590c661e86f7741e566b646a": "Car first aid kit",
    "60098af40accd37ef2175f27": "CAT hemostatic tourniquet",
    "5e831507ea0a7c419c2f9bd9": "Esmarch tourniquet",
    "5e8488fa988a8701445df1e4": "CALOK-B hemostatic applicator",
    "5d02778e86f774203e7dedbe": "CMS surgical kit",
    "5910968f86f77425cf569c32": "Weapon repair kit",
    "5991b51486f77447b112d44f": "MS2000 Marker",
    "56dff026d2720bb8668b4567": "5.45x39mm BS gs",
    "59e0d99486f7744a32234762": "7.62x39mm BP gzh",
    "5efb0da7a29a85116f6ea05f": "9x19mm PBP gzh",
    "57a0e5022459774d1673f889": "9x39mm SP-6 gs",
    "5c0d668f86f7747ccb7f13b2": "9x39mm SPP gs",
    "5f0596629e22f464da6bbdd9": ".366 TKM AP-M",
    "5d6e68a8a4b9360b6c0d54e2": "12/70 AP-20 armor-piercing slug",
    "5beed0f50db834001c062b12": "RPK-16 5.45x39 light machine gun",
    "57c44b372459772d2b39b8ce": "AS VAL 9x39 special assault rifle",
    "57838ad32459774a17445cd2": "VSS Vintorez 9x39 special sniper rifle",
    "5c46fbd72e2216398b5a8c9c": "SVDS 7.62x54R sniper rifle",
    "5ab8e79e86f7742d8b372e78": "BNTI Gzhel-K body armor",
    "5aafbde786f774389d0cbc0f": "Ammunition case",
    "590c60fc86f77412b13fddcf": "Documents case",
    "59fafd4b86f7745ca07e1232": "Key tool",
    "5c0d5e4486f77478390952fe": "5.45x39mm PPBS gs Igolnik",
    "61962d879bb3d20b0946d385": "9x39mm PAB-9 gs",
    "601aa3d2b2bcb34913271e6d": "7.62x39mm MAI AP",
    "5c0d688c86f77413ae3407b2": "9x39mm BP gs",
    "628a60ae6b1d481ff772e9c8": "Rifle Dynamics RD-704 7.62x39 assault rifle",
    "5c0e625a86f7742d77340f62": "BNTI Zhuk body armor (EMR)",
    "59fb023c86f7746d0d4b423c": "Weapon case",
    "59fb042886f7746c5005a7b2": "Item case",
    "5c0a840b86f7742ffa4f2482": "T H I C C item case",
    "5a1eaa87fcdbcb001865f75e": "Trijicon REAP-IR thermal scope",
    "5d1b5e94d7ad1a2b865a96b0": "FLIR RS-32 2.25-9x 35mm 60Hz thermal riflescope",
    "5c0d56a986f774449d5de529": "9x39mm SPP gs",
    "5d02797c86f774203f38e30a": "Surv12 field surgical kit"
})

# Built-in cash fallback prices for Tony barter-only offers when no catalog/API price exists.
BUILT_IN_PRICES = {
    "57372140245977611f70ee91": 950,
    "56d59d3ad2720bdb418b4577": 1050,
    "56dff4a2d2720bbd668b456a": 950,
    "56dff3afd2720bba668b4567": 1250,
    "5656d7c34bdc2d9d198b4587": 1600,
    "59e6542b86f77411dc52a77a": 850,
    "59e655cb86f77411dc52a77b": 1150,
    "560d5e524bdc2d25448b4571": 900,
    "5448bd6b4bdc2dfc2f8b4569": 27500,
    "57dc2fa62459775949412633": 56500,
    "59e6152586f77473dc057aa1": 82500,
    "59e6687d86f77411d949b251": 89500,
    "54491c4f4bdc2db1078b4568": 36500,
    "60339954d62c9b14ed777c06": 84500,
    "5e4abfed86f77406a2713cf7": 10500,
    "5648a69d4bdc2ded0b8b457b": 24000,
    "5648a7494bdc2d9d488b4583": 16000,
    "5c0e5bab86f77461f55ed1f3": 42000,
    "5c06c6a80db834001b735491": 22000,
    "544fb25a4bdc2dfb738b4567": 2400,
    "5751a25924597722c463c472": 3600,
    "5755356824597772cb798962": 4500,
    "544fb3364bdc2d34748b456a": 2600,
    "544fb37f4bdc2dee738b4567": 6200,
    "590c2e1186f77425357b6124": 75000,
    "56dff421d2720b5f5a8b4567": 1700,
    "56dfef82d2720bbd668b4567": 2400,
    "56dff061d2720bb5668b4567": 3200,
    "59e4cf5286f7741778269d8a": 1700,
    "5c925fa22e221601da359b7b": 4100,
    "57a0dfb82459774d3078b56c": 2600,
    "5d6e6911a4b9361bd5780d52": 3500,
    "5644bd2b4bdc2d3b4c8b4572": 42000,
    "5ac66d2e5acfc43b321d4b53": 50000,
    "59d6088586f774275f37482f": 56000,
    "5ac66d9b5acfc4001633997a": 64000,
    "59984ab886f7743e98271174": 42000,
    "576165642459773c7a400233": 43000,
    "59e7643b86f7742cbf2c109a": 39000,
    "5d5d646386f7742797261fd9": 52000,
    "5a7c4850e899ef00150be885": 54000,
    "5aa7cfc0e5b5b00015693143": 59000,
    "544fb45d4bdc2dee738b4568": 17500,
    "590c661e86f7741e566b646a": 11000,
    "60098af40accd37ef2175f27": 5200,
    "5e831507ea0a7c419c2f9bd9": 5200,
    "5e8488fa988a8701445df1e4": 11200,
    "5d02778e86f774203e7dedbe": 38000,
    "5910968f86f77425cf569c32": 210000,
    "5991b51486f77447b112d44f": 260000,
    "56dff026d2720bb8668b4567": 6200,
    "59e0d99486f7744a32234762": 7500,
    "5efb0da7a29a85116f6ea05f": 6400,
    "57a0e5022459774d1673f889": 5200,
    "5c0d668f86f7747ccb7f13b2": 8500,
    "5f0596629e22f464da6bbdd9": 6800,
    "5d6e68a8a4b9360b6c0d54e2": 6200,
    "5beed0f50db834001c062b12": 95000,
    "57c44b372459772d2b39b8ce": 105000,
    "57838ad32459774a17445cd2": 118000,
    "5c46fbd72e2216398b5a8c9c": 128000,
    "5ab8e79e86f7742d8b372e78": 170000,
    "5aafbde786f774389d0cbc0f": 120000,
    "590c60fc86f77412b13fddcf": 90000,
    "59fafd4b86f7745ca07e1232": 160000,
    "5c0d5e4486f77478390952fe": 11200,
    "61962d879bb3d20b0946d385": 13200,
    "601aa3d2b2bcb34913271e6d": 15500,
    "5c0d688c86f77413ae3407b2": 15500,
    "628a60ae6b1d481ff772e9c8": 165000,
    "5c0e625a86f7742d77340f62": 90000,
    "59fb023c86f7746d0d4b423c": 250000,
    "59fb042886f7746c5005a7b2": 400000,
    "5c0a840b86f7742ffa4f2482": 4500000,
    "5a1eaa87fcdbcb001865f75e": 400000,
    "5d1b5e94d7ad1a2b865a96b0": 170000
}


# Curated fallback barter pool. Values are tuning numbers used only by this generator;
# they are not pulled from the live SPT database. Keep them close to Tony's theme:
# tools, weapon parts, alcohol, cigarettes, valuables, meds, and useful junk.
BARTER_ITEM_POOL: list[dict[str, Any]] = [
    {"TplId": "57347b8b24597737dd42e192", "ItemName": "Classic matches", "Value": 2500, "Tags": ["cheap", "utility", "food", "generic"], "MaxCount": 6},
    {"TplId": "5e2af2bc86f7746d3f3c33fc", "ItemName": "Hunting matches", "Value": 6500, "Tags": ["cheap", "utility", "weapon", "generic"], "MaxCount": 5},
    {"TplId": "57347c1124597737fb1379e3", "ItemName": "Duct tape", "Value": 7000, "Tags": ["tool", "weapon", "armor", "generic"], "MaxCount": 6},
    {"TplId": "5734795124597738002c6176", "ItemName": "Insulating tape", "Value": 4500, "Tags": ["tool", "weapon", "cheap", "generic"], "MaxCount": 6},
    {"TplId": "5e2af29386f7746d4159f077", "ItemName": "KEKTAPE duct tape", "Value": 13000, "Tags": ["tool", "weapon", "armor", "generic"], "MaxCount": 5},
    {"TplId": "590c2d8786f774245b1f03f3", "ItemName": "Screwdriver", "Value": 9000, "Tags": ["tool", "weapon", "generic"], "MaxCount": 5},
    {"TplId": "590c2b4386f77425357b6123", "ItemName": "Pliers", "Value": 11000, "Tags": ["tool", "weapon", "armor", "generic"], "MaxCount": 5},
    {"TplId": "590c31c586f774245e3141b2", "ItemName": "Pack of nails", "Value": 15000, "Tags": ["tool", "armor", "case", "generic"], "MaxCount": 5},
    {"TplId": "5d1c819a86f774771b0acd6c", "ItemName": "Weapon parts", "Value": 22000, "Tags": ["weapon", "suppressor", "case", "generic"], "MaxCount": 12},
    {"TplId": "5d6fc78386f77449d825f9dc", "ItemName": "Gunpowder \"Eagle\"", "Value": 35000, "Tags": ["ammo", "weapon", "case"], "MaxCount": 8},
    {"TplId": "5d0375ff86f774186372f685", "ItemName": "Military cable", "Value": 45000, "Tags": ["weapon", "case", "tech", "generic"], "MaxCount": 6},
    {"TplId": "5d1b2fa286f77425227d1674", "ItemName": "Electric motor", "Value": 65000, "Tags": ["tool", "case", "tech", "generic"], "MaxCount": 6},
    {"TplId": "5d03775b86f774203e7e0c4b", "ItemName": "Phased array element", "Value": 150000, "Tags": ["tech", "thermal", "case", "armor"], "MaxCount": 6},
    {"TplId": "5e2aedd986f7746d404f3aa4", "ItemName": "GreenBat lithium battery", "Value": 70000, "Tags": ["tech", "case", "generic"], "MaxCount": 6},
    {"TplId": "5d40407c86f774318526545a", "ItemName": "Bottle of Tarkovskaya vodka", "Value": 25000, "Tags": ["alcohol", "armor", "weapon", "generic"], "MaxCount": 8},
    {"TplId": "5d403f9186f7743cac3f229b", "ItemName": "Bottle of Dan Jackiel whiskey", "Value": 45000, "Tags": ["alcohol", "armor", "weapon", "generic"], "MaxCount": 6},
    {"TplId": "5d1b376e86f774252519444e", "ItemName": "Bottle of Fierce Hatchling moonshine", "Value": 220000, "Tags": ["alcohol", "valuable", "case", "thermal", "armor"], "MaxCount": 8},
    {"TplId": "5734758f24597738025ee253", "ItemName": "Golden neck chain", "Value": 35000, "Tags": ["valuable", "armor", "case", "generic"], "MaxCount": 10},
    {"TplId": "59faf7ca86f7740dbe19f6c2", "ItemName": "Roler Submariner gold wrist watch", "Value": 90000, "Tags": ["valuable", "armor", "case", "generic"], "MaxCount": 8},
    {"TplId": "5d235a5986f77443f6329bc6", "ItemName": "Gold skull ring", "Value": 65000, "Tags": ["valuable", "armor", "case", "helmet", "generic"], "MaxCount": 8},
    {"TplId": "5c1267ee86f77416ec610f72", "ItemName": "Chain with Prokill medallion", "Value": 85000, "Tags": ["valuable", "medical", "injector", "generic"], "MaxCount": 6},
    {"TplId": "590de71386f774347051a052", "ItemName": "Antique teapot", "Value": 55000, "Tags": ["valuable", "case", "generic"], "MaxCount": 6},
    {"TplId": "590c651286f7741e566b6461", "ItemName": "Slim diary", "Value": 25000, "Tags": ["intel", "case", "generic"], "MaxCount": 6},
    {"TplId": "5c12613b86f7743bbe2c3f76", "ItemName": "Intelligence folder", "Value": 300000, "Tags": ["intel", "case", "thermal", "valuable"], "MaxCount": 8},
    {"TplId": "59faff1d86f7746c51718c9c", "ItemName": "Physical Bitcoin", "Value": 600000, "Tags": ["valuable", "case", "thermal"], "MaxCount": 10},
    {"TplId": "57347d7224597744596b4e72", "ItemName": "Can of beef stew (Small)", "Value": 14000, "Tags": ["food", "cheap", "generic"], "MaxCount": 6},
    {"TplId": "575062b524597720a31c09a1", "ItemName": "Can of Ice Green tea", "Value": 12000, "Tags": ["food", "cheap", "generic"], "MaxCount": 6},
    {"TplId": "5751435d24597720a27126d1", "ItemName": "Can of Max Energy energy drink", "Value": 13000, "Tags": ["food", "medical", "generic"], "MaxCount": 6},
    {"TplId": "62a0a043cf4a99369e2624a5", "ItemName": "Bottle of OLOLO Multivitamins", "Value": 18000, "Tags": ["medical", "injector", "generic"], "MaxCount": 6},
    {"TplId": "5d1b32c186f774252167a530", "ItemName": "Analog thermometer", "Value": 25000, "Tags": ["medical", "injector", "tech"], "MaxCount": 6},
    {"TplId": "590a3d9c86f774385926e510", "ItemName": "Ultraviolet lamp", "Value": 28000, "Tags": ["medical", "tech", "injector"], "MaxCount": 6},
    {"TplId": "5734770f24597738025ee254", "ItemName": "Strike Cigarettes", "Value": 8000, "Tags": ["cigarette", "medical", "cheap", "generic"], "MaxCount": 8},
    {"TplId": "573476d324597737da2adc13", "ItemName": "Malboro Cigarettes", "Value": 9000, "Tags": ["cigarette", "armor", "cheap", "generic"], "MaxCount": 8},
    {"TplId": "5672cb124bdc2d1a0f8b4568", "ItemName": "AA Battery", "Value": 8000, "Tags": ["tech", "tool", "cheap", "generic"], "MaxCount": 8},
    {"TplId": "5e2aef7986f7746d3f3c33f5", "ItemName": "Repellent", "Value": 17000, "Tags": ["medical", "food", "cheap", "generic"], "MaxCount": 6},
]



BARTER_POOL_NAME_BY_TPL = {str(component["TplId"]): str(component["ItemName"]) for component in BARTER_ITEM_POOL}
BARTER_POOL_VALUE_BY_TPL = {str(component["TplId"]): float(component["Value"]) for component in BARTER_ITEM_POOL}

# Known ammo templates from Tony's curated assort. Used only so generated ammo barters
# are priced as a pack of rounds instead of a single round. Generic name matching
# below catches future/custom ammo rows when their names start with a caliber.
KNOWN_AMMO_TPLS: set[str] = {
    "5f0596629e22f464da6bbdd9",  # .366 TKM AP-M
    "59e655cb86f77411dc52a77b",  # .366 TKM EKO
    "59e6542b86f77411dc52a77a",  # .366 TKM FMJ
    "560d5e524bdc2d25448b4571",  # 12/70 7mm buckshot
    "5d6e68a8a4b9360b6c0d54e2",  # 12/70 AP-20
    "5d6e6911a4b9361bd5780d52",  # 12/70 flechette
    "56dfef82d2720bbd668b4567",  # 5.45 BP
    "56dff026d2720bb8668b4567",  # 5.45 BS
    "56dff061d2720bb5668b4567",  # 5.45 BT
    "56dff2ced2720bb4668b4567",  # 5.45 PP
    "5c0d5e4486f77478390952fe",  # 5.45 PPBS Igolnik
    "56dff3afd2720bba668b4567",  # 5.45 PS
    "56dff421d2720b5f5a8b4567",  # 5.45 SP
    "59e0d99486f7744a32234762",  # 7.62 BP
    "601aa3d2b2bcb34913271e6d",  # 7.62 MAI AP
    "64b7af434b75259c590fa893",  # 7.62 PP
    "5656d7c34bdc2d9d198b4587",  # 7.62 PS
    "59e4cf5286f7741778269d8a",  # 7.62 T-45M1
    "57372140245977611f70ee91",  # 9x18 SP7
    "5c925fa22e221601da359b7b",  # 9x19 AP 6.3
    "5efb0da7a29a85116f6ea05f",  # 9x19 PBP
    "56d59d3ad2720bdb418b4577",  # 9x19 Pst
    "5c0d56a986f774449d5de529",  # 9x19 RIP
    "5c0d688c86f77413ae3407b2",  # 9x39 BP
    "61962d879bb3d20b0946d385",  # 9x39 PAB-9
    "57a0dfb82459774d3078b56c",  # 9x39 SP-5
    "57a0e5022459774d1673f889",  # 9x39 SP-6
    "5c0d668f86f7747ccb7f13b2",  # 9x39 SPP
    "5e023d48186a883be655e551",  # 7.62x54 BS
    "5887431f2459777e1612938f",  # 7.62x54 LPS
    "560d61e84bdc2da74d8b4571",  # 7.62x54 SNB
    "6a427a2e38a6d33bffe9829b",  # Tony/custom 7.62x54 LPS clone
    "6a427a2e38a6d33bffe98292",  # Tony/custom 7.62x54 BS clone
    "6a427a2e38a6d33bffe9829d",  # Tony/custom 7.62x54 SNB clone
}

AMMO_NAME_START_RE = re.compile(
    r"^(?:\.366|\.300|\.338|\.357|\.45|4\.6x30|5\.45x39|5\.56x45|7\.62x25|7\.62x39|7\.62x51|7\.62x54|9x18|9x19|9x21|9x39|12/70|20/70|12\.7x55)\b",
    re.IGNORECASE,
)

AMMO_PACK_SIZE_RE = re.compile(r"\((\d+)\s*pcs?\)", re.IGNORECASE)
AMMO_PACK_NAME_RE = re.compile(r"\s+(?:ammo\s+)?pack\s*\(\d+\s*pcs?\).*?$", re.IGNORECASE)

# Offline fallback pack templates for common Tony ammo rows. The generator still prefers
# tarkov.dev or locale/catalog name discovery when available. If a row is not here and no
# pack can be discovered, that ammo row will remain cash-only for runtime randomization.
BUILT_IN_AMMO_PACKS: dict[str, dict[str, Any]] = {
    # Tony fallback ammo pack targets. These keep offline generation working when
    # tarkov.dev/cache/locales are not available. The generator still prefers
    # discovered pack templates when it can find them by name.
    "5f0596629e22f464da6bbdd9": {"AmmoBarterPackTplId": "657023f81419851aef03e6f1", "AmmoBarterPackItemName": ".366 TKM AP-M ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "59e655cb86f77411dc52a77b": {"AmmoBarterPackTplId": "657024011419851aef03e6f4", "AmmoBarterPackItemName": ".366 TKM EKO ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "560d5e524bdc2d25448b4571": {"AmmoBarterPackTplId": "657024361419851aef03e6fa", "AmmoBarterPackItemName": "12/70 7mm buckshot ammo pack (25 pcs)", "AmmoBarterPackSize": 25},
    "5d6e68a8a4b9360b6c0d54e2": {"AmmoBarterPackTplId": "64898838d5b4df6140000a20", "AmmoBarterPackItemName": "12/70 AP-20 ammo pack (25 pcs)", "AmmoBarterPackSize": 25},
    "5d6e6911a4b9361bd5780d52": {"AmmoBarterPackTplId": "65702474bfc87b3a34093226", "AmmoBarterPackItemName": "12/70 flechette ammo pack (25 pcs)", "AmmoBarterPackSize": 25},
    "56dfef82d2720bbd668b4567": {"AmmoBarterPackTplId": "5737292724597765e5728562", "AmmoBarterPackItemName": "5.45x39mm BP gs ammo pack (120 pcs)", "AmmoBarterPackSize": 120},
    "56dff026d2720bb8668b4567": {"AmmoBarterPackTplId": "57372b832459776701014e41", "AmmoBarterPackItemName": "5.45x39mm BS gs ammo pack (120 pcs)", "AmmoBarterPackSize": 120},
    "56dff061d2720bb5668b4567": {"AmmoBarterPackTplId": "57372c21245977670937c6c2", "AmmoBarterPackItemName": "5.45x39mm BT gs ammo pack (120 pcs)", "AmmoBarterPackSize": 120},
    "56dff2ced2720bb4668b4567": {"AmmoBarterPackTplId": "57372d1b2459776862260581", "AmmoBarterPackItemName": "5.45x39mm PP gs ammo pack (120 pcs)", "AmmoBarterPackSize": 120},
    "5c0d5e4486f77478390952fe": {"AmmoBarterPackTplId": "657025ebc5d7d4cb4d078588", "AmmoBarterPackItemName": "5.45x39mm PPBS gs Igolnik ammo pack (120 pcs)", "AmmoBarterPackSize": 120},
    "56dff3afd2720bba668b4567": {"AmmoBarterPackTplId": "57372e73245977685d4159b4", "AmmoBarterPackItemName": "5.45x39mm PS gs ammo pack (120 pcs)", "AmmoBarterPackSize": 120},
    "59e0d99486f7744a32234762": {"AmmoBarterPackTplId": "64acea16c4eda9354b0226b0", "AmmoBarterPackItemName": "7.62x39mm BP gzh ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "601aa3d2b2bcb34913271e6d": {"AmmoBarterPackTplId": "6489851fc827d4637f01791b", "AmmoBarterPackItemName": "7.62x39mm MAI AP ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "64b7af434b75259c590fa893": {"AmmoBarterPackTplId": "64ace9f9c4eda9354b0226aa", "AmmoBarterPackItemName": "7.62x39mm PP gzh ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "5656d7c34bdc2d9d198b4587": {"AmmoBarterPackTplId": "5649ed104bdc2d3d1c8b458b", "AmmoBarterPackItemName": "7.62x39mm PS gzh ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "57372140245977611f70ee91": {"AmmoBarterPackTplId": "657026341419851aef03e730", "AmmoBarterPackItemName": "9x18mm PM SP7 gzh ammo pack (50 pcs)", "AmmoBarterPackSize": 50},
    "5c925fa22e221601da359b7b": {"AmmoBarterPackTplId": "65702591c5d7d4cb4d07857c", "AmmoBarterPackItemName": "9x19mm AP 6.3 ammo pack (50 pcs)", "AmmoBarterPackSize": 50},
    "5efb0da7a29a85116f6ea05f": {"AmmoBarterPackTplId": "648987d673c462723909a151", "AmmoBarterPackItemName": "9x19mm PBP ammo pack (50 pcs)", "AmmoBarterPackSize": 50},
    "56d59d3ad2720bdb418b4577": {"AmmoBarterPackTplId": "657025a81419851aef03e724", "AmmoBarterPackItemName": "9x19mm Pst gzh ammo pack (50 pcs)", "AmmoBarterPackSize": 50},
    "5c0d56a986f774449d5de529": {"AmmoBarterPackTplId": "5c1127bdd174af44217ab8b9", "AmmoBarterPackItemName": "9x19mm RIP ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "5c0d688c86f77413ae3407b2": {"AmmoBarterPackTplId": "6489854673c462723909a14e", "AmmoBarterPackItemName": "9x39mm BP ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "61962d879bb3d20b0946d385": {"AmmoBarterPackTplId": "657025cfbfc87b3a34093253", "AmmoBarterPackItemName": "9x39mm PAB-9 gs ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "57a0dfb82459774d3078b56c": {"AmmoBarterPackTplId": "657025d4c5d7d4cb4d078585", "AmmoBarterPackItemName": "9x39mm SP-5 gs ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "57a0e5022459774d1673f889": {"AmmoBarterPackTplId": "657025dabfc87b3a34093256", "AmmoBarterPackItemName": "9x39mm SP-6 gs ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "5c0d668f86f7747ccb7f13b2": {"AmmoBarterPackTplId": "657025dfcfc010a0f5006a3b", "AmmoBarterPackItemName": "9x39mm SPP gs ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "6a427a2e38a6d33bffe9829b": {"AmmoBarterPackTplId": "65702577cfc010a0f5006a2c", "AmmoBarterPackItemName": "7.62x54mm R LPS gzh ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "6a427a2e38a6d33bffe98292": {"AmmoBarterPackTplId": "648984b8d5b4df6140000a1a", "AmmoBarterPackItemName": "7.62x54mm R BS gs ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
    "6a427a2e38a6d33bffe9829d": {"AmmoBarterPackTplId": "560d75f54bdc2da74d8b4573", "AmmoBarterPackItemName": "7.62x54mm R SNB gzh ammo pack (20 pcs)", "AmmoBarterPackSize": 20},
}

# Runtime-loaded custom ammo support. Use --custom-ammo-tpl for loose ammo
# templates that do not have names the regex can recognize, and use
# --custom-ammo-pack-map for custom loose-ammo -> ammo-pack template mapping.
CUSTOM_AMMO_TPLS: set[str] = set()
CUSTOM_AMMO_PACKS: dict[str, dict[str, Any]] = {}

# Broader fallback for custom calibers. The weapon-name guard in is_ammo_offer()
# prevents guns like "7.62x39 assault rifle" from being treated as ammo.
GENERIC_AMMO_NAME_START_RE = re.compile(
    r"^(?:\.?\d+(?:\.\d+)?x\d+(?:\.\d+)?(?:mm)?|\d{1,2}/\d{2}|\.\d+(?:\.\d+)?)\b",
    re.IGNORECASE,
)

# Offline readability for Tony/custom cloned 7.62x54R rows.
BUILT_IN_NAMES.update({
    "6a427a2e38a6d33bffe9829b": "7.62x54mm R LPS gzh",
    "6a427a2e38a6d33bffe98292": "7.62x54mm R BS gs",
    "6a427a2e38a6d33bffe9829d": "7.62x54mm R SNB gzh",
    "65702577cfc010a0f5006a2c": "7.62x54mm R LPS gzh ammo pack (20 pcs)",
    "648984b8d5b4df6140000a1a": "7.62x54mm R BS gs ammo pack (20 pcs)",
    "560d75f54bdc2da74d8b4573": "7.62x54mm R SNB gzh ammo pack (20 pcs)",
})

WEAPON_NAME_HINTS = (
    "assault rifle", "sniper rifle", "bolt-action", "carbine", "shotgun",
    "submachine gun", "light machine gun", "pistol", "suppressor", "handguard",
    "muzzle", "gas tube", "helmet", "armor", "rig", "grenade",
)


def get_known_ammo_pack_tpl_ids() -> set[str]:
    pack_tpl_ids = {
        str(pack_info.get("AmmoBarterPackTplId", ""))
        for pack_info in BUILT_IN_AMMO_PACKS.values()
        if str(pack_info.get("AmmoBarterPackTplId", ""))
    }
    pack_tpl_ids.update(
        str(pack_info.get("AmmoBarterPackTplId", ""))
        for pack_info in CUSTOM_AMMO_PACKS.values()
        if str(pack_info.get("AmmoBarterPackTplId", ""))
    )
    return pack_tpl_ids


def strip_json_comments_and_trailing_commas(text: str) -> str:
    """Simple fallback for JSON files with comments/trailing commas."""
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


def get_ci(obj: dict[str, Any], *keys: str, default: Any = None) -> Any:
    """Case-insensitive-ish dictionary getter for SPT C# and raw JSON shapes."""
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
    """Return the actual key name already present in a dict, ignoring case."""
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
    """Get/create a mutable assort section while preserving the file's existing key casing."""
    existing_key = get_existing_key_ci(assort, preferred_key, *alternate_keys)
    if existing_key is None:
        assort[preferred_key] = default_value
        return assort[preferred_key]

    section = assort.get(existing_key)
    if section is None:
        section = default_value
        assort[existing_key] = section
    return section


def parse_id_list(values: list[str] | None) -> set[str]:
    parsed: set[str] = set()
    for value in values or []:
        for part in str(value).replace(";", ",").split(","):
            part = part.strip()
            if part:
                parsed.add(part)
    return parsed


def normalize_custom_ammo_pack_info(loose_tpl: str, raw_value: Any, default_pack_size: int) -> dict[str, Any] | None:
    """Normalize custom loose-ammo -> pack mapping rows from JSON."""
    if isinstance(raw_value, str):
        pack_tpl = raw_value.strip()
        if not pack_tpl:
            return None
        return {
            "AmmoBarterPackTplId": pack_tpl,
            "AmmoBarterPackSize": max(1, int(default_pack_size or 30)),
        }

    if not isinstance(raw_value, dict):
        return None

    pack_tpl = str(get_ci(
        raw_value,
        "AmmoBarterPackTplId",
        "ammoBarterPackTplId",
        "PackTplId",
        "packTplId",
        "TplId",
        "tplId",
        "_tpl",
        default="",
    ) or "").strip()
    if not pack_tpl:
        return None

    pack_size_raw = get_ci(
        raw_value,
        "AmmoBarterPackSize",
        "ammoBarterPackSize",
        "PackSize",
        "packSize",
        "Size",
        "size",
        default=default_pack_size,
    )
    try:
        pack_size = max(1, int(pack_size_raw or default_pack_size or 30))
    except (TypeError, ValueError):
        pack_size = max(1, int(default_pack_size or 30))

    info = {
        "AmmoBarterPackTplId": pack_tpl,
        "AmmoBarterPackSize": pack_size,
    }

    pack_name = get_ci(
        raw_value,
        "AmmoBarterPackItemName",
        "ammoBarterPackItemName",
        "PackName",
        "packName",
        "ItemName",
        "itemName",
        "Name",
        "name",
        default=None,
    )
    if pack_name:
        info["AmmoBarterPackItemName"] = str(pack_name)

    return info


def load_custom_ammo_pack_map(path: Path | None, default_pack_size: int) -> tuple[dict[str, dict[str, Any]], list[str]]:
    """Load optional custom ammo pack mappings for cloned/modded ammo."""
    warnings: list[str] = []
    if path is None:
        return {}, warnings

    if not path.exists():
        warnings.append(f"Custom ammo pack map was not found: {path}")
        return {}, warnings

    data = load_json(path)
    rows: list[tuple[str, Any]] = []

    if isinstance(data, dict):
        rows = [(str(loose_tpl), raw_value) for loose_tpl, raw_value in data.items()]
    elif isinstance(data, list):
        for entry in data:
            if not isinstance(entry, dict):
                continue
            loose_tpl = str(get_ci(
                entry,
                "LooseAmmoTplId",
                "looseAmmoTplId",
                "AmmoTplId",
                "ammoTplId",
                "TplId",
                "tplId",
                "_tpl",
                default="",
            ) or "").strip()
            if loose_tpl:
                rows.append((loose_tpl, entry))
    else:
        warnings.append(f"Custom ammo pack map ignored {path}: expected a JSON object or list")
        return {}, warnings

    mapping: dict[str, dict[str, Any]] = {}
    skipped = 0
    for loose_tpl, raw_value in rows:
        loose_tpl = str(loose_tpl or "").strip()
        if not loose_tpl:
            skipped += 1
            continue
        info = normalize_custom_ammo_pack_info(loose_tpl, raw_value, default_pack_size)
        if info is None:
            skipped += 1
            continue
        mapping[loose_tpl] = info

    if mapping:
        warnings.append(f"Loaded {len(mapping)} custom ammo pack mappings from {path}")
    if skipped:
        warnings.append(f"Skipped {skipped} invalid custom ammo pack mappings from {path}")

    return mapping, warnings



def get_candidate_tpl_id_from_obj(obj: dict[str, Any], current_key: str | None = None) -> str | None:
    """Return a likely template id for a custom item object or keyed item row."""
    candidates = [
        current_key,
        get_ci(obj, "id", "Id", "_id", "TplId", "tplId", "TemplateId", "templateId", default=None),
    ]
    for candidate in candidates:
        if looks_like_item_id(candidate):
            return str(candidate).strip()
    return None


def normalize_filter_tpl_values(raw_filter: Any) -> list[str]:
    """Normalize StackSlots _props.filters[].Filter values into loose-ammo template ids."""
    raw_values: list[Any]
    if isinstance(raw_filter, list):
        raw_values = raw_filter
    elif isinstance(raw_filter, str):
        raw_values = [raw_filter]
    else:
        return []

    tpl_ids: list[str] = []
    for raw_value in raw_values:
        if not isinstance(raw_value, str):
            continue
        for part in raw_value.replace(";", ",").split(","):
            tpl = part.strip()
            if looks_like_item_id(tpl):
                tpl_ids.append(tpl)
    return tpl_ids


def extract_stack_slot_loose_ammo_mappings(
    pack_tpl: str,
    pack_name: str | None,
    stack_slots: Any,
    default_pack_size: int,
) -> dict[str, dict[str, Any]]:
    """Read custom ammo pack StackSlots and return loose-ammo -> pack info mappings.

    Expected custom pack shape:
    StackSlots: [{
      "_name": "cartridges",
      "_parent": "<pack tpl>",
      "_max_count": 20,
      "_props": {"filters": [{"Filter": ["<loose ammo tpl>"]}]}
    }]

    Important: Filter is the loose ammo tpl. The pack tpl comes from the containing
    pack item or, when available, the StackSlot _parent.
    """
    mapping: dict[str, dict[str, Any]] = {}
    if not looks_like_item_id(pack_tpl) or not isinstance(stack_slots, list):
        return mapping

    for slot in stack_slots:
        if not isinstance(slot, dict):
            continue

        slot_name = str(get_ci(slot, "_name", "name", "Name", default="") or "").strip().lower()
        # Ammo packs use a cartridges stack slot. Do not require this name, because
        # some custom JSON omits it, but prefer it when present.
        if slot_name and slot_name != "cartridges":
            continue

        # The slot Filter below is the loose ammo tpl. The pack tpl is the
        # containing item tpl, with StackSlot _parent as a stronger hint when present.
        slot_parent_tpl = str(get_ci(slot, "_parent", "parent", "Parent", "parentId", "ParentId", default="") or "").strip()
        effective_pack_tpl = slot_parent_tpl if looks_like_item_id(slot_parent_tpl) else pack_tpl

        max_count_raw = get_ci(slot, "_max_count", "max_count", "MaxCount", "maxCount", default=default_pack_size)
        try:
            pack_size = max(1, int(max_count_raw or default_pack_size or 30))
        except (TypeError, ValueError):
            pack_size = max(1, int(default_pack_size or 30))

        props = get_ci(slot, "_props", "props", "Props", default={})
        filters = get_ci(props if isinstance(props, dict) else {}, "filters", "Filters", default=[])
        if not isinstance(filters, list):
            continue

        for filter_row in filters:
            if not isinstance(filter_row, dict):
                continue
            # Every Filter entry is the loose ammo template this pack accepts.
            allowed_tpls = normalize_filter_tpl_values(get_ci(filter_row, "Filter", "filter", default=[]))
            for loose_tpl in allowed_tpls:
                if loose_tpl == effective_pack_tpl:
                    continue
                pack_info: dict[str, Any] = {
                    "AmmoBarterPackTplId": effective_pack_tpl,
                    "AmmoBarterPackSize": pack_size,
                }
                if is_readable_item_name(pack_name):
                    pack_info["AmmoBarterPackItemName"] = str(pack_name).strip()
                mapping.setdefault(loose_tpl, pack_info)

    return mapping



def has_ammo_pack_hint(obj: dict[str, Any], pack_tpl: str | None, pack_name: str | None) -> bool:
    """Avoid treating magazines or weapons with cartridge StackSlots as ammo packs."""
    if pack_tpl and pack_tpl in get_known_ammo_pack_tpl_ids():
        return True

    if is_readable_item_name(pack_name):
        normalized_name = str(pack_name).lower()
        if "ammo pack" in normalized_name or " pack" in normalized_name or normalized_name.endswith("pack"):
            return True

    cloned_tpl = str(get_ci(
        obj,
        "itemTplToClone",
        "ItemTplToClone",
        "tplToClone",
        "TplToClone",
        "cloneTpl",
        "CloneTpl",
        default="",
    ) or "").strip()
    if cloned_tpl and cloned_tpl in get_known_ammo_pack_tpl_ids():
        return True

    return False

def scan_db_ammo_pack_mappings_from_json_value(
    value: Any,
    mapping: dict[str, dict[str, Any]],
    default_pack_size: int,
    current_key: str | None = None,
    current_tpl: str | None = None,
    current_name: str | None = None,
    current_pack_like: bool = False,
) -> None:
    """Recursively scan db/data JSON for custom ammo pack StackSlots filters."""
    if isinstance(value, dict):
        local_tpl = get_candidate_tpl_id_from_obj(value, current_key) or current_tpl
        local_name = read_inline_item_name(value) or (CUSTOM_DB_NAMES.get(local_tpl or "") if local_tpl else None) or current_name
        local_pack_like = current_pack_like or has_ammo_pack_hint(value, local_tpl, local_name)

        stack_slots = get_ci(value, "StackSlots", "stackSlots", "stack_slots", default=None)
        if stack_slots is not None and local_tpl and local_pack_like:
            extracted = extract_stack_slot_loose_ammo_mappings(
                pack_tpl=local_tpl,
                pack_name=local_name,
                stack_slots=stack_slots,
                default_pack_size=default_pack_size,
            )
            for loose_tpl, pack_info in extracted.items():
                mapping.setdefault(loose_tpl, pack_info)

        for raw_key, child in value.items():
            scan_db_ammo_pack_mappings_from_json_value(
                child,
                mapping=mapping,
                default_pack_size=default_pack_size,
                current_key=str(raw_key),
                current_tpl=local_tpl,
                current_name=local_name,
                current_pack_like=local_pack_like,
            )
        return

    if isinstance(value, list):
        for child in value:
            scan_db_ammo_pack_mappings_from_json_value(
                child,
                mapping=mapping,
                default_pack_size=default_pack_size,
                current_key=current_key,
                current_tpl=current_tpl,
                current_name=current_name,
                current_pack_like=current_pack_like,
            )


def path_looks_like_ammo_pack_file(path: Path) -> bool:
    """True for local custom ammo pack files such as pack_545x39mm_bt-r_gs.json."""
    stem = path.stem.lower()
    return (
        stem.startswith("pack_")
        or stem.startswith("ammo_pack_")
        or stem.endswith("_pack")
        or "ammo_pack" in stem
    )


def load_db_ammo_pack_mappings(paths: list[Path], default_pack_size: int) -> tuple[dict[str, dict[str, Any]], list[str]]:
    """Auto-detect custom loose-ammo -> pack mappings from local db/data item JSON.

    This lets custom ammo packs define their relationship normally through their
    StackSlots cartridge filter instead of requiring a separate --ammo-pack-map row.
    """
    warnings: list[str] = []
    mapping: dict[str, dict[str, Any]] = {}

    existing_paths = [path for path in paths if path.expanduser().exists()]
    if not existing_paths:
        return mapping, warnings

    json_files = iter_db_json_files(existing_paths)
    unreadable_count = 0

    for json_file in json_files:
        try:
            data = load_json(json_file)
        except Exception:
            unreadable_count += 1
            continue

        before_count = len(mapping)
        file_pack_like = path_looks_like_ammo_pack_file(json_file)
        scan_db_ammo_pack_mappings_from_json_value(
            data,
            mapping=mapping,
            default_pack_size=max(1, int(default_pack_size or 30)),
            current_name=json_file.stem if file_pack_like else None,
            current_pack_like=file_pack_like,
        )
        added_count = len(mapping) - before_count
        if added_count:
            warnings.append(f"Auto-detected {added_count} custom ammo pack mappings from StackSlots in {json_file}")

    if mapping:
        warnings.append(
            f"Auto-detected {len(mapping)} total custom ammo pack mappings from StackSlots Filter loose-ammo links"
        )
    if unreadable_count:
        warnings.append(
            f"Skipped {unreadable_count} unreadable local db/data JSON files while auto-detecting ammo packs"
        )

    return mapping, warnings

def configure_custom_ammo_support(
    custom_ammo_tpls: set[str],
    custom_ammo_pack_map: dict[str, dict[str, Any]],
) -> None:
    """Install custom ammo hints into the module-level matcher state."""
    CUSTOM_AMMO_TPLS.update(str(tpl).strip() for tpl in custom_ammo_tpls if str(tpl).strip())
    for loose_tpl, pack_info in custom_ammo_pack_map.items():
        loose_tpl = str(loose_tpl or "").strip()
        if not loose_tpl or not isinstance(pack_info, dict):
            continue
        CUSTOM_AMMO_PACKS[loose_tpl] = dict(pack_info)
        CUSTOM_AMMO_TPLS.add(loose_tpl)


def get_item_id(item: dict[str, Any]) -> str:
    return str(get_ci(item, "_id", "Id", "id", default=""))


def get_item_tpl(item: dict[str, Any]) -> str:
    return str(get_ci(item, "_tpl", "Template", "Tpl", "TemplateId", default=""))


def get_parent_id(item: dict[str, Any]) -> str:
    return str(get_ci(item, "parentId", "ParentId", "parent_id", default=""))


def get_slot_id(item: dict[str, Any]) -> str:
    return str(get_ci(item, "slotId", "SlotId", "slot_id", default=""))


def get_payment_tpl(payment: dict[str, Any]) -> str:
    return str(get_ci(payment, "_tpl", "Template", "Tpl", "TemplateId", default=""))


def get_payment_count(payment: dict[str, Any]) -> float:
    value = get_ci(payment, "count", "Count", default=0)
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def clean_number(value: float) -> int | float:
    return int(value) if float(value).is_integer() else value


def load_name_and_price_catalog(path: Path | None) -> tuple[dict[str, str], dict[str, float]]:
    names: dict[str, str] = {}
    prices: dict[str, float] = {}

    if path is None or not path.exists():
        return names, prices

    data = load_json(path)
    if not isinstance(data, list):
        return names, prices

    for row in data:
        if not isinstance(row, dict):
            continue

        tpl = str(get_ci(row, "TplId", "tplId", "_tpl", "Template", default=""))
        if not tpl:
            continue

        name = get_ci(row, "ItemName", "itemName", "Name", default=None)
        if name is not None and tpl not in names:
            names[tpl] = str(name)

        price = get_ci(row, "Price", "price", default=None)
        if price is not None and tpl not in prices:
            try:
                prices[tpl] = float(price)
            except (TypeError, ValueError):
                pass

    return names, prices


def load_existing_item_settings(path: Path | None) -> tuple[list[dict[str, Any]], list[str]]:
    """Load an existing items.json file for --keep-current-settings mode."""
    warnings: list[str] = []

    if path is None:
        warnings.append("--keep-current-settings was enabled, but no current settings file was selected")
        return [], warnings

    if not path.exists():
        warnings.append(f"--keep-current-settings was enabled, but current settings file was not found: {path}")
        return [], warnings

    data = load_json(path)
    if not isinstance(data, list):
        warnings.append(f"--keep-current-settings ignored {path}: expected a JSON list")
        return [], warnings

    rows: list[dict[str, Any]] = []
    skipped = 0
    for row in data:
        if isinstance(row, dict):
            rows.append(copy.deepcopy(row))
        else:
            skipped += 1

    if skipped:
        warnings.append(f"--keep-current-settings skipped {skipped} non-object rows from {path}")

    warnings.append(f"Loaded {len(rows)} current settings rows from {path}")
    return rows, warnings


def get_row_offer_id(row: dict[str, Any]) -> str:
    return str(get_ci(row, "OfferId", "offerId", "Id", "id", default="") or "")


def get_row_tpl_id(row: dict[str, Any]) -> str:
    return str(get_ci(row, "TplId", "tplId", "_tpl", "Template", default="") or "")


def should_always_barter(row_or_tpl: dict[str, Any] | str) -> bool:
    """True only for rows that must never be converted to cash by the runtime randomizer."""
    if isinstance(row_or_tpl, dict):
        tpl_id = get_row_tpl_id(row_or_tpl)
    else:
        tpl_id = str(row_or_tpl or "")

    return tpl_id in ALWAYS_BARTER_TPL_IDS


def apply_always_barter_flags(rows: list[dict[str, Any]]) -> int:
    """Force AlwaysBarter to false on every row except configured always-barter TplIds."""
    changed_count = 0

    for row in rows:
        if not isinstance(row, dict):
            continue

        expected_value = should_always_barter(row)
        if row.get("AlwaysBarter") is not expected_value:
            row["AlwaysBarter"] = expected_value
            changed_count += 1

    return changed_count


def should_always_in_stock(row_or_tpl: dict[str, Any] | str) -> bool:
    """True only for rows that should never be zeroed by the stock randomizer."""
    if isinstance(row_or_tpl, dict):
        tpl_id = get_row_tpl_id(row_or_tpl)
    else:
        tpl_id = str(row_or_tpl or "")

    return tpl_id in ALWAYS_IN_STOCK_TPL_IDS


def apply_always_in_stock_flags(rows: list[dict[str, Any]]) -> int:
    """Force AlwaysInStock to false on every row except configured protected TplIds."""
    changed_count = 0

    for row in rows:
        if not isinstance(row, dict):
            continue

        expected_value = should_always_in_stock(row)
        if row.get("AlwaysInStock") is not expected_value:
            row["AlwaysInStock"] = expected_value
            changed_count += 1

    return changed_count


def order_items_row(row: dict[str, Any]) -> dict[str, Any]:
    """Keep generated items.json stable and easy to review."""
    preferred_order = [
        "OfferId",
        "TplId",
        "ItemName",
        "Price",
        "Currency",
        "CashOnly",
        "BarterScheme",
        "AlwaysBarter",
        "AlwaysInStock",
        "AmmoBarterPackTplId",
        "PackOfferId",
    ]

    ordered: dict[str, Any] = {}
    for key in preferred_order:
        if key in row:
            ordered[key] = row[key]

    for key, value in row.items():
        if key not in ordered:
            ordered[key] = value

    return ordered


def order_items_rows(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [order_items_row(row) if isinstance(row, dict) else row for row in rows]


def is_missing_setting_value(key: str, value: Any) -> bool:
    """True when a field should be considered missing and safe to fill.

    We intentionally do not treat False, 0, empty lists, or existing barter
    schemes as missing because those may be deliberate Tony tuning choices.
    """
    if value is None:
        return True
    if key in {"OfferId", "TplId", "ItemName", "Currency"} and isinstance(value, str):
        return value.strip() == ""
    return False


def fill_missing_settings(existing_row: dict[str, Any], generated_row: dict[str, Any]) -> tuple[dict[str, Any], int]:
    """Preserve existing settings and copy only missing top-level fields."""
    merged = copy.deepcopy(existing_row)
    filled_count = 0

    for key, generated_value in generated_row.items():
        if key not in merged or is_missing_setting_value(key, merged.get(key)):
            merged[key] = copy.deepcopy(generated_value)
            filled_count += 1

    return merged, filled_count


def merge_current_settings(
    generated_rows: list[dict[str, Any]],
    existing_rows: list[dict[str, Any]],
    current_settings_path: Path | None,
) -> tuple[list[dict[str, Any]], list[str]]:
    """Keep current item settings, fill missing fields, and append new assort rows.

    Matching is OfferId-first because duplicate TplIds are allowed in trader
    assorts. TplId fallback is only used when the generated TplId is unique.
    """
    warnings: list[str] = []

    if not existing_rows:
        warnings.append("--keep-current-settings had no existing rows to preserve; wrote fully generated output")
        return generated_rows, warnings

    generated_by_offer: dict[str, int] = {}
    generated_tpl_counts: Counter[str] = Counter()
    generated_by_tpl: dict[str, int] = {}

    for index, row in enumerate(generated_rows):
        offer_id = get_row_offer_id(row)
        tpl_id = get_row_tpl_id(row)
        if offer_id:
            generated_by_offer[offer_id] = index
        if tpl_id:
            generated_tpl_counts[tpl_id] += 1
            generated_by_tpl.setdefault(tpl_id, index)

    merged_rows: list[dict[str, Any]] = []
    used_generated_indexes: set[int] = set()
    preserved_existing_rows = 0
    appended_missing_rows = 0
    filled_missing_fields = 0
    ignored_stale_rows = 0
    ambiguous_tpl_matches = 0

    for existing_row in existing_rows:
        offer_id = get_row_offer_id(existing_row)
        tpl_id = get_row_tpl_id(existing_row)
        generated_index: int | None = None

        if offer_id and offer_id in generated_by_offer:
            generated_index = generated_by_offer[offer_id]
        elif tpl_id and generated_tpl_counts.get(tpl_id, 0) == 1:
            generated_index = generated_by_tpl[tpl_id]
        elif tpl_id and generated_tpl_counts.get(tpl_id, 0) > 1:
            ambiguous_tpl_matches += 1

        if generated_index is None:
            ignored_stale_rows += 1
            continue

        if generated_index in used_generated_indexes:
            ignored_stale_rows += 1
            continue

        merged_row, filled_count = fill_missing_settings(existing_row, generated_rows[generated_index])
        merged_rows.append(merged_row)
        used_generated_indexes.add(generated_index)
        preserved_existing_rows += 1
        filled_missing_fields += filled_count

    for index, generated_row in enumerate(generated_rows):
        if index in used_generated_indexes:
            continue
        merged_rows.append(copy.deepcopy(generated_row))
        appended_missing_rows += 1

    source_text = f" from {current_settings_path}" if current_settings_path is not None else ""
    warnings.append(
        "--keep-current-settings merge complete"
        f"{source_text}: preserved {preserved_existing_rows} existing rows, "
        f"filled {filled_missing_fields} missing fields, appended {appended_missing_rows} new rows"
    )

    if ignored_stale_rows:
        warnings.append(
            f"--keep-current-settings ignored {ignored_stale_rows} existing rows that are not in the current assort output"
        )
    if ambiguous_tpl_matches:
        warnings.append(
            f"--keep-current-settings could not TplId-match {ambiguous_tpl_matches} existing rows because that TplId appears more than once; add OfferId to those rows to preserve them"
        )

    return merged_rows, warnings




def preserve_current_barter_schemes(
    generated_rows: list[dict[str, Any]],
    existing_rows: list[dict[str, Any]],
    current_settings_path: Path | None,
) -> tuple[list[dict[str, Any]], list[str]]:
    """Overwrite/regenerate every generated field except existing real BarterScheme values.

    Matching is OfferId-first because duplicate TplIds are allowed in trader
    assorts. TplId fallback is only used when the generated TplId is unique.

    Empty lists and cash-only schemes are not considered preserved barters here.
    This is important for --fill-empty-barters and ammo pack rows: old
    config/items.json entries with ``BarterScheme: []`` should not wipe out the
    newly generated item-for-item barter recipe.
    """
    warnings: list[str] = []

    if not existing_rows:
        warnings.append("--overwrite-except-barter had no existing rows to preserve; wrote fully generated output")
        return generated_rows, warnings

    existing_by_offer: dict[str, dict[str, Any]] = {}
    existing_tpl_counts: Counter[str] = Counter()
    existing_by_tpl: dict[str, dict[str, Any]] = {}

    for existing_row in existing_rows:
        if not isinstance(existing_row, dict):
            continue

        offer_id = get_row_offer_id(existing_row)
        tpl_id = get_row_tpl_id(existing_row)
        if offer_id:
            existing_by_offer.setdefault(offer_id, existing_row)
        if tpl_id:
            existing_tpl_counts[tpl_id] += 1
            existing_by_tpl.setdefault(tpl_id, existing_row)

    merged_rows: list[dict[str, Any]] = []
    preserved_barter_rows = 0
    generated_barter_rows = 0
    ambiguous_tpl_matches = 0
    existing_match_without_barter = 0
    empty_existing_barter_rows = 0
    cash_only_existing_barter_rows = 0

    for generated_row in generated_rows:
        merged_row = copy.deepcopy(generated_row)
        offer_id = get_row_offer_id(generated_row)
        tpl_id = get_row_tpl_id(generated_row)
        existing_row: dict[str, Any] | None = None

        if offer_id and offer_id in existing_by_offer:
            existing_row = existing_by_offer[offer_id]
        elif tpl_id and existing_tpl_counts.get(tpl_id, 0) == 1:
            existing_row = existing_by_tpl[tpl_id]
        elif tpl_id and existing_tpl_counts.get(tpl_id, 0) > 1:
            ambiguous_tpl_matches += 1

        if existing_row is not None:
            existing_barter_key = get_existing_key_ci(existing_row, "BarterScheme", "barterScheme", "barter_scheme")
            if existing_barter_key is not None:
                existing_barter_scheme = copy.deepcopy(existing_row[existing_barter_key])

                if is_real_barter_scheme(existing_barter_scheme):
                    merged_row["BarterScheme"] = existing_barter_scheme
                    preserved_barter_rows += 1
                else:
                    # Keep the generated row's barter. This fills rows that had
                    # BarterScheme: [] or a simple cash-only scheme in old config/items.json.
                    generated_barter_rows += 1
                    if not existing_barter_scheme:
                        empty_existing_barter_rows += 1
                    elif is_cash_only_scheme(existing_barter_scheme):
                        cash_only_existing_barter_rows += 1
            else:
                existing_match_without_barter += 1
                generated_barter_rows += 1
        else:
            generated_barter_rows += 1

        merged_rows.append(merged_row)

    source_text = f" from {current_settings_path}" if current_settings_path is not None else ""
    warnings.append(
        "--overwrite-except-barter complete"
        f"{source_text}: preserved existing real BarterScheme on {preserved_barter_rows} rows, "
        f"used generated BarterScheme on {generated_barter_rows} rows"
    )

    if empty_existing_barter_rows:
        warnings.append(
            f"--overwrite-except-barter replaced {empty_existing_barter_rows} empty existing BarterScheme values with generated BarterScheme values"
        )
    if cash_only_existing_barter_rows:
        warnings.append(
            f"--overwrite-except-barter replaced {cash_only_existing_barter_rows} cash-only existing BarterScheme values with generated BarterScheme values"
        )
    if existing_match_without_barter:
        warnings.append(
            f"--overwrite-except-barter found {existing_match_without_barter} matching existing rows without BarterScheme; generated BarterScheme was kept for those rows"
        )
    if ambiguous_tpl_matches:
        warnings.append(
            f"--overwrite-except-barter could not TplId-match {ambiguous_tpl_matches} generated rows because that TplId appears more than once in current settings; add OfferId to preserve those barter schemes"
        )

    return merged_rows, warnings


def load_locale_names(path: Path | None) -> dict[str, str]:
    names: dict[str, str] = {}

    if path is None or not path.exists():
        return names

    data = load_json(path)

    # Some SPT locale exports are {"Value": {...}}.
    value_data = get_ci(data, "Value", "value", default=None) if isinstance(data, dict) else None
    if isinstance(value_data, dict):
        data = value_data

    if not isinstance(data, dict):
        return names

    for key, value in data.items():
        key_str = str(key)

        # Standard locale key: "<tpl> Name": "Readable item name"
        if key_str.endswith(" Name"):
            tpl = key_str[:-5]
            names[tpl] = str(value)
            continue

        # Alternate/nested shape: "<tpl>": {"Name": "Readable item name"}
        if isinstance(value, dict):
            nested_name = get_ci(value, "Name", "name", default=None)
            if nested_name is not None:
                names[key_str] = str(nested_name)

    return names


ITEM_ID_RE = re.compile(r"^[a-fA-F0-9]{24}$")


def looks_like_item_id(value: Any) -> bool:
    return isinstance(value, str) and ITEM_ID_RE.fullmatch(value.strip()) is not None


def is_unknown_item_name(value: Any) -> bool:
    return isinstance(value, str) and value.strip().startswith("UNKNOWN_ITEM_")


def is_readable_item_name(value: Any) -> bool:
    if not isinstance(value, str):
        return False

    text = value.strip()
    if not text:
        return False

    # Do not accidentally use ids, generated fallback names, or common internal slot names.
    if looks_like_item_id(text):
        return False
    if text.startswith("UNKNOWN_ITEM_"):
        return False
    if text.lower() in {"hideout", "mod_equipment", "mod_scope", "mod_magazine", "mod_mount"}:
        return False

    return True


def choose_readable_item_name(*values: Any) -> str | None:
    for value in values:
        if is_readable_item_name(value):
            return str(value).strip()
    return None


def read_nested_locale_name(obj: dict[str, Any]) -> str | None:
    """Read common custom-item locale shapes from WTT/CommonLib-style JSON."""
    locale_container = get_ci(
        obj,
        "locales",
        "Locales",
        "locale",
        "Locale",
        "customLocales",
        "CustomLocales",
        "localization",
        "Localization",
        default=None,
    )

    if not isinstance(locale_container, dict):
        return None

    preferred_language_keys = (
        "en",
        "en-US",
        "en_US",
        "English",
        "english",
        "global",
        "default",
    )

    locale_rows: list[Any] = []
    for lang_key in preferred_language_keys:
        lang_row = get_ci(locale_container, lang_key, default=None)
        if lang_row is not None:
            locale_rows.append(lang_row)

    locale_rows.extend(locale_container.values())

    for row in locale_rows:
        if isinstance(row, dict):
            name = choose_readable_item_name(
                get_ci(row, "Name", "name", "ItemName", "itemName", default=None),
                get_ci(row, "ShortName", "shortName", "Short", "short", default=None),
            )
            if name:
                return name
        elif is_readable_item_name(row):
            return str(row).strip()

    return None


def read_inline_item_name(obj: dict[str, Any]) -> str | None:
    """Read readable names directly stored on a custom item object."""
    nested_locale_name = read_nested_locale_name(obj)
    if nested_locale_name:
        return nested_locale_name

    return choose_readable_item_name(
        get_ci(obj, "ItemName", "itemName", "Name", "name", "DisplayName", "displayName", default=None),
        get_ci(obj, "ShortName", "shortName", "Short", "short", default=None),
    )


def add_db_name(names: dict[str, str], tpl: str, item_name: str) -> bool:
    tpl = str(tpl or "").strip()
    if not looks_like_item_id(tpl) or not is_readable_item_name(item_name):
        return False

    # Prefer the first real full name found. Locale/customItems files scanned earlier
    # should not be overwritten by later incidental matches.
    names.setdefault(tpl, str(item_name).strip())
    return True


def scan_db_names_from_json_value(value: Any, names: dict[str, str], current_key: str | None = None) -> None:
    """Recursively scan local db/data JSON for custom item names and locale rows."""
    if isinstance(value, dict):
        # Locale shape: "<tpl> Name": "Readable name" or "<tpl> ShortName": "Short".
        for raw_key, raw_value in value.items():
            key = str(raw_key)
            if key.endswith(" Name"):
                add_db_name(names, key[:-5], str(raw_value))
            elif key.endswith(" ShortName"):
                tpl = key[:-10]
                if tpl not in names:
                    add_db_name(names, tpl, str(raw_value))
            elif key.endswith(" Short Name"):
                tpl = key[:-11]
                if tpl not in names:
                    add_db_name(names, tpl, str(raw_value))

        possible_ids = [
            current_key,
            get_ci(value, "id", "Id", "_id", "TplId", "tplId", "TemplateId", "templateId", default=None),
        ]
        item_name = read_inline_item_name(value)
        if item_name:
            for possible_id in possible_ids:
                if looks_like_item_id(possible_id):
                    add_db_name(names, str(possible_id), item_name)

        for raw_key, child in value.items():
            scan_db_names_from_json_value(child, names, current_key=str(raw_key))
        return

    if isinstance(value, list):
        for child in value:
            scan_db_names_from_json_value(child, names, current_key=current_key)


def iter_db_json_files(paths: list[Path]) -> list[Path]:
    json_files: list[Path] = []
    seen: set[Path] = set()

    for raw_path in paths:
        path = raw_path.expanduser()
        if not path.exists():
            continue

        if path.is_file() and path.suffix.lower() == ".json":
            candidates = [path]
        elif path.is_dir():
            candidates = sorted(path.rglob("*.json"))
        else:
            candidates = []

        for candidate in candidates:
            resolved = candidate.resolve()
            if resolved in seen:
                continue
            seen.add(resolved)
            json_files.append(candidate)

    return json_files


def load_db_item_names(paths: list[Path], enabled: bool = True) -> tuple[dict[str, str], list[str]]:
    """Load custom item names from local db/data JSON files.

    This is deliberately tolerant because Tony/WTT/CommonLib files can be shaped as:
    - {"<tpl>": {"locales": {"en": {"name": "..."}}}}
    - {"<tpl> Name": "Readable name"}
    - [{"id": "<tpl>", "name": "Readable name"}]
    """
    warnings: list[str] = []
    names: dict[str, str] = {}

    if not enabled:
        warnings.append("db custom item name lookup disabled")
        return names, warnings

    existing_paths = [path for path in paths if path.expanduser().exists()]
    if not existing_paths:
        return names, warnings

    json_files = iter_db_json_files(existing_paths)
    unreadable_count = 0

    for json_file in json_files:
        try:
            data = load_json(json_file)
        except Exception:
            unreadable_count += 1
            continue

        before_count = len(names)
        scan_db_names_from_json_value(data, names)
        added_count = len(names) - before_count
        if added_count:
            warnings.append(f"Loaded {added_count} custom item names from {json_file}")

    if json_files:
        warnings.append(f"Scanned {len(json_files)} local db/data JSON files for custom item names")
    if unreadable_count:
        warnings.append(f"Skipped {unreadable_count} unreadable local db/data JSON files while loading custom item names")

    return names, warnings


def load_tarkov_dev_cache(cache_path: Path, max_age_seconds: int) -> tuple[dict[str, str], dict[str, float], list[str]]:
    warnings: list[str] = []
    names: dict[str, str] = {}
    prices: dict[str, float] = {}

    if not cache_path.exists():
        return names, prices, warnings

    try:
        cache = load_json(cache_path)
    except Exception as ex:
        warnings.append(f"Could not read tarkov.dev cache {cache_path}: {ex}")
        return names, prices, warnings

    if not isinstance(cache, dict):
        warnings.append(f"Ignoring tarkov.dev cache {cache_path}: expected JSON object")
        return names, prices, warnings

    fetched_at = float(cache.get("fetchedAt", 0) or 0)
    age = time.time() - fetched_at
    if age > max_age_seconds:
        warnings.append(f"tarkov.dev cache is older than {max_age_seconds} seconds; refreshing if API is reachable")
        return names, prices, warnings

    raw_items = cache.get("items", [])
    if not isinstance(raw_items, list):
        warnings.append(f"Ignoring tarkov.dev cache {cache_path}: items was not a list")
        return names, prices, warnings

    names, prices = parse_tarkov_dev_items(raw_items)
    warnings.append(f"Loaded {len(names)} names from tarkov.dev cache: {cache_path}")
    return names, prices, warnings


def parse_tarkov_dev_items(raw_items: list[Any]) -> tuple[dict[str, str], dict[str, float]]:
    names: dict[str, str] = {}
    prices: dict[str, float] = {}

    for item in raw_items:
        if not isinstance(item, dict):
            continue

        item_id = str(item.get("id") or "")
        if not item_id:
            continue

        name = item.get("name") or item.get("shortName")
        if name:
            names[item_id] = str(name)

        # basePrice is stable game data. avg24hPrice can be useful if basePrice is missing.
        price = item.get("avg24hPrice")
        if price is None:
            price = item.get("basePrice")
        if price is not None:
            try:
                prices[item_id] = float(price)
            except (TypeError, ValueError):
                pass

    return names, prices


def post_tarkov_dev_query(query: str, timeout_seconds: float) -> dict[str, Any]:
    payload = json.dumps({"query": query}).encode("utf-8")
    request = urllib.request.Request(
        TARKOV_DEV_GRAPHQL_URL,
        data=payload,
        method="POST",
        headers={
            "Accept": "application/json",
            "Content-Type": "application/json",
            "User-Agent": "TonyTraderItemsGenerator/1.1 (+local modding script)",
        },
    )

    with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
        response_text = response.read().decode("utf-8")

    parsed = json.loads(response_text)
    if not isinstance(parsed, dict):
        raise RuntimeError("tarkov.dev returned a non-object response")
    return parsed


def fetch_tarkov_dev_items(timeout_seconds: float) -> list[dict[str, Any]]:
    # Current tarkov.dev docs/readme examples use `items { id name shortName ... }`.
    # Some older examples used `items(lang: en)`, so keep that as a fallback.
    queries = [
        """
        query TonyItemNames {
          items {
            id
            name
            shortName
            basePrice
            avg24hPrice
          }
        }
        """,
        """
        query TonyItemNames {
          items(lang: en) {
            id
            name
            shortName
            basePrice
            avg24hPrice
          }
        }
        """,
    ]

    last_error: Any = None
    for query in queries:
        parsed = post_tarkov_dev_query(query, timeout_seconds)

        errors = parsed.get("errors")
        if errors:
            last_error = errors
            continue

        data = parsed.get("data")
        if isinstance(data, dict) and isinstance(data.get("items"), list):
            return data["items"]

        last_error = "response did not contain data.items"

    raise RuntimeError(f"tarkov.dev GraphQL lookup failed: {last_error}")


def get_tarkov_dev_names_and_prices(
    enabled: bool,
    cache_path: Path | None,
    refresh_cache: bool,
    timeout_seconds: float,
) -> tuple[dict[str, str], dict[str, float], list[str]]:
    warnings: list[str] = []
    if not enabled:
        warnings.append("tarkov.dev lookup disabled")
        return {}, {}, warnings

    if cache_path is not None and not refresh_cache:
        cached_names, cached_prices, cache_warnings = load_tarkov_dev_cache(
            cache_path,
            TARKOV_DEV_CACHE_MAX_AGE_SECONDS,
        )
        warnings.extend(cache_warnings)
        if cached_names:
            return cached_names, cached_prices, warnings

    try:
        raw_items = fetch_tarkov_dev_items(timeout_seconds)
        names, prices = parse_tarkov_dev_items(raw_items)
        warnings.append(f"Fetched {len(names)} names from tarkov.dev")

        if cache_path is not None:
            cache_path.parent.mkdir(parents=True, exist_ok=True)
            cache_payload = {
                "fetchedAt": time.time(),
                "source": TARKOV_DEV_GRAPHQL_URL,
                "items": raw_items,
            }
            cache_path.write_text(json.dumps(cache_payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
            warnings.append(f"Saved tarkov.dev cache: {cache_path}")

        return names, prices, warnings
    except (urllib.error.URLError, TimeoutError, RuntimeError, json.JSONDecodeError, OSError) as ex:
        warnings.append(f"tarkov.dev lookup failed: {ex}")

        if cache_path is not None:
            # Last-resort cache load, even if it is stale.
            try:
                cache = load_json(cache_path)
                raw_items = cache.get("items", []) if isinstance(cache, dict) else []
                if isinstance(raw_items, list):
                    names, prices = parse_tarkov_dev_items(raw_items)
                    if names:
                        warnings.append(f"Using stale tarkov.dev cache: {cache_path}")
                        return names, prices, warnings
            except Exception as cache_ex:
                warnings.append(f"Stale tarkov.dev cache also failed: {cache_ex}")

        return {}, {}, warnings


def resolve_name(
    tpl: str,
    tarkov_dev_names: dict[str, str],
    locale_names: dict[str, str],
    catalog_names: dict[str, str],
) -> str:
    tpl = str(tpl or "").strip()
    if tpl in CURRENCY_NAME_BY_TPL:
        return CURRENCY_NAME_BY_TPL[tpl]

    # Only accept real readable names from each source. This prevents an old
    # config/items.json catalog entry like UNKNOWN_ITEM_<tpl> from blocking a
    # better name found in the local custom db/data scan.
    for source in (tarkov_dev_names, locale_names, catalog_names, CUSTOM_DB_NAMES):
        name = source.get(tpl) if isinstance(source, dict) else None
        if is_readable_item_name(name):
            return str(name).strip()

    # Generated barter pool and built-ins are safe fallback readability sources.
    if is_readable_item_name(BARTER_POOL_NAME_BY_TPL.get(tpl)):
        return str(BARTER_POOL_NAME_BY_TPL[tpl]).strip()
    if is_readable_item_name(BUILT_IN_NAMES.get(tpl)):
        return str(BUILT_IN_NAMES[tpl]).strip()

    return f"UNKNOWN_ITEM_{tpl}"


def resolve_known_name(
    tpl: str,
    tarkov_dev_names: dict[str, str],
    locale_names: dict[str, str],
    catalog_names: dict[str, str],
) -> str | None:
    name = resolve_name(tpl, tarkov_dev_names, locale_names, catalog_names)
    return name if is_readable_item_name(name) else None


def refresh_unknown_item_names_in_barter_scheme(
    scheme: Any,
    tarkov_dev_names: dict[str, str],
    locale_names: dict[str, str],
    catalog_names: dict[str, str],
) -> int:
    """Replace UNKNOWN/missing ItemName fields inside a preserved BarterScheme.

    This keeps the barter recipe itself intact: TplId and Count are not changed.
    Only the human-readable ItemName field is refreshed.
    """
    changed_count = 0

    if not isinstance(scheme, list):
        return changed_count

    for option in scheme:
        if not isinstance(option, list):
            continue
        for payment in option:
            if not isinstance(payment, dict):
                continue

            payment_tpl = str(get_ci(payment, "TplId", "tplId", "_tpl", "Template", "TemplateId", default="") or "").strip()
            if not payment_tpl:
                continue

            item_name_key = get_existing_key_ci(payment, "ItemName", "itemName", "Name", "name") or "ItemName"
            current_name = payment.get(item_name_key)
            if is_readable_item_name(current_name):
                continue

            resolved_name = resolve_known_name(payment_tpl, tarkov_dev_names, locale_names, catalog_names)
            if resolved_name:
                payment[item_name_key] = resolved_name
                changed_count += 1

    return changed_count


def refresh_unknown_item_names_in_rows(
    rows: list[dict[str, Any]],
    tarkov_dev_names: dict[str, str],
    locale_names: dict[str, str],
    catalog_names: dict[str, str],
) -> int:
    """Refresh UNKNOWN_ITEM_* names from db/locale/catalog without changing barters."""
    changed_count = 0

    for row in rows:
        if not isinstance(row, dict):
            continue

        tpl_id = get_row_tpl_id(row)
        current_name = row.get("ItemName")
        if tpl_id and not is_readable_item_name(current_name):
            resolved_name = resolve_known_name(tpl_id, tarkov_dev_names, locale_names, catalog_names)
            if resolved_name:
                row["ItemName"] = resolved_name
                changed_count += 1

        changed_count += refresh_unknown_item_names_in_barter_scheme(
            row.get("BarterScheme"),
            tarkov_dev_names,
            locale_names,
            catalog_names,
        )

    return changed_count


def normalize_barter_scheme(
    raw_scheme: Any,
    tarkov_dev_names: dict[str, str],
    locale_names: dict[str, str],
    catalog_names: dict[str, str],
) -> list[list[dict[str, Any]]]:
    normalized: list[list[dict[str, Any]]] = []

    if not isinstance(raw_scheme, list):
        return normalized

    for raw_option in raw_scheme:
        if not isinstance(raw_option, list):
            continue

        option: list[dict[str, Any]] = []
        for raw_payment in raw_option:
            if not isinstance(raw_payment, dict):
                continue

            payment_tpl = get_payment_tpl(raw_payment)
            if not payment_tpl:
                continue

            option.append(
                {
                    "TplId": payment_tpl,
                    "ItemName": resolve_name(payment_tpl, tarkov_dev_names, locale_names, catalog_names),
                    "Count": clean_number(get_payment_count(raw_payment)),
                }
            )

        if option:
            normalized.append(option)

    return normalized


def is_currency_tpl(tpl: str) -> bool:
    return tpl in CURRENCY_BY_TPL


def is_cash_only_scheme(scheme: list[list[dict[str, Any]]]) -> bool:
    if not scheme:
        return False
    return all(len(option) == 1 and is_currency_tpl(str(option[0].get("TplId", ""))) for option in scheme)


def find_primary_cash_payment(scheme: list[list[dict[str, Any]]]) -> tuple[float, str] | None:
    # Prefer simple cash-only option.
    for option in scheme:
        if len(option) == 1 and is_currency_tpl(str(option[0].get("TplId", ""))):
            return float(option[0].get("Count", 0)), CURRENCY_BY_TPL[str(option[0].get("TplId"))]

    # Otherwise find the first cash component in a mixed barter.
    for option in scheme:
        for payment in option:
            tpl = str(payment.get("TplId", ""))
            if is_currency_tpl(tpl):
                return float(payment.get("Count", 0)), CURRENCY_BY_TPL[tpl]

    return None


def choose_fallback_price(
    sold_tpl: str,
    catalog_prices: dict[str, float],
    tarkov_dev_prices: dict[str, float],
    default_price: float,
) -> float:
    # Preserve user/config catalog prices first, because these are tuned for Tony.
    if sold_tpl in catalog_prices:
        return catalog_prices[sold_tpl]
    if sold_tpl in tarkov_dev_prices:
        return tarkov_dev_prices[sold_tpl]
    if sold_tpl in BUILT_IN_PRICES:
        return float(BUILT_IN_PRICES[sold_tpl])
    return default_price


def is_real_barter_scheme(scheme: list[list[dict[str, Any]]]) -> bool:
    """True when at least one payment option requires a non-currency item."""
    return any(
        any(not is_currency_tpl(str(payment.get("TplId", ""))) for payment in option)
        for option in scheme
    )


def is_ammo_offer(item_name: str, sold_tpl: str) -> bool:
    """True for ammo rows. Avoids weapon names that merely include a caliber."""
    if sold_tpl in KNOWN_AMMO_TPLS or sold_tpl in CUSTOM_AMMO_TPLS or sold_tpl in CUSTOM_AMMO_PACKS:
        return True

    normalized_name = (item_name or "").strip().lower()
    if not normalized_name or normalized_name.startswith("unknown_item_"):
        return False

    if any(hint in normalized_name for hint in WEAPON_NAME_HINTS):
        return False

    if "ammo pack" in normalized_name:
        return True

    if "ammo" in normalized_name and not any(hint in normalized_name for hint in WEAPON_NAME_HINTS):
        return True

    return (
        AMMO_NAME_START_RE.search(normalized_name) is not None
        or GENERIC_AMMO_NAME_START_RE.search(normalized_name) is not None
    )


def ammo_pack_size_for_offer(item_name: str, sold_tpl: str, default_pack_size: int) -> int:
    """Return the barter pack size for ammo rows. Non-ammo rows return 1."""
    if not is_ammo_offer(item_name, sold_tpl):
        return 1

    # Keep this user-tunable. A default of 30 fits most rifle/SMG ammo and
    # gives Tony barters a clean "one pack" feel.
    return max(1, int(default_pack_size or 30))


def parse_ammo_pack_size(item_name: str, default_pack_size: int) -> int:
    match = AMMO_PACK_SIZE_RE.search(item_name or "")
    if match:
        try:
            return max(1, int(match.group(1)))
        except (TypeError, ValueError):
            pass
    return max(1, int(default_pack_size or 30))


def normalize_ammo_pack_match_name(item_name: str) -> str:
    """Normalize loose ammo and ammo pack names to the same comparable base."""
    name = (item_name or "").lower().strip()
    name = AMMO_PACK_NAME_RE.sub("", name)
    name = AMMO_PACK_SIZE_RE.sub("", name)
    name = name.replace("ammo pack", "")
    # Custom pack locales sometimes say just "pack" instead of "ammo pack".
    # Strip it only as a standalone word so ammo names like "BP" / "PBP" stay intact.
    name = re.sub(r"\bpack\b", "", name)
    name = re.sub(r"\s+", " ", name)
    return name.strip(" -")


def find_ammo_pack_for_offer(
    item_name: str,
    sold_tpl: str,
    tarkov_dev_names: dict[str, str],
    locale_names: dict[str, str],
    catalog_names: dict[str, str],
    default_pack_size: int,
) -> dict[str, Any] | None:
    """Find the matching ammo pack template for a loose ammo offer.

    The output row keeps TplId as the loose bullet template for cash pricing, but
    also stores AmmoBarterPackTplId so the runtime loader can swap the sold item
    to the pack template whenever that offer rolls barter.
    """
    if not is_ammo_offer(item_name, sold_tpl):
        return None

    if sold_tpl in BUILT_IN_AMMO_PACKS:
        fallback = dict(BUILT_IN_AMMO_PACKS[sold_tpl])
        fallback.setdefault("AmmoBarterPackSize", max(1, int(default_pack_size or 30)))
        return fallback

    if sold_tpl in CUSTOM_AMMO_PACKS:
        custom = dict(CUSTOM_AMMO_PACKS[sold_tpl])
        custom.setdefault("AmmoBarterPackSize", max(1, int(default_pack_size or 30)))
        pack_tpl = str(custom.get("AmmoBarterPackTplId", ""))
        if pack_tpl and not custom.get("AmmoBarterPackItemName"):
            resolved_pack_name = resolve_name(pack_tpl, tarkov_dev_names, locale_names, catalog_names)
            if resolved_pack_name.startswith("UNKNOWN_ITEM_"):
                resolved_pack_name = f"{item_name} ammo pack ({custom['AmmoBarterPackSize']} pcs)"
            custom["AmmoBarterPackItemName"] = resolved_pack_name
        return custom

    target_name = normalize_ammo_pack_match_name(item_name)
    if not target_name:
        return None

    # Merge all known name sources. Later entries should not overwrite earlier
    # tarkov.dev results, because those are usually the cleanest current names.
    all_names: dict[str, str] = {}
    for source in (tarkov_dev_names, locale_names, catalog_names, CUSTOM_DB_NAMES, BUILT_IN_NAMES):
        for tpl, name in source.items():
            all_names.setdefault(str(tpl), str(name))

    candidates: list[tuple[int, int, str, str]] = []
    known_pack_tpl_ids = get_known_ammo_pack_tpl_ids()
    for tpl, candidate_name in all_names.items():
        candidate_lower = candidate_name.lower()
        candidate_has_pack_word = (
            "ammo pack" in candidate_lower
            or re.search(r"\bpack\b", candidate_lower) is not None
            or tpl in known_pack_tpl_ids
        )
        if not candidate_has_pack_word:
            continue

        candidate_base = normalize_ammo_pack_match_name(candidate_name)
        if not candidate_base:
            continue

        if candidate_base == target_name:
            score = 0
        elif candidate_base.startswith(target_name) or target_name.startswith(candidate_base):
            score = 1
        else:
            continue

        pack_size = parse_ammo_pack_size(candidate_name, default_pack_size)
        # Prefer exact name matches, then packs close to the default size.
        size_distance = abs(pack_size - max(1, int(default_pack_size or 30)))
        candidates.append((score, size_distance, tpl, candidate_name))

    if not candidates:
        return None

    candidates.sort(key=lambda row: (row[0], row[1], row[3].lower()))
    _, _, pack_tpl, pack_name = candidates[0]
    return {
        "AmmoBarterPackTplId": pack_tpl,
        "AmmoBarterPackItemName": pack_name,
        "AmmoBarterPackSize": parse_ammo_pack_size(pack_name, default_pack_size),
    }


def is_ammo_pack_root_offer(
    item_name: str,
    sold_tpl: str,
    known_pack_tpl_ids: set[str] | None = None,
) -> bool:
    """True for standalone ammo pack assort roots that should not become items.json rows.

    Tony keeps these pack offers in assort.json so the runtime can swap a loose
    ammo offer to a pack offer when the row rolls barter. The config/items.json
    row should point at the pack offer through PackOfferId instead of writing
    a second visible row for the pack itself.
    """
    pack_tpl_ids = known_pack_tpl_ids or get_known_ammo_pack_tpl_ids()
    if sold_tpl in pack_tpl_ids:
        return True

    return "ammo pack" in (item_name or "").lower()


def build_ammo_pack_offer_id_lookup(
    sellable_roots: list[dict[str, Any]],
    tarkov_dev_names: dict[str, str],
    locale_names: dict[str, str],
    catalog_names: dict[str, str],
) -> dict[str, str]:
    """Map ammo pack template IDs to their root assort offer IDs.

    This lets the generated loose-ammo row match the target schema:
    AmmoBarterPackTplId tells the runtime what template to sell, and PackOfferId
    tells it which hidden/static pack offer in assort.json belongs to that ammo.
    """
    pack_offer_id_by_tpl: dict[str, str] = {}
    known_pack_tpl_ids = get_known_ammo_pack_tpl_ids()

    for root in sellable_roots:
        offer_id = get_item_id(root)
        sold_tpl = get_item_tpl(root)
        if not offer_id or not sold_tpl:
            continue

        item_name = resolve_name(sold_tpl, tarkov_dev_names, locale_names, catalog_names)
        if is_ammo_pack_root_offer(item_name, sold_tpl, known_pack_tpl_ids):
            pack_offer_id_by_tpl.setdefault(sold_tpl, offer_id)

    return pack_offer_id_by_tpl


def denormalize_barter_scheme_for_assort(scheme: list[list[dict[str, Any]]]) -> list[list[dict[str, Any]]]:
    """Convert generated items.json BarterScheme rows back to raw assort barter_scheme shape."""
    raw_scheme: list[list[dict[str, Any]]] = []

    if not isinstance(scheme, list):
        return raw_scheme

    for option in scheme:
        if not isinstance(option, list):
            continue

        raw_option: list[dict[str, Any]] = []
        for payment in option:
            if not isinstance(payment, dict):
                continue

            payment_tpl = str(payment.get("TplId", "") or "")
            if not payment_tpl:
                continue

            raw_option.append({
                "count": clean_number(float(payment.get("Count", 0) or 0)),
                "_tpl": payment_tpl,
            })

        if raw_option:
            raw_scheme.append(raw_option)

    return raw_scheme


def deterministic_assort_offer_id(existing_ids: set[str], loose_offer_id: str, pack_tpl_id: str) -> str:
    """Create a stable 24-char hex root offer id for generated pack offers."""
    seed = f"tony-generated-ammo-pack:{loose_offer_id}:{pack_tpl_id}"
    for salt in range(1000):
        suffix = "" if salt == 0 else f":{salt}"
        candidate = hashlib.sha1(f"{seed}{suffix}".encode("utf-8")).hexdigest()[:24]
        if candidate not in existing_ids:
            return candidate

    raise RuntimeError(f"Could not generate unique ammo pack offer id for {pack_tpl_id}")


def get_number_ci(obj: dict[str, Any], *keys: str, default: float | None = None) -> float | None:
    value = get_ci(obj, *keys, default=default)
    if value is None:
        return default
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def set_scaled_upd_count(upd: dict[str, Any], key: str, source_value: float | None, divisor: int) -> None:
    if source_value is None:
        return

    divisor = max(1, int(divisor or 1))
    scaled_value = max(1, int(float(source_value) // divisor))
    upd[key] = scaled_value


def build_generated_ammo_pack_root_item(
    pack_offer_id: str,
    pack_tpl_id: str,
    source_root: dict[str, Any],
    pack_count: int,
) -> dict[str, Any]:
    """Build a new hideout root offer for a missing ammo pack template."""
    source_upd = get_ci(source_root, "upd", "Upd", default=None)
    source_upd = copy.deepcopy(source_upd) if isinstance(source_upd, dict) else {}

    pack_count = max(1, int(pack_count or 1))
    stack_count = get_number_ci(source_upd, "StackObjectsCount", "stackObjectsCount", default=None)
    buy_restriction_max = get_number_ci(source_upd, "BuyRestrictionMax", "buyRestrictionMax", default=None)

    if not source_upd:
        source_upd = {"StackObjectsCount": 1, "UnlimitedCount": False}

    if stack_count is not None:
        set_scaled_upd_count(source_upd, "StackObjectsCount", stack_count, pack_count)
    else:
        source_upd.setdefault("StackObjectsCount", 1)

    if buy_restriction_max is not None:
        set_scaled_upd_count(source_upd, "BuyRestrictionMax", buy_restriction_max, pack_count)

    # Generated root offers should start clean every time.
    if "BuyRestrictionCurrent" in source_upd:
        source_upd["BuyRestrictionCurrent"] = 0

    return {
        "_id": pack_offer_id,
        "_tpl": pack_tpl_id,
        "parentId": "hideout",
        "slotId": "hideout",
        "upd": source_upd,
    }


def add_missing_ammo_pack_offer_to_assort(
    assort: dict[str, Any],
    source_root: dict[str, Any],
    pack_tpl_id: str,
    normalized_scheme: list[list[dict[str, Any]]],
    pack_count: int,
) -> tuple[str, bool]:
    """Add a root pack offer, barter_scheme row, and loyalty row to assort.json data."""
    items = ensure_assort_section(assort, "items", "Items", default_value=[])
    barter_scheme = ensure_assort_section(assort, "barter_scheme", "BarterScheme", default_value={})
    loyal_level_items = ensure_assort_section(
        assort,
        "loyal_level_items",
        "LoyalLevelItems",
        "loyalLevelItems",
        default_value={},
    )

    if not isinstance(items, list):
        raise ValueError("assort items section is not a list")
    if not isinstance(barter_scheme, dict):
        raise ValueError("assort barter_scheme section is not an object")
    if not isinstance(loyal_level_items, dict):
        raise ValueError("assort loyal_level_items section is not an object")

    existing_pack_offer = next(
        (item for item in items if isinstance(item, dict) and get_parent_id(item) == "hideout" and get_item_tpl(item) == pack_tpl_id),
        None,
    )
    if existing_pack_offer is not None:
        existing_offer_id = get_item_id(existing_pack_offer)
        if existing_offer_id:
            changed = False
            if existing_offer_id not in barter_scheme:
                barter_scheme[existing_offer_id] = denormalize_barter_scheme_for_assort(normalized_scheme)
                changed = True
            if existing_offer_id not in loyal_level_items:
                loyal_level_items[existing_offer_id] = loyal_level_items.get(get_item_id(source_root), 1)
                changed = True
            return existing_offer_id, changed

    existing_ids = {get_item_id(item) for item in items if isinstance(item, dict) and get_item_id(item)}
    source_offer_id = get_item_id(source_root)
    pack_offer_id = deterministic_assort_offer_id(existing_ids, source_offer_id, pack_tpl_id)

    items.append(build_generated_ammo_pack_root_item(
        pack_offer_id=pack_offer_id,
        pack_tpl_id=pack_tpl_id,
        source_root=source_root,
        pack_count=pack_count,
    ))
    barter_scheme[pack_offer_id] = denormalize_barter_scheme_for_assort(normalized_scheme)
    loyal_level_items[pack_offer_id] = loyal_level_items.get(source_offer_id, 1)

    return pack_offer_id, True


def infer_barter_tags(item_name: str, sold_tpl: str) -> list[str]:
    name = item_name.lower()
    tags = {"generic"}

    if any(token in name for token in ("ammo", "x39", "x19", "x18", "12/70", ".366", "9x39", "7.62", "5.45")):
        tags.update({"ammo", "weapon"})
    if any(token in name for token in ("ak", "vss", "val", "rpk", "vityaz", "saiga", "shotgun", "rifle", "carbine", "suppressor", "muzzle", "handguard", "gas tube")):
        tags.update({"weapon"})
    if any(token in name for token in ("suppressor", "pbs", "silencer")):
        tags.update({"suppressor", "weapon"})
    if any(token in name for token in ("armor", "armored", "helmet", "mask", "rig", "paca", "gzhel", "zhuk", "6b")):
        tags.update({"armor"})
    if "helmet" in name:
        tags.update({"helmet", "armor"})
    if any(token in name for token in ("med", "bandage", "tourniquet", "splint", "surgical", "salewa", "cms", "surv", "injector", "stim", "painkiller")):
        tags.update({"medical"})
    if any(token in name for token in ("injector", "stimulant", "propital", "m.u.l.e", "perfotoran", "sj12", "adrenaline")):
        tags.update({"injector", "medical"})
    if any(token in name for token in ("case", "documents", "key tool")):
        tags.update({"case", "valuable"})
    if any(token in name for token in ("thermal", "flir", "reap-ir")):
        tags.update({"thermal", "tech", "valuable"})
    if any(token in name for token in ("grenade", "vog", "rgd", "rgo", "rgn", "f-1")):
        tags.update({"weapon", "ammo"})
    if any(token in name for token in ("water", "ration", "stew", "aquamari")):
        tags.update({"food"})
    if any(token in name for token in ("backpack", "bag")):
        tags.update({"generic", "tool"})

    return sorted(tags)


def choose_barter_pool_for_offer(item_name: str, sold_tpl: str, price: float) -> list[dict[str, Any]]:
    wanted_tags = set(infer_barter_tags(item_name, sold_tpl))

    # Cheap goods should not ask for bitcoins or rare tech.
    if price < 10_000:
        allowed_value = 20_000
    elif price < 50_000:
        allowed_value = 90_000
    elif price < 200_000:
        allowed_value = 250_000
    elif price < 1_000_000:
        allowed_value = 650_000
    else:
        allowed_value = 1_000_000

    candidates = []
    for component in BARTER_ITEM_POOL:
        if component["TplId"] == sold_tpl:
            continue
        component_tags = set(component.get("Tags", []))
        component_value = float(component.get("Value", 0) or 0)
        if component_value <= 0 or component_value > allowed_value:
            continue
        if component_tags & wanted_tags or "generic" in component_tags:
            candidates.append(component)

    if candidates:
        return candidates

    return [component for component in BARTER_ITEM_POOL if component["TplId"] != sold_tpl]


def make_payment_component(component: dict[str, Any], count: int) -> dict[str, Any]:
    return {
        "TplId": str(component["TplId"]),
        "ItemName": str(component["ItemName"]),
        "Count": int(max(1, count)),
    }


def get_barter_component_tpls(scheme: list[list[dict[str, Any]]]) -> list[str]:
    """Return non-currency barter ingredient TplIds used by a scheme.

    The generator counts each ingredient type once per offer, not by stack
    count. This prevents one ingredient from appearing in too many different
    recipes while still allowing normal stacks such as 2x duct tape.
    """
    tpl_ids: list[str] = []
    if not isinstance(scheme, list):
        return tpl_ids

    for option in scheme:
        if not isinstance(option, list):
            continue
        for payment in option:
            if not isinstance(payment, dict):
                continue
            tpl = str(payment.get("TplId", ""))
            if tpl and not is_currency_tpl(tpl):
                tpl_ids.append(tpl)

    return tpl_ids


def record_barter_scheme_usage(
    scheme: list[list[dict[str, Any]]],
    usage_counts: Counter[str] | None,
) -> None:
    if usage_counts is None:
        return

    # Count each ingredient template once per row/offer.
    for tpl in sorted(set(get_barter_component_tpls(scheme))):
        usage_counts[tpl] += 1


def filter_components_by_usage_cap(
    components: list[dict[str, Any]],
    usage_counts: Counter[str] | None,
    max_uses_per_item: int,
) -> list[dict[str, Any]]:
    """Prefer components below the configured global usage cap.

    If every candidate is already at the cap, return the original list so the
    generator can still make a valid recipe instead of failing. The report will
    warn when a cap had to be exceeded.
    """
    if max_uses_per_item <= 0 or usage_counts is None:
        return components

    under_cap = [
        component for component in components
        if usage_counts[str(component.get("TplId", ""))] < max_uses_per_item
    ]
    return under_cap or components


def barter_usage_sort_key(
    component: dict[str, Any],
    usage_counts: Counter[str] | None,
    max_uses_per_item: int,
) -> tuple[int, int]:
    if max_uses_per_item <= 0 or usage_counts is None:
        return (0, 0)

    tpl = str(component.get("TplId", ""))
    current_uses = int(usage_counts[tpl])
    over_cap = 1 if current_uses >= max_uses_per_item else 0
    return (over_cap, current_uses)


def count_barter_ingredient_usage_from_rows(rows: list[dict[str, Any]]) -> Counter[str]:
    usage_counts: Counter[str] = Counter()
    for row in rows:
        if not isinstance(row, dict):
            continue
        scheme = row.get("BarterScheme", [])
        if isinstance(scheme, list):
            record_barter_scheme_usage(scheme, usage_counts)
    return usage_counts


def build_barter_ingredient_name_map(rows: list[dict[str, Any]]) -> dict[str, str]:
    names: dict[str, str] = {}
    for row in rows:
        if not isinstance(row, dict):
            continue
        scheme = row.get("BarterScheme", [])
        if not isinstance(scheme, list):
            continue
        for option in scheme:
            if not isinstance(option, list):
                continue
            for payment in option:
                if not isinstance(payment, dict):
                    continue
                tpl = str(payment.get("TplId", ""))
                if not tpl or is_currency_tpl(tpl):
                    continue
                item_name = str(payment.get("ItemName", "") or "")
                if item_name:
                    names.setdefault(tpl, item_name)
    return names


def format_barter_usage_summary(
    usage_counts: Counter[str],
    name_by_tpl: dict[str, str],
    limit: int = 5,
) -> str:
    if not usage_counts:
        return "none"

    parts: list[str] = []
    for tpl, count in usage_counts.most_common(max(1, int(limit or 5))):
        name = name_by_tpl.get(tpl) or BARTER_POOL_NAME_BY_TPL.get(tpl) or tpl
        parts.append(f"{name} x{count}")
    return "; ".join(parts)


def make_cash_payment_scheme(price: float, currency: str) -> list[list[dict[str, Any]]]:
    currency_code = (currency or "RUB").upper()
    currency_tpl = TPL_BY_CURRENCY.get(currency_code, RUB_TPL)
    return [[
        {
            "TplId": currency_tpl,
            "ItemName": CURRENCY_NAME_BY_TPL.get(currency_tpl, currency_code),
            "Count": clean_number(round(float(price or 0))),
        }
    ]]


def calculate_ammo_pack_price(price: float, pack_count: int, value_multiplier: float) -> float:
    pack_count = max(1, int(pack_count or 1))
    multiplier = max(float(value_multiplier or 1.0), 0.01)
    return round(float(price or 0) * pack_count * multiplier)


def generate_barter_scheme_for_price(
    sold_tpl: str,
    item_name: str,
    price: float,
    value_multiplier: float,
    max_components: int,
    rng: random.Random,
    pack_count: int = 1,
    barter_usage_counts: Counter[str] | None = None,
    barter_max_uses_per_item: int = 0,
) -> list[list[dict[str, Any]]]:
    """Create one plausible non-currency barter option from the item cash Price.

    For ammo, pack_count should be greater than 1 so the generated barter
    represents buying a pack of rounds, not paying a barter item for one round.
    """
    pack_count = max(1, int(pack_count or 1))
    target = max(float(price or 0) * pack_count * max(value_multiplier, 0.01), 1.0)
    max_components = max(1, min(int(max_components), 6))

    if target < 10_000:
        component_slots = 1
    elif target < 50_000:
        component_slots = rng.randint(1, min(2, max_components))
    elif target < 200_000:
        component_slots = rng.randint(2, min(3, max_components))
    else:
        component_slots = rng.randint(3, max_components)

    pool = choose_barter_pool_for_offer(item_name, sold_tpl, target)
    pool = filter_components_by_usage_cap(pool, barter_usage_counts, barter_max_uses_per_item)
    pool = sorted(pool, key=lambda component: float(component.get("Value", 0) or 0))

    selected: list[dict[str, Any]] = []
    remaining = target
    used_tpls: set[str] = set()

    for slot_index in range(component_slots):
        slots_left = component_slots - slot_index
        ideal_piece_value = max(remaining / max(slots_left, 1), 1.0)

        # Keep candidates near the current target piece. Add a little randomness so
        # repeated runs with different seeds do not all look identical.
        available = [component for component in pool if component["TplId"] not in used_tpls]
        available = filter_components_by_usage_cap(available, barter_usage_counts, barter_max_uses_per_item)
        near = [
            component for component in available
            if float(component.get("Value", 0) or 0) <= max(ideal_piece_value * 1.75, 5_000)
        ]
        if not near:
            near = available or pool

        ranked = sorted(
            near,
            key=lambda component: (
                barter_usage_sort_key(component, barter_usage_counts, barter_max_uses_per_item),
                abs(float(component.get("Value", 0) or 0) - ideal_piece_value),
            ),
        )
        sample_count = min(len(ranked), 5)
        component = rng.choice(ranked[:sample_count]) if sample_count else rng.choice(pool)
        component_value = max(float(component.get("Value", 1) or 1), 1.0)

        count = max(1, round(ideal_piece_value / component_value))
        count = min(count, int(component.get("MaxCount", 8) or 8))

        selected.append(make_payment_component(component, count))
        used_tpls.add(str(component["TplId"]))
        remaining -= component_value * count

    # If the result undershoots hard, increase the last stack count when possible.
    total_value = sum(
        BARTER_POOL_VALUE_BY_TPL.get(str(payment["TplId"]), 0.0) * int(payment["Count"])
        for payment in selected
    )
    if selected and total_value < target * 0.65:
        last = selected[-1]
        component = next((component for component in BARTER_ITEM_POOL if component["TplId"] == last["TplId"]), None)
        if component is not None:
            component_value = max(float(component.get("Value", 1) or 1), 1.0)
            needed_extra = max(0, round((target - total_value) / component_value))
            max_count = int(component.get("MaxCount", 8) or 8)
            last["Count"] = min(max_count, int(last["Count"]) + needed_extra)

    return [selected]


def calculate_barter_scheme_value(scheme: list[list[dict[str, Any]]]) -> float:
    """Approximate generated barter value using this script's barter pool tuning values."""
    if not scheme:
        return 0.0

    # Each outer option is an alternate price. For validation, use the cheapest valid option.
    option_values: list[float] = []
    for option in scheme:
        total = 0.0
        for payment in option:
            tpl = str(payment.get("TplId", ""))
            try:
                count = float(payment.get("Count", 0) or 0)
            except (TypeError, ValueError):
                count = 0.0
            total += BARTER_POOL_VALUE_BY_TPL.get(tpl, 0.0) * count
        if total > 0:
            option_values.append(total)

    return min(option_values) if option_values else 0.0


def generate_barter_scheme_covering_target(
    sold_tpl: str,
    item_name: str,
    target_value: float,
    max_components: int,
    rng: random.Random,
    barter_usage_counts: Counter[str] | None = None,
    barter_max_uses_per_item: int = 0,
) -> list[list[dict[str, Any]]]:
    """Create a barter option that actually lands near the requested value.

    This is stricter than the generic generator and is used for ammo pack barter
    rows. It prevents bad results such as asking for one tape for a 36k ammo pack.
    """
    target = max(float(target_value or 0), 1.0)
    max_components = max(1, min(int(max_components or 1), 6))
    pool = choose_barter_pool_for_offer(item_name, sold_tpl, target)
    pool = filter_components_by_usage_cap(pool, barter_usage_counts, barter_max_uses_per_item)

    bundles: list[dict[str, Any]] = []
    for component in pool:
        value = float(component.get("Value", 0) or 0)
        if value <= 0:
            continue

        max_count = int(component.get("MaxCount", 8) or 8)
        # Allow enough count to reach the target, but do not make silly huge stacks.
        max_count_for_target = max(1, min(max_count, int((target * 1.35 + value - 1) // value)))

        for count in range(1, max_count_for_target + 1):
            total = value * count
            # Keep reasonable overpays, but allow one high-value item when no better item exists.
            if total > target * 1.55 and count > 1:
                continue
            bundles.append(
                {
                    "component": component,
                    "count": count,
                    "value": total,
                    "tpl": str(component["TplId"]),
                }
            )

    if not bundles:
        return generate_barter_scheme_for_price(
            sold_tpl=sold_tpl,
            item_name=item_name,
            price=target,
            value_multiplier=1.0,
            max_components=max_components,
            rng=rng,
            pack_count=1,
            barter_usage_counts=barter_usage_counts,
            barter_max_uses_per_item=barter_max_uses_per_item,
        )

    def score_bundle_set(bundle_set: list[dict[str, Any]]) -> tuple[float, int, float, int, float]:
        total = sum(float(bundle["value"]) for bundle in bundle_set)
        distance = abs(total - target)
        usage_score = 0
        if barter_usage_counts is not None and barter_max_uses_per_item > 0:
            usage_score = sum(int(barter_usage_counts[str(bundle["tpl"])]) for bundle in bundle_set)

        # Strongly punish underpaying by more than 15%.
        if total < target * 0.85:
            distance += target * 2.0

        # Mildly punish overpaying by more than 35%, but sometimes it is unavoidable.
        if total > target * 1.35:
            distance += (total - target * 1.35) * 0.75

        component_type_count = len(bundle_set)
        total_item_count = sum(int(bundle["count"]) for bundle in bundle_set)
        return (distance, usage_score, abs(total - target), component_type_count, total_item_count)

    # Search a trimmed list of good bundle choices. This gives stable, near-value
    # results without exploding runtime for large assortments.
    bundles.sort(key=lambda bundle: score_bundle_set([bundle]))
    search_space = bundles[:36]

    best: list[dict[str, Any]] | None = None
    best_score: tuple[float, int, float, int, float] | None = None

    def consider(candidate: list[dict[str, Any]]) -> None:
        nonlocal best, best_score
        if not candidate:
            return
        tpl_list = [str(bundle["tpl"]) for bundle in candidate]
        if len(set(tpl_list)) != len(tpl_list):
            return
        score = score_bundle_set(candidate)
        if best_score is None or score < best_score:
            best = list(candidate)
            best_score = score

    # Try single components first.
    for bundle in search_space:
        consider([bundle])

    # Try small mixed trades. Four component types is enough for the values Tony sells.
    import itertools
    max_combo_size = min(max_components, 4)
    for combo_size in range(2, max_combo_size + 1):
        for combo in itertools.combinations(search_space, combo_size):
            consider(list(combo))

    if not best:
        best = [search_space[0]]

    # Randomize ordering only; keep the selected value-correct recipe.
    best = list(best)
    rng.shuffle(best)

    return [[make_payment_component(bundle["component"], int(bundle["count"])) for bundle in best]]


def should_generate_barter_scheme(mode: str, scheme: list[list[dict[str, Any]]]) -> bool:
    mode = (mode or "none").lower()
    if mode == "none":
        return False
    if mode == "all":
        return True
    if mode == "cash-only":
        return is_cash_only_scheme(scheme)
    if mode == "missing":
        return not is_real_barter_scheme(scheme)
    raise ValueError(f"unknown generate barter mode: {mode}")


def generate_items(
    assort: dict[str, Any],
    tarkov_dev_names: dict[str, str],
    tarkov_dev_prices: dict[str, float],
    locale_names: dict[str, str],
    catalog_names: dict[str, str],
    catalog_prices: dict[str, float],
    force_cash_only_rows: bool,
    default_price: float,
    generate_barter_schemes: str,
    barter_value_multiplier: float,
    barter_max_components: int,
    barter_rng: random.Random,
    ammo_barter_pack_size: int,
    barter_max_uses_per_item: int,
    update_assort_missing_ammo_packs: bool,
) -> tuple[list[dict[str, Any]], list[str], bool]:
    items = get_ci(assort, "items", "Items", default=[])
    barter_scheme = get_ci(assort, "barter_scheme", "BarterScheme", default={})

    if not isinstance(items, list):
        raise ValueError("assort.json does not contain an items/Items list")
    if not isinstance(barter_scheme, dict):
        raise ValueError("assort.json does not contain a barter_scheme/BarterScheme object")

    sellable_roots = []
    for item in items:
        if not isinstance(item, dict):
            continue

        parent_id = get_parent_id(item)
        slot_id = get_slot_id(item)
        if parent_id == "hideout" and (slot_id in ("", "hideout")):
            sellable_roots.append(item)

    ammo_pack_offer_id_by_tpl = build_ammo_pack_offer_id_lookup(
        sellable_roots,
        tarkov_dev_names,
        locale_names,
        catalog_names,
    )
    known_pack_tpl_ids = get_known_ammo_pack_tpl_ids()

    output: list[dict[str, Any]] = []
    warnings: list[str] = []
    barter_usage_counts: Counter[str] = Counter()
    assort_modified = False

    for root in sellable_roots:
        offer_id = get_item_id(root)
        sold_tpl = get_item_tpl(root)

        if not offer_id:
            warnings.append("Found sellable root with no id; skipped")
            continue
        if not sold_tpl:
            warnings.append(f"Offer {offer_id} has no tpl/template; skipped")
            continue

        item_name = resolve_name(sold_tpl, tarkov_dev_names, locale_names, catalog_names)
        if is_ammo_pack_root_offer(item_name, sold_tpl, known_pack_tpl_ids):
            warnings.append(
                f"Skipped standalone ammo pack offer {offer_id} / {sold_tpl}; "
                "linked from loose ammo rows with PackOfferId instead"
            )
            continue

        raw_scheme = barter_scheme.get(offer_id, [])
        normalized_scheme = normalize_barter_scheme(raw_scheme, tarkov_dev_names, locale_names, catalog_names)
        cash_payment = find_primary_cash_payment(normalized_scheme)

        if cash_payment is not None:
            price, currency = cash_payment
        else:
            price = choose_fallback_price(sold_tpl, catalog_prices, tarkov_dev_prices, default_price)
            currency = "RUB"
            if not normalized_scheme:
                warnings.append(f"Offer {offer_id} / {sold_tpl} has no usable barter scheme; using fallback price {price} RUB")
            else:
                warnings.append(f"Offer {offer_id} / {sold_tpl} is barter-only; using fallback Price {price} RUB for the cash fallback field")

        original_cash_only = is_cash_only_scheme(normalized_scheme)
        is_ammo = is_ammo_offer(item_name, sold_tpl)
        ammo_pack_info = find_ammo_pack_for_offer(
            item_name=item_name,
            sold_tpl=sold_tpl,
            tarkov_dev_names=tarkov_dev_names,
            locale_names=locale_names,
            catalog_names=catalog_names,
            default_pack_size=ammo_barter_pack_size,
        )
        pack_count = int(ammo_pack_info.get("AmmoBarterPackSize", ammo_barter_pack_size)) if ammo_pack_info else ammo_pack_size_for_offer(item_name, sold_tpl, ammo_barter_pack_size)
        barter_scheme_value_basis = "Unit"

        if is_ammo and pack_count > 1:
            # Ammo is special: barter offers represent a whole ammo pack, not one loose round.
            # Generate a normal item-for-item barter with a target value of:
            # bullet Price x pack size x barter value multiplier.
            # Runtime swaps the sold template to AmmoBarterPackTplId when this offer rolls barter.
            pack_price = calculate_ammo_pack_price(float(price or 0), pack_count, barter_value_multiplier)
            generated_scheme = generate_barter_scheme_covering_target(
                sold_tpl=sold_tpl,
                item_name=item_name,
                target_value=pack_price,
                max_components=barter_max_components,
                rng=barter_rng,
                barter_usage_counts=barter_usage_counts,
                barter_max_uses_per_item=barter_max_uses_per_item,
            )
            if generated_scheme:
                normalized_scheme = generated_scheme

            generated_value = calculate_barter_scheme_value(normalized_scheme)
            if generated_value < pack_price * 0.80:
                warnings.append(
                    f"WARNING: Ammo barter for {item_name} ({offer_id}) is still below target: "
                    f"generated {clean_number(generated_value)} vs target {clean_number(pack_price)} {currency}"
                )
            barter_scheme_value_basis = "Pack"
            warnings.append(
                f"Generated ammo pack item-barter scheme for {item_name} ({offer_id}) "
                f"from {pack_count} rounds x {clean_number(price)} {currency} = target value {clean_number(pack_price)} {currency}"
            )
        elif should_generate_barter_scheme(generate_barter_schemes, normalized_scheme):
            generated_scheme = generate_barter_scheme_for_price(
                sold_tpl=sold_tpl,
                item_name=item_name,
                price=float(price or 0),
                value_multiplier=barter_value_multiplier,
                max_components=barter_max_components,
                rng=barter_rng,
                pack_count=1,
                barter_usage_counts=barter_usage_counts,
                barter_max_uses_per_item=barter_max_uses_per_item,
            )
            if generated_scheme:
                normalized_scheme = generated_scheme
                warnings.append(f"Generated barter scheme for {item_name} ({offer_id}) from Price {clean_number(price)} {currency}")

        row = {
            "OfferId": offer_id,
            "TplId": sold_tpl,
            "ItemName": item_name,
            "Price": clean_number(price),
            "Currency": currency,
            # Keep cash-only rows cash by default. The runtime randomizer can still use the
            # generated BarterScheme when it picks the offer as part of the barter 15%.
            "CashOnly": True,
            "BarterScheme": normalized_scheme,
            # Default false on every row. Toolset is forced true below by TplId.
            "AlwaysBarter": should_always_barter(sold_tpl),
            # Default false on every row. WI-FI Camera and MS2000 Marker are forced true below by TplId.
            "AlwaysInStock": should_always_in_stock(sold_tpl),
        }

        if is_ammo:
            if ammo_pack_info:
                # Runtime only needs the pack tpl. Pack size/name are used by this
                # generator internally to value the barter correctly, not by items.json.
                pack_tpl_id = str(ammo_pack_info["AmmoBarterPackTplId"])
                row["AmmoBarterPackTplId"] = pack_tpl_id

                pack_offer_id = ammo_pack_offer_id_by_tpl.get(pack_tpl_id, "")
                if not pack_offer_id and update_assort_missing_ammo_packs:
                    pack_offer_id, created_pack_offer = add_missing_ammo_pack_offer_to_assort(
                        assort=assort,
                        source_root=root,
                        pack_tpl_id=pack_tpl_id,
                        normalized_scheme=normalized_scheme,
                        pack_count=pack_count,
                    )
                    ammo_pack_offer_id_by_tpl[pack_tpl_id] = pack_offer_id
                    if created_pack_offer:
                        assort_modified = True
                        warnings.append(
                            f"Added/updated standalone ammo pack offer {pack_offer_id} for {item_name} ({offer_id}) "
                            f"using pack template {pack_tpl_id}"
                        )

                if pack_offer_id:
                    row["PackOfferId"] = pack_offer_id
                    warnings.append(
                        f"Ammo barter pack target for {item_name} ({offer_id}): "
                        f"{pack_tpl_id} via pack offer {pack_offer_id}"
                    )
                else:
                    warnings.append(
                        f"Ammo barter pack target for {item_name} ({offer_id}): {pack_tpl_id}; "
                        "no matching standalone pack offer was found in assort.json and --no-update-assort was used, so PackOfferId was not written"
                    )
            else:
                warnings.append(
                    f"No ammo pack template found for {item_name} ({offer_id}); "
                    "runtime randomizer should keep this ammo offer cash-only unless you add AmmoBarterPackTplId manually"
                )

        record_barter_scheme_usage(normalized_scheme, barter_usage_counts)
        output.append(row)

    if barter_max_uses_per_item > 0:
        name_by_tpl = build_barter_ingredient_name_map(output)
        overused = {tpl: count for tpl, count in barter_usage_counts.items() if count > barter_max_uses_per_item}
        if overused:
            overused_text = format_barter_usage_summary(Counter(overused), name_by_tpl, limit=10)
            warnings.append(
                f"WARNING: barter ingredient cap {barter_max_uses_per_item} was exceeded because not enough valid alternatives were available: {overused_text}"
            )
        warnings.append(
            f"Barter ingredient usage cap active: max {barter_max_uses_per_item} offers per ingredient; "
            f"most used: {format_barter_usage_summary(barter_usage_counts, name_by_tpl, limit=5)}"
        )

    output.sort(key=lambda row: (str(row["ItemName"]).lower(), str(row["OfferId"])))

    root_tpl_counts = Counter(get_item_tpl(root) for root in sellable_roots)
    duplicate_tpls = {tpl: count for tpl, count in root_tpl_counts.items() if count > 1}
    if duplicate_tpls:
        warnings.append(f"Duplicate TplIds preserved by OfferId: {duplicate_tpls}")

    unknown_names = [row["TplId"] for row in output if str(row["ItemName"]).startswith("UNKNOWN_ITEM_")]
    if unknown_names:
        warnings.append(f"Unknown item names: {sorted(set(unknown_names))}")

    return output, warnings, assort_modified


def build_report(out_path: Path, output: list[dict[str, Any]], warnings: list[str]) -> str:
    cash_default_rows = sum(1 for row in output if row.get("CashOnly") is True)
    real_barter_scheme_rows = sum(1 for row in output if is_real_barter_scheme(row.get("BarterScheme", [])))
    always_in_stock_rows = sum(1 for row in output if row.get("AlwaysInStock") is True)
    cash_only_scheme_rows = sum(1 for row in output if is_cash_only_scheme(row.get("BarterScheme", [])))
    generated_rows = sum(
        1 for warning in warnings
        if warning.startswith("Generated barter scheme for ")
        or warning.startswith("Generated ammo pack item-barter scheme for ")
    )
    generated_ammo_pack_rows = sum(1 for warning in warnings if warning.startswith("Generated ammo pack item-barter scheme for "))
    ammo_pack_target_rows = sum(1 for row in output if row.get("AmmoBarterPackTplId"))
    ammo_pack_offer_id_rows = sum(1 for row in output if row.get("PackOfferId"))
    ammo_rows_without_pack_target = sum(
        1 for row in output
        if is_ammo_offer(str(row.get("ItemName", "")), str(row.get("TplId", "")))
        and not row.get("AmmoBarterPackTplId")
    )
    always_barter_rows = sum(1 for row in output if row.get("AlwaysBarter") is True)

    duplicate_tpl_counts = Counter(str(row.get("TplId", "")) for row in output)
    duplicate_tpls = {tpl: count for tpl, count in duplicate_tpl_counts.items() if count > 1}
    barter_usage_counts = count_barter_ingredient_usage_from_rows(output)
    barter_name_by_tpl = build_barter_ingredient_name_map(output)

    lines = [
        "items.json generation report",
        "============================",
        f"Output: {out_path}",
        f"Rows written: {len(output)}",
        f"Cash default rows (CashOnly=true): {cash_default_rows}",
        f"AlwaysBarter rows: {always_barter_rows}",
        f"AlwaysInStock rows: {always_in_stock_rows}",
        f"Rows with real barter scheme: {real_barter_scheme_rows}",
        f"Rows with cash-only scheme: {cash_only_scheme_rows}",
        f"Generated barter schemes: {generated_rows}",
        f"Generated ammo pack item-barter schemes: {generated_ammo_pack_rows}",
        f"Ammo rows with pack template target: {ammo_pack_target_rows}",
        f"Ammo rows with pack offer id: {ammo_pack_offer_id_rows}",
        f"Ammo rows missing pack template target: {ammo_rows_without_pack_target}",
        f"Unique TplIds: {len(duplicate_tpl_counts)}",
        f"Duplicate TplIds preserved: {duplicate_tpls}",
        f"Unique barter ingredients: {len(barter_usage_counts)}",
        f"Most-used barter ingredients: {format_barter_usage_summary(barter_usage_counts, barter_name_by_tpl, limit=5)}",
        "",
        "Warnings:",
    ]

    if warnings:
        generated_warning_count = sum(
            1 for warning in warnings
            if warning.startswith("Generated barter scheme for ")
            or warning.startswith("Generated ammo pack item-barter scheme for ")
        )
        non_generated_warnings = [
            warning for warning in warnings
            if not warning.startswith("Generated barter scheme for ")
            and not warning.startswith("Generated ammo pack item-barter scheme for ")
        ]
        if generated_warning_count:
            lines.append(f"  - Generated barter scheme details suppressed in report summary: {generated_warning_count} rows")
        lines.extend(f"  - {warning}" for warning in non_generated_warnings)
    else:
        lines.append("  none")

    return "\n".join(lines) + "\n"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=f"Generate Tony config/items.json from data/assort.json v{SCRIPT_VERSION}")
    parser.add_argument("--version", action="version", version=f"generate_items_from_assort.py {SCRIPT_VERSION}")
    parser.add_argument("--assort", default="data/assort.json", help="Path to trader assort.json")
    parser.add_argument("--assort-out", default=None, help="Path to write updated assort.json when missing ammo pack offers are added. Defaults to --assort")
    parser.add_argument("--no-update-assort", dest="update_assort_missing_ammo_packs", action="store_false", help="Do not add missing standalone ammo pack offers to assort.json")
    parser.set_defaults(update_assort_missing_ammo_packs=True)
    parser.add_argument("--out", default="config/items.json", help="Path to write generated items.json")
    parser.add_argument("--catalog", default=None, help="Optional existing items.json/list used for readable names and fallback prices")
    parser.add_argument("--locale", default=None, help="Optional SPT English locale JSON used for readable item names")
    parser.add_argument("--db", action="append", default=None, help="Local db/data folder or JSON file to scan for custom item names. Can be repeated. Defaults to db and data when present")
    parser.add_argument("--no-db-names", action="store_true", help="Do not scan local db/data JSON files for custom item names")
    parser.add_argument("--report", default=None, help="Optional report output path. Defaults to <out>.report.txt")
    parser.add_argument("--keep-current-settings", "--fill-missing-only", action="store_true", help="Preserve existing items.json rows/settings and only fill missing fields or append missing assort rows")
    parser.add_argument("--overwrite-except-barter", "--preserve-current-barter", action="store_true", help="Regenerate every items.json field from assort/db, but preserve existing BarterScheme values from --current-settings")
    parser.add_argument("--current-settings", default=None, help="Optional existing items.json path used by --keep-current-settings or --overwrite-except-barter. Defaults to --catalog when provided, otherwise --out")
    parser.add_argument("--cash-only", action="store_true", help="Deprecated: items.json rows are now always generated with CashOnly=true while preserving BarterScheme for runtime randomization")
    parser.add_argument("--default-price", type=float, default=0.0, help="Fallback RUB price for barter-only rows with no catalog/tarkov.dev price")
    parser.add_argument(
        "--generate-barter-schemes",
        choices=["none", "cash-only", "missing", "all"],
        default="none",
        help=(
            "Generate non-currency BarterScheme recipes from Price. "
            "cash-only = replace simple cash schemes only; missing = replace rows without a real barter; all = replace every row."
        ),
    )
    parser.add_argument(
        "--fill-empty-barters",
        action="store_true",
        help=(
            "Shortcut for --generate-barter-schemes missing. "
            "Preserves existing real BarterScheme values and only fills rows that have no non-currency barter."
        ),
    )
    parser.add_argument("--barter-value-multiplier", type=float, default=1.0, help="Generated barter target value multiplier based on Price")
    parser.add_argument("--barter-max-components", type=int, default=4, help="Max different item types in a generated barter recipe")
    parser.add_argument("--barter-max-uses-per-item", type=int, default=0, help="Maximum number of generated offers that should use the same barter ingredient. 0 disables the cap")
    parser.add_argument("--barter-seed", type=int, default=1337, help="Seed for deterministic generated barter recipes")
    parser.add_argument("--ammo-barter-pack-size", type=int, default=30, help="Ammo generated barters are valued as this many rounds instead of one round")
    parser.add_argument("--custom-ammo-tpl", action="append", default=[], help="Extra loose ammo template ID to force as ammo. Can be repeated or comma-separated")
    parser.add_argument("--custom-ammo-pack-map", "--ammo-pack-map", dest="custom_ammo_pack_map", default=None, help="Optional JSON mapping custom loose ammo TplIds to ammo pack TplIds/pack sizes")

    tarkov_dev_group = parser.add_mutually_exclusive_group()
    tarkov_dev_group.add_argument("--tarkov-dev", dest="tarkov_dev", action="store_true", help="Use tarkov.dev API for item names/prices; default")
    tarkov_dev_group.add_argument("--no-tarkov-dev", dest="tarkov_dev", action="store_false", help="Do not call tarkov.dev; use only cache/catalog/locale/built-ins")
    parser.set_defaults(tarkov_dev=True)

    parser.add_argument("--tarkov-dev-cache", default=None, help="Path to tarkov.dev item cache. Defaults to <out dir>/tarkovdev_items_cache.json")
    parser.add_argument("--refresh-tarkov-dev-cache", "--refresh-cache", dest="refresh_tarkov_dev_cache", action="store_true", help="Ignore existing tarkov.dev cache and fetch fresh data")
    parser.add_argument("--tarkov-dev-timeout", type=float, default=20.0, help="tarkov.dev request timeout in seconds")

    return parser.parse_args()


def main() -> int:
    args = parse_args()

    assort_path = Path(args.assort)
    assort_out_path = Path(args.assort_out) if args.assort_out else assort_path
    out_path = Path(args.out)
    catalog_path = Path(args.catalog) if args.catalog else None
    locale_path = Path(args.locale) if args.locale else None
    report_path = Path(args.report) if args.report else out_path.with_suffix(out_path.suffix + ".report.txt")
    tarkov_dev_cache_path = Path(args.tarkov_dev_cache) if args.tarkov_dev_cache else out_path.parent / "tarkovdev_items_cache.json"
    current_settings_path = Path(args.current_settings) if args.current_settings else (catalog_path if catalog_path is not None else out_path)

    cli_warnings: list[str] = []
    if bool(args.fill_empty_barters):
        if str(args.generate_barter_schemes).lower() == "none":
            args.generate_barter_schemes = "missing"
            cli_warnings.append(
                "--fill-empty-barters enabled: using --generate-barter-schemes missing"
            )
        else:
            cli_warnings.append(
                f"--fill-empty-barters left --generate-barter-schemes={args.generate_barter_schemes} unchanged"
            )

    if not assort_path.exists():
        raise FileNotFoundError(f"assort file not found: {assort_path}")

    assort = load_json(assort_path)
    if not isinstance(assort, dict):
        raise ValueError("assort file must be a JSON object")

    catalog_names, catalog_prices = load_name_and_price_catalog(catalog_path)
    locale_names = load_locale_names(locale_path)
    db_name_paths = [Path(path) for path in (args.db or ["db", "data"])]
    db_names, db_name_warnings = load_db_item_names(
        db_name_paths,
        enabled=not bool(args.no_db_names),
    )
    CUSTOM_DB_NAMES.clear()
    CUSTOM_DB_NAMES.update(db_names)
    existing_item_settings: list[dict[str, Any]] = []
    current_settings_warnings: list[str] = []
    if args.keep_current_settings and args.overwrite_except_barter:
        raise ValueError("Use either --keep-current-settings or --overwrite-except-barter, not both")

    if args.keep_current_settings or args.overwrite_except_barter:
        existing_item_settings, current_settings_warnings = load_existing_item_settings(current_settings_path)

    custom_ammo_map_path = Path(args.custom_ammo_pack_map) if args.custom_ammo_pack_map else None
    custom_ammo_pack_map, custom_ammo_warnings = load_custom_ammo_pack_map(
        custom_ammo_map_path,
        default_pack_size=max(1, int(args.ammo_barter_pack_size or 30)),
    )
    db_ammo_pack_map, db_ammo_pack_warnings = load_db_ammo_pack_mappings(
        db_name_paths,
        default_pack_size=max(1, int(args.ammo_barter_pack_size or 30)),
    )
    custom_ammo_warnings.extend(db_ammo_pack_warnings)
    if db_ammo_pack_map:
        overlapping_custom_pack_rows = sorted(set(db_ammo_pack_map) & set(custom_ammo_pack_map))
        # Start with auto-detected StackSlots mappings, then let the explicit
        # --ammo-pack-map file override anything the user hand-tuned.
        merged_custom_ammo_pack_map = dict(db_ammo_pack_map)
        merged_custom_ammo_pack_map.update(custom_ammo_pack_map)
        custom_ammo_pack_map = merged_custom_ammo_pack_map
        if overlapping_custom_pack_rows:
            custom_ammo_warnings.append(
                f"Explicit --ammo-pack-map entries overrode {len(overlapping_custom_pack_rows)} auto-detected StackSlots mappings"
            )
    configure_custom_ammo_support(
        custom_ammo_tpls=parse_id_list(args.custom_ammo_tpl),
        custom_ammo_pack_map=custom_ammo_pack_map,
    )

    tarkov_dev_names, tarkov_dev_prices, tarkov_dev_warnings = get_tarkov_dev_names_and_prices(
        enabled=bool(args.tarkov_dev),
        cache_path=tarkov_dev_cache_path,
        refresh_cache=bool(args.refresh_tarkov_dev_cache),
        timeout_seconds=float(args.tarkov_dev_timeout),
    )

    output, warnings, assort_modified = generate_items(
        assort=assort,
        tarkov_dev_names=tarkov_dev_names,
        tarkov_dev_prices=tarkov_dev_prices,
        locale_names=locale_names,
        catalog_names=catalog_names,
        catalog_prices=catalog_prices,
        force_cash_only_rows=args.cash_only,
        default_price=args.default_price,
        generate_barter_schemes=args.generate_barter_schemes,
        barter_value_multiplier=args.barter_value_multiplier,
        barter_max_components=args.barter_max_components,
        barter_rng=random.Random(args.barter_seed),
        ammo_barter_pack_size=args.ammo_barter_pack_size,
        barter_max_uses_per_item=max(0, int(args.barter_max_uses_per_item or 0)),
        update_assort_missing_ammo_packs=bool(args.update_assort_missing_ammo_packs),
    )
    if args.keep_current_settings:
        output, merge_warnings = merge_current_settings(
            generated_rows=output,
            existing_rows=existing_item_settings,
            current_settings_path=current_settings_path,
        )
        warnings.extend(merge_warnings)

    if args.overwrite_except_barter:
        output, preserve_warnings = preserve_current_barter_schemes(
            generated_rows=output,
            existing_rows=existing_item_settings,
            current_settings_path=current_settings_path,
        )
        warnings.extend(preserve_warnings)

    refreshed_unknown_name_count = refresh_unknown_item_names_in_rows(
        rows=output,
        tarkov_dev_names=tarkov_dev_names,
        locale_names=locale_names,
        catalog_names=catalog_names,
    )
    if refreshed_unknown_name_count:
        warnings.append(
            f"Refreshed {refreshed_unknown_name_count} UNKNOWN/missing ItemName values from locale/catalog/db name sources"
        )

    final_barter_max_uses_per_item = max(0, int(args.barter_max_uses_per_item or 0))
    if final_barter_max_uses_per_item > 0:
        final_usage_counts = count_barter_ingredient_usage_from_rows(output)
        final_name_by_tpl = build_barter_ingredient_name_map(output)
        final_overused = {
            tpl: count
            for tpl, count in final_usage_counts.items()
            if count > final_barter_max_uses_per_item
        }
        if final_overused:
            warnings.append(
                f"WARNING: final items.json has barter ingredients over cap {final_barter_max_uses_per_item}: "
                f"{format_barter_usage_summary(Counter(final_overused), final_name_by_tpl, limit=10)}. "
                "This can happen when --keep-current-settings preserves existing tuned BarterScheme values."
            )

    always_barter_changed_count = apply_always_barter_flags(output)
    if always_barter_changed_count:
        warnings.append(
            f"Forced AlwaysBarter flags on {always_barter_changed_count} rows: "
            "false for all rows except Toolset (590c2e1186f77425357b6124)"
        )

    always_in_stock_changed_count = apply_always_in_stock_flags(output)
    if always_in_stock_changed_count:
        warnings.append(
            f"Forced AlwaysInStock flags on {always_in_stock_changed_count} rows: "
            "false for all rows except WI-FI Camera (5b4391a586f7745321235ab2) "
            "and MS2000 Marker (5991b51486f77447b112d44f)"
        )

    output = order_items_rows(output)

    warnings = tarkov_dev_warnings + db_name_warnings + current_settings_warnings + custom_ammo_warnings + cli_warnings + warnings

    if assort_modified:
        assort_out_path.parent.mkdir(parents=True, exist_ok=True)
        assort_out_path.write_text(json.dumps(assort, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")
        warnings.append(f"Updated assort.json with generated ammo pack offers: {assort_out_path}")

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(output, indent=4, ensure_ascii=False) + "\n", encoding="utf-8")

    report = build_report(out_path, output, warnings)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(report, encoding="utf-8")

    print(report)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
