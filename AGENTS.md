# FocusGate — Agent Instructions

## Project Structure

Two separate products in one repo:

| Directory | What | Stack |
|-----------|------|-------|
| `src/` | USB modem gateway (Windows service) | .NET 10, C#, SQLite, ASP.NET Core Dashboard |
| `focusgate-web/` | Cloud admin dashboard (Next.js) | Next.js 16, React 19, MongoDB Atlas, Tailwind 4 |

### .NET Gateway (5 projects)

```
src/FocusGate.Core/          — Models, enums, PathService (no deps)
src/FocusGate.Infrastructure/ — DbContext, services, MongoDB sync
src/FocusGate.AT/             — COM port modem entry point
src/FocusGate.HiLink/         — Huawei HTTP modem entry point
src/FocusGate.Dashboard/      — ASP.NET Core Razor Pages (port 5080)
```

### Next.js Web App

```
focusgate-web/src/app/       — Pages: login, admin, dashboard, API routes
focusgate-web/src/lib/       — MongoDB models, auth, utilities
focusgate-web/src/components/ — React components
focusgate-web/src/types/     — TypeScript types
```

## Build & Run Commands

### .NET Gateway

```powershell
# Build (must be 0 warnings, 0 errors)
dotnet build FocusGate.sln

# Run from source
dotnet run --project src/FocusGate.HiLink    # HiLink modems + auto-launches Dashboard
dotnet run --project src/FocusGate.AT         # AT modems + auto-launches Dashboard
dotnet run --project src/FocusGate.Dashboard  # Dashboard only (port 5080)

# Publish self-contained
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/hilink
dotnet publish src/FocusGate.AT -c Release -r win-x64 --self-contained -o dist/at
dotnet publish src/FocusGate.Dashboard -c Release -r win-x64 --self-contained -o dist/dashboard

# After publishing Dashboard, copy to hilink dist:
Copy-Item dist\dashboard\FocusGate.Dashboard.exe dist\hilink\ -Force
Copy-Item dist\dashboard\FocusGate.Dashboard.dll dist\hilink\ -Force
Copy-Item dist\dashboard\FocusGate.Dashboard.pdb dist\hilink\ -Force
Copy-Item dist\dashboard\FocusGate.Dashboard.deps.json dist\hilink\ -Force
Copy-Item dist\dashboard\FocusGate.Dashboard.runtimeconfig.json dist\hilink\ -Force
Copy-Item dist\dashboard\FocusGate.Dashboard.staticwebassets.endpoints.json dist\hilink\ -Force
Copy-Item dist\dashboard\appsettings.json dist\hilink\ -Force
Copy-Item dist\dashboard\web.config dist\hilink\ -Force
Copy-Item dist\dashboard\en dist\hilink\en -Recurse -Force
Copy-Item dist\dashboard\fr dist\hilink\fr -Recurse -Force
Copy-Item dist\dashboard\ar dist\hilink\ar -Recurse -Force
Copy-Item dist\dashboard\wwwroot dist\hilink\wwwroot -Recurse -Force
```

### Next.js Web App

```powershell
cd focusgate-web
npm run dev      # Dev server (port 3000, --webpack flag required)
npm run build    # Production build (--webpack flag required)
npm run lint     # ESLint
npm start        # Production server
```

## Critical Conventions

### .NET

- **Target framework:** `net10.0` (not net8.0, not net9.0)
- **Passwords:** Plain text — NO hashing, NO SHA256, NO BCrypt. `User.Password` stores raw text.
- **Database:** SQLite via EF Core. `DatabaseWriteChannel` serializes ALL writes through `Channel<T>`. Never write to DbContext directly from service code.
- **PRAGMA foreign_keys=ON** runs at startup via `DatabaseInitializer`.
- **Soft delete:** `ArchivedAt` field on all entities. Never hard-delete. Global query filters exclude archived records (`ArchivedAt == null`). Use `IgnoreQueryFilters()` to see archived.
- **Config:** `config.json` in `%APPDATA%\FocusGate\`. Auto-created by `ConfigMerger`. Never edit manually — use `set-config` console command.
- **MongoDB URI:** Real URI in `config.json` only. NEVER commit real URI to source code. Placeholder in `ConfigMerger.cs` is `user:password@cluster.example.net`.
- **MongoDB sync is non-fatal** — app works fine without it. MongoSyncService has 15s startup delay, 5 retry attempts with 30s intervals. PullFromMongoAsync is resilient per-collection — one bad collection doesn't kill all sync.
- **MongoDB pull uses in-memory matching** — Loads local records by ID list, matches in Dictionary. EF Core can't translate `Func<T, object>` in LINQ expressions (CS1963).
- **MongoDB collection names are ALL lowercase** — .NET `FocusGateMongoClient.cs` uses `"modems"`, `"simcards"`, etc. Next.js Mongoose models must match.
- **MongoDB `_id` is Number (long)** — NOT ObjectId. `BsonClassMap.MapIdMember(m => m.Id)` maps C# `long Id` to MongoDB `_id`.
- **Balance architecture:** SMS from Mobilis is a TRIGGER only. Never parse amounts from SMS text. `*222#` USSD is the single source of truth for `SimCard.Balance`.
- **MachineId:** Each machine has a unique ID from `MachineInfoService`. Dev machine: `d26b1c221259fb12`. Client (BERRAR): `419c0cfc97666753`.
- **HTMX in Dashboard:** POST handlers must use `Response.Headers["HX-Redirect"]` + `return new EmptyResult()` — NOT `RedirectToPage()`. `_ViewStart.cshtml` sets `Layout = null` for `HX-Request` header.
- **Dashboard DI:** Uses `AddFocusGateDashboard()` (lightweight — no MongoSync, no ConsoleCommandHandler, no RestartService).
- **Safe shutdown:** `writeChannel.CompleteAsync()` in `ApplicationStopping`. Dashboard process tracked and killed in `ApplicationStopped`.

### Next.js Web App

- **Next.js 16** — has breaking changes from earlier versions. Check `node_modules/next/dist/docs/` before writing code.
- **Dev command:** `npm run dev` uses `--webpack` flag (required).
- **Auth:** `next-auth` v4 with credentials provider. `NEXTAUTH_SECRET` and `MONGODB_URI` in `.env.local`.
- **MongoDB:** Mongoose 9.x. Models in `src/lib/models/`. Connection in `src/lib/mongodb.ts`.
- **No API route for gateway** — the web app reads MongoDB directly. The .NET gateway pushes data to MongoDB Atlas.

## Data Flow

```
USB Modems → .NET Gateway → SQLite (local) → MongoDB Atlas (cloud) ← Next.js Web App (writes users/withdrawals)
```

### Data Ownership (who writes each collection)

| Collection | Writer | Notes |
|------------|--------|-------|
| `modems` | .NET only | Status, IMEI, ComPort, Brand |
| `simcards` | .NET only | Balance (from USSD), IMSI, PhoneNumber |
| `smsrecords` | .NET only | SMS received by modems |
| `users` | Next.js only | Created/edited by admin in web UI |
| `usermodems` | Next.js only | Assign/remove modem-to-user |
| `balancehistories` | .NET only | SIM balance change records (from USSD) |
| `withdrawalrequests` | Next.js only | User requests, admin approve/reject |
| `userbalancehistories` | Next.js only | Created on withdrawal approval |

Both systems pull from MongoDB → SQLite. .NET pushes modem/SIM/SMS data. Next.js pushes user/assignment/withdrawal data.

## MongoDB Collections (8)

`modems`, `simcards`, `smsrecords`, `users`, `usermodems`, `balancehistories`, `withdrawalrequests`, `userbalancehistories`

Full schema reference: `MONGO_SCHEMA.md`

## Deployment

### Client PC (BERRAR)

- **MachineId:** `419c0cfc97666753`
- **Data path:** `C:\Users\BERRAR\AppData\Roaming\FocusGate\`
- **Deploy:** Copy `dist\hilink\*` to client PC, run `FocusGate.HiLink.exe`
- **Database reset:** Delete `focusgate.db` + `-shm` + `-wal` files, restart to re-seed `admin:admin`

### Mutexes & Pipes

- `Global\FocusGate_HiLink` — prevents duplicate HiLink instances
- `Global\FocusGate_AT` — prevents duplicate AT instances
- `FocusGate_Restart` — named pipe for restart/stop signals

## Gotchas

- **Dashboard process locks DLLs** — kill `FocusGate.Dashboard` before rebuilding
- **SumAsync on decimal** not supported by SQLite — use `ToListAsync()` then sum in C#
- **ConfigMerger takes file path** not directory path: `Path.Combine(dataDir, "config.json")`
- **`User` property on PageModel** conflicts with `Model.User` — use `new` keyword
- **Global query filters** apply to all queries unless `IgnoreQueryFilters()` is used
- **Admin user hidden from Users page** — filtered by `Role != UserRole.Admin` by design
- **No tests exist** — verify with `dotnet build` (0 warnings, 0 errors) and manual browser testing
