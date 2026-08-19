# FocusGate — Agent Instructions

## Project Structure

USB modem gateway — reads SMS, checks balance via MeetMob API, credits user wallets, syncs to MongoDB Atlas.

| Directory | What | Stack |
|-----------|------|-------|
| `src/FocusGate.Core/` | Models, enums, interfaces (no deps) | .NET 10, C# |
| `src/FocusGate.Infrastructure/` | DbContext, services, MongoDB sync | .NET 10, EF Core, SQLite, MongoDB |
| `src/FocusGate.HiLink/` | Huawei USB modem entry point | .NET 10, HiLink HTTP API |

## Build & Run Commands

```powershell
# Build (must be 0 warnings, 0 errors)
dotnet build FocusGate.sln

# Run from source
dotnet run --project src/FocusGate.HiLink

# Publish self-contained
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/focusgate
```

## Critical Conventions

- **Target framework:** `net10.0` (not net8.0, not net9.0)
- **Passwords:** Plain text — NO hashing, NO SHA256, NO BCrypt. `User.Password` stores raw text.
- **Database:** SQLite via EF Core. `DatabaseWriteChannel` serializes ALL writes through `Channel<T>`. Never write to DbContext directly from service code.
- **PRAGMA foreign_keys=ON** runs at startup via `DatabaseInitializer`.
- **Soft delete:** `ArchivedAt` field on all entities. Never hard-delete. Global query filters exclude archived records (`ArchivedAt == null`). Use `IgnoreQueryFilters()` to see archived.
- **Config:** `config.json` in `%APPDATA%\FocusGate\`. Auto-created by `ConfigMerger`. Never edit manually — use `set-config` console command.
- **MongoDB URI:** Real URI in `config.json` only. NEVER commit real URI to source code. Placeholder in `ConfigMerger.cs` is `user:password@cluster.example.net`.
- **MongoDB sync is non-fatal** — app works fine without it. MongoSyncService has 5s startup delay, 5 retry attempts with 30s intervals. PullFromMongoAsync is resilient per-collection — one bad collection doesn't kill all sync.
- **MongoDB pull uses in-memory matching** — Loads local records by ID list, matches in Dictionary. EF Core can't translate `Func<T, object>` in LINQ expressions (CS1963).
- **MongoDB collection names are ALL lowercase** — .NET `FocusGateMongoClient.cs` uses `"modems"`, `"simcards"`, etc. Next.js Mongoose models must match.
- **MongoDB `_id` is Number (long)** — NOT ObjectId. `BsonClassMap.MapIdMember(m => m.Id)` maps C# `long Id` to MongoDB `_id`.
- **Balance architecture & Single Source of Truth:**
  - **User Wallet Credit:** Handled **exclusively** in `DatabaseWriteChannel.cs` (`HandleInsertMeetMobHistoryAsync`) when MeetMob history shows a balance increase. SMS recharge detection only logs — does NOT credit user wallet. MeetMob is the single source of truth.
  - **SIM Hardware Balance:** Tracked in `SimCards` table (`sim.Balance`) and `BalanceHistories` table. Updated via MeetMob API (`acctList[0].balanceResult[0].totalAmount`) or USSD `*222#` snapshots.
  - **Independent Systems:** SMS (5s poll), MeetMob (60s check), Watchdog (30s) run independently with no coupling. SMS detection only — MeetMob handles all balance/credit logic.
  - **MeetMob cooldowns:** Per-phone cooldowns (30s max). No exponential backoff. WAF cooldown per-phone (60s). `pwdWillExpired` retry immediately.
  - **Credit rules:** User wallet ONLY credited when a valid recharge SMS is processed and user is assigned to the modem. New SIM starts at Balance=0.
- **MachineId:** Each machine has a unique ID from `MachineInfoService`. Dev machine: `d26b1c221259fb12`. Client (BERRAR): `419c0cfc97666753`. Client (Alaafi): `fb96ac5207011ae1`.
- **Safe shutdown:** `writeChannel.CompleteAsync()` in `ApplicationStopped` (after host.RunAsync returns).

## Data Flow

```
USB Modems → .NET Gateway → SQLite (local) → MongoDB Atlas (cloud) ← Next.js Web App (writes users/withdrawals)
```

**Data Ownership:** `Modems`, `SimCards`, `SmsRecords`, `BalanceHistories` — .NET only. `Users`, `UserModems`, `WithdrawalRequests`, `UserBalanceHistories` — Dashboard/Next.js only.

MongoDB Collections (8): `modems`, `simcards`, `smsrecords`, `users`, `usermodems`, `balancehistories`, `withdrawalrequests`, `userbalancehistories`.

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
- **Verify with build** — `dotnet build FocusGate.sln` (0 warnings, 0 errors)
- **USSD lock timeout** — HiLinkCommandService.SendUssdAsync has 15s lock timeout; AT has 10s
- **SendUssdAsync on HiLink** sends `POST /api/ussd/send` then polls `GET /api/ussd/get` every 2s
- **125002 error** means SMS inbox full — DeleteAllSmsAsync falls back to index-based deletion (1-50)
- **MSF.100206 error** means MeetMob OTP rate limit ("sending interval not less than 2 minutes") — SendOtpAsync detects this and sets 120s cooldown
- **Session refresh failure** clears _sessionCookie, _csrfToken, sets _isOpen=false — forces clean re-handshake
- **MeetMob login uses OTP** — requires SIM to receive SMS. Phone format: local (0XXXXXXXXX), not country code
- **MeetMob token TTL** — 45 minutes, proactive refresh at 5 minutes before expiry
- **MeetMob backoff** — per-phone cooldowns, no exponential backoff. WAF cooldown 60s.
- **MeetMob is primary** — USSD *222# is fallback only. System always tries MeetMob first.
- **Auto-restart** — RestartService auto-restarts every 8 hours for clean memory.
- **Hourly cleanup** — Purges expired MeetMob tokens, deletes SMS records older than 60 days.

## Config Keys (config.json)

| Key | Default | Description |
|-----|---------|-------------|
| `modem.timezone_offset_hours` | `1` | UTC offset for modem date parsing (Algeria = UTC+1) |
| `display.timezone_offset_hours` | `""` (empty = use Algeria TZ) | Override display timezone. Empty = use `Africa/Algiers` |
| `modem.max_count` | `15` | Maximum modems per orchestrator |
| `modem.sms.poll.interval` | `5` | SMS poll interval in seconds |
| `modem.watchdog.interval` | `30` | Watchdog check interval in seconds |
| `modem.ussd.balance_code` | `*222#` | USSD code for balance check |
| `modem.ussd.phone_code` | `*101#` | USSD code for phone number |
| `mongodb.uri` | (cluster URI) | MongoDB Atlas connection string |
| `mongodb.database` | `focusgate` | MongoDB database name |
| `sync.interval_seconds` | `30` | MongoDB sync interval in seconds |
| `meetmob.base_url` | `https://meetmob.mobilis.dz` | MeetMob API base URL |
| `meetmob.password` | `00000` | MeetMob password |
| `meetmob.token_ttl` | `2700` | Token TTL in seconds (45min) |
| `meetmob.http_timeout` | `7` | HTTP timeout in seconds |
| `meetmob.login_cooldown` | `3` | Login cooldown in seconds (per-phone) |
| `meetmob.fallback_cooldown` | `3` | Fallback cooldown in seconds (per-phone) |
| `meetmob.check.interval` | `60` | MeetMob check interval in seconds |
