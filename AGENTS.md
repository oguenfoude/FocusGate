# FocusGate — Agent Instructions

## Project Structure

Two separate products in one repo:

| Directory | What | Stack |
|-----------|------|-------|
| `src/` | USB modem gateway (Windows service) | .NET 10, C#, SQLite, ASP.NET Core Dashboard |
| `focusgate-web/` | Cloud admin dashboard (Next.js) | Next.js 16, React 19, MongoDB Atlas, Tailwind 4 |

### .NET Gateway (5 projects)

```
src/FocusGate.Core/           — Models, enums, PathService (no deps, 19 .cs files)
src/FocusGate.Infrastructure/ — DbContext, services, MongoDB sync (15 .cs files)
src/FocusGate.AT/             — COM port modem entry point (3 .cs files)
src/FocusGate.HiLink/         — Huawei HTTP modem entry point (2 .cs files)
src/FocusGate.Dashboard/      — ASP.NET Core Razor Pages (port 5080, 9 pages)
```

### Next.js Web App (89 files in src/)

```
focusgate-web/src/app/        — Pages: login, admin, dashboard, API routes (37 files)
focusgate-web/src/components/ — React components (admin/9, dashboard/5, shared/6, ui/8, root/3 = 31)
focusgate-web/src/lib/        — MongoDB models, utilities (19 files)
focusgate-web/src/i18n/       — Translations: en.json, fr.json, ar.json (3 files)
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

# Publish self-contained (per-branch, see Deployment section)
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/<branch>
dotnet publish src/FocusGate.Dashboard -c Release -r win-x64 --self-contained -o dist/<branch>-dashboard
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
- **MongoDB URI:** Real URI in `config.json` only. NEVER commit real URI to source code. Placeholder in `ConfigMerger.cs` contains `admin:admin@cluster0...` as example.
- **MongoDB sync is non-fatal** — app works fine without it. MongoSyncService has 15s startup delay, 5 retry attempts with 30s intervals. PullFromMongoAsync is resilient per-collection — one bad collection doesn't kill all sync.
- **MongoDB pull uses in-memory matching** — Loads local records by ID list, matches in Dictionary. EF Core can't translate `Func<T, object>` in LINQ expressions (CS1963).
- **MongoDB collection names are ALL lowercase** — .NET `FocusGateMongoClient.cs` uses `"modems"`, `"simcards"`, etc. Next.js Mongoose models must match.
- **MongoDB `_id` is Number (long)** — NOT ObjectId. `BsonClassMap.MapIdMember(m => m.Id)` maps C# `long Id` to MongoDB `_id`.
- **Balance architecture:** SMS from Mobilis is a TRIGGER only. Never parse amounts from SMS text. `*222#` USSD is the single source of truth for `SimCard.Balance`.
- **MachineId:** Each machine has a unique ID generated at runtime from hardware fingerprint (MAC + MachineName + UserName + MachineGuid). Config key `machine.id` can override. Dev machine: `d26b1c221259fb12`. Client (BERRAR): `419c0cfc97666753`. New PC (bmsoft): `b0a458aebe2393a4`.
- **HTMX in Dashboard:** POST handlers must use `Response.Headers["HX-Redirect"]` + `return new EmptyResult()` — NOT `RedirectToPage()`. `_ViewStart.cshtml` sets `Layout = null` for `HX-Request` header.
- **Dashboard DI:** Uses `AddFocusGateDashboard()` (lightweight — no MongoSync, no ConsoleCommandHandler, no RestartService).
- **Safe shutdown:** `writeChannel.CompleteAsync()` in `ApplicationStopped` (after host.RunAsync returns). Dashboard process tracked and killed in `ApplicationStopped`.

### Next.js

- **`--webpack` flag required** for `npm run dev` and `npm run build` — Next.js 16 webpack mode
- **MongoDB `_id` precision:** IDs > `Number.MAX_SAFE_INTEGER` (9007199254740991) lose precision in JavaScript. `nextId()` uses `Date.now() * 1000` (safe). Old code used `* 10000` — some records in MongoDB have oversized IDs that can't be round-tripped through JSON. Use raw MongoDB collection queries (`mongoose.connection.db.collection(...)`) with `as Record<string, unknown>` cast when dealing with these IDs.
- **Online status:** Use `status === 4` directly. The .NET side already manages Online/Offline transitions. Do NOT add `updatedAt` staleness checks — MongoDB sync can be delayed, causing false Offline.
- **Locale-aware dates:** Use `formatDate()` / `formatShortDate()` from `@/lib/date-utils` (NOT `date-fns` `format()`). These respect the language setting (en/fr/ar).
- **Locale-aware numbers:** All `toLocaleString()` calls must use the locale from `useLanguage()` hook. Pattern: `const loc = locale === 'fr' ? 'fr-FR' : locale === 'ar' ? 'ar-DZ' : 'en-US'`. Used in StatCards, ModemTable, UserTable, WithdrawalTable, WarningsContent, UserDetail, WithdrawForm, HistoryList.
- **Safe number conversion:** Use `toNum()` / `toNumOrNull()` from `@/lib/number-utils` for MongoDB `Decimal128` fields. `Number()` on Decimal128 gives `[object Object]`.
- **i18n:** Translation keys under `sms.types.*` in en.json, fr.json, ar.json. Use `t('sms.types.otp')` etc. in components. Settings page has 20+ keys under `settings.*`. All date/number labels translated.
- **Dashboard userId:** Stored in `localStorage` via `UserIdProvider` context. Sub-pages read from URL params (`?userId=X`) with localStorage fallback, wrapped in `<Suspense>` for `useSearchParams`.
- **Sidebar admin detection:** Uses pathname-based `isAdmin` via `useSyncExternalStore` + localStorage. No useEffect/setState.
- **No ComPort in web** — ComPort removed from all Next.js pages (admin and dashboard). Only .NET side uses it.
- **User dashboard balance** — Hidden from SIM cards view. Users see wallet balance only, not SIM balance.
- **Withdrawal flow** — User submits: creates pending request, balance NOT deducted immediately. Admin approves: deducts withdrawal amount from user balance, records BalanceHistory(source=4) + UserBalanceHistory(type=1). Admin rejects: no balance change.

## Data Flow

```
USB Modems → .NET Gateway → SQLite (local) → MongoDB Atlas (cloud) ← Next.js Web App (writes users/withdrawals)
```

### Startup Sequence

```
1. Mutex check (Global\FocusGate_HiLink or Global\FocusGate_AT)
2. ConfigMerger.EnsureConfig() — creates/merges config.json (atomic write)
3. DatabaseInitializer.Initialize() — EnsureCreated + PRAGMAs + column migrations + indexes
4. DatabaseWriteChannel.Start() — begins processing write queue
5. MongoSyncService waits 15s, then connects to MongoDB (5 retries, 30s apart)
6. HiLinkDiscovery probes IPs (parallel, 2s timeout) → finds modems
7. For each modem found:
   a. HiLinkCommandService.OpenAsync() — gets session cookie + CSRF token
   b. ModemHandler created → StartAsync():
      - GetImeiAsync, GetImsiAsync, GetNetworkRegistrationAsync
      - Insert modem + SIM to SQLite
      - *222# balance check → update SimCard.Balance
      - *101# phone detection (if missing)
      - Read startup SMS → save to SQLite → delete from SIM
      - Start 3 async loops (watchdog, SMS poll, network retry)
8. Orphan check — marks missing modems as Offline
```

### Steady State (every 30s)

```
SMS Poll (30s): ReadAllSmsAsync → save to SQLite (dedup: SimCardId+Sender+Content+ReceivedAt) → DeleteAllSmsAsync
Watchdog (30s): TryRefreshSessionAsync → IsAliveAsync → mark Online/Offline/Disconnect
Network Retry (2min): GetNetworkRegistrationAsync → mark Online + write status
MongoDB Sync (30s): push SQLite changes → pull MongoDB changes
Scan Cycle (30s): probe for new modems → orphan check for missing modems
```

### Mobilis SMS Trigger

```
When recharge/transfer SMS from "Mobilis" or "77111" detected:
  → Parse "Solde" from SMS content
  → *222# USSD to confirm real balance
  → Update SimCard.Balance + BalanceHistory
  → Credit user balance if increase detected
```

### Key: `*222#` Only Fires At

1. **Startup** — once per modem when connected
2. **Mobilis SMS** — when recharge/transfer SMS detected
3. **Never periodically** — no auto-refresh, no timer, no retry loop

## Data Ownership (who writes each SQLite table)

| Table | Writer | Notes |
|-------|--------|-------|
| `Modems` | .NET only | Status, IMEI, ComPort, Brand, Model, UpdatedAt |
| `SimCards` | .NET only | Balance (from USSD), IMSI, PhoneNumber, IsActive |
| `SmsRecords` | .NET only | SMS received by modems |
| `BalanceHistories` | .NET only | SIM balance change records (from USSD) |
| `Users` | Dashboard/Next.js | Created/edited by admin |
| `UserModems` | Dashboard/Next.js | Assign/remove modem-to-user |
| `WithdrawalRequests` | Dashboard/Next.js | User requests, admin approve/reject |
| `UserBalanceHistories` | Dashboard/Next.js | Created on withdrawal approval |

Both .NET Gateway and Dashboard read from SQLite. Next.js Web App reads from MongoDB.

## MongoDB Collections (8)

`modems`, `simcards`, `smsrecords`, `users`, `usermodems`, `balancehistories`, `withdrawalrequests`, `userbalancehistories`

Full schema reference: `MONGO_SCHEMA.md`

## Per-Modem Architecture

### ModemHandler

Single handler per connected modem. Manages modem lifecycle with 3 async loops:

| Loop | Interval | What it does |
|------|----------|-------------|
| **Watchdog** | 30s | HiLink: TryRefreshSessionAsync → IsAliveAsync → Online/Offline/Disconnect. AT: send "AT" → Online/Disconnect |
| **SMS Poll** | 30s | ReadAllSmsAsync → save to SQLite → DeleteAllSmsAsync → check for Mobilis balance SMS → trigger *222# if recharge |
| **Network Retry** | 2min | GetNetworkRegistrationAsync → mark Online + write status |

**Startup:** GetImei → GetImsi → GetNetworkReg → *222# balance → *101# phone (if missing) → startup SMS read → start loops

**Shutdown:** Cancel CTS → loops exit → Dispose AT service → orchestrator removes handler

### HiLinkCommandService

HTTP API for Huawei HiLink modems:
- `OpenAsync(ip)` — tries HTTP then HTTPS, gets SesInfo/CsrfToken
- `TryRefreshSessionAsync()` — re-fetches SesInfo/CsrfToken, clears state on failure
- `SendGetAsync/SendPostAsync` — with `LastRequestFailed` flag, throws on non-2xx
- `ReadAllSmsAsync()` — XML parsing, throws on HTTP failure (caller catches and disconnects)
- `SendUssdAsync(code, timeout)` — sends USSD, polls `/api/ussd/get`, 15s lock timeout
- `GetBalanceAsync()` — sends *222#, parses "Solde" from response
- `DeleteAllSmsAsync()` — deletes SMS by index, fallback 1-50 on 125002 inbox full

### AtCommandService (in FocusGate.AT)

Serial port AT commands:
- Multi-baud opening (9600/115200/57600/19200)
- AT+CMGL SMS reading with UDH concatenation reassembly + consecutive-index merge
- GSM 7-bit/UTF-16/ISO-8859-1 SMS decoding
- AT+CUSD USSD with hex/UTF-16/plain text response decoding
- 10s lock timeout on SendCommand/SendUssd

### HiLinkModemOrchestrator (in FocusGate.HiLink)

BackgroundService scanning IPs every 30s (max 15 modems):
- Parallel probe → create HiLinkCommandService → create ModemHandler
- Blacklists IPs after 3 failures; known modem IPs retried indefinitely
- Orphan check: marks missing modems Offline (skipped when new handlers starting)

### DatabaseWriteChannel

Single serialized write queue using `Channel<Op>`. ALL DB writes go through here.

**Operations:**
- `InsertModem` — modem + SIM (atomic — two SaveChanges: first Modem, then SimCard with FK)
- `UpdateModemStatus` — status + UpdatedAt
- `TouchModemUpdatedAt` — heartbeat (no status change)
- `UpsertSimCard` — detect SIM changes
- `UpdateSimCardPhone` — phone number from USSD
- `UpdateSimBalance` — balance from *222# + BalanceHistory
- `UpdateSimBalanceFromSms` — balance from Mobilis SMS + user credit
- `InsertSms` — with dedup (SimCardId+Sender+Content+ReceivedAt) + Mobilis trigger
- `UpdateOrphanedModems` — marks missing modems Offline
- `CreateWithdrawalRequest` / `ProcessWithdrawal` — withdrawal workflow

### MongoSyncService

BackgroundService. Bidirectional sync every 30s:
- **Push:** SQLite → MongoDB (upsert by `_id` + `machineId`). Per-collection counts logged
- **Pull:** MongoDB → SQLite (in-memory matching by ID). SimCard balance only overwritten when `remote.UpdatedAt > local.UpdatedAt`
- `_lastSyncAt` only advances when BOTH push AND pull succeed
- `_initialSyncDone` only set on full success
- `SafeUpsertAsync` handles DuplicateKey by claiming with `_id`-only filter
- `StopAsync` performs final sync before shutdown

## Dashboard Pages (ASP.NET Razor Pages — 9 total)

| Page | Purpose |
|------|---------|
| `Index` | Dashboard home: 4 stat cards (modems, SIM balance online-only, user wallets, pending withdrawals) |
| `Modems` | Modem list with filter pills (All/Online/Offline/Assigned/Unassigned), HTMX 5s refresh |
| `ModemDetail` | Modem detail: Info + Balance History + SMS tabs |
| `Users` | User CRUD with search, archived toggle, add user modal, edit modal |
| `UserDetail` | User detail: Modems + Wallet + History + SMS tabs, Edit User button |
| `Withdrawals` | Withdrawal requests: All/Pending/Approved/Rejected filter tabs, approve/reject |
| `Warnings` | Modems with high SIM balance (>= 45000 DA) |
| `AdminSettings` | Change username and password |
| `SetLanguage` | Switch between EN/FR/AR |

## Console Commands

`help`, `status`, `modems`, `modem <id>`, `sms [modemId] [days]`, `sim <modemId>`, `config`, `set-config <k> <v>`, `setmongo <uri>`, `users`, `adduser <u> <p> [d]`, `assign <uid> <mid>`, `unassign <uid> <mid>`, `settle <modId> <amt> [note]`, `report balance|sms [id] [days]`, `exit`

## Deployment

### 3-Branch Strategy

Each branch has a different default MongoDB database baked into the compiled DLL:

| Branch | Default MongoDB DB | Publish Folder | Target |
|--------|-------------------|----------------|--------|
| `main` | `focusgate` | `dist/main/` | BERRAR PC / dev |
| `alaafi` | `alaafi` | `dist/alaafi/` | alaafi deployment |
| `flixiDz` | `flixiDz` | `dist/flixiDz/` | bmsoft PC |

All branches have identical code — only `ConfigMerger.cs` and `DependencyInjection.cs` differ (the `mongodb.database` default).

### Build & Publish (per branch)

```powershell
# 1. Switch to branch
git checkout <branch>    # main, alaafi, or flixiDz

# 2. Verify default DB in source
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

# 7. Cleanup temp folder
Remove-Item dist\<branch>-dashboard -Recurse -Force
```

### Deploy to Target PC

1. Copy `dist/<branch>/*` to the PC
2. Run `FocusGate.HiLink.exe` — auto-creates config + SQLite database
3. Dashboard opens at `http://localhost:5080`
4. Configure `mongodb.uri` in config.json for cloud sync (if needed)

### Machine IDs

| Machine | MachineId | Notes |
|---------|-----------|-------|
| Dev (local) | `d26b1c221259fb12` | Generated from hardware fingerprint |
| Client (BERRAR) | `419c0cfc97666753` | 10 modems, main branch |
| bmsoft | `b0a458aebe2393a4` | 1 modem, flixiDz branch |

### Database Reset

Delete `focusgate.db` + `-shm` + `-wal` files, restart to re-seed `admin:admin`.

### Mutexes & Pipes

- `Global\FocusGate_HiLink` — prevents duplicate HiLink instances
- `Global\FocusGate_AT` — prevents duplicate AT instances
- `FocusGate_Restart` — named pipe for restart/stop signals (accepts "restart" or "stop")

## Gotchas

- **Dashboard process locks DLLs** — kill `FocusGate.Dashboard` before rebuilding
- **SumAsync on decimal** not supported by SQLite — use `ToListAsync()` then sum in C#
- **ConfigMerger takes file path** not directory path: `Path.Combine(dataDir, "config.json")`
- **`User` property on PageModel** conflicts with `Model.User` — use `new` keyword
- **Global query filters** apply to all queries unless `IgnoreQueryFilters()` is used
- **Admin user hidden from Users page** — filtered by `Role != UserRole.Admin` by design
- **No tests exist** — verify with `dotnet build` (0 warnings, 0 errors) and manual browser testing
- **USSD lock timeout** — HiLinkCommandService.SendUssdAsync has 15s lock timeout; AT has 10s
- **SendUssdAsync on HiLink** sends `POST /api/ussd/send` then polls `GET /api/ussd/get` every 2s
- **125002 error** means SMS inbox full — DeleteAllSmsAsync falls back to index-based deletion (1-50)
- **Session refresh failure** clears _sessionCookie, _csrfToken, sets _isOpen=false — forces clean re-handshake
- **Dashboard home total SIM balance** = online SIMs only — offline SIM balances excluded from total
- **Balance staleness** — Online modems show green balance; offline modems show gray + "(last known)" label
- **Withdrawal approval** deducts withdrawal amount (not zero) — `newBalance = Math.max(0, oldBalance - amount)`
- **MongoDB oversized IDs** — Old records created with `Date.now() * 10000` have IDs > MAX_SAFE_INTEGER. PATCH handlers use raw MongoDB collection to avoid precision loss
- **Not-found page** is client component — needs `useLanguage()` hook for i18n, can't be server component
- **User detail admin page** — converted to client component for i18n header
- **LiveProvider key path** — modem detail uses `modemDetail.*` (NOT `usersDetail.*`)
- **`date-fns` removed** — replaced by custom `lib/date-utils.ts` with locale-aware formatting
- **`NEXTAUTH_SECRET`/`NEXTAUTH_URL`** — removed from `.env.local` (auth removed from web app)
