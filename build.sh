#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

# Non-interactive shells may not have dotnet on PATH.
if ! command -v dotnet >/dev/null 2>&1 && [ -d "$HOME/.dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
fi

COMPOSE=(docker compose -f docker-compose.dev.yml --env-file .env)

cmd_setup() {
  if [ ! -f .env ]; then
    # Strong password that satisfies SQL Server complexity rules.
    pw="Mx1!$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)"
    printf 'MSSQL_SA_PASSWORD=%s\n' "$pw" > .env
    chmod 600 .env
    echo "Generated .env"
  fi
  dotnet tool restore
  # Chromium for PDF reports (Microsoft.Playwright); also installs the OS libraries it needs, which uses sudo.
  dotnet run --project src/Maldex.Web -- --install-browsers
}

cmd_dev_db() {
  [ -f .env ] || cmd_setup
  "${COMPOSE[@]}" up -d
  echo "Waiting for SQL Server to become healthy..."
  for _ in $(seq 1 60); do
    cid="$("${COMPOSE[@]}" ps -q sql)"
    status="$(docker inspect -f '{{.State.Health.Status}}' "$cid" 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then echo "SQL Server is healthy on localhost:14333"; return 0; fi
    sleep 5
  done
  echo "SQL Server did not become healthy in time" >&2
  return 1
}

cmd_test() {
  dotnet test --solution Maldex.slnx --ignore-exit-code 8  # 8 = a project has no tests yet
  if [ -f ui/package.json ]; then
    npm --prefix ui test -- --run
  fi
}

# The dev backend needs the SA password that `setup` wrote to .env (appsettings.Development.json carries no secret).
export_dev_connection() {
  [ -f .env ] || cmd_setup
  if [ -z "${ConnectionStrings__Maldex:-}" ]; then
    set -a; . ./.env; set +a
    export ConnectionStrings__Maldex="Server=localhost,14333;Database=maldex;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True"
  fi
}

# Build the React SPA and replace src/Maldex.Web/wwwroot with it (generated; gitignored), so Maldex.Web hosts it same-origin.
cmd_build_spa() {
  [ -d ui/node_modules ] || npm --prefix ui ci
  npm --prefix ui run build
  rm -rf src/Maldex.Web/wwwroot
  mkdir -p src/Maldex.Web/wwwroot
  cp -R ui/dist/. src/Maldex.Web/wwwroot/
}

cmd_build() {
  cmd_build_spa
  dotnet publish src/Maldex.Web -c Release
}

BACKEND_PID=""
VITE_PID=""
BACKEND_LOG=".data/backend.log"

GUACD_STARTED=0

# Real mode only (MALDEX_MODE=Real): guacd proxies the live view to the VM's VNC console, on 127.0.0.1:4822.
start_guacd() {
  [ "${MALDEX_MODE:-Fake}" = "Real" ] || return 0
  docker rm -f maldex-guacd >/dev/null 2>&1 || true
  docker run -d --rm --name maldex-guacd --network host guacamole/guacd:1.5.5 /opt/guacamole/sbin/guacd -b 127.0.0.1 -L info -f >/dev/null
  GUACD_STARTED=1
  export Maldex__Mode=Real
}

stop_guacd() {
  if [ "$GUACD_STARTED" = "1" ]; then docker stop maldex-guacd >/dev/null 2>&1 || true; GUACD_STARTED=0; fi
}

stop_children() {
  stop_guacd
  for pid in "$VITE_PID" "$BACKEND_PID"; do
    if [ -n "$pid" ]; then kill "$pid" 2>/dev/null || true; fi
  done
  for pid in "$VITE_PID" "$BACKEND_PID"; do
    if [ -n "$pid" ]; then wait "$pid" 2>/dev/null || true; fi
  done
  VITE_PID=""; BACKEND_PID=""
}

port_5080_busy() { ss -ltn 2>/dev/null | grep -q ':5080 '; }

# Build (Debug) and start Maldex.Web on :5080 in the background. Run the dll directly (not `dotnet run`) so the
# PID we hold is the server itself and a plain kill stops it. Extra environment is inherited from the caller.
start_backend() {
  if port_5080_busy; then echo "Something is already listening on :5080; stop it first." >&2; return 1; fi
  export_dev_connection
  dotnet build src/Maldex.Web -c Debug --nologo -v q
  mkdir -p .data
  (
    cd src/Maldex.Web
    ASPNETCORE_ENVIRONMENT=Development exec dotnet bin/Debug/net10.0/Maldex.Web.dll
  ) >"$BACKEND_LOG" 2>&1 &
  BACKEND_PID=$!
}

wait_for_api() {
  echo "Waiting for http://localhost:5080/api/me ..."
  for _ in $(seq 1 120); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5080/api/me || true)" = "200" ]; then
      echo "Backend is up."; return 0
    fi
    if ! kill -0 "$BACKEND_PID" 2>/dev/null; then
      echo "Backend exited early; log:" >&2; tail -n 40 "$BACKEND_LOG" >&2; return 1
    fi
    sleep 1
  done
  echo "Backend did not answer /api/me with 200 in time; log:" >&2
  tail -n 40 "$BACKEND_LOG" >&2
  return 1
}

# Live development: backend on :5080 plus the Vite dev server (which proxies /api and /hubs to it).
cmd_run() {
  cmd_dev_db
  [ -d ui/node_modules ] || npm --prefix ui ci
  trap stop_children EXIT INT TERM
  start_guacd
  start_backend
  wait_for_api
  echo "Backend on http://localhost:5080 (log: $BACKEND_LOG). Starting Vite..."
  npm --prefix ui run dev
}

# End-to-end browser smoke against the HOSTED SPA on :5080 (same origin, no Vite), fake backend at 10x replay speed.
cmd_smoke() {
  cmd_dev_db
  cmd_build_spa
  (cd ui && npx playwright install chromium)
  trap stop_children EXIT INT TERM
  Maldex__Fake__Replay__Speed=10 start_backend
  wait_for_api
  local rc=0
  (cd ui && npx playwright test) || rc=$?
  stop_children
  trap - EXIT INT TERM
  return "$rc"
}

case "${1:-}" in
  setup)  cmd_setup ;;
  dev-db) cmd_dev_db ;;
  test)   cmd_test ;;
  run)    cmd_run ;;
  smoke)  cmd_smoke ;;
  build)  cmd_build ;;
  e2e)    echo "Real-VM E2E is implemented in Plan 1b"; exit 2 ;;
  *) echo "usage: $0 setup|dev-db|run|test|smoke|build|e2e" >&2; exit 64 ;;
esac
