# FocusGate — USB Modem SMS Gateway

Automated SMS credit transfer gateway for Mobilis Algeria. Supports both **COM port modems (AT)** and **Huawei HiLink modems (HTTP)**. 1-10 modems, SMS reception with consecutive-index merge, balance tracking via *222# USSD confirmation, user wallet system, MongoDB cloud sync. Windows 10/11 only, SQLite, no web API.

---

## Table of Contents

1. [How It Works](#how-it-works)
2. [Requirements](#requirements)
3. [Quick Start](#quick-start)
4. [Architecture](#architecture)
5. [AT Modems (COM Port)](#at-modems-com-port)
6. [HiLink Modems (Huawei HTTP)](#hilink-modems-huawei-http)
7. [Database Schema](#database-schema)
8. [SMS Processing](#sms-processing)
9. [Balance Tracking](#balance-tracking)
10. [MongoDB Cloud Sync](#mongodb-cloud-sync)
11. [Configuration](#configuration)
12. [Console Commands](#console-commands)
13. [Deployment](#deployment)
14. [Source Files](#source-files)

---

## How It Works

```
1. Plug in USB modem(s) — two detection methods run:
   AT:    Scan COM ports → probe with AT commands → IMEI/IMSI
   HiLink: Scan network gateways → probe HTTP API → IMEI/IMSI
2. FocusGate reads IMEI + IMSI from each modem
3. No SIM (empty IMSI) → skipped, waits for SIM
4. SIM present → startup sequence:
   AT:    Force modem mode, *101# phone, *222# balance, configure SMS
   HiLink: Device info via HTTP, *101# phone, *222# balance via HTTP API
5. Every 30s: read SMS from SIM → save to DB → delete from SIM
6. Every 30s: watchdog health check (AT command or HTTP ping)
7. All SMS saved to DB (dedup: same sender+content = skip)
8. Recharge/transfer SMS from Mobilis → triggers *222# to confirm real balance
9. *222# response → SimCard.Balance = new value (overwrite) → delta credited to User.Balance
10. Modem unplugged → Offline, freed for re-detection
11. MongoDB syncs all 8 tables to cloud every 30 seconds
```

---

## Requirements

| Requirement | Details |
|-------------|---------|
| OS | Windows 10 or 11 (x64) |
| SDK | .NET 10 (build only — dist/ is self-contained, no .NET needed at runtime) |
| Hardware | USB modem(s) with SIM card |
| SIM | Mobilis Algeria |

---

## Quick Start

### Development

```powershell
cd D:\FocusGate
dotnet build FocusGate.sln

# Run AT modems (COM port):
dotnet run --project src/FocusGate.AT

# Run HiLink modems (Huawei HTTP):
dotnet run --project src/FocusGate.HiLink
```

### Published (standalone)

```powershell
# Build both (self-contained, no .NET needed on target)
dotnet publish src/FocusGate.AT -c Release -r win-x64 --self-contained true -o dist/at
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained true -o dist/hilink

# Run
dist/at/FocusGate.AT.exe
dist/hilink/FocusGate.HiLink.exe
```

### First Run

1. App auto-creates config in `%APPDATA%\FocusGate\config.json`
2. Default admin user seeded: `admin` / `admin`
3. Set MongoDB URI: type `setmongo <uri>` at console, then `exit` and restart
4. 10 modems auto-detected, all balances fetched via *222#

---

## Architecture

```
FocusGate.Core/              — Shared interfaces, models, DTOs, enums (20 files)
FocusGate.Infrastructure/    — Shared services (DB, Mongo, Config, ModemHandler, etc.) (14 files)
FocusGate.AT/                — Standalone exe: COM port modem gateway (3 source files)
FocusGate.HiLink/            — Standalone exe: HiLink HTTP modem gateway (2 source files)
FocusGate.Desktop/           — WPF Dashboard (unused, to be deleted)
```

### Dependency Graph

```
AT      → Core, Infrastructure
HiLink  → Core, Infrastructure
Infrastructure → Core
```

### Two Executables

| Executable | What It Scans | Interface Used |
|-----------|---------------|----------------|
| `FocusGate.AT.exe` | COM ports via `SerialPort.GetPortNames()` | `AtCommandService` (serial AT) |
| `FocusGate.HiLink.exe` | Network gateways via `NetworkInterface` | `HiLinkCommandService` (HTTP API) |

Both share the same database (`%APPDATA%\FocusGate\focusgate.db`), config (`%APPDATA%\FocusGate\config.json`), and MongoDB sync. Run one or both — same data.

### Single-Writer Pattern

All SQLite writes go through `DatabaseWriteChannel` using `Channel<T>`. Thread-safe, no deadlocks.

### Async System

ModemHandler uses proper async `Task` loops with `SemaphoreSlim` (prevents overlapping access) and `CancellationTokenSource` (clean shutdown).

---

## AT Modems (COM Port)

**Run:** `dotnet run --project src/FocusGate.AT`

### What It Does

1. Scans COM ports every 30 seconds
2. Probes each port with AT commands (auto-baud: 9600→115200→57600→19200)
3. Detects IMEI, IMSI, manufacturer, model, brand
4. Forces modem mode (ZTE: `AT+ZCDRUN=2`, Huawei: `AT^U2DIAG=0`)
5. Configures SMS (text mode, charset, storage)
6. Polls SMS every 30s, USSD for phone/balance

### Multi-Brand Support

| Brand | Force Mode Command |
|-------|-------------------|
| ZTE | AT+ZCDRUN=2 |
| Huawei | AT^U2DIAG=0 |
| Quectel | (none needed) |
| SIMCom | (none needed) |
| Other | (none needed) |

### AT Config Keys

| Key | Default | Description |
|-----|---------|-------------|
| `at.enabled` | `true` | Enable/disable AT scanning |
| `at.probe_timeout_ms` | `8000` | COM port probe timeout |
| `serial.read.timeout` | `5000` | SerialPort.ReadTimeout |

---

## HiLink Modems (Huawei HTTP)

**Run:** `dotnet run --project src/FocusGate.HiLink`

### What It Does

1. Auto-discovers network adapter gateway IPs (no hardcoded IPs needed)
2. Probes each IP for Huawei HiLink HTTP API (HTTP then HTTPS fallback)
3. Gets IMEI, IMSI, model via `/api/device/information`
4. USSD for phone (`*101#`) and balance (`*222#`) via `/api/ussd/send`
5. SMS read/delete via `/api/sms/sms-list` and `/api/sms/delete-sms`
6. Watchdog via `/api/monitoring/status` (HTTP ping)

### Network Adapter Auto-Detection

HiLink uses `System.Net.NetworkInformation.NetworkInterface` to find all gateway IPs automatically. Also includes common HiLink defaults: `192.168.8.1`, `192.168.200.1`, `192.168.1.1`.

Override with `hilink.scan_ips` in config if needed.

### HiLink API Endpoints

| Endpoint | Purpose |
|----------|---------|
| `GET /api/webserver/SesTokInfo` | Session + CSRF token |
| `GET /api/device/information` | IMEI, IMSI, model |
| `GET /api/monitoring/status` | Connection status |
| `POST /api/ussd/send` | USSD commands (form-urlencoded) |
| `GET /api/ussd/get` | Poll USSD response (no CSRF) |
| `GET /api/ussd/release` | Release USSD session |
| `POST /api/sms/sms-list` | Read SMS |
| `POST /api/sms/delete-sms` | Delete SMS |

### HiLink Config Keys

| Key | Default | Description |
|-----|---------|-------------|
| `hilink.enabled` | `true` | Enable/disable HiLink scanning |
| `hilink.scan_ips` | *(empty = auto-detect)* | Comma-separated IPs, empty = auto-detect gateways |
| `hilink.probe_timeout_ms` | `2000` | HTTP probe timeout |

---

## Database Schema

SQLite: `%APPDATA%\FocusGate\focusgate.db` — 8 tables. All tables have `UpdatedAt`, `ArchivedAt` (soft-delete), and `MachineId`.

| Table | Description |
|-------|-------------|
| Modems | IMEI, ComPort, Status, Brand, Manufacturer, Model |
| SimCards | IMSI, PhoneNumber, Balance, Status, VerifiedAt, LastSeen |
| SmsRecords | SenderNumber, Content, ReceivedAt |
| Users | Username, Password (SHA256), Role, Balance (wallet) |
| UserModems | UserId ↔ ModemId assignment (with AssignedAt/RemovedAt) |
| BalanceHistories | SimCard balance over time, Source (USSD/SMS/Settlement/Withdrawal), UserId (nullable) |
| WithdrawalRequests | User withdrawal requests (Pending→Approved/Rejected) |
| UserBalanceHistories | User wallet transactions (credit/debit history, Type: 0=credit, 1=withdrawal) |

### User System

- Users are assigned to modems via the `assign <userId> <modemId>` console command
- When a SIM receives credit, `User.Balance` increases by the delta (only if assigned)
- Unassigned modems: SIM balance updates but credits stay on SIM (UserId = null)
- Withdrawals: user requests → admin approves → `User.Balance` deducted + UserBalanceHistory created

### Database Indexes

| Table | Columns | Type |
|-------|---------|------|
| Modems | IMEI | Unique |
| SimCards | ModemId, IsActive | Composite |
| Users | Username | Unique |
| UserModems | UserId, ModemId | Unique composite |

---

## SMS Processing

### Polling (every 30s)

```
1. Read all SMS (AT: AT+CMGL="ALL", HiLink: POST /api/sms/sms-list)
2. Decode content (AT: multi-strategy charset detection, HiLink: plain text)
3. Save each SMS to DB (with dedup)
4. If Mobilis recharge/transfer SMS → triggers *222# balance check
5. If Mobilis "Solde" SMS → extracts balance → UpdateSimBalanceFromSms
6. Delete from SIM/device
```

### Two-Pass SMS Merge

**Pass 1 — UDH-based:** Parses concatenated SMS headers, groups by reference number.

**Pass 2 — Consecutive-index merge:** Mobilis SMSC splits long SMS without UDH headers. Merges consecutive indices from same sender.

---

## Balance Tracking

### How Balance Updates Work

**SimCard.Balance** is the carrier truth — always updated via *222# USSD, never from SMS text.

```
Recharge/transfer SMS from Mobilis → stored in DB → triggers *222# USSD
*222# response: "Sama, Solde 5500,00DA" → balance extracted
→ SimCard.Balance = 5500 (overwrite, not +=)
→ delta = 5500 - 5000 = 500
→ User.Balance += 500 (if user is assigned to this modem)
```

### Two Delivery Paths for *222#

| Path | How | Handler |
|------|-----|---------|
| Direct USSD | `GetBalanceAsync()` gets response via HTTP/AT | `UpdateSimBalanceFromSms` |
| SMS fallback | Response arrives as SMS from Mobilis | `HandleInsertSmsAsync` detects "Solde" → same handler |

Both paths converge on `HandleUpdateSimBalanceFromSmsAsync` — no double-count (delta = 0 if already updated).

### What Happens Per SMS Type

| SMS From | Content | Action |
|----------|---------|--------|
| Mobilis/77111 | "Rechargé de 500 DZD" | Stored → triggers *222# → balance confirmed |
| Mobilis/77111 | "montant de 500 DZD" | Stored → triggers *222# → balance confirmed |
| Mobilis/77111 | "Sama, Solde 5500,00DA..." | Stored → balance extracted → `UpdateSimBalanceFromSms` |
| Mobilis/77111 | Offer (Sama Mix, etc.) | Stored only, no balance change |
| Other sender | Any | Stored only |

### User Wallet (User.Balance)

- **Credit**: SIM balance increases → user gets the delta (only if user is assigned via `assign` command)
- **Withdrawal**: User requests → admin approves → deducted from User.Balance + UserBalanceHistory (Type=1)
- **Unassigned modem**: SIM balance updates but User.Balance unchanged (credits stay on SIM)

### Balance Sources

| Source | Enum | When |
|--------|------|------|
| USSD | 0 | Periodic *222# check (no user credit) |
| SMS | 1 | *222# triggered by recharge/transfer SMS (credits user) |
| Settlement | 2 | `settle` console command |
| Withdrawal | 4 | Admin approves withdrawal request |

---

## MongoDB Cloud Sync

Bidirectional sync between SQLite and MongoDB Atlas. 8 collections. MachineId-based isolation (compound filter). Runs every 30 seconds. **Non-fatal** — app works without MongoDB.

### Setup

1. ConfigMerger writes a placeholder URI on first run
2. Use `setmongo <uri>` console command to set the real URI
3. Restart the app — connection is tested with a 5-second ping

### Config Keys

| Key | Default |
|-----|---------|
| `mongodb.uri` | *(placeholder — set via `setmongo` command)* |
| `mongodb.database` | `focusgate` |
| `sync.interval_seconds` | `30` |

### URI Reading

`DependencyInjection` uses `ReadFlatConfig()` to read config.json directly via `System.Text.Json.JsonDocument`. This bypasses `IConfiguration` which treats dots in flat keys as hierarchy separators.

### Sync Behavior

- **Push**: SQLite → MongoDB (records updated since last sync, filtered by MachineId)
- **Pull**: MongoDB → SQLite (records updated since last sync, filtered by MachineId)
- **Conflict resolution**: Newer `UpdatedAt` wins. Duplicate key → claim by overwriting old machine's record.
- **First sync**: Full sync (push all + pull all). Subsequent: incremental only.

---

## Configuration

File: `%APPDATA%\FocusGate\config.json` — auto-created on first run. All keys:

| Key | Default | Description |
|-----|---------|-------------|
| `gateway.name` | `FocusGate` | Display name |
| `gateway.admin.password` | `ChangeMeImmediately` | Admin credentials |
| `machine.id` | *(auto)* | Machine fingerprint (SHA256 hardware hash) |
| `modem.watchdog.interval` | `30` | Watchdog timer (seconds) |
| `modem.sms.poll.interval` | `30` | SMS poll timer (seconds) |
| `modem.balance.poll.interval` | `30` | Balance check timer (minutes) |
| `modem.ussd.phone_code` | `*101#` | USSD phone code |
| `modem.ussd.balance_code` | `*222#` | USSD balance code |
| `modem.ussd.dcs` | `15` | USSD data coding scheme |
| `serial.read.timeout` | `5000` | SerialPort.ReadTimeout |
| `mongodb.uri` | *(placeholder)* | MongoDB Atlas URI — set via `setmongo` |
| `mongodb.database` | `focusgate` | MongoDB database name |
| `sync.interval_seconds` | `30` | MongoDB sync interval |
| `hilink.enabled` | `true` | HiLink scanning |
| `hilink.scan_ips` | *(empty = auto)* | HiLink IPs override |
| `hilink.probe_timeout_ms` | `2000` | HiLink HTTP timeout |
| `at.enabled` | `true` | AT scanning |
| `at.probe_timeout_ms` | `8000` | AT COM probe timeout |
| `sms.verification.enabled` | `true` | SMS verification |
| `sms.verification.threshold` | `50000` | SMS verification threshold |
| `sms.verification.interval` | `60` | SMS verification interval |
| `sms.parser.strict` | `false` | Strict SMS parsing |
| `balance.limit.default` | `50000` | Default balance limit |
| `alert.low_balance_threshold` | `10` | Low balance alert threshold |
| `app.version` | `1.0.0` | App version |

`ConfigMerger` adds missing keys on startup. Existing values are never overwritten.

---

## Console Commands

| Command | Description |
|---------|-------------|
| `help` | Show all commands |
| `status` | Dashboard: modems, SMS, balance |
| `modems` | List all modems |
| `modem <id>` | Modem detail |
| `sms [modemId] [days]` | Recent SMS |
| `sim <modemId>` | SIM history |
| `settle <modId> <amt> [note]` | Manual balance adjustment |
| `report balance [id] [days]` | Balance history |
| `report sms [id] [days]` | SMS report |
| `users` | List users |
| `adduser <u> <p> [d]` | Add user |
| `assign <uid> <mid>` | Assign modem to user |
| `unassign <uid> <mid>` | Unassign modem from user |
| `config` | Show config |
| `set-config <k> <v>` | Set any config key |
| `setmongo <uri>` | Set MongoDB URI and show restart instructions |
| `exit` | Shutdown |

---

## Deployment

### Self-Contained Build

```powershell
dotnet publish src/FocusGate.AT -c Release -r win-x64 --self-contained true -o dist/at
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained true -o dist/hilink
```

### Data Storage

All data is stored in `%APPDATA%\FocusGate\`:

| Path | Contents |
|------|----------|
| `%APPDATA%\FocusGate\config.json` | Configuration (auto-created) |
| `%APPDATA%\FocusGate\focusgate.db` | SQLite database |
| `%APPDATA%\FocusGate\logs\` | Rolling log files (30 days) |

Override with `FOCUSGATE_DATA` environment variable.

### dist/ Layout

```
dist/
  at/
    FocusGate.AT.exe        ← COM port modem gateway
    *.dll                   ← .NET runtime + dependencies
  hilink/
    FocusGate.HiLink.exe    ← HiLink HTTP modem gateway
    *.dll                   ← .NET runtime + dependencies
```

### Update Procedure

1. Stop the running exe
2. Copy new exe + dlls over old
3. Do NOT touch `%APPDATA%\FocusGate\` (config + database preserved)
4. Run again — `ConfigMerger` adds new keys, `DatabaseInitializer` adds new columns

---

## Source Files

### FocusGate.Core (20 files, ~367 lines)

| File | Description |
|------|-------------|
| Models/Modem.cs | Modem entity |
| Models/SimCard.cs | SIM card entity |
| Models/SmsRecord.cs | SMS record entity |
| Models/User.cs | User entity |
| Models/UserModem.cs | User-modem assignment |
| Models/BalanceHistory.cs | Balance history entity (UserId nullable) |
| Models/WithdrawalRequest.cs | Withdrawal request entity |
| Models/UserBalanceHistory.cs | User wallet history |
| Enums/ModemStatus.cs | Unknown→PendingNetwork (0-7) |
| Enums/ModemBrand.cs | Unknown→MediaTek (0-7), Other (99) |
| Enums/NetworkRegistration.cs | NotRegistered/Registered/Denied/Unknown |
| Enums/BalanceSource.cs | USSD/SMS/Settlement/Manual/Withdrawal |
| Enums/UserRole.cs | Admin/User |
| Enums/SimStatus.cs | Active/Replaced/Expired |
| Enums/WithdrawalStatus.cs | Pending/Approved/Rejected |
| DTOs/RawSmsMessage.cs | Index, Status, Sender, Content, ReceivedAt |
| Interfaces/IAtCommandService.cs | 14 members (AT + HiLink implement) |
| Interfaces/IConfigProvider.cs | Get(string), Get\<T\>(string) |
| Services/PathService.cs | Data directory in `%APPDATA%\FocusGate\` |
| Services/LicenseService.cs | RSA verification (kept, not used at runtime) |

### FocusGate.Infrastructure (14 files, ~3,699 lines)

| File | Description |
|------|-------------|
| Data/FocusGateDbContext.cs | 8 DbSets, FK config, SaveChanges override, MachineId injection |
| Data/DatabaseInitializer.cs | EnsureCreated + PRAGMAs + migrations |
| Data/FocusGateMongoClient.cs | MongoDB Atlas connection (8 collections, nullable URI, ExtractHost) |
| Services/DatabaseWriteChannel.cs | Channel\<T> single-writer, 12 operations |
| Services/ModemHandler.cs | Startup + watchdog + SMS poll + *222# balance trigger |
| Services/ConsoleCommandHandler.cs | 17 console commands |
| Services/RestartService.cs | Named pipe IPC |
| Services/JsonConfigProvider.cs | JSON config with hot-reload (5s) |
| Services/MachineInfoService.cs | SHA256 hardware fingerprint |
| Services/MongoSyncService.cs | Bidirectional cloud sync (8 tables) |
| Services/HiLinkCommandService.cs | Huawei HiLink HTTP API client (UseCookies=false, rolling CSRF) |
| Services/HiLinkDiscovery.cs | Network gateway auto-detection (parallel probing) |
| Services/ConfigMerger.cs | Config key auto-merge (placeholder URI on first run) |
| DependencyInjection.cs | All shared service registration + ReadFlatConfig |

### FocusGate.AT (3 source files, ~1,003 lines)

| File | Description |
|------|-------------|
| Program.cs | Entry point, DI, mutex `Global\FocusGate_AT` |
| Services/AtCommandService.cs | Serial AT commands, auto-baud, USSD, SMS (French number parsing) |
| Services/AtModemOrchestrator.cs | COM port scanning every 30s |

### FocusGate.HiLink (2 source files, ~342 lines)

| File | Description |
|------|-------------|
| Program.cs | Entry point, DI, mutex `Global\FocusGate_HiLink` (return on contention) |
| Services/HiLinkModemOrchestrator.cs | Network scanning every 30s, max 10 modems |

### FocusGate.Desktop (unused)

WPF Dashboard — dark theme, auto-refresh 5s, system tray. Exists in source but not deployed.

---

## Known Limitations

- **ZTE MF667 USSD:** Does not send `+CUSD:` URC via serial AT. Phone from CNUM, balance from SMS.
- **SerialPort.ReadLine() on USB:** Returns empty strings on USB serial adapters. Uses `ReadExisting()` with polling.
- **MongoDB DNS SRV:** Requires internet. App works fine without it — cloud sync is non-fatal.

---

## Developer Contact

**Email:** ouguenfoude@gmail.com
