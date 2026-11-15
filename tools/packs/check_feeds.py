#!/usr/bin/env python3
"""
Checks that every feed in a content pack's sources.json answers and parses, and optionally removes the ones that do not.

    python tools/packs/check_feeds.py Data/packs/web/sources.json            # report
    python tools/packs/check_feeds.py Data/packs/web/sources.json --prune    # rewrite the file without dead feeds

A feed passes when it returns HTTP 200 and contains at least one <item> or <entry>. A UA is sent because several publishers reject
the default Python one. Run it before shipping a pack, and now and then afterwards: feeds move.
"""
import argparse, concurrent.futures as cf, json, re, sys, urllib.request

UA = "Mozilla/5.0 (compatible; AiPulse-pack-check/1.0; +https://github.com/Druvo/AiPulse)"


def check(source):
    url = source["Url"]
    try:
        request = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/rss+xml, application/atom+xml, application/xml, text/xml, */*"})
        with urllib.request.urlopen(request, timeout=20) as response:
            body = response.read(400_000).decode("utf-8", "replace")
            items = len(re.findall(r"<item[\s>]|<entry[\s>]", body))
            if response.status != 200:
                return source, False, f"HTTP {response.status}"
            if items == 0:
                return source, False, "no <item>/<entry> in the response"
            return source, True, f"{items} items"
    except Exception as e:  # network errors, HTTP errors, timeouts
        return source, False, f"{type(e).__name__}: {str(e)[:80]}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--prune", action="store_true")
    args = ap.parse_args()
    sources = json.load(open(args.file, encoding="utf-8"))
    with cf.ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(check, sources))
    alive = []
    for source, ok, detail in results:
        print(("ok   " if ok else "DEAD ") + source["Name"] + "  " + detail)
        if ok:
            alive.append(source)
    print(f"{len(alive)}/{len(sources)} alive")
    if args.prune and len(alive) != len(sources):
        with open(args.file, "w", encoding="utf-8", newline="\n") as f:
            json.dump(alive, f, indent=2, ensure_ascii=False)
            f.write("\n")
        print("pruned", len(sources) - len(alive), "dead feeds")


if __name__ == "__main__":
    main()
