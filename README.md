# FocusGate — USB Modem Gateway Management System

A complete solution for managing Huawei HiLink USB modems, SIM cards, SMS, and user wallets. Built with two products in one repo: a .NET Windows service gateway and a Next.js cloud dashboard.

---

## Architecture

```
USB Modems → .NET Gateway → SQLite (local) → MongoDB Atlas (cloud) ← Next.js Web App (reads MongoDB)
                                                  ↑
                                          ASP.NET Dashboard (reads SQLite, port 5080)
```

| Component | Stack | Port | Database |
|-----------|-------|------|----------|
| .NET Gateway | .NET 10, C#, SQLite | — | `focusgate.db` |
| .NET Dashboard | ASP.NET Razor Pages | 5080 | SQLite (read-only) |
| Next.js Web App | Next.js 16, React 19, MongoDB | 3000 | MongoDB Atlas |

---

## Project Structure

```
FocusGate/
├── src/
│   ├── FocusGate.Core/              — Models, enums, PathService (no deps)
│   ├── FocusGate.Infrastructure/    — DbContext, services, MongoDB sync
│   ├── FocusGate.AT/                — COM port modem entry point
│   ├── FocusGate.HiLink/            — Huawei HTTP modem entry point
│   └── FocusGate.Dashboard/         — ASP.NET Razor Pages dashboard
│
├── focusgate-web/                   — Next.js cloud admin dashboard
│   ├── src/
│   │   ├── app/                     — Pages: login, admin, dashboard, API routes
│   │   ├── components/              — React components (admin/, dashboard/, shared/)
│   │   ├── lib/                     — MongoDB models, auth, utilities
│   │   └── i18n/                    — Translations: en.json, fr.json, ar.json
│   └── ...
│
├── dist/                            — Published executables
│   ├── hilink/                      — Full HiLink gateway + dashboard
│   ├── at/                          — Full AT modem gateway + dashboard
│   └── dashboard/                   — Dashboard only
│
├── FocusGate.sln                    — Solution file
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

# Run HiLink modems (auto-launches Dashboard)
dotnet run --project src/FocusGate.HiLink

# Run AT modems (auto-launches Dashboard)
dotnet run --project src/FocusGate.AT

# Dashboard only
dotnet run --project src/FocusGate.Dashboard
```

Dashboard opens at `http://localhost:5080`.

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
# Publish HiLink gateway + dashboard to dist/hilink/
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/hilink
dotnet publish src/FocusGate.Dashboard -c Release -r win-x64 --self-contained -o dist/dashboard
Copy-Item dist\dashboard\FocusGate.Dashboard.exe dist\hilink\ -Force
# ... (copy all Dashboard files to dist/hilink/, see AGENTS.md for full list)
```

---

## Key Features

### .NET Dashboard (localhost:5080)

| Page | What it does |
|------|-------------|
| **Dashboard** | 4 stat cards (modems, SIM balance online-only, user wallets, pending withdrawals), recent SMS |
| **Modems** | Modem list with filter pills (All/Online/Offline/Assigned/Unassigned), 5s auto-refresh |
| **ModemDetail** | Info + Balance History + SMS tabs, unassign action |
| **Users** | User CRUD with search, archive/restore, **edit username/password/display name** |
| **UserDetail** | Modems + Wallet + History + SMS tabs, assign/unassign modems, create withdrawal |
| **Withdrawals** | Approve/reject withdrawal requests, admin note |
| **Warnings** | Modems with high SIM balance (>= 45,000 DA) |
| **AdminSettings** | Change admin username and password |
| **SetLanguage** | Switch between EN/FR/AR |

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
1. Mutex check (Global\FocusGate_HiLink or Global\FocusGate_AT)
2. ConfigMerger creates/merges config.json in %APPDATA%\FocusGate\
3. DatabaseInitializer: EnsureCreated + PRAGMAs + column migrations + indexes
4. DatabaseWriteChannel starts processing write queue
5. MongoSyncService waits 15s, connects to MongoDB (5 retries)
6. HiLinkDiscovery probes IPs → finds modems
7. For each modem: connect → get IMEI/IMSI → insert to SQLite → *222# balance → start 3 loops
```

### Steady State (every 30s)

```
SMS Poll (30s): Read SMS → save to SQLite → dedup → delete from SIM → Mobilis trigger if recharge
Watchdog (30s): Session refresh → alive check → mark Online/Offline/Disconnect
Network Retry (2min): Network registration check → mark Online
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
| **Balance** | Only changed by `*222#` USSD — triggered at startup + Mobilis SMS |
| **MachineId** | Tracks original creator — never overwritten on push/pull |
| **Timestamp guards** | Pull operations only overwrite when remote is newer |
| **No restarts** | System must be stable and self-recovering |
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
3. Open `http://localhost:5080` — dashboard works immediately
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
dotnet publish src/FocusGate.Dashboard -c Release -r win-x64 --self-contained -o dist/dashboard

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
