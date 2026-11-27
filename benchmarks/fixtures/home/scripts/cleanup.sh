#!/bin/bash
# Remove temporary files from ~/tmp and say how many went.
n=0
for f in "$HOME"/tmp/*.tmp; do
  [ -e "$f" ] || continue
  rm -- "$f"
  n=$((n + 1))
done
echo "cleanup: removed $n temporary files"
