import argparse
import json
import re
import urllib.request
from pathlib import Path


API_URL = "https://api.tarkov.dev/graphql"


QUERY = """
query TonyWesternBlacklistItems {
  items {
    id
    name
    shortName
    types
    bsgCategoryId
    category {
      id
      name
      normalizedName
    }
    categories {
      id
      name
      normalizedName
    }
    handbookCategories {
      id
      name
      normalizedName
    }
    properties {
      __typename
      ... on ItemPropertiesAmmo {
        caliber
        ammoType
      }
    }
  }
}
"""


# Things Tony should reject.
# This intentionally catches guns, mags, suppressors, optics, and western gun parts.
WESTERN_TERMS = [
    # weapon platforms
    "m4a1", "m4", "m16", "ar-15", "ar15", "adar", "tx-15", "tx15",
    "hk 416", "hk416", "g36", "scar", "scar-l", "scar-h",
    "mcx", "mpx", "mp5", "mp7", "ump", "p90",
    "sa-58", "fal", "m1a", "rsass", "sr-25", "sr25", "g28",
    "mk-18", "mk18", "m700", "axmc",
    "vector", "m870", "m590", "590a1", "benelli", "m3 super 90",
    "glock", "g17", "g18c", "p226", "m9a3", "five-seven", "fn 5-7",
    "m1911", "1911", "m45a1", "usp",

    # brands/manufacturers/optic brands
    "colt", "daniel defense", "dd ", "kac", "knights armament",
    "lmt", "magpul", "bcm", "sig sauer", "sig ", "fn ",
    "hk ", "heckler", "eotech", "aimpoint", "trijicon", "leupold",
    "vortex", "nightforce", "steiner", "ncstar", "hogue",
    "mesa tactical", "troy", "jp enterprises", "surefire", "gemtech",
    "silencerco", "desert tech",

    # western/NATO calibers to reject as ammo/mags
    "5.56x45", "5.56", ".300 blackout", "300 blackout", ".300 blk",
    "7.62x51", ".308", ".338 lapua", ".338 lm",
    ".45 acp", "4.6x30", "5.7x28", ".357 magnum"
]


# These words help avoid catching random barter/food/armor items that happen to have a brand-like word.
WEAPON_RELATED_TERMS = [
    "weapon", "gun", "rifle", "carbine", "pistol", "smg", "shotgun",
    "ammo", "round", "cartridge", "magazine", "mag ",
    "suppressor", "silencer", "muzzle", "flash hider", "compensator",
    "scope", "sight", "optic", "reflex", "mount", "ring",
    "receiver", "barrel", "handguard", "stock", "buffer tube",
    "charging handle", "gas block", "pistol grip", "foregrip",
    "tactical device", "rail"
]


# Things Tony should still accept even if a loose rule accidentally touches them.
# Keep this small; add to it from the report when needed.
ALLOW_TERMS = [
    "ak-", "akm", "aks", "ak-74", "ak-101", "ak-102", "ak-103", "ak-104", "ak-105",
    "rpk", "pp-19", "kedr", "klin", "vityaz", "saiga",
    "as val", "vss", "svd", "sv-98", "mosin", "toz", "makarov",
    "tt pistol", "aps", "apb", "pb pistol",
    "zenit", "b-10", "b-11", "b-13", "b-30", "b-33", "psO", "pso"
]


def post_graphql(query: str) -> dict:
    body = json.dumps({"query": query}).encode("utf-8")
    req = urllib.request.Request(
        API_URL,
        data=body,
        headers={
            "Content-Type": "application/json",
            "Accept": "application/json",
            "User-Agent": "Tony_Origins blacklist generator"
        },
        method="POST"
    )

    with urllib.request.urlopen(req, timeout=60) as response:
        payload = json.loads(response.read().decode("utf-8"))

    if "errors" in payload:
        raise RuntimeError(json.dumps(payload["errors"], indent=2))

    return payload["data"]


def item_text(item: dict) -> str:
    chunks = [
        item.get("name") or "",
        item.get("shortName") or "",
        " ".join(item.get("types") or []),
        item.get("bsgCategoryId") or ""
    ]

    for cat in item.get("categories") or []:
        chunks.append(cat.get("name") or "")
        chunks.append(cat.get("normalizedName") or "")
        chunks.append(cat.get("id") or "")

    for cat in item.get("handbookCategories") or []:
        chunks.append(cat.get("name") or "")
        chunks.append(cat.get("normalizedName") or "")
        chunks.append(cat.get("id") or "")

    props = item.get("properties")
    if props:
        chunks.append(props.get("__typename") or "")
        chunks.append(props.get("caliber") or "")
        chunks.append(props.get("ammoType") or "")

    return " ".join(chunks).lower()


def has_term(text: str, terms: list[str]) -> str | None:
    for term in terms:
        t = term.lower()
        if t.strip() in text:
            return term
    return None


def is_weapon_related(text: str) -> bool:
    return has_term(text, WEAPON_RELATED_TERMS) is not None


def classify(item: dict) -> tuple[bool, str]:
    text = item_text(item)

    allow_hit = has_term(text, ALLOW_TERMS)
    if allow_hit:
        return False, f"allowed by exception: {allow_hit}"

    western_hit = has_term(text, WESTERN_TERMS)
    if not western_hit:
        return False, "no western term"

    if not is_weapon_related(text):
        return False, f"western term found but not weapon-related: {western_hit}"

    return True, f"western weapon-related match: {western_hit}"


def load_existing_base_ids(base_path: Path) -> list[str]:
    if not base_path or not base_path.exists():
        return []

    data = json.loads(base_path.read_text(encoding="utf-8"))
    return data.get("items_buy_prohibited", {}).get("id_list", []) or []


def apply_to_base_json(base_path: Path, ids: list[str]) -> None:
    data = json.loads(base_path.read_text(encoding="utf-8"))

    data.setdefault("items_buy_prohibited", {})
    data["items_buy_prohibited"].setdefault("category", [])
    data["items_buy_prohibited"]["id_list"] = ids

    base_path.write_text(json.dumps(data, indent=4, ensure_ascii=False), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-json", help="Optional path to Tony base.json to update")
    parser.add_argument("--apply", action="store_true", help="Actually update base.json")
    parser.add_argument("--out", default="config/tony_western_buy_blacklist.generated.json")
    parser.add_argument("--report", default="tools/report_tony_western_buy_blacklist.txt")
    args = parser.parse_args()

    data = post_graphql(QUERY)
    items = data["items"]

    blocked = []
    report_rows = []

    for item in items:
        should_block, reason = classify(item)
        if should_block:
            blocked.append({
                "id": item["id"],
                "name": item.get("name") or "",
                "shortName": item.get("shortName") or "",
                "types": item.get("types") or [],
                "reason": reason
            })

    blocked.sort(key=lambda x: x["name"].lower())

    generated_ids = [x["id"] for x in blocked]

    existing_ids = []
    base_path = Path(args.base_json) if args.base_json else None
    if base_path:
        existing_ids = load_existing_base_ids(base_path)

    final_ids = sorted(set(existing_ids + generated_ids))

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(final_ids, indent=4), encoding="utf-8")

    report_path = Path(args.report)
    report_path.parent.mkdir(parents=True, exist_ok=True)

    report_lines = [
        "Tony Western buy blacklist report",
        "=================================",
        f"Total Tarkov.dev items scanned: {len(items)}",
        f"Generated blacklist IDs: {len(generated_ids)}",
        f"Existing base.json blacklist IDs: {len(existing_ids)}",
        f"Final merged IDs: {len(final_ids)}",
        "",
        "Blocked items:",
        ""
    ]

    for item in blocked:
        report_lines.append(f"{item['id']} | {item['name']} | {item['reason']}")

    report_path.write_text("\n".join(report_lines), encoding="utf-8")

    if args.apply:
        if not base_path:
            raise RuntimeError("--apply requires --base-json")
        apply_to_base_json(base_path, final_ids)

    print(f"Wrote blacklist: {out_path}")
    print(f"Wrote report: {report_path}")
    if args.apply:
        print(f"Updated base.json: {base_path}")
    else:
        print("Dry run only. Use --apply to update Tony base.json.")


if __name__ == "__main__":
    main()