# tools/find_groundzero_safe_ids.py
# Run from anywhere:
# py tools/find_groundzero_safe_ids.py "C:\RealSPT"

import json
import math
import re
import sys
from pathlib import Path

DOOR_POS = {
    "x": 58.2430344,
    "y": 24.5026741,
    "z": 172.438
}

SEARCH_RADIUS = 12.0

SAFE_WORDS = [
    "safe",
    "сейф",
    "container_safe",
    "scontainer_safe",
    "s_container_safe",
    "lootable_safe"
]

LOCATION_WORDS = [
    "sandbox",
    "groundzero",
    "ground_zero"
]


def strip_jsonc(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    text = re.sub(r"(^|[^:])//.*", r"\1", text)
    text = re.sub(r",(\s*[}\]])", r"\1", text)
    return text


def dist(a, b):
    return math.sqrt(
        (a["x"] - b["x"]) ** 2 +
        (a["y"] - b["y"]) ** 2 +
        (a["z"] - b["z"]) ** 2
    )


def as_float(value):
    try:
        return float(value)
    except Exception:
        return None


def get_pos(obj):
    if not isinstance(obj, dict):
        return None

    possible = [
        obj.get("position"),
        obj.get("Position"),
        obj.get("pos"),
        obj.get("Pos"),
        obj.get("_props", {}).get("position") if isinstance(obj.get("_props"), dict) else None,
        obj.get("_props", {}).get("Position") if isinstance(obj.get("_props"), dict) else None,
        obj.get("transform", {}).get("position") if isinstance(obj.get("transform"), dict) else None,
        obj.get("Transform", {}).get("Position") if isinstance(obj.get("Transform"), dict) else None,
    ]

    for p in possible:
        if isinstance(p, dict):
            x = as_float(p.get("x") or p.get("X"))
            y = as_float(p.get("y") or p.get("Y"))
            z = as_float(p.get("z") or p.get("Z"))
            if x is not None and y is not None and z is not None:
                return {"x": x, "y": y, "z": z}

        if isinstance(p, list) and len(p) >= 3:
            x = as_float(p[0])
            y = as_float(p[1])
            z = as_float(p[2])
            if x is not None and y is not None and z is not None:
                return {"x": x, "y": y, "z": z}

    return None


def object_text(obj):
    try:
        return json.dumps(obj, ensure_ascii=False).lower()
    except Exception:
        return str(obj).lower()


def get_id(obj):
    if not isinstance(obj, dict):
        return ""

    for key in ["id", "_id", "Id", "ID", "templateId", "template", "_tpl", "tpl"]:
        value = obj.get(key)
        if isinstance(value, str) and value:
            return value

    return ""


def get_name(obj):
    if not isinstance(obj, dict):
        return ""

    for key in ["name", "Name", "template", "Template", "path", "Path"]:
        value = obj.get(key)
        if isinstance(value, str) and value:
            return value

    return ""


def walk(obj, file_path, results):
    if isinstance(obj, dict):
        text = object_text(obj)
        pos = get_pos(obj)

        looks_like_safe = any(word in text for word in SAFE_WORDS)
        looks_like_container = any(word in text for word in ["container", "loot", "static"])

        if pos:
            distance = dist(pos, DOOR_POS)

            if distance <= SEARCH_RADIUS and (looks_like_safe or looks_like_container):
                results.append({
                    "distance": distance,
                    "id": get_id(obj),
                    "name": get_name(obj),
                    "position": pos,
                    "safeMatch": looks_like_safe,
                    "file": str(file_path)
                })

        for value in obj.values():
            walk(value, file_path, results)

    elif isinstance(obj, list):
        for value in obj:
            walk(value, file_path, results)


def main():
    if len(sys.argv) < 2:
        print('Usage: py tools/find_groundzero_safe_ids.py "C:\\RealSPT"')
        sys.exit(1)

    root = Path(sys.argv[1])

    if not root.exists():
        print(f"SPT path does not exist: {root}")
        sys.exit(1)

    files = []
    for ext in ["*.json", "*.jsonc"]:
        files.extend(root.rglob(ext))

    map_files = []
    for file in files:
        path_lower = str(file).lower()
        if any(word in path_lower for word in LOCATION_WORDS):
            map_files.append(file)

    results = []

    for file in map_files:
        try:
            raw = file.read_text(encoding="utf-8")
            data = json.loads(strip_jsonc(raw))
            walk(data, file, results)
        except Exception:
            continue

    results.sort(key=lambda x: (not x["safeMatch"], x["distance"]))

    print()
    print("Ground Zero safe/container candidates near:")
    print(DOOR_POS)
    print("=" * 80)

    if not results:
        print("No safe/container candidates found.")
        print("Raise SEARCH_RADIUS from 12.0 to 25.0 and run again.")
        return

    for r in results[:80]:
        print()
        print(f"Distance: {r['distance']:.2f}m")
        print(f"ID:       {r['id']}")
        print(f"Name:     {r['name']}")
        print(f"Safe?:    {r['safeMatch']}")
        print(f"Pos:      {r['position']}")
        print(f"File:     {r['file']}")


if __name__ == "__main__":
    main()