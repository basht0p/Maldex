#!/usr/bin/env bash
# Install smoke assertions, run as root inside the container after the package is installed: packaging/ci/smoke.sh <deb|rpm>
# Exits nonzero if any assertion fails. systemctl may be unavailable in a container, so the units are checked as files.
set -uo pipefail

KIND="${1:-}"
case "$KIND" in deb|rpm) ;; *) echo "usage: smoke.sh <deb|rpm>" >&2; exit 64 ;; esac

FAILS=0
pass() { echo "ok   $*"; }
fail() { echo "FAIL $*" >&2; FAILS=$((FAILS+1)); }

# the five service users
for u in maldex maldex-mitm maldex-dns maldex-isroot maldex-isuser; do
  if id "$u" >/dev/null 2>&1; then pass "user $u"; else fail "user $u missing"; fi
done

# sudoers grant parses
if [ -f /etc/sudoers.d/maldex ] && visudo -c >/dev/null 2>&1 && visudo -cf /etc/sudoers.d/maldex >/dev/null 2>&1; then pass "visudo -c"; else fail "visudo -c failed or /etc/sudoers.d/maldex missing"; fi

# units present (files; systemctl may not work here)
for unit in maldex.service maldex-host-guard.service; do
  if [ -f "/usr/lib/systemd/system/$unit" ] || [ -f "/lib/systemd/system/$unit" ]; then pass "unit $unit"; else fail "unit $unit not installed"; fi
done

# the app binary runs
if /opt/maldex/app/Maldex.Web --version >/dev/null 2>&1; then pass "Maldex.Web --version"; else fail "/opt/maldex/app/Maldex.Web --version did not exit 0"; fi

# Started the way the unit starts it (its Environment= lines, as maldex, from /var/lib/maldex): the content root, the SPA and
# the settings files resolve under /opt/maldex/app, not the working directory.
unit=/usr/lib/systemd/system/maldex.service; [ -f "$unit" ] || unit=/lib/systemd/system/maldex.service
if [ -f "$unit" ]; then
  mapfile -t unit_env < <(sed -n 's/^Environment=//p' "$unit")
  paths="$(cd /var/lib/maldex && runuser -u maldex -- env "${unit_env[@]}" /opt/maldex/app/Maldex.Web --print-paths 2>&1)"
  if printf '%s\n' "$paths" | grep -qE '^ContentRoot=/opt/maldex/app/?$' \
     && printf '%s\n' "$paths" | grep -qx 'IndexHtml=present' \
     && printf '%s\n' "$paths" | grep -qx 'Config=/opt/maldex/app/appsettings.json' \
     && printf '%s\n' "$paths" | grep -qx 'Config=/opt/maldex/app/appsettings.Real.json' \
     && printf '%s\n' "$paths" | grep -qx 'Config=/opt/maldex/app/appsettings.Package.json'; then
    pass "unit environment: content root, SPA and settings under /opt/maldex/app"
  else
    fail "unit environment: content root, SPA or settings not under /opt/maldex/app"; printf '%s\n' "$paths" >&2
  fi
fi

# maldex-setup --check runs without crashing and reports FAIL lines (no KVM in CI). FAIL lines go to stderr.
if [ -x /usr/sbin/maldex-setup ]; then
  out="$(/usr/sbin/maldex-setup --check 2>&1)"; rc=$?
  if [ "$rc" -gt 1 ]; then fail "maldex-setup --check crashed (exit $rc)"; printf '%s\n' "$out" >&2
  elif ! printf '%s\n' "$out" | grep -qE '^(ok  |FAIL) '; then fail "maldex-setup --check printed no step lines"; printf '%s\n' "$out" >&2
  elif ! printf '%s\n' "$out" | grep -q '^FAIL '; then fail "maldex-setup --check reported no FAIL (expected some without KVM)"; printf '%s\n' "$out" >&2
  else pass "maldex-setup --check ($(printf '%s\n' "$out" | grep -c '^FAIL ') FAIL lines expected in CI)"; fi
else
  fail "/usr/sbin/maldex-setup not installed"
fi

# config file perms
cfg=/etc/maldex/appsettings.Production.json
if [ -f "$cfg" ]; then
  got="$(stat -c '%a %U:%G' "$cfg")"
  if [ "$got" = "640 root:maldex" ]; then pass "$cfg is 0640 root:maldex"; else fail "$cfg is '$got', expected '640 root:maldex'"; fi
else
  fail "$cfg missing"
fi

# maldex-admin is installed and refuses a non-root caller (spec §8)
if [ -x /usr/sbin/maldex-admin ] && [ -x /opt/maldex/bin/maldex-admin ]; then
  if [ "$(id -u)" != 0 ] || ! command -v runuser >/dev/null 2>&1; then
    pass "maldex-admin installed (the non-root refusal is checked only as root with runuser)"
  elif out="$(runuser -u maldex-isuser -- /usr/sbin/maldex-admin list 2>&1)"; rc=$?; [ "$rc" = 1 ] && grep -q 'must be run as root' <<<"$out"; then
    pass "maldex-admin refuses a non-root caller"
  else
    fail "maldex-admin as a non-root user: exit $rc, '$out'"
  fi
else
  fail "/usr/sbin/maldex-admin not installed"
fi

# ---- the app, started the way the unit starts it (as maldex, from /var/lib/maldex, with the unit's Environment= lines) ----
# With MALDEX_SMOKE_SQL (a connection string to a throwaway SQL Server; smoke-deb) the app runs against it: maldex-admin creates
# an admin and POST /api/auth/login must accept it. Without it the connection string points at a closed port and the check is that
# GET /api/auth/config answers before the database error. If the app exits before serving, that is reported and the check falls
# back to --print-paths (asserted above); see the ruling in the task 9 report.
APP_URL=http://127.0.0.1:5080
APP_LOG=/tmp/maldex-smoke-app.log
if [ -f "$unit" ] && [ -f "$cfg" ]; then
  if [ -n "${MALDEX_SMOKE_SQL:-}" ]; then
    cs="$MALDEX_SMOKE_SQL"; mode=db
  else
    cs="Server=127.0.0.1,1;Database=maldex;User Id=maldex;Password=smoke;TrustServerCertificate=True;Connect Timeout=2"; mode=closed
  fi
  case "$cs" in *\"*|*\\*) fail "the smoke connection string may not contain a quote or backslash"; cs="" ;; esac
  if [ -n "$cs" ]; then
    # keeps the file's mode and owner (root:maldex 0640): the template's empty ConnectionStrings:Maldex gets the value
    shopt -u patsub_replacement 2>/dev/null || true
    body="$(cat "$cfg")"
    if [[ "$body" == *'"Maldex": ""'* ]]; then
      printf '%s\n' "${body/'"Maldex": ""'/\"Maldex\": \"$cs\"}" > "$cfg"
    else
      fail "$cfg has no empty ConnectionStrings:Maldex to fill"
    fi

    admin_ok=1
    if [ "$mode" = db ]; then
      smoke_pw="$(head -c 24 /dev/urandom | base64 | tr -d '/+=')Aa1"
      if out="$(printf '%s\n' "$smoke_pw" | /usr/sbin/maldex-admin create-user smoke --admin --password-stdin 2>&1)"; then
        pass "maldex-admin create-user smoke --admin"
      else
        fail "maldex-admin create-user failed: $out"; admin_ok=0
      fi
      if [ "$admin_ok" = 1 ]; then
        if out="$(/usr/sbin/maldex-admin list --json 2>&1)" && grep -q '"userName":"smoke"' <<<"$out" && grep -q '^#config ' <<<"$out"; then
          pass "maldex-admin list --json shows the admin and the #config line"
        else
          fail "maldex-admin list --json: $out"
        fi
      fi
    fi

    mapfile -t unit_env < <(sed -n 's/^Environment=//p' "$unit")
    (cd /var/lib/maldex && exec runuser -u maldex -- env "${unit_env[@]}" /opt/maldex/app/Maldex.Web) > "$APP_LOG" 2>&1 &
    app_pid=$!
    served=0; exited=0; config_json=""
    for _ in $(seq 1 60); do
      if config_json="$(curl -fsS --max-time 3 "$APP_URL/api/auth/config" 2>/dev/null)"; then served=1; break; fi
      if ! kill -0 "$app_pid" 2>/dev/null; then exited=1; break; fi
      sleep 1
    done
    if [ "$served" = 1 ]; then
      if grep -q '"passwordLogin":true' <<<"$config_json"; then pass "GET /api/auth/config answers ($mode)"; else fail "GET /api/auth/config: $config_json"; fi
      if [ "$mode" = db ] && [ "$admin_ok" = 1 ]; then
        login="$(printf '{"userName":"smoke","password":"%s"}' "$smoke_pw" \
          | curl -sS --max-time 20 -X POST -H 'Content-Type: application/json' -H 'X-Maldex-Csrf: 1' --data-binary @- "$APP_URL/api/auth/login" 2>&1)"
        if grep -q '"next":"done"' <<<"$login"; then pass "POST /api/auth/login signs the admin in"; else fail "POST /api/auth/login: $login"; fi
      fi
    elif [ "$exited" = 0 ]; then
      fail "the app is running but did not serve $APP_URL/api/auth/config within 60 s"; tail -n 40 "$APP_LOG" >&2
    elif [ "$mode" = db ]; then
      wait "$app_pid" 2>/dev/null; fail "the app exited (status $?) instead of serving against the smoke database"; tail -n 40 "$APP_LOG" >&2
    else
      # Closed database port: the app migrates before it listens, so it may only die of the database error. Exit 78 (a
      # configuration error) or any other crash fails the smoke.
      wait "$app_pid" 2>/dev/null; app_rc=$?
      if [ "$app_rc" != 78 ] && grep -qE 'SqlException|network-related|error: 40|A network-related or instance-specific error' "$APP_LOG"; then
        pass "app start against a closed port: exited ($app_rc) with the database connection error, as expected"
      else
        fail "app start against a closed port: exit status $app_rc without a database connection error in the log"; tail -n 40 "$APP_LOG" >&2
      fi
    fi
    if [ "$exited" = 0 ]; then kill "$app_pid" 2>/dev/null; pkill -u maldex -f /opt/maldex/app/Maldex.Web 2>/dev/null; wait "$app_pid" 2>/dev/null; fi
  fi
else
  fail "no unit file or config to start the app from"
fi

if [ "$FAILS" -ne 0 ]; then echo "smoke ($KIND): $FAILS assertion(s) failed" >&2; exit 1; fi
echo "smoke ($KIND): all assertions passed"
