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

  # the two signing secrets appear only in the build job's Sign step env, and no other use of secrets exists
  # (catches secrets[...], toJSON(secrets), secrets.OTHER)
  stray=$(awk '
    /^  [A-Za-z0-9_-]+:[ \t]*$/ { job=$1 }
    /^[ \t]*- name:/ { step=$0 }
    /^[ \t]*#/ { next }
    /secrets/ {
      if ($0 ~ /secrets\.RPM_GPG_(PRIVATE_KEY|PASSPHRASE)[ ]*\}\}/ && job == "build:" && step ~ /Sign/) next
      print NR": "$0
    }
  ' "$WF")
  n=$(grep -cE 'secrets\.RPM_GPG_(PRIVATE_KEY|PASSPHRASE)' "$WF")
  if [ -z "$stray" ] && [ "$n" = 2 ]; then ok "signing secrets only in the build job's Sign step"; else fail "signing secrets misplaced (count=$n): $stray"; fi

  # contents: write only under the release job, once
  cw=$(awk '/^  [A-Za-z0-9_-]+:[ \t]*$/ { job=$1 } /contents: write/ { print job }' "$WF")
  if [ "$cw" = "release:" ] && grep -qE '^permissions:' "$WF"; then ok "contents: write only in the release job"; else fail "contents: write must appear exactly once, under release (found: ${cw:-none})"; fi
  if grep -qE "tags:[ ]*\['v\*'\]" "$WF" && grep -q workflow_dispatch "$WF"; then ok "triggers"; else fail "triggers must be v* tag push and workflow_dispatch"; fi

  # a workflow_dispatch (even on a tag ref) must not publish; the version comes from the event, not the ref type
  if grep -qE "^\s*if:.*github\.event_name == 'push'.*refs/tags/v" "$WF"; then ok "release gated on push event"; else fail "release job if: must require github.event_name == 'push' and a v tag"; fi
  if grep -q 'github.event_name' "$WF" && ! grep -q 'ref_type' "$WF" && grep -q 'dryrun' "$WF"; then ok "version chosen by event"; else fail "build version must be chosen by github.event_name, not ref_type"; fi
  if [ "$(grep -c 'persist-credentials: false' "$WF")" = "$(grep -c 'actions/checkout@' "$WF")" ]; then ok "checkouts do not persist credentials"; else fail "every checkout needs persist-credentials: false"; fi
  if grep -q "rpm -K.*signatures OK" "$WF"; then ok "rpm signature asserted"; else fail "smoke-rpm must assert 'signatures OK' from rpm -K"; fi
  # CI-only: the rpm job installs ~450 packages from a mirror; it needs fast mirrors, parallel downloads and a timeout
  if grep -q 'fastestmirror=True' "$WF" && grep -q 'max_parallel_downloads' "$WF" && awk '/^  smoke-rpm:/{j=1;next} /^  [A-Za-z0-9_-]+:[ \t]*$/{j=0} j&&/timeout-minutes:/{f=1} END{exit !f}' "$WF"; then ok "smoke-rpm dnf tuning and timeout"; else fail "smoke-rpm needs dnf fastestmirror/max_parallel_downloads and timeout-minutes"; fi
  if grep -q 'if: always()' "$WF"; then ok "always() cleanup step"; else fail "missing if: always() cleanup step"; fi
fi

# CI scripts parse
shopt -s nullglob
ci=(packaging/ci/*.sh)
if [ "${#ci[@]}" -ge 2 ] && bash -n "${ci[@]}"; then ok "bash -n packaging/ci/*.sh"; else fail "packaging/ci scripts missing or have syntax errors"; fi
if command -v shellcheck >/dev/null 2>&1 && [ "${#ci[@]}" -gt 0 ]; then shellcheck "${ci[@]}" && ok shellcheck || fail shellcheck; fi
if command -v actionlint >/dev/null 2>&1 && [ -f "$WF" ]; then actionlint "$WF" && ok actionlint || fail actionlint; fi

[ "$FAILS" = 0 ]
