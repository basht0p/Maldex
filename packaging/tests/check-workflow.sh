#!/usr/bin/env bash
# Static checks for the release workflow and its CI scripts (packaging Task 4). Standalone: exits nonzero on any failure.
set -uo pipefail
cd "$(dirname "$0")/../.."
FAILS=0
fail() { echo "FAIL $*" >&2; FAILS=$((FAILS+1)); }
ok() { echo "ok   $*"; }
WF=.github/workflows/release.yml

# release.yml exists and is the only file in .github/workflows
if [ -f "$WF" ] && [ "$(find .github/workflows -type f | wc -l)" = 1 ]; then ok "release.yml is the only workflow"; else fail "release.yml must exist and be the only file in .github/workflows"; fi

if [ -f "$WF" ]; then
  # every uses: is pinned to a 40-hex SHA
  bad=$(grep -E '^\s*-?\s*uses:' "$WF" | grep -Ev 'uses:\s*[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}(\s|$)') || true
  if [ -z "$bad" ] && grep -qE 'uses:' "$WF"; then ok "all uses: pinned to a full commit SHA"; else fail "unpinned uses: ${bad:-none found}"; fi

  # the two signing secrets appear only in the build job's Sign step env
  stray=$(awk '
    /^  [A-Za-z0-9_-]+:[ \t]*$/ { job=$1 }
    /^[ \t]*- name:/ { step=$0 }
    /secrets\.RPM_GPG_(PRIVATE_KEY|PASSPHRASE)/ { if (job != "build:" || step !~ /Sign/) print NR": "$0 }
  ' "$WF")
  n=$(grep -cE 'secrets\.RPM_GPG_(PRIVATE_KEY|PASSPHRASE)' "$WF")
  if [ -z "$stray" ] && [ "$n" = 2 ]; then ok "signing secrets only in the build job's Sign step"; else fail "signing secrets misplaced (count=$n): $stray"; fi

  # only the release job has contents: write
  if [ "$(grep -c 'contents: write' "$WF")" = 1 ] && grep -qE '^permissions:' "$WF"; then ok "contents: write only once (release job)"; else fail "contents: write must appear exactly once"; fi
  if grep -qE "tags:[ ]*\['v\*'\]" "$WF" && grep -q workflow_dispatch "$WF"; then ok "triggers"; else fail "triggers must be v* tag push and workflow_dispatch"; fi
fi

# CI scripts parse
shopt -s nullglob
ci=(packaging/ci/*.sh)
if [ "${#ci[@]}" -ge 2 ] && bash -n "${ci[@]}"; then ok "bash -n packaging/ci/*.sh"; else fail "packaging/ci scripts missing or have syntax errors"; fi
if command -v shellcheck >/dev/null 2>&1 && [ "${#ci[@]}" -gt 0 ]; then shellcheck "${ci[@]}" && ok shellcheck || fail shellcheck; fi
if command -v actionlint >/dev/null 2>&1 && [ -f "$WF" ]; then actionlint "$WF" && ok actionlint || fail actionlint; fi

[ "$FAILS" = 0 ]
