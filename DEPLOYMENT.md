# Deploying Maldex

This guide stands up Maldex on a single Linux host with Docker. The whole stack is
driven by one script, `deploy/maldexctl`.

> **Scope.** This packages what the app does *today*: the ASP.NET web host, the EF Core
> database schema, and the on-disk artifact store. Features still in development
> (UI, authentication, YARA/Suricata processing, sandbox VM orchestration) are not
> packaged yet. See [Extending the packaging](#extending-the-packaging) for how each one
> slots in when it lands.

## What you get

```
deploy/maldexctl install
        |
        v
  sql ──healthy──> db-init ──ok──> migrate ──ok──> web  (127.0.0.1:8080)
  SQL Server       create DB +     EF Core         Maldex.Web
  (optional)       app login       migrations
```

| Service   | Image                                  | Runs      | Purpose |
|-----------|----------------------------------------|-----------|---------|
| `sql`     | `mcr.microsoft.com/mssql/server:2022`  | always    | Bundled SQL Server. Optional (`bundled-db` profile). Not exposed to the host. |
| `db-init` | same as `sql` (for `sqlcmd`)           | once      | Creates the database and a least-privilege `maldex_app` login. Idempotent. |
| `migrate` | `maldex-migrate` (built from `Dockerfile`) | once  | EF Core migrations bundle. Applies pending migrations as the admin login. |
| `web`     | `maldex-web` (built from `Dockerfile`) | always    | The app, as the non-root `app` user, on plain HTTP port 8080. |

Data lives in two named volumes: `maldex_sql-data` (bundled database) and
`maldex_artifacts` (content-addressed sample/artifact store).

Two database logins are used on purpose:

- **Admin** (`sa` by default) is used only by `db-init` and `migrate`, for DDL.
- **App** (`maldex_app`) is what the running web app gets: `db_datareader`,
  `db_datawriter` and `EXECUTE`, no schema changes. A compromised web process
  can't alter or drop the schema.

## Requirements

- Linux x86_64 host (the SQL Server image is x86_64 only; on ARM, use an external SQL Server)
- Docker Engine 24+ with the Compose v2 plugin (`docker compose version`)
- 4 GB RAM minimum (SQL Server alone wants 2 GB)
- Outbound access to `mcr.microsoft.com` and `api.nuget.org` for the first build
- A clone of this repository

## Quick start

```bash
git clone https://github.com/basht0p/maldex.git
cd maldex
./deploy/maldexctl install
```

`install` will:

1. Check Docker, Compose, CPU architecture and memory.
2. Create `deploy/.env` from `deploy/.env.example` with mode `600`, generating
   random 32-character passwords for the admin and app logins.
3. Ask you to accept the SQL Server EULA (bundled database only). Pass
   `--accept-sql-eula` to accept non-interactively.
4. Build the `maldex-web` and `maldex-migrate` images.
5. Start the stack, wait for the web container to report healthy, and print the URL.

Re-running `install` is safe: an existing `deploy/.env` and its secrets are kept.

Check it:

```bash
curl http://127.0.0.1:8080/        # -> Maldex
./deploy/maldexctl status
```

**Back up `deploy/.env`.** It holds the only copy of the generated database passwords.

## Configuration

All settings live in `deploy/.env`. The annotated template is
[`deploy/.env.example`](deploy/.env.example). The main settings:

| Variable | Default | Notes |
|----------|---------|-------|
| `COMPOSE_PROFILES` | `bundled-db` | Remove `bundled-db` to use an external SQL Server. |
| `MALDEX_VERSION` | `local` | Tag applied to locally built images. |
| `MALDEX_HTTP_BIND` / `MALDEX_HTTP_PORT` | `127.0.0.1` / `8080` | Where the app listens on the host. |
| `MALDEX_DB_HOST` / `MALDEX_DB_PORT` | `sql` / `1433` | `sql` is the bundled container. |
| `MALDEX_DB_NAME` | `maldex` | Created by `db-init` if missing. |
| `MALDEX_DB_ADMIN_USER` / `_PASSWORD` | `sa` / generated | DDL login for `db-init` and `migrate`. |
| `MALDEX_DB_APP_USER` / `_PASSWORD` | `maldex_app` / generated | Runtime login for `web`. |
| `MALDEX_DB_TRUST_SERVER_CERT` | `True` | Set `False` when the SQL Server cert is trusted by this host. |
| `MSSQL_PID` | `Express` | SQL Server edition (see licensing below). |
| `MSSQL_MEMORY_LIMIT_MB` | `2048` | Bundled SQL Server memory cap. |

Inside the container the app sees standard ASP.NET configuration keys:

| Env var in `web` | Config key | Used by |
|------------------|------------|---------|
| `ConnectionStrings__Maldex` | `ConnectionStrings:Maldex` | Provided for when the app registers its `DbContext` (it doesn't yet). |
| `Maldex__Artifacts__Root` | `Maldex:Artifacts:Root` | `ArtifactStoreOptions` (`/var/lib/maldex/artifacts`). |

After editing `deploy/.env`, apply it with `./deploy/maldexctl up`. Changing
`MALDEX_DB_APP_PASSWORD` is enough to rotate the app password: `db-init` re-syncs the login
on every `up`.

### SQL Server licensing

The bundled database defaults to **Express**, which is free for production use
(10 GB per database). `Developer` is free but **not licensed for production**.
`Standard`/`Enterprise` need your own license. You must accept the
[SQL Server EULA](https://go.microsoft.com/fwlink/?linkid=857698) either way.

### Using an existing SQL Server

1. In `deploy/.env`, set `COMPOSE_PROFILES=` (empty), then set `MALDEX_DB_HOST`,
   `MALDEX_DB_PORT`, and an admin login that can `CREATE DATABASE` and `CREATE LOGIN`.
2. If the server presents a certificate this host trusts, set `MALDEX_DB_TRUST_SERVER_CERT=False`.
3. Run `./deploy/maldexctl install` (or `up`).

`db-init` creates the database and app login there, and `migrate` applies the schema.
The admin credentials are only used by those two one-shot containers.

## TLS and reverse proxy

The app speaks plain HTTP on `127.0.0.1:8080` and the stack does not terminate TLS.
Put your existing reverse proxy (nginx, Caddy, Traefik, HAProxy, a load balancer) in front
and proxy to `http://127.0.0.1:8080`. Keep `MALDEX_HTTP_BIND=127.0.0.1` unless the proxy
runs on another host. A bundled proxy can be added once there's a UI and sign-in
worth fronting.

## Operations

```bash
./deploy/maldexctl status            # service state
./deploy/maldexctl logs [web|migrate|db-init|sql]
./deploy/maldexctl up                # start / apply .env changes
./deploy/maldexctl down              # stop (data is kept)
./deploy/maldexctl down -v           # stop AND delete all data volumes
```

### Upgrading

```bash
git pull
./deploy/maldexctl upgrade
```

`upgrade` rebuilds both images from the checkout (pulling fresh base images), then `up`
runs `db-init` and `migrate` before replacing the `web` container. If a migration fails,
`web` is not replaced: fix the problem (see `maldexctl logs migrate`) and run `up` again.
The old `web` container keeps serving while migrations run, so avoid releases that make
breaking schema changes without a maintenance window.

To run only the database steps: `./deploy/maldexctl migrate`.

### Backup

```bash
./deploy/maldexctl backup                    # -> deploy/backups/<UTC timestamp>/
./deploy/maldexctl backup /srv/backups/maldex-$(date +%F)
```

A backup folder contains:

| File | Contents |
|------|----------|
| `maldex.bak` | `COPY_ONLY` full backup with checksums (bundled database only) |
| `artifacts.tar.gz` | The artifact store volume |
| `env` | A copy of `deploy/.env`, **including secrets** |

The folder is created `0700`. With an external database, back it up with your own
SQL Server tooling. The script prints a warning and backs up the rest. `deploy/backups/`
is git-ignored, but it's still on the same disk: copy backups off the host.

The artifact store may hold live malware samples. Treat backup media accordingly.

### Restore

```bash
./deploy/maldexctl restore /srv/backups/maldex-2026-10-18
```

`restore` stops `web`, replaces the bundled database (`WITH REPLACE`) and the artifact
store from the backup folder, then runs `up`. `db-init` re-maps the app login, so a backup
from another host restores cleanly. It does **not** overwrite `deploy/.env`; copy the
backup's `env` file into place first if you're rebuilding a host from scratch.

## Security notes

- `deploy/.env` is created `0600` and is git-ignored. Never commit it.
- SQL Server is not published to the host; only containers on the compose network reach it.
- The web container runs as the non-root `app` user (UID 1654), with its port bound to localhost.
- Passwords are generated from `/dev/urandom` and avoid `'`, `;` and `"`, which would break
  connection strings or `init.sql`. Keep to that if you set your own.
- `MALDEX_DB_TRUST_SERVER_CERT=True` is fine for the bundled server on the private compose
  network. For an external server across a network, use a trusted certificate and `False`.

## Troubleshooting

| Symptom | Check |
|---------|-------|
| `sql` never becomes healthy | `maldexctl logs sql`. Usually the EULA isn't accepted, the password fails policy, or the host has less than 2 GB RAM free. |
| `db-init` exits non-zero | `maldexctl logs db-init`. Wrong admin credentials or host for an external server. |
| `migrate` exits non-zero | `maldexctl logs migrate`. `web` stays on the previous version until it succeeds. |
| Port 8080 already in use | Change `MALDEX_HTTP_PORT` and run `maldexctl up`. |
| Build fails fetching packages | The host needs outbound HTTPS to `api.nuget.org` and `mcr.microsoft.com`. |

## Extending the packaging

The image and compose file intentionally cover only what the app does today. As features
land, extend them in step:

| When this lands | Packaging change |
|-----------------|------------------|
| App registers `MaldexDbContext` | Read `ConnectionStrings:Maldex`; it's already provided to `web`. No deploy change. |
| A `/healthz` endpoint | Swap the `web` healthcheck from a TCP probe to an HTTP probe. |
| `ui/` (Node) | Add a Node build stage to the `Dockerfile` that outputs into the web project before `dotnet publish`. |
| Cookie / Entra sign-in | Persist ASP.NET Data Protection keys (a volume or the database), add the Entra settings to `.env.example`, and enable forwarded headers for the proxy. |
| YARA / Suricata processing | Install the pinned native packages in the `web` stage (or a separate worker image). |
| Sandbox VM orchestration | Decide where the hypervisor lives. Driving VMs from a container needs device or socket access and is its own design. |
| `./build.sh build` / `run` (Task 19) | Can wrap `docker build` against this `Dockerfile`. |
| Published images (GHCR) | Lets `install` pull instead of building: the step closest to "apt install maldex". |
