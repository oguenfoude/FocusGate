# FocusGate — USB Modem Gateway Management System

A complete solution for managing Huawei HiLink USB modems, SIM cards, SMS, and user wallets. Built with two products in one repo: a .NET Windows service gateway and a Next.js cloud dashboard.

---

## Architecture

```
USB Modems → .NET Gateway → SQLite (local) → MongoDB Atlas (cloud) ← Next.js Web App (reads MongoDB)
```

| Component | Stack | Port | Database |
|-----------|-------|------|----------|
| .NET Gateway | .NET 10, C#, SQLite | — | `focusgate.db` |
| Next.js Web App | Next.js 16, React 19, MongoDB | 3000 | MongoDB Atlas |

---

## Project Structure

```
FocusGate/
├── src/
│   ├── FocusGate.Core/              — Models, enums, PathService (no deps)
│   ├── FocusGate.Infrastructure/    — DbContext, services, MongoDB sync
│   └── FocusGate.HiLink/            — Huawei USB modem entry point (the only gateway)
│
├── focusgate-web/                   — Next.js cloud admin dashboard
│   ├── src/
│   │   ├── app/                     — Pages: login, admin, dashboard, API routes
│   │   ├── components/              — React components (admin/, dashboard/, shared/)
│   │   ├── lib/                     — MongoDB models, auth, utilities
│   │   └── i18n/                    — Translations: en.json, fr.json, ar.json
│   └── ...
│
├── dist/                            — Published executables (gitignored, from `dotnet publish`)
│
├── FocusGate.sln                    — Solution file (3 C# projects)
└── AGENTS.md                        — Agent instructions
```

---

## Branches

| Branch | MongoDB Database | Use Case |
|--------|-----------------|----------|
| `main` | `focusgate` | Original/dev — BERRAR machine |
| `alaafi` | `alaafi` | alaafi deployment |
| `flixiDz` | `flixiDz` | bmsoft machine |

All branches have the same code — only the default MongoDB database name differs in `ConfigMerger.cs` and `DependencyInjection.cs`.

---

## Quick Start

### .NET Gateway (from source)

```powershell
# Build
dotnet build FocusGate.sln

# Run HiLink gateway (console: help, status, modems, exit)
dotnet run --project src/FocusGate.HiLink
```

Use the console commands (`status`, `modems`, `sms`, `users`, …) to inspect the gateway. Type `help` for the full list.

### Next.js Web App

```powershell
cd focusgate-web
npm install
npm run dev -- --webpack    # Dev server on port 3000
npm run build -- --webpack  # Production build
npm start                   # Production server
```

### Publish for Deployment

```powershell
# Publish HiLink gateway to dist/hilink/
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/hilink
```

---

## Key Features

### Gateway console (no web UI — use these commands)

| Command | What it does |
|------|-------------|
| `status` | Live dashboard (modems online/offline, SMS count, balances, MongoDB state) |
| `modems` / `modem <id>` | Modem list / modem detail + SIM + SMS stats |
| `sms [modemId] [days]` / `sim <modemId>` | Recent SMS / SIM history for a modem |
| `users` / `adduser <u> <p> [d]` | List users / create user |
| `assign <uid> <mid>` / `unassign <uid> <mid>` | Assign modem to user / remove assignment |
| `settle <modId> <amt> [note]` | Manual balance adjustment (logged as settlement) |
| `report balance/sms` | Balance history / SMS report by date range |
| `config` / `set-config <k> <v>` / `setmongo <uri>` | View / change config / set MongoDB URI |

### Next.js Web App

**Admin Panel** (`/admin`):
- Dashboard with online-only SIM balance, modem status, user wallets
- Modem management with status, balance staleness indicator (green=online, gray=offline+"last known")
- User management with create, edit (display name + password), archive/restore
- SMS logs with modem filter and timeframe filter
- Withdrawal management with approve/reject, admin note
- High balance warnings (>= 45,000 DA)

**User Dashboard** (`/dashboard`):
- SIM cards (phone number, online/offline status, last seen)
- Mobilis SMS with type classification (OTP, promo, recharge, transfer)
- Wallet credit/debit history
- Withdrawal requests

**Features**:
- Full i18n (English, French, Arabic)
- Locale-aware number formatting and dates
- 30s auto-refresh via SWR
- SSE live toast notifications for new SMS/balance changes
- Mobile-responsive with hamburger menu
- localStorage-based auth (no server-side sessions)

---

## Data Flow

### Startup Sequence

```
1. Mutex check (Global\FocusGate_HiLink — single instance)
2. ConfigMerger creates/merges config.json in %APPDATA%\FocusGate\
3. DatabaseInitializer: EnsureCreated + PRAGMAs + column migrations + indexes
4. DatabaseWriteChannel starts processing write queue
5. MongoSyncService waits 5s, connects to MongoDB (retries forever, non-fatal)
6. HiLinkDiscovery probes IPs → finds modems
7. For each modem: connect → get IMEI/IMSI → insert to SQLite → *101# phone → MeetMob OTP login → start loops
```

### Steady State

```
SMS Poll (5s): Read SMS → save to SQLite → dedup → delete from SIM → MeetMob check if recharge
Watchdog (30s): Session refresh → alive check → mark Online/Offline
MeetMob check (60s): OTP login → balance → recharge history → credit user wallets
MongoDB Sync (30s): Push SQLite → MongoDB, Pull MongoDB → SQLite
```

---

## Configuration

Config file: `%APPDATA%\FocusGate\config.json` (auto-created by ConfigMerger)

| Key | Default | Description |
|-----|---------|-------------|
| `mongodb.uri` | SRV connection string | MongoDB Atlas connection URI |
| `mongodb.database` | `focusgate` / `alaafi` / `flixiDz` | MongoDB database name |
| `sync.interval_seconds` | `30` | MongoDB sync interval |
| `hilink.scan_ips` | `192.168.8.1,192.168.200.1,192.168.1.1` | IPs to scan for modems |

**MongoDB**: Direct connection string (not SRV) — SRV DNS fails on some networks.

---

## Conventions

| Rule | Detail |
|------|--------|
| **Passwords** | Plain text — NO hashing, NO SHA256, NO BCrypt |
| **SQLite** | Primary local database — writes always succeed locally |
| **Soft delete** | `ArchivedAt` field on all entities — never hard-delete |
| **Online status** | `status === 4` only — no stale `updatedAt` checks on web |
| **Balance** | MeetMob is source of truth — user wallets credited only from MeetMob history; USSD `*222#` is fallback |
| **MachineId** | Tracks original creator — never overwritten on push/pull |
| **Timestamp guards** | Pull operations only overwrite when remote is newer |
| **Auto-restart** | RestartService restarts every 8h; self-recovering loops everywhere |
| **MongoDB sync** | Non-fatal — app works fine without it |
| **No git push** | Until user explicitly says so |

---

## Credentials

| Type | Username | Password |
|------|----------|----------|
| Admin | `admin` | `admin` |
| Test user | `oussama` | `oussama` |

---

## Deployment

### New PC

1. Copy `dist/hilink/*` to the PC
2. Run `FocusGate.HiLink.exe` — auto-creates config + SQLite database
3. Type `status` in the console — gateway works immediately
4. Configure `mongodb.uri` and `mongodb.database` in config.json for cloud sync

### Database Reset

Delete `focusgate.db` + `-shm` + `-wal` files, restart to re-seed `admin:admin`.

### Machine IDs

| Machine | MachineId |
|---------|-----------|
| Dev (local) | `d26b1c221259fb12` |
| Client (BERRAR) | `419c0cfc97666753` |
| bmsoft | `b0a458aebe2393a4` |

---

## Build Commands Reference

```powershell
# .NET
dotnet build FocusGate.sln                                    # Build (0 warnings, 0 errors)
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/hilink

# Next.js
cd focusgate-web
npm run dev -- --webpack       # Dev server
npm run build -- --webpack    # Production build
npm run lint                  # ESLint
npm start                     # Production server
```

---

## License

Private project — FocusGate Team.
