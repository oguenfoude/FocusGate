# FocusGate — USB Modem SMS Gateway

Automated SMS credit transfer gateway for Mobilis Algeria. Supports both **COM port modems (AT)** and **Huawei HiLink modems (HTTP)**. 1-10 modems, SMS reception with consecutive-index merge, balance tracking via BalanceHistory, MongoDB cloud sync, and a **Desktop WPF Dashboard**. Windows 10/11 only, SQLite, no web API.

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
7. Periodic balance check every 30 minutes
8. All SMS saved to DB (dedup: same sender+content within 5min = skip)
9. Modem unplugged → Offline, freed for re-detection
10. MongoDB syncs all data to cloud every 30 seconds
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
# Build both
dotnet publish src/FocusGate.AT -c Release -o dist/at
dotnet publish src/FocusGate.HiLink -c Release -o dist/hilink

# Run
dist/at/FocusGate.AT.exe
dist/hilink/FocusGate.HiLink.exe
```

### Admin Account

Default admin user is auto-seeded on first run: `admin` / `admin`

---

## Architecture

```
FocusGate.Core/              — Shared interfaces, models, DTOs, enums
FocusGate.Infrastructure/    — Shared services (DB, Mongo, Config, ModemHandler, etc.)
FocusGate.AT/                — Standalone exe: COM port modem gateway
FocusGate.HiLink/            — Standalone exe: HiLink HTTP modem gateway
FocusGate.Desktop/           — WPF Dashboard (to be deleted)
```

### Dependency Graph

```
AT      → Core, Infrastructure
HiLink  → Core, Infrastructure
Desktop → Core, Infrastructure
Infrastructure → Core
```

### Two Executables

| Executable | What It Scans | Interface Used |
|-----------|---------------|----------------|
| `FocusGate.AT.exe` | COM ports via `SerialPort.GetPortNames()` | `AtCommandService` (serial AT) |
| `FocusGate.HiLink.exe` | Network gateways via `NetworkInterface` | `HiLinkCommandService` (HTTP API) |

Both share the same database (`data/focusgate.db`), config (`data/config.json`), and MongoDB sync. Run one or both — same data.

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
2. Probes each IP for Huawei HiLink HTTP API
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
| `POST /api/ussd/send` | USSD commands |
| `POST /api/sms/sms-list` | Read SMS |
| `POST /api/sms/delete-sms` | Delete SMS |

### HiLink Config Keys

| Key | Default | Description |
|-----|---------|-------------|
| `hilink.enabled` | `true` | Enable/disable HiLink scanning |
| `hilink.scan_ips` | *(empty = auto-detect)* | Comma-separated IPs, empty = auto-detect gateways |
| `hilink.probe_timeout_ms` | `3000` | HTTP probe timeout |

---

## Database Schema

SQLite: `data/focusgate.db` — 8 tables. All tables have `UpdatedAt`, `ArchivedAt` (soft-delete), and `MachineId`.

| Table | Description |
|-------|-------------|
| Modems | IMEI, ComPort, Status, Brand, Manufacturer, Model |
| SimCards | IMSI, PhoneNumber, Balance, IsActive |
| SmsRecords | SenderNumber, Content, ReceivedAt |
| Users | Username, Password (SHA256), Role, Balance |
| UserModems | UserId ↔ ModemId assignment |
| BalanceHistories | Balance over time, Source (USSD/SMS/Settlement) |
| WithdrawalRequests | User withdrawal requests |
| UserBalanceHistories | User wallet transactions |

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
2. Decode content (AT: multi-strategy, HiLink: plain text)
3. Save each SMS to DB (with dedup)
4. Delete from SIM/device
```

### Two-Pass SMS Merge

**Pass 1 — UDH-based:** Parses concatenated SMS headers, groups by reference number.

**Pass 2 — Consecutive-index merge:** Mobilis SMSC splits long SMS without UDH headers. Merges consecutive indices from same sender.

---

## Balance Tracking

Balance is tracked in `BalanceHistories` — inserted **only when balance changes**.

| Source | Value | When |
|--------|-------|------|
| USSD | 0 | `*222#` check |
| SMS | 1 | "Solde" in SMS content |
| Settlement | 2 | `settle` command |

---

## MongoDB Cloud Sync

Bidirectional sync between SQLite and MongoDB Atlas. 8 collections. MachineId-based isolation (compound filter). Runs every 30 seconds. **Non-fatal** — app works without MongoDB.

| Key | Default |
|-----|---------|
| `mongodb.uri` | *(placeholder — set in config.json)* |
| `mongodb.database` | `focusgate` |
| `sync.interval_seconds` | `30` |

---

## Configuration

File: `data/config.json` — auto-created on first run. All keys:

| Key | Default | Description |
|-----|---------|-------------|
| `gateway.name` | `FocusGate` | Display name |
| `gateway.admin.password` | `ChangeMeImmediately` | Admin credentials |
| `machine.id` | *(auto)* | Machine fingerprint |
| `modem.watchdog.interval` | `30` | Watchdog timer (seconds) |
| `modem.sms.poll.interval` | `30` | SMS poll timer (seconds) |
| `modem.balance.poll.interval` | `30` | Balance check timer (minutes) |
| `modem.ussd.phone_code` | `*101#` | USSD phone code |
| `modem.ussd.balance_code` | `*222#` | USSD balance code |
| `serial.read.timeout` | `5000` | SerialPort.ReadTimeout |
| `mongodb.uri` | *(placeholder)* | MongoDB Atlas URI |
| `mongodb.database` | `focusgate` | MongoDB database |
| `sync.interval_seconds` | `30` | Sync interval |
| `hilink.enabled` | `true` | HiLink scanning |
| `hilink.scan_ips` | *(empty = auto)* | HiLink IPs override |
| `hilink.probe_timeout_ms` | `3000` | HiLink HTTP timeout |
| `at.enabled` | `true` | AT scanning |
| `at.probe_timeout_ms` | `8000` | AT COM probe timeout |

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
| `assign <uid> <mid>` | Assign modem |
| `unassign <uid> <mid>` | Unassign modem |
| `config` | Show config |
| `set-config <k> <v>` | Set config |
| `exit` | Shutdown |

---

## Deployment

### Self-Contained Build

```powershell
dotnet publish src/FocusGate.AT -c Release -r win-x64 --self-contained true -o dist/at
dotnet publish src/FocusGate.HiLink -c Release -r win-x64 --self-contained true -o dist/hilink
```

### dist/ Layout

```
dist/
  at/
    FocusGate.AT.exe        ← COM port modem gateway
    *.dll                   ← .NET runtime + dependencies
  hilink/
    FocusGate.HiLink.exe    ← HiLink HTTP modem gateway
    *.dll                   ← .NET runtime + dependencies
  data/                     ← shared data (both exes)
    config.json
    focusgate.db
    logs/
```

Copy the relevant folder to the target PC. No .NET runtime needed.

### Update

1. Stop the running exe
2. Copy new files over old (skip `data/` folder)
3. Run again — `ConfigMerger` adds new keys, `DatabaseInitializer` adds new columns

---

## Source Files

### FocusGate.Core (20 files)

| File | Description |
|------|-------------|
| Models/Modem.cs | Modem entity |
| Models/SimCard.cs | SIM card entity |
| Models/SmsRecord.cs | SMS record entity |
| Models/User.cs | User entity |
| Models/UserModem.cs | User-modem assignment |
| Models/BalanceHistory.cs | Balance history entity |
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
| Services/PathService.cs | Data directory resolution |
| Services/LicenseService.cs | RSA verification (kept, not used at runtime) |

### FocusGate.Infrastructure (12 files)

| File | Description |
|------|-------------|
| Data/FocusGateDbContext.cs | 8 DbSets, FK config, SaveChanges override |
| Data/DatabaseInitializer.cs | EnsureCreated + PRAGMAs + migrations |
| Data/FocusGateMongoClient.cs | MongoDB Atlas connection |
| Services/DatabaseWriteChannel.cs | Channel\<T\> single-writer, 11 operations |
| Services/ModemHandler.cs | Startup + watchdog + SMS poll + balance loops |
| Services/ConsoleCommandHandler.cs | 16 console commands |
| Services/RestartService.cs | Named pipe IPC |
| Services/JsonConfigProvider.cs | JSON config with hot-reload |
| Services/MachineInfoService.cs | SHA256 hardware fingerprint |
| Services/MongoSyncService.cs | Bidirectional cloud sync |
| Services/HiLinkCommandService.cs | Huawei HiLink HTTP API client |
| Services/HiLinkDiscovery.cs | Network gateway auto-detection |
| Services/ConfigMerger.cs | Config key auto-merge |
| DependencyInjection.cs | All shared service registration |

### FocusGate.AT (4 files)

| File | Description |
|------|-------------|
| Program.cs | Entry point, DI, mutex `Global\FocusGate_AT` |
| Services/AtCommandService.cs | Serial AT commands, auto-baud, USSD, SMS |
| Services/AtModemOrchestrator.cs | COM port scanning every 30s |
| FocusGate.AT.csproj | WinExe, net10.0, System.IO.Ports |

### FocusGate.HiLink (3 files)

| File | Description |
|------|-------------|
| Program.cs | Entry point, DI, mutex `Global\FocusGate_HiLink` |
| Services/HiLinkModemOrchestrator.cs | Network scanning every 30s |
| FocusGate.HiLink.csproj | WinExe, net10.0 |

### FocusGate.Desktop (15 files)

WPF Dashboard — dark theme, auto-refresh 5s, system tray, restart/stop buttons. To be deleted.

---

## Known Limitations

- **ZTE MF667 USSD:** Does not send `+CUSD:` URC via serial AT. Phone from CNUM, balance from SMS.
- **SerialPort.ReadLine() on USB:** Returns empty strings on USB serial adapters. Uses `ReadExisting()` with polling.

---

## Developer Contact

**Email:** ouguenfoude@gmail.com
