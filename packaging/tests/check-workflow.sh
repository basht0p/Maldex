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
  # CI-only: the rpm job installs ~450 packages from a mirror; it needs fast mirrors, parallel downloads and a timeout (all in smoke-rpm)
  rpmjob=$(awk '/^  smoke-rpm:/{j=1;print;next} /^  [A-Za-z0-9_-]+:[ \t]*$/{j=0} j' "$WF")
  if printf '%s\n' "$rpmjob" | grep -q 'fastestmirror=True' && printf '%s\n' "$rpmjob" | grep -q 'max_parallel_downloads' && printf '%s\n' "$rpmjob" | grep -q 'timeout-minutes:'; then ok "smoke-rpm dnf tuning and timeout"; else fail "smoke-rpm needs dnf fastestmirror/max_parallel_downloads and timeout-minutes"; fi
  # a branch push must not be mistaken for a release tag: the tag version needs the push event AND a v* ref name
  if grep -qF 'if [ "$EVENT" = push ] && [[ "$REF_NAME" == v* ]]; then' "$WF"; then ok "tag version needs push event and v* ref"; else fail "version derivation must require EVENT=push and [[ \"\$REF_NAME\" == v* ]]"; fi
  if grep -q 'if: always()' "$WF"; then ok "always() cleanup step"; else fail "missing if: always() cleanup step"; fi
  # a top-level concurrency group keyed on the ref, never cancelling a run in progress (a re-pushed tag must not race a release)
  conc=$(awk '/^concurrency:/{c=1;next} /^[^ #]/{c=0} c' "$WF")
  if printf '%s\n' "$conc" | grep -qE '^  group: .*\$\{\{ github\.ref \}\}' && printf '%s\n' "$conc" | grep -qE '^  cancel-in-progress: false$'; then ok "concurrency group per ref, no cancel-in-progress"; else fail "release.yml needs a top-level concurrency: group keyed on github.ref with cancel-in-progress: false"; fi

  # smoke-deb runs the app against a throwaway SQL Server Express container (the smoke then creates an admin and signs in)
  debjob=$(awk '/^  smoke-deb:/{j=1;print;next} /^  [A-Za-z0-9_-]+:[ \t]*$/{j=0} j' "$WF")
  if printf '%s\n' "$debjob" | grep -q 'mcr.microsoft.com/mssql/server:2025-latest' \
     && printf '%s\n' "$debjob" | grep -q 'MSSQL_PID=Express' && printf '%s\n' "$debjob" | grep -q 'ACCEPT_EULA=Y' \
     && printf '%s\n' "$debjob" | grep -q 'MALDEX_SMOKE_SQL'; then
    ok "smoke-deb has an mssql service and MALDEX_SMOKE_SQL"
  else
    fail "smoke-deb needs the mssql 2025-latest container (MSSQL_PID=Express, ACCEPT_EULA=Y) and MALDEX_SMOKE_SQL"
  fi
  # smoke-rpm stays database-free (the closed-port variant of the smoke)
  if printf '%s\n' "$rpmjob" | grep -q 'MALDEX_SMOKE_SQL\|mssql'; then fail "smoke-rpm must not use SQL Server"; else ok "smoke-rpm has no SQL Server"; fi
  # the SA password: generated in the job, masked before anything else, passed on by environment, never literal or echoed
  sa_ok=1
  printf '%s\n' "$debjob" | grep -q 'openssl rand' || sa_ok=0
  printf '%s\n' "$debjob" | grep -q '::add-mask::' || sa_ok=0
  printf '%s\n' "$debjob" | grep -q 'GITHUB_ENV' || sa_ok=0
  # no literal password: MSSQL_SA_PASSWORD only appears as a bare name (-e NAME) or an assignment from the generated value
  if printf '%s\n' "$debjob" | grep -E 'MSSQL_SA_PASSWORD=' | grep -Ev 'MSSQL_SA_PASSWORD="?\$(\{?pw\}?|\{?MSSQL_SA_PASSWORD\}?)"?' | grep -q .; then sa_ok=0; fi
  # the value is only ever handed on (echo/printf/cat of it may not appear except the add-mask and GITHUB_ENV lines)
  if printf '%s\n' "$debjob" | grep -E '(echo|printf|cat)[^|>]*(\$pw|\$\{pw\}|MSSQL_SA_PASSWORD|MALDEX_SMOKE_SQL)' \
       | grep -Ev '::add-mask::|GITHUB_ENV' | grep -q .; then sa_ok=0; fi
  if printf '%s\n' "$debjob" | grep -qE 'set -x|bash -x|docker (run|exec).*(-e|--env) *MSSQL_SA_PASSWORD=[^$"]'; then sa_ok=0; fi
  if [ "$sa_ok" = 1 ]; then ok "SA password is generated, not hard-coded or echoed"; else fail "smoke-deb's SA password must be generated (openssl rand), masked (::add-mask::) first, passed via GITHUB_ENV, and never echoed or literal"; fi
fi

# CI scripts parse
shopt -s nullglob
ci=(packaging/ci/*.sh)
if [ "${#ci[@]}" -ge 2 ] && bash -n "${ci[@]}"; then ok "bash -n packaging/ci/*.sh"; else fail "packaging/ci scripts missing or have syntax errors"; fi
if command -v shellcheck >/dev/null 2>&1 && [ "${#ci[@]}" -gt 0 ]; then shellcheck "${ci[@]}" && ok shellcheck || fail shellcheck; fi
if command -v actionlint >/dev/null 2>&1 && [ -f "$WF" ]; then actionlint "$WF" && ok actionlint || fail actionlint; fi

[ "$FAILS" = 0 ]
