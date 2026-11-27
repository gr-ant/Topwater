"""Mount a Notion page tree, let a bash script restructure it, review the change set, and apply it only when asked.

    NOTION_TOKEN=ntn_... python examples/notion_restructure.py --root <page id or URL>           # dry run: prints the changes
    NOTION_TOKEN=ntn_... python examples/notion_restructure.py --root <page id or URL> --apply   # applies them

The token comes from NOTION_TOKEN, or a NOTION_TOKEN= line in ~/.config/topwater/notion.env. The integration must be
shared with the root page (docs/connectors/notion.md). The script below stands in for what an agent would run through
the bash tool: it is written for a tree like the one in the docs (Projects, Wiki, Inbox (unsorted), Archive 2023,
Meeting Notes) and leaves alone what it doesn't find.
"""
import argparse
import asyncio
import difflib
import os
import sys
from pathlib import Path

from topwater import Shell
from topwater.connectors.notion import NotionSource

MOUNT = "/mnt/notion"

# What the agent runs. Every change stays in the machine until the host applies the change set.
RESTRUCTURE = r'''
cd /mnt/notion
inbox="Inbox (unsorted)"
project="Projects/Website Redesign"

# Website thoughts belong with the project.
[ -f "$inbox/website redesign - more thoughts.md" ] && mv "$inbox/website redesign - more thoughts.md" "$project/More thoughts.md"

# A note about the pricing page becomes a task on the project page, and the note goes to the trash.
for f in "$inbox"/*.md; do
  [ "$f" = "$inbox/index.md" ] && continue
  if grep -qi "pricing page" "$f"; then
    printf '\n## From the inbox\n\n- [ ] %s\n' "$(cat "$f")" >> "$project/index.md"
    rm "$f"
  fi
done

# "Copy of Onboarding" is an older, shorter copy of Wiki/Onboarding when each of its lines starts a line there.
copy="$inbox/Copy of Onboarding.md"
if [ -f "$copy" ] && awk 'NR==FNR { orig[++n] = $0; next }
     { ok = 0; for (i = 1; i <= n; i++) if (index(orig[i], $0) == 1) ok = 1; if (!ok) bad++ }
     END { exit bad > 0 }' Wiki/Onboarding.md "$copy"; then
  rm "$copy"
fi

# Pages with no text go to the trash (grep says 1 only for a file it read and found empty, never for one it could not
# read); the idea goes to Projects; the personal note gets a clearer name.
for f in "$inbox"/*.md; do
  [ "$f" = "$inbox/index.md" ] && continue
  grep -q '[^[:space:]]' "$f"; [ $? -eq 1 ] && rm "$f"
done
[ -f "$inbox/random idea.md" ] && mv "$inbox/random idea.md" "Projects/Docs search idea.md"
grep -lx "call dentist.*" "$inbox"/Notes*.md 2>/dev/null | while read -r f; do mv "$f" "$inbox/Personal reminders.md"; done

printf '\nSorted by the agent: website notes are under Projects, duplicates and empty pages are in the trash.\n' >> "$inbox/index.md"
'''


def token() -> str:
    if os.environ.get("NOTION_TOKEN"):
        return os.environ["NOTION_TOKEN"]
    env = Path.home() / ".config" / "topwater" / "notion.env"
    for line in env.read_text().splitlines() if env.exists() else []:
        if line.startswith("NOTION_TOKEN="):
            return line.split("=", 1)[1].strip().strip('"')
    sys.exit("Set NOTION_TOKEN (an internal integration's token) or put NOTION_TOKEN= in ~/.config/topwater/notion.env")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--root", default=os.environ.get("NOTION_ROOT"), required="NOTION_ROOT" not in os.environ, help="the page to mount (id or URL)")
    ap.add_argument("--apply", action="store_true", help="apply the change set to Notion (default: only show it)")
    args = ap.parse_args()

    secret = token()
    source = NotionSource(lambda: secret, args.root, id="demo")
    shell = Shell()
    shell.max_call_duration = 300  # Notion allows about three requests a second
    shell.mount(MOUNT, source, "staged")
    print(f"Mounted {args.root} at {MOUNT} (staged)\n")

    for command in ("find /mnt/notion -name '*.md' | sort", "grep -c '' /mnt/notion/'Inbox (unsorted)'/*.md /mnt/notion/Wiki/Onboarding.md", RESTRUCTURE):
        r = shell.run(command)
        print("$ " + command.strip().split("\n")[0] + (" ..." if "\n" in command.strip() else ""))
        print(r.stdout + r.stderr, end="")

    changes = shell.pending_changes(MOUNT)
    print(f"\nChange set ({len(changes)} changes):")
    for c in changes:
        print(f"  {c.kind} {c.path}" + (f" -> {c.to}" if c.to else ""))
        if c.kind == "write":
            before = asyncio.run(source.read(c.path, 10_000_000)).decode() if c.version else ""
            for line in list(difflib.unified_diff(before.splitlines(), c.content.decode().splitlines(), "before", "after", lineterm=""))[2:]:
                print("      " + line)
    if not args.apply:
        print("\nNothing was changed in Notion. Run again with --apply to apply these changes.")
        return
    result = shell.apply_changes(MOUNT)
    print(f"\nApplied {len(result.applied)} of {len(changes)} changes.")
    if result.failed is not None:
        print(f"Stopped at {result.failed.kind} {result.failed.path}: {result.error}" + (" (changed in Notion meanwhile)" if result.conflict else ""))
    shell.refresh_mount(MOUNT)
    print(shell.run("find /mnt/notion | sort").stdout, end="")


if __name__ == "__main__":
    main()
