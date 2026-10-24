# Unhinged Transport Solutions (UTS)

A mock courier company tracking system — because the parcels were going between cities anyway.
Public-facing tracking portal + admin panel, with a serious corporate face and a wholesome soul of
"whoever is travelling that way carries it".

## Stack

| Layer     | Tech                                                     |
|-----------|----------------------------------------------------------|
| Backend   | ASP.NET Core (.NET 10), minimal APIs                     |
| Data      | PostgreSQL 17, Dapper (micro-ORM)                        |
| Migrations| Numbered embedded SQL, applied at startup, tracked in `schema_versions` (DbUp-style, zero extra deps) |
| Frontend  | Vue 3 + TypeScript + Vite (served as static files by the API) |
| Auth      | Hand-rolled OIDC authorization-code + PKCE flow (Authentik) |
| Telegram  | [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) (typed Bot API client, long polling) |
| Deploy    | Docker Compose (`app` + `db`)                            |

### NuGet dependencies on a flaky-TLS machine

TLS from `dotnet` can fail on this machine (`NU1301`), so NuGet sources are locked to a local
offline feed in [`nuget.config`](nuget.config) pointing at `packages-local/` (gitignored, not in
the repo). **After a fresh clone the folder is empty and restore fails** — populate it via curl:

```bash
while read id ver; do
  curl -s -o "packages-local/$id.$ver.nupkg" \
    "https://api.nuget.org/v3-flatcontainer/$id/$ver/$id.$ver.nupkg"
done <<EOF
dapper 2.1.66
microsoft.bcl.asyncinterfaces 9.0.1
microsoft.extensions.dependencyinjection.abstractions 8.0.2
microsoft.extensions.logging.abstractions 8.0.2
microsoft.extensions.options 8.0.0
npgsql 9.0.5
system.reflection.emit.ilgeneration 4.7.0
system.reflection.emit.lightweight 4.7.0
system.threading.tasks.extensions 4.5.4
system.text.json 8.0.5
telegram.bot 22.10.3.3
EOF
```

## Quick start (dev)

```bash
# 1. start just the database
cd deploy
docker compose up -d db

# 2. run the API locally (dev fake admin login is enabled by launcher)
cd ..
dotnet run --project src/Uts.Api
# App on http://localhost:5000. User the Vite dev server for hot reload:
cd src/Uts.Web && npm install && npm run dev   # http://localhost:5173
```

**Local-only**: if `AUTH__DEVAUTH=true`, a fake admin login is available:

```bash
curl -c cookies.txt -X POST 'http://localhost:5000/api/dev/login?name=Dom'
```

> ⚠️ On this machine, `dotnet`'s outgoing TLS sometimes fails ("bad certificate format" — a flaky
> native runtime issue). If local `dotnet run` misbehaves, run the full stack via Docker — TLS
> inside the Linux container works fine:

> ℹ️ For **build/restore** failures from the same TLS flakiness, the repo pins NuGet to the local
> offline feed (`nuget.config` → `packages-local/`) — see "NuGet dependencies" above.

```bash
cd deploy && docker compose up -d --build
# app on http://localhost:8080
```

## Production deployment

```bash
cd deploy
cp .env.example .env   # fill in — see below
docker compose up -d --build
```

### Environment variables

| Variable               | Meaning                                                      |
|------------------------|--------------------------------------------------------------|
| `POSTGRES_PASSWORD`    | DB password (required)                                       |
| `UTS_PORT`             | Host port for the app (default 8080)                         |
| `OIDC_AUTHORITY`       | Authentik base URL, e.g. `https://authentik.example.com`     |
| `OIDC_CLIENT_ID`       | OIDC provider client ID                                      |
| `OIDC_CLIENT_SECRET`   | OIDC provider client secret                                  |
| `OIDC_SCOPES`          | Space-separated scopes (default `openid profile email groups`) |
| `OIDC_GROUPS_CLAIM`    | Claim carrying group membership (default `groups`)           |
| `AUTH_ADMIN_GROUP`     | Authentik group whose members may use the admin panel (default `uts-admins`) |
| `AUTH_BEHIND_PROXY`    | `true` when behind a reverse proxy (X-Forwarded-* honoured)  |
| `AUTH_DEV_AUTH`        | DEV ONLY fake login endpoint — **never enable publicly**     |
| `TELEGRAM__BOTTOKEN`   | BotFather token; omit to keep the bot module off             |
| `TELEGRAM__SITEPUBLICURL` | Public base URL — tracking buttons in bot messages        |
| `TELEGRAM__COURIERCHATIDS` | Comma-separated chat IDs allowed to run courier commands (`/leaving`, `/arrived`, `/deliver`, `/packlist`) |

## Telegram bot

Recipients get Polish push notifications about their parcels (status changes, trip departures/
arrivals); the courier drives the packlist straight from the chat. Message delivery goes through
an outbox table (survives restarts, retried with backoff).

Setup:
1. Create a bot with [@BotFather](https://t.me/BotFather), copy the token into `TELEGRAM__BOTTOKEN`.
2. Message the bot once and open `https://api.telegram.org/bot<TOKEN>/getUpdates` to read your
   `chat.id` — put it into `TELEGRAM__COURIERCHATIDS` to unlock courier commands.
3. Clients bind their chat two ways: the admin panel (**Clients → TG claim**) or the public
   tracking page after PIN unlock (shows a one-click claim link when the recipient is unbound).
4. Recipients can mute/unmute anytime with `/notifications` in the chat.

## Authentik setup

1. **Create a group** `uts-admins` (Directory → Groups) and add the admins to it.
2. **Create an application + provider**:
   - Provider type: *OAuth2/OpenID Connect*, confidential client.
   - Redirect URI: `https://<your-host>/signin/uts`
   - Scopes: default (`openid profile email add groups` — Authentik's userinfo includes `groups`).
3. Copy the client ID/secret into `.env` (`OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET`).
4. Set `OIDC_AUTHORITY` to the Authentik base URL — OIDC endpoints are discovered
   automatically from `/.well-known/openid-configuration`.
5. Set `AUTH_BEHIND_PROXY=true` if you terminate TLS at a reverse proxy.
6. Restart the app container: `docker compose restart app`.

Users in the `uts-admins` group get full access. Anyone else is explicitly told they're not staff.

## How it works

### Tracking numbers
`UTS2137` + 6-digit zero-padded sequence + destination city code → `UTS2137000012WRO`.
The sequence is a Postgres `sequence`, so numbers are unique across restarts.

### Access codes (PIN)
Every shipment gets a random 6-digit code. The public tracking page shows status, route and a
generic timeline without it; entering the code unlocks sender, recipient, contents, transport mode,
planned trip and internal notes. Codes are stored in plaintext (they're shared openly with the
recipient anyway) and unlock attempts are rate-limited (5 per 10 minutes per IP+number).
Admins can regenerate a code anytime.

### Status flow
`Registered → Picked up → In transit → Delivered`, with `Cancelled` / `Lost` as side exits.
Illegal transitions are rejected with a 409. Every change writes a timeline event automatically.

### Trips
Planned trips (origin, destination, date) act as dispatch buckets. Shipments are assigned to
*planned* trips only; trip destination must match shipment destination. Assignments and
reassignments are auto-logged on the public timeline. Completing or cancelling a trip releases
its shipments back to "unscheduled".

## API overview

| Endpoint | Method | Auth | Description |
|---|---|---|---|
| `/api/track/{number}` | GET | none | Public tracking info (PIN-locked details hidden) |
| `/api/track/{number}/unlock` | POST | rate-limited | `{ "pin": "123456" }` → full details |
| `/auth/login`, `/auth/logout` | GET | – | OIDC redirects |
| `/api/auth/state` | GET | – | `{ authenticated, isAdmin, name }` |
| `/api/admin/shipments` | GET | admin | list, filters: `q`, `status`, `city`, `clientId`, `tripId` (`none` = unassigned) |
| `/api/admin/shipments` | POST | admin | create → `{ pin, shipment }` (PIN shown once) |
| `/api/admin/shipments/{id}` | GET | admin | detail + timeline |
| `…/{id}/status` | POST | admin | `{ "status": "picked_up" }` — validates transitions |
| `…/{id}/trip` | POST | admin | `{ "tripId": 3 }` or `{ "tripId": null }` |
| `…/{id}/events` | POST | admin | `{ "message": "..." }` manual timeline note |
| `…/{id}/pin` | POST | admin | regenerate access code |
| `/api/admin/clients` | GET/POST/PUT | admin | client records (name, contact, telegram chat id, notes) |
| `/api/admin/cities` | GET/POST/PUT | admin | city codes + activation toggle |
| `/api/admin/trips` | GET/POST/PUT | admin | planned trips |

## CI → GHCR

`.github/workflows/build-docker.yml` builds the production image on every push to
`main` (and on `v*` tags) and pushes it to GHCR:

```
ghcr.io/<owner>/<repo>:{latest,1.2.3,sha-…}
```

On the server you can then run without building anything:

```bash
cd deploy
UTS_IMAGE=ghcr.io/<owner>/<repo>:latest docker compose up -d
```

(The compose file falls back to building locally when `UTS_IMAGE` is unset.)

## Project layout

```
src/Uts.Api/            # ASP.NET Core backend
  Migrations/            # embedded SQL, applied on startup
  Db/                    # data source + migration runner
  Domain/                # models + status state machine
  Endpoints/             # public tracking, auth, admin endpoints
  Services/              # tracking numbers, PINs, OIDC flow
src/Uts.Web/            # Vue 3 SPA (Vite)
deploy/                 # docker-compose.yml + .env.example
```

## Future plans (already prepared in the schema)

- Deleting shipments is intentionally absent — history stays.
