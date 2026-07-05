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
│   ├── FocusGate.Core/              — Models, enums, PathService (no deps, 19 .cs files)
│   ├── FocusGate.Infrastructure/    — DbContext, services, MongoDB sync (15 .cs files)
│   ├── FocusGate.AT/                — COM port modem entry point (3 .cs files)
│   ├── FocusGate.HiLink/            — Huawei HTTP modem entry point (2 .cs files)
│   └── FocusGate.Dashboard/         — ASP.NET Razor Pages dashboard (9 pages)
│
├── focusgate-web/                   — Next.js cloud admin dashboard (89 files in src/)
│   ├── src/
│   │   ├── app/                     — Pages: login, admin, dashboard, API routes (37 files)
│   │   ├── components/              — React components (admin/9, dashboard/5, shared/6, ui/8)
│   │   ├── lib/                     — MongoDB models, utilities (19 files)
│   │   └── i18n/                    — Translations: en.json, fr.json, ar.json
│   └── ...
│
├── dist/                            — Published executables (per-branch)
│   ├── main/                        — Full HiLink gateway + dashboard (focusgate DB)
│   ├── alaafi/                      — Full HiLink gateway + dashboard (alaafi DB)
│   └── flixiDz/                     — Full HiLink gateway + dashboard (flixiDz DB)
│
├── FocusGate.sln                    — Solution file
├── AGENTS.md                        — Agent instructions
└── README.md                        — This file
```

---

## Branches

| Branch | MongoDB Database | Publish Folder | Target Machine |
|--------|-----------------|----------------|----------------|
| `main` | `focusgate` | `dist/main/` | BERRAR PC / dev |
| `alaafi` | `alaafi` | `dist/alaafi/` | alaafi deployment |
| `flixiDz` | `flixiDz` | `dist/flixiDz/` | bmsoft PC |

All branches have identical code — only `ConfigMerger.cs` and `DependencyInjection.cs` differ (the `mongodb.database` default).

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

### Publish for Deployment (per branch)

```powershell
# 1. Switch to target branch
git checkout <branch>    # main, alaafi, or flixiDz

# 2. Verify default DB
Select-String -Path src\FocusGate.Infrastructure\Services\ConfigMerger.cs -Pattern 'mongodb.database'

# 3. Build
dotnet build FocusGate.sln

# 4. Publish HiLink (entry point)
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/<branch>

# 5. Publish Dashboard
dotnet publish src/FocusGate.Dashboard -c Release -r win-x64 --self-contained -o dist/<branch>-dashboard

# 6. Copy Dashboard files to HiLink dist
Copy-Item dist\<branch>-dashboard\FocusGate.Dashboard.exe dist\<branch>\ -Force
Copy-Item dist\<branch>-dashboard\FocusGate.Dashboard.dll dist\<branch>\ -Force
Copy-Item dist\<branch>-dashboard\FocusGate.Dashboard.pdb dist\<branch>\ -Force
Copy-Item dist\<branch>-dashboard\FocusGate.Dashboard.deps.json dist\<branch>\ -Force
Copy-Item dist\<branch>-dashboard\FocusGate.Dashboard.runtimeconfig.json dist\<branch>\ -Force
Copy-Item dist\<branch>-dashboard\FocusGate.Dashboard.staticwebassets.endpoints.json dist\<branch>\ -Force
Copy-Item dist\<branch>-dashboard\appsettings.json dist\<branch>\ -Force
Copy-Item dist\<branch>-dashboard\web.config dist\<branch>\ -Force
Copy-Item dist\<branch>-dashboard\en dist\<branch>\en -Recurse -Force
Copy-Item dist\<branch>-dashboard\fr dist\<branch>\fr -Recurse -Force
Copy-Item dist\<branch>-dashboard\ar dist\<branch>\ar -Recurse -Force
Copy-Item dist\<branch>-dashboard\wwwroot dist\<branch>\wwwroot -Recurse -Force

# 7. Cleanup
Remove-Item dist\<branch>-dashboard -Recurse -Force
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
- SIM cards (phone number, online/offline status, last seen) — balance hidden from users
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
5. MongoSyncService waits 15s, connects to MongoDB (5 retries, 30s apart)
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
| `mongodb.uri` | Direct connection string | MongoDB Atlas connection URI (not SRV) |
| `mongodb.database` | `focusgate` / `alaafi` / `flixiDz` | MongoDB database name (per-branch) |
| `sync.interval_seconds` | `30` | MongoDB sync interval |
| `hilink.scan_ips` | `192.168.8.1,192.168.200.1,192.168.1.1` | IPs to scan for modems |
| `modem.watchdog.interval` | `30` | Watchdog loop interval (seconds) |
| `modem.sms.poll.interval` | `30` | SMS poll loop interval (seconds) |
| `modem.ussd.balance_code` | `*222#` | USSD code for balance check |
| `modem.ussd.phone_code` | `*101#` | USSD code for phone number |

**MongoDB**: Uses direct connection string (not SRV) — SRV DNS fails on some networks.

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
| **Balance staleness** | Online=green, Offline=gray+"(last known)" |
| **Dashboard home** | SIM balance = online modems only |
| **Withdrawal approval** | Deducts withdrawal amount (not zero) |
| **User SIM cards** | Balance hidden — users see wallet only |
| **Locale-aware** | All numbers/dates use selected language (en/fr/ar) |

---

## Credentials

| Type | Username | Password |
|------|----------|----------|
| Admin | `admin` | `admin` |
| Test user | `oussama` | `oussama` |

---

## Deployment

### Deploy to New PC

1. Copy `dist/<branch>/*` to the PC
2. Run `FocusGate.HiLink.exe` — auto-creates config + SQLite database
3. Open `http://localhost:5080` — dashboard works immediately
4. Configure `mongodb.uri` in config.json for cloud sync (if needed)

### Database Reset

Delete `focusgate.db` + `-shm` + `-wal` files, restart to re-seed `admin:admin`.

### Machine IDs

| Machine | MachineId | Branch | Notes |
|---------|-----------|--------|-------|
| Dev (local) | `d26b1c221259fb12` | main | Generated from hardware fingerprint |
| Client (BERRAR) | `419c0cfc97666753` | main | 10 modems |
| bmsoft | `b0a458aebe2393a4` | flixiDz | 1 modem |

---

## Build Commands Reference

```powershell
# .NET
dotnet build FocusGate.sln                                    # Build (0 warnings, 0 errors)
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/<branch>
dotnet publish src/FocusGate.Dashboard -c Release -r win-x64 --self-contained -o dist/<branch>-dashboard

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
