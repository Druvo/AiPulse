#!/usr/bin/env python3
"""
Writes glossary.json, tools.json, practices.json and learning.json for each content pack from the readable lists in tools/packs/content/<id>.py,
in the exact shapes the app loads. Re-run after editing a content file; PackContentTests checks the result (valid JSON, unique keys,
every related term resolves, every link is absolute).
"""
import importlib, json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(os.path.dirname(os.path.dirname(HERE)), "Data", "packs")
sys.path.insert(0, os.path.join(HERE, "content"))


def dump(path, rows):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(rows, f, indent=2, ensure_ascii=False)
        f.write("\n")


def main():
    for pid in ("web", "dotnet", "python", "devops", "security"):
        content = importlib.import_module(pid)
        folder = os.path.join(ROOT, pid)
        os.makedirs(folder, exist_ok=True)

        dump(os.path.join(folder, "glossary.json"), [
            {"Term": t, "Aliases": aliases, "Category": cat, "ShortDef": short, "Details": details, "WhenToUse": when,
             "Related": related, **({"LearnMoreUrl": url} if url else {})}
            for (t, aliases, cat, short, details, when, related, url) in content.GLOSSARY])

        dump(os.path.join(folder, "tools.json"), [
            {"Name": n, "Category": cat, "OneLiner": one, "BestFor": best, "NotIdealFor": notideal, "TokenTips": tips, "Url": url}
            for (n, cat, one, best, notideal, tips, url) in content.TOOLS])

        dump(os.path.join(folder, "practices.json"), [
            {"Title": t, "Category": cat, "Body": body} for (t, cat, body) in content.PRACTICES])

        dump(os.path.join(folder, "learning.json"), [
            {"Title": t, "Level": level, "Why": why,
             "Steps": [{"Text": text, "Url": url} for (text, url) in steps], "RelatedTerms": related}
            for (t, level, why, steps, related) in content.LEARNING])

        print(pid, len(content.GLOSSARY), "terms,", len(content.TOOLS), "tools,", len(content.LEARNING), "modules")


if __name__ == "__main__":
    main()
