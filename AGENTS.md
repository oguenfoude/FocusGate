# FocusGate — Agent Instructions

USB modem gateway (C# .NET 10) + cloud dashboard (Next.js 16). Two repos in one folder: root git tracks **C# only** — `focusgate-web/` is a **separate git repo** (root `.gitignore` ignores it). Commit web changes inside `focusgate-web/`, never from root.

## Layout

- `src/FocusGate.Core/` — models, enums, interfaces, `PathService`. No external deps. `net10.0`.
- `src/FocusGate.Infrastructure/` — `FocusGateDbContext` (SQLite), `DatabaseWriteChannel`, `MongoSyncService`, MeetMob/HiLink services.
- `src/FocusGate.HiLink/` — the **only** entry point (`Program.cs` + `HiLinkModemOrchestrator`). Single instance via mutex `Global\FocusGate_HiLink`.
- `focusgate-web/` — separate repo. Next.js 16.2.9 (**always pass `--webpack`**, Turbopack breaks build), React 19, Mongoose. Admin `/admin`, user `/dashboard`.
- `scripts/cleanup_duplicates.sql` — the only script. `MONGO_SCHEMA.md` — MongoDB schema reference.
- Does **not** exist: `src/FocusGate.AT`, `src/FocusGate.Dashboard`, `.github/workflows`, any test project, `opencode.json`.

## Commands

```powershell
dotnet build FocusGate.sln                                              # must be 0 warnings, 0 errors
dotnet run --project src/FocusGate.HiLink                               # gateway (Ctrl+C = graceful shutdown)
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained -o dist/hilink

cd focusgate-web
npm install
npm run dev -- --webpack     # port 3000
npm run build -- --webpack   # production build
npm start                    # production server
npm run lint                 # ESLint (10 pre-existing errors: setState-in-effect — don't chase)
npx tsc --noEmit             # typecheck (passes clean)
```

No `dotnet test` (no test project — `InternalsVisibleTo("FocusGate.Tests")` dangles). No `npm test`.

## C# conventions

- `net10.0`, Nullable + ImplicitUsings, `NuGetAudit=false`. EF Core 8 on net10.
- **Passwords are plain text.** Never hash, never SHA256/BCrypt. `User.Password` stores raw text.
- **All DB writes go through `DatabaseWriteChannel`** (`Channel<WriteOperation>`, SingleReader). Never write via `DbContext` directly from service code. Exception: interactive `ConsoleCommandHandler` admin commands use their own scoped `DbContext` (user-initiated, not modem loops). `CompleteAsync()` runs on shutdown after `host.RunAsync` returns.
- SQLite at `%APPDATA%\FocusGate\focusgate.db` (`FOCUSGATE_DATA` env overrides). `EnsureCreated` (no migrations) + `DatabaseInitializer` PRAGMAs (`WAL`, `busy_timeout=5000`, `foreign_keys=ON`) + column backfills. **No `SumAsync` on decimal** (SQLite) — `ToListAsync()` then sum in C#.
- **Soft delete everywhere:** `ArchivedAt`, global query filters (`ArchivedAt == null`). `IgnoreQueryFilters()` to see archived.
- `MachineId` stamped on insert, never overwritten; persisted to `config.json` (`machine.id`).
- `config.json` at `%APPDATA%\FocusGate\config.json`, auto-created/merged by `ConfigMerger` (**takes file path**, not dir). Never hand-edit — use `set-config` console command. Real MongoDB URI lives only there; source placeholder is `user:password@cluster.example.net`.
- MongoDB: collection names **all lowercase**; `_id` is **Number (long)** via `MapIdMember` (never ObjectId); upsert key `{_id}`; bidirectional push+pull with timestamp guards (remote wins only if newer); **sync is non-fatal**. `MongoSyncService`: 5s startup delay, retries forever with exponential backoff (30s doubling, 300s cap). Pull uses in-memory matching — never `Func<>` inside LINQ (CS1963).
- `ModemStatus.Online == 4` is the only online check. No stale-`updatedAt` logic.
- **Balance single source of truth = MeetMob.** User wallets credited **exclusively** in `DatabaseWriteChannel.HandleInsertMeetMobHistoryAsync` on MeetMob balance increase. SMS recharge detection only logs. SIM balance from `acctList[0].balanceResult[0].totalAmount`; USSD `*222#` is fallback only. New SIM starts at 0. Loops are independent: SMS poll 5s, MeetMob check 60s, watchdog 30s, sync 30s.
- MeetMob: OTP login, phone in local format `0XXXXXXXXX`. Token TTL 2700s (45m), refreshed 5m early. Per-phone cooldowns only (login 3s, fallback 3s, WAF 60s) — no exponential backoff. `MSF.100206` = OTP rate limit → 120s cooldown. `pwdWillExpired` → retry immediately. Token cache voided daily at midnight.
- HiLink USSD: `POST /api/ussd/send`, poll `GET /api/ussd/get` every 2s, 15s lock timeout. `125002` = inbox full → index-based delete (1–50). Session refresh failure clears cookie/CSRF and sets `_isOpen=false`.
- `RestartService` restarts every 8h. Daily SMS cleanup (>60 days). Expired MeetMob tokens purged hourly.
- Second launch signals restart over NamedPipe `FocusGate_Restart` and exits.

## Next.js conventions

- Env (`.env.local`, never commit): `MONGODB_URI`, `JWT_SECRET`, `NEXT_PUBLIC_BRAND_NAME`.
- Mongoose: lowercase `collection`, `_id: Number` (matches C# long), `timestamps: false`, `archivedAt: null` = active. Cached connection (`connectDB`), `serverExternalPackages: ['mongoose']`.
- Auth: JWT via `jose` (HS256, 24h) in HttpOnly Secure cookie; client routing via `localStorage` `userId`/`role` (`0`=Admin, `1`=User). Login accepts legacy plaintext passwords and **auto-migrates to bcrypt on success**. Rate limits: login 5/min, admin APIs 60/min.
- Web-created docs use `machineId: 'web'` and `nextId()` (`Date.now()*1000 + counter`, counter rolls at 1000 — same-ms collisions possible across instances).
- **Ownership: web writes `users`, `usermodems`, `withdrawalrequests`, `userbalancehistories` only.** Never write `modems`, `simcards`, `smsrecords`. One exception: approving a withdrawal appends a single `balancehistories` row (`source=4 Withdrawal`, `simCardId/modemId=null`, `machineId='web'`, `nextId()` Number `_id`) — safe because gateway upserts on `{_id}` (no id collision) and `balancehistories` is push-only (never pulled into SQLite). Admin user list excludes `role 0` by design.
- UI: 30s SWR refresh, i18n en/fr/ar, zod validation, amounts via `toNum()`.

## Data flow

```
USB modems → HiLink discovery (hilink.scan_ips) → per-modem loops → SQLite → MongoDB Atlas ⇄ Next.js
```

Startup: mutex → `ConfigMerger` → `DatabaseInitializer` → write channel → `host.RunAsync` (Mongo connects after 5s). Shutdown: Ctrl+C → graceful → `writeChannel.CompleteAsync()`.

Collections (8): `modems`, `simcards`, `smsrecords`, `balancehistories`, `users`, `usermodems`, `withdrawalrequests`, `userbalancehistories`.

## Branches / machines

| Branch | MongoDB database |
|--------|-----------------|
| `main` | `focusgate` (BERRAR `419c0cfc97666753`) |
| `alaafi` | `alaafi` |
| `flixiDz` | `flixiDz` (bmsoft `b0a458aebe2393a4`) |

Dev machine: `d26b1c221259fb12`. Code identical across branches — only `mongodb.database` default differs. Never commit a real URI. No git push until the user says so. Dev creds: `admin:admin`.

## Config keys (`config.json`, `ConfigMerger` defaults)

| Key | Default |
|-----|---------|
| `gateway.name` | `FocusGate` |
| `gateway.admin.password` | `ChangeMeImmediately` |
| `machine.id` | `""` (auto-persisted) |
| `modem.max_count` | `30` |
| `modem.sms.poll.interval` | `5` |
| `modem.watchdog.interval` | `30` |
| `modem.timezone_offset_hours` | `0` |
| `display.timezone_offset_hours` | `""` (= Africa/Algiers) |
| `modem.ussd.balance_code` | `*222#` |
| `modem.ussd.phone_code` | `*101#` |
| `modem.ussd.dcs` | `15` |
| `serial.read.timeout` | `5000` |
| `hilink.enabled` | `true` |
| `hilink.scan_ips` | `""` |
| `hilink.probe_timeout_ms` | `2000` |
| `at.enabled` | `true` |
| `at.probe_timeout_ms` | `8000` |
| `sms.verification.enabled` | `true` |
| `sms.verification.threshold` | `50000` |
| `sms.verification.interval` | `60` |
| `sms.parser.strict` | `false` |
| `balance.limit.default` | `50000` |
| `alert.low_balance_threshold` | `10` |
| `mongodb.uri` | placeholder (never commit real URI) |
| `mongodb.database` | `focusgate` |
| `sync.interval_seconds` | `30` |
| `meetmob.base_url` | `https://meetmob.mobilis.dz` |
| `meetmob.password` | `00000` |
| `meetmob.otp_shortcode` | `100` |
| `meetmob.otp_poll_timeout` | `60` |
| `meetmob.otp_poll_interval` | `1` |
| `meetmob.token_ttl` | `2700` |
| `meetmob.http_timeout` | `7` |
| `meetmob.login_cooldown` | `3` |
| `meetmob.fallback_cooldown` | `3` |
| `meetmob.check.interval` | `60` |
| `app.version` | `1.0.0` |
