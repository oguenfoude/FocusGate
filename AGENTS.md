# FocusGate — Agent Instructions

## Project Structure

Two separate products in one repo:

| Directory | What | Stack |
|-----------|------|-------|
| `src/` | USB modem gateway (Windows service) | .NET 10, C#, SQLite |
| `focusgate-web/` | Cloud admin dashboard (Next.js) | Next.js 16, React 19, MongoDB Atlas, Tailwind 4 |

### .NET Gateway (4 projects)

```
src/FocusGate.Core/           — Models, enums, PathService (no deps)
src/FocusGate.Infrastructure/ — DbContext, services, MongoDB sync
src/FocusGate.HiLink/         — Huawei HTTP modem entry point
src/FocusGate.Tests/          — xunit tests (25+ test files)
```

### Next.js Web App

See `focusgate-web/AGENTS.md` for Next.js-specific rules (Next.js 16 breaking changes).

```
focusgate-web/src/app/        — Pages: login, admin, dashboard, API routes
focusgate-web/src/lib/        — MongoDB models, auth, utilities (date-utils, number-utils, id-generator)
focusgate-web/src/components/ — React components (admin/, dashboard/, shared/)
focusgate-web/src/i18n/       — Translations: en.json, fr.json, ar.json
```

## Build & Run Commands

### .NET Gateway

```powershell
# Build (must be 0 warnings, 0 errors)
dotnet build FocusGate.sln

# Run from source
dotnet run --project src/FocusGate.HiLink    # HiLink modems

# Tests
dotnet test FocusGate.sln                     # Run all xunit tests

# Publish self-contained
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/focusgate
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
- **Balance architecture & Single Source of Truth:**
  - **User Wallet Credit:** Handled **exclusively** in `DatabaseWriteChannel.cs` (`HandleInsertMeetMobHistoryAsync`) when MeetMob history shows a balance increase. SMS recharge detection only logs — does NOT credit user wallet. MeetMob is the single source of truth.
  - **SIM Hardware Balance:** Tracked in `SimCards` table (`sim.Balance`) and `BalanceHistories` table. Updated via MeetMob API (`acctList[0].balanceResult[0].totalAmount`) or USSD `*222#` snapshots.
  - **Independent Systems:** SMS (5s poll), MeetMob (5s check), Watchdog (30s) run independently with no coupling. SMS detection only — MeetMob handles all balance/credit logic.
  - **MeetMob cooldowns:** Per-phone cooldowns (30s max). No exponential backoff. WAF cooldown per-phone (60s). `pwdWillExpired` retry immediately.
  - **Credit rules:** User wallet ONLY credited when a valid recharge SMS is processed and user is assigned to the modem. New SIM starts at Balance=0.
- **MachineId:** Each machine has a unique ID from `MachineInfoService`. Dev machine: `d26b1c221259fb12`. Client (BERRAR): `419c0cfc97666753`. Client (Alaafi): `fb96ac5207011ae1`.
- **HTMX in Dashboard:** POST handlers must use `Response.Headers["HX-Redirect"]` + `return new EmptyResult()` — NOT `RedirectToPage()`. `_ViewStart.cshtml` sets `Layout = null` for `HX-Request` header.
- **Safe shutdown:** `writeChannel.CompleteAsync()` in `ApplicationStopped` (after host.RunAsync returns).

### Next.js

- **`--webpack` flag required** for `npm run dev` and `npm run build` — Next.js 16 webpack mode
- **MongoDB `_id` precision:** IDs > `Number.MAX_SAFE_INTEGER` (9007199254740991) lose precision in JavaScript. `nextId()` uses `Date.now() * 1000` (safe). Old code used `* 10000` — some records in MongoDB have oversized IDs that can't be round-tripped through JSON. Use raw MongoDB collection queries (`mongoose.connection.db.collection(...)`) with `as Record<string, unknown>` cast when dealing with these IDs.
- **Online status:** Use `status === 4` directly. The .NET side already manages Online/Offline transitions. Do NOT add `updatedAt` staleness checks — MongoDB sync can be delayed, causing false Offline.
- **Locale-aware dates:** Use `formatDate()` / `formatShortDate()` from `@/lib/date-utils` (NOT `date-fns` `format()`). These respect the language setting (en/fr/ar).
- **Safe number conversion:** Use `toNum()` / `toNumOrNull()` from `@/lib/number-utils` for MongoDB `Decimal128` fields. `Number()` on Decimal128 gives `[object Object]`.
- **i18n:** Translation keys under `sms.types.*` in en.json, fr.json, ar.json. Use `t('sms.types.otp')` etc. in components.
- **Dashboard userId:** Stored in `localStorage` via `UserIdProvider` context. Sub-pages read from URL params (`?userId=X`) with localStorage fallback, wrapped in `<Suspense>` for `useSearchParams`.
- **Sidebar admin detection:** Uses pathname-based `isAdmin` via `useSyncExternalStore` + localStorage. No useEffect/setState.

## Data Flow

```
USB Modems → .NET Gateway → SQLite (local) → MongoDB Atlas (cloud) ← Next.js Web App (writes users/withdrawals)
```

Both .NET Gateway and Next.js Web App read from SQLite.

**Data Ownership:** `Modems`, `SimCards`, `SmsRecords`, `BalanceHistories` — .NET only. `Users`, `UserModems`, `WithdrawalRequests`, `UserBalanceHistories` — Dashboard/Next.js only.

MongoDB Collections (8): `modems`, `simcards`, `smsrecords`, `users`, `usermodems`, `balancehistories`, `withdrawalrequests`, `userbalancehistories`. Full schema: `MONGO_SCHEMA.md`

## Branches

| Branch | MongoDB Database | Use Case |
|--------|-----------------|----------|
| `main` | `focusgate` | Original/dev — BERRAR machine |
| `alaafi` | `alaafi` | Alaafi deployment |
| `flixiDz` | `flixiDz` | bmsoft machine |

Code is identical across branches — only `mongodb.database` default differs in config.

## Gotchas

- **SumAsync on decimal** not supported by SQLite — use `ToListAsync()` then sum in C#
- **ConfigMerger takes file path** not directory path: `Path.Combine(dataDir, "config.json")`
- **Global query filters** apply to all queries unless `IgnoreQueryFilters()` is used
- **Admin user hidden from Users page** — filtered by `Role != UserRole.Admin` by design
- **Verify with build + tests** — `dotnet build FocusGate.sln` (0 warnings, 0 errors) + `dotnet test FocusGate.sln`
- **USSD lock timeout** — HiLinkCommandService.SendUssdAsync has 15s lock timeout; AT has 10s
- **SendUssdAsync on HiLink** sends `POST /api/ussd/send` then polls `GET /api/ussd/get` every 2s
- **125002 error** means SMS inbox full — DeleteAllSmsAsync falls back to index-based deletion (1-50)
- **Session refresh failure** clears _sessionCookie, _csrfToken, sets _isOpen=false — forces clean re-handshake
- **MeetMob login uses OTP** — requires SIM to receive SMS. Phone format: local (0XXXXXXXXX), not country code
- **MeetMob token TTL** — 45 minutes, proactive refresh at 40 minutes in watchdog loop
- **MeetMob backoff** — 2min → 5min → 30min on consecutive failures. Success resets counter.
- **MeetMob is primary** — USSD *222# is fallback only. System always tries MeetMob first.

## Config Keys (config.json)

| Key | Default | Description |
|-----|---------|-------------|
| `modem.timezone_offset_hours` | `1` | UTC offset for modem date parsing (Algeria = UTC+1) |
| `display.timezone_offset_hours` | `""` (empty = use Algeria TZ) | Override display timezone. Empty = use `Africa/Algiers` |
| `modem.max_count` | `15` | Maximum modems per orchestrator |
| `modem.ussd.balance_code` | `*222#` | USSD code for balance check |
| `modem.ussd.phone_code` | `*101#` | USSD code for phone number |
| `mongodb.uri` | (cluster URI) | MongoDB Atlas connection string |
| `mongodb.database` | `focusgate` | MongoDB database name |
| `meetmob.base_url` | `https://meetmob.mobilis.dz` | MeetMob API base URL |
| `meetmob.password` | `00000` | MeetMob password |
| `meetmob.token_ttl` | `2700` | Token TTL in seconds (45min) |
| `meetmob.http_timeout` | `10` | HTTP timeout in seconds |
| `meetmob.login_cooldown` | `5` | Login cooldown in seconds (per-phone) |
| `meetmob.fallback_cooldown` | `5` | Fallback cooldown in seconds (per-phone) |
| `meetmob.check.interval` | `60` | MeetMob check interval in seconds |
