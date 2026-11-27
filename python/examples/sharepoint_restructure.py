"""Mount a SharePoint site staged, let a bash script restructure it, print the plan, and apply it only with --apply.

    python examples/sharepoint_restructure.py                  # dry run: the change set, nothing written
    python examples/sharepoint_restructure.py --apply          # apply it (conflicts are checked per entry)
    python examples/sharepoint_restructure.py --base zz-copy --item "zz copy Status page" --apply

The script is what an agent would run through its bash tool: it files the Inbox into Projects and Archive, keeps one of
each "Budget final*" by a hash of its text, names "Untitled document.docx" after its first line (or its date when it is
empty), marks the Project Tracker item "Status page" as In progress, and in the Marketing subsite drops "Copy of" the
plan when its text is the plan's (or files it under Campaigns/old when it is an older draft) and files the post drafts. --base runs it on a folder inside Documents (a scratch copy) instead of the
library's top, --item names the tracker item. Settings and sign-in: graph_auth.py (docs/connectors/graph.md).
"""
from __future__ import annotations

import argparse
import shlex
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import graph_auth  # noqa: E402

from topwater import Shell  # noqa: E402
from topwater.connectors.graph import GraphSource  # noqa: E402

SCRIPT = r"""
set -u
cd {docs}
mkdir -p Projects/Designs Archive
# one of each budget file by content: Office files carry their own metadata, so compare the text, not the bytes
declare -A seen
for f in Inbox/Budget\ final*.xlsx Budget\ final*.xlsx; do
  [ -f "$f" ] || continue
  h=$(to-markdown "$f" | sha256sum | cut -c1-64)
  if [ -n "${{seen[$h]:-}}" ]; then rm -- "$f"; echo "duplicate of ${{seen[$h]}}: $f (removed)"; else seen[$h]=$f; fi
done
for f in Inbox/*; do
  case "$f" in
    *mockup*|*.png)                    mv -- "$f" Projects/Designs/ ;;
    *contract*|*notes.txt|*Processed)  mv -- "$f" Archive/ ;;
    *)                                 [ -f "$f" ] && mv -- "$f" Projects/ ;;
  esac
done
# name the untitled document after its first line of text, or its date when it has none
if [ -f 'Untitled document.docx' ]; then
  title=$(to-markdown 'Untitled document.docx' | grep -m1 '[[:alnum:]]' | sed 's/^#* *//' | tr -d '/:*?"<>|\\' | cut -c1-60)
  name="${{title:-Notes $(date -r 'Untitled document.docx' +%Y-%m-%d)}}.docx"
  mv 'Untitled document.docx' "$name" && echo "renamed: Untitled document.docx -> $name"
fi
sed -i 's/^Status: .*/Status: "In progress"/' {item} && echo "marked In progress: $(basename {item} .md)"
if [ -d {mkt} ]; then
  cd {mkt}
  plan='Campaigns/Black Friday 2026 - plan.docx'; copy='Inbox/Copy of Black Friday 2026 - plan.docx'
  if [ -f "$copy" ] && [ -f "$plan" ]; then
    if [ "$(to-markdown "$copy" | sha256sum)" = "$(to-markdown "$plan" | sha256sum)" ]; then
      rm -- "$copy"; echo "duplicate of $plan: $copy (removed)"
    else
      mkdir -p Campaigns/old && mv -- "$copy" Campaigns/old/ && echo "older draft of $plan: $copy (moved to Campaigns/old)"
    fi
  fi
  [ -f 'Inbox/social post drafts.md' ] && mkdir -p Campaigns/Drafts && mv 'Inbox/social post drafts.md' Campaigns/Drafts/
fi
true
"""


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--apply", action="store_true", help="apply the change set (default: print it only)")
    ap.add_argument("--base", default="", help="a folder inside Documents (and Marketing/Documents) to work on")
    ap.add_argument("--item", default="Status page", help="the Project Tracker item to mark In progress")
    a = ap.parse_args()
    settings, token = graph_auth.settings(), graph_auth.provider()
    if not settings or not token:
        sys.exit("sign in first: python examples/graph_auth.py sign-in")
    source = GraphSource(token, site=settings["GRAPH_SITE_URL"])
    shell = Shell()
    shell.max_call_duration = 300
    shell.mount("/mnt/sp", source, "staged")
    sub = f"/{a.base}" if a.base else ""
    script = SCRIPT.format(docs=shlex.quote(f"/mnt/sp/Documents{sub}"), mkt=shlex.quote(f"/mnt/sp/Marketing/Documents{sub}"),
                           item=shlex.quote(f"/mnt/sp/Project Tracker/{a.item}.md"))
    # a remote site is listed in parts (5 seconds when mounted); list the folders the script globs in first, as an agent
    # would after a "not listed yet"
    shell.run(f"find {shlex.quote(f'/mnt/sp/Documents{sub}')} {shlex.quote(f'/mnt/sp/Marketing/Documents{sub}')} >/dev/null 2>&1")
    r = shell.run(script)
    print(r.stdout, end="")
    if r.stderr:
        print(r.stderr, end="", file=sys.stderr)
    changes = shell.pending_changes("/mnt/sp")
    print(f"\n{len(changes)} changes:")
    for c in changes:
        line = f"  {c.kind:6} {c.path}" + (f" -> {c.to}" if c.to else "")
        note = source.note(c)
        print(line + (f"\n         note: {note}" if note else ""))
    if not a.apply:
        print("\ndry run: nothing was written (add --apply to apply)")
        return
    res = shell.apply_changes("/mnt/sp")
    print(f"\napplied {len(res.applied)} of {len(changes)}")
    if res.failed is not None:
        print(f"stopped at {res.failed.kind} {res.failed.path}: {'conflict: ' if res.conflict else ''}{res.error}")
        print(f"not tried: {len(res.not_tried)}")
        sys.exit(1)


if __name__ == "__main__":
    main()
