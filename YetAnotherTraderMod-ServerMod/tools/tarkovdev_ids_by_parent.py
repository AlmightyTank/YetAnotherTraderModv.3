import argparse
import json
import time
import urllib.request
from pathlib import Path

API_URL = "https://api.tarkov.dev/graphql"

QUERY = """
query {
  items(lang: en) {
    id
    name
    shortName
    bsgCategoryId
    category {
      id
      name
      parent {
        id
        name
        parent {
          id
          name
          parent {
            id
            name
            parent {
              id
              name
            }
          }
        }
      }
    }
    categories {
      id
      name
      parent {
        id
        name
        parent {
          id
          name
          parent {
            id
            name
            parent {
              id
              name
            }
          }
        }
      }
    }
    handbookCategories {
      id
      name
      parent {
        id
        name
        parent {
          id
          name
          parent {
            id
            name
            parent {
              id
              name
            }
          }
        }
      }
    }
  }
}
"""

def post_graphql(query):
    data = json.dumps({"query": query}).encode("utf-8")

    req = urllib.request.Request(
        API_URL,
        data=data,
        headers={
            "Content-Type": "application/json",
            "User-Agent": "YATM-tools/1.0"
        },
        method="POST"
    )

    with urllib.request.urlopen(req, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))

def cache_is_valid(cache_path, cache_hours):
    if not cache_path.exists():
        return False

    if cache_hours <= 0:
        return True

    age_seconds = time.time() - cache_path.stat().st_mtime
    max_age_seconds = cache_hours * 60 * 60

    return age_seconds <= max_age_seconds

def load_items_with_cache(cache_path, cache_hours, refresh_cache):
    cache_path.parent.mkdir(parents=True, exist_ok=True)

    if not refresh_cache and cache_is_valid(cache_path, cache_hours):
        with open(cache_path, "r", encoding="utf-8") as f:
            cached = json.load(f)

        print(f"Using cached tarkov.dev data: {cache_path}")
        return cached["data"]["items"]

    print("Fetching item data from tarkov.dev...")

    result = post_graphql(QUERY)

    if "errors" in result:
        raise RuntimeError(json.dumps(result["errors"], indent=4))

    with open(cache_path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=4, ensure_ascii=False)

    print(f"Saved cache: {cache_path}")

    return result["data"]["items"]

def category_chain_contains(category, parent_id):
    current = category

    while current:
        if current.get("id") == parent_id:
            return True

        current = current.get("parent")

    return False

def item_matches_parent(item, parent_id):
    if item.get("bsgCategoryId") == parent_id:
        return True

    if category_chain_contains(item.get("category"), parent_id):
        return True

    for category in item.get("categories") or []:
        if category_chain_contains(category, parent_id):
            return True

    for category in item.get("handbookCategories") or []:
        if category_chain_contains(category, parent_id):
            return True

    return False

def main():
    parser = argparse.ArgumentParser()

    parser.add_argument(
        "--parent",
        required=True,
        help="Parent/category ID to match"
    )

    parser.add_argument(
        "--out",
        required=True,
        help="Output JSON file"
    )

    parser.add_argument(
        "--cache",
        default="data/cache/tarkovdev_items_cache.json",
        help="Local tarkov.dev cache file"
    )

    parser.add_argument(
        "--cache-hours",
        type=int,
        default=168,
        help="How long to use cached data before refreshing. Default: 168 hours / 7 days. Use 0 to never expire."
    )

    parser.add_argument(
        "--refresh-cache",
        action="store_true",
        help="Force refresh the tarkov.dev cache"
    )

    args = parser.parse_args()

    cache_path = Path(args.cache)
    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    items = load_items_with_cache(
        cache_path=cache_path,
        cache_hours=args.cache_hours,
        refresh_cache=args.refresh_cache
    )

    matched_ids = [
        item["id"]
        for item in items
        if item_matches_parent(item, args.parent)
    ]

    matched_ids = sorted(set(matched_ids))

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(matched_ids, f, indent=4)

    print(f"Wrote {len(matched_ids)} IDs to {out_path}")

if __name__ == "__main__":
    main()