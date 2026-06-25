# FocusGate — USB Modem SMS Gateway

Automated SMS credit transfer gateway for Mobilis Algeria. 1-10 USB modems, SMS reception with consecutive-index merge, balance tracking via BalanceHistory, MongoDB cloud sync, RSA licensing, and a **Desktop WPF Dashboard** with system tray. Windows 10/11 only, SQLite, no web API.

---

## Table of Contents

1. [How It Works](#how-it-works)
2. [Requirements](#requirements)
3. [Installation](#installation)
4. [Update](#update)
5. [Architecture](#architecture)
6. [Database Schema](#database-schema)
7. [Startup Sequence](#startup-sequence)
8. [Modem Detection](#modem-detection)
9. [SMS Processing](#sms-processing)
10. [Balance Tracking](#balance-tracking)
11. [USSD Protocol](#ussd-protocol)
12. [MongoDB Cloud Sync](#mongodb-cloud-sync)
13. [Machine Identity & Licensing](#machine-identity--licensing)
14. [Lifecycle Scenarios](#lifecycle-scenarios)
15. [FocusGate Desktop Dashboard](#focusgate-desktop-dashboard)
16. [Console Commands](#console-commands)
17. [Configuration](#configuration)
18. [Deployment](#deployment)
19. [Source Files](#source-files)
20. [Real-World Test Results](#real-world-test-results)

---

## How It Works

```
1. Plug in USB modem(s) — auto-detected on COM ports
2. FocusGate reads IMEI + IMSI from each modem
3. No SIM (empty IMSI) → skipped, waits for SIM
4. SIM present → startup sequence:
   - Force modem mode (ZTE: AT+ZCDRUN=2, Huawei: AT^U2DIAG=0)
   - *101# → get phone number (if modem supports USSD via AT)
   - *222# → get balance (if modem supports USSD via AT)
   - Read existing SMS from SIM
   - Start watchdog (30s) + poll timer (30s)
5. Every 30s: read SMS from SIM → save to DB → delete from SIM
6. Mobilis SMS (7711198105108105115) with "Solde" → extract balance → BalanceHistory (only if changed)
7. Any SMS received → run *222# to check balance (60s cooldown)
8. Credit transfer SMS ("montant de") → BalanceHistory (Source=SMS) + auto-credit User.Balance
9. All SMS from all senders saved to DB (dedup: same sender+content within 5min = skip)
10. Modem unplugged → Offline, freed for re-detection
11. MongoDB syncs all data to cloud every 30 seconds
12. Desktop dashboard shows live status (auto-refreshes every 5s)
13. System tray: close minimizes to tray, exit via tray with confirmation
14. Click FocusGate.exe while running → restarts both apps via named pipe
15. RSA license verification on every startup (machine-locked, no expiry, auto-generated)
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

## Installation

### From Source (Developer)

```powershell
cd D:\FocusGate
dotnet build FocusGate.sln

# Run everything (Hardware + Desktop auto-launches):
dotnet run --project src\FocusGate.Hardware
```

**One command runs everything.** FocusGate.exe starts the background service, then auto-launches the Desktop dashboard.

### From Installer (Production)

Run `dist/FocusGate-Setup.exe` — installs to `C:\Program Files\FocusGate\` with:
- Desktop shortcut
- Start Menu group (FocusGate + Uninstall)
- Auto-start on boot (registry Run key)
- Kills old processes before overwrite
- Uninstall cleans data folder

### From dist/ (Portable)

The `dist/` folder is a **self-contained flat build** — no .NET runtime needed on the target PC.

```
dist/
  FocusGate.exe             ← entry point (launches both apps)
  FocusGate.Desktop.exe     ← WPF dashboard
  FocusGate-Setup.exe       ← installer (optional)
  *.dll                     ← .NET runtime + all dependencies
  data/                     ← created on first run (next to exe)
    config.json             ← settings
    focusgate.db            ← database
    license.json            ← RSA license (auto-generated on first run)
    logs/                   ← rolling daily logs
```

**To install (portable):**
1. Copy the `dist/` folder to the target PC
2. Double-click `FocusGate.exe`
3. Done — first run creates the database, generates license, seeds admin user

**Admin account:** `admin` / `admin` (auto-seeded on first run)

---

## Update

**No data loss.** Two options:

### Option A: Installer (recommended)
1. Run new `FocusGate-Setup.exe` — kills old processes, overwrites files
2. Data folder preserved (license.json only overwritten if missing)

### Option B: Manual
1. Stop FocusGate (close Desktop via tray → Exit, or kill process)
2. Copy new `dist/` files over old (skip `data/` folder)
3. Run `FocusGate.exe`

What happens automatically:
- `DatabaseInitializer` adds any new columns via `AddColumnIfMissing()`
- `ConfigMerger` adds any new config keys with default values (existing values preserved)
- MongoDB sync resumes with the same MachineId
- All data (DB, config, logs) untouched

---

## Architecture

FocusGate consists of 4 projects:

```text
FocusGate.Core           (no dependencies, shared models/enums)
FocusGate.Infrastructure (EF Core, MongoDB, config)
  ├── Data/FocusGateDbContext.cs       (Hardware write access)
  ├── Data/FocusGateMongoClient.cs     (MongoDB Atlas connection)
  ├── Data/DatabaseInitializer.cs      (schema migration)
  ├── Services/DatabaseWriteChannel.cs (single-writer Channel<T>)
  ├── Services/MongoSyncService.cs     (bidirectional cloud sync)
  ├── Services/MachineInfoService.cs   (hardware fingerprint)
  └── DependencyInjection.cs
FocusGate.Hardware       (WinExe — AT commands, USSD, SMS polling)
  ├── Program.cs                       (entry point, DB init, Desktop launch, restart-via-pipe)
  ├── ConfigMerger.cs                  (auto-merge config keys on update)
  ├── Services/AtCommandService.cs
  ├── Services/ModemHandler.cs         (async loops, SemaphoreSlim, CancellationToken)
  ├── Services/ModemOrchestrator.cs
  ├── Services/ConsoleCommandHandler.cs
  └── Services/RestartService.cs       (named pipe IPC)
FocusGate.Desktop        (WPF App — Fluent dark-themed dashboard with system tray)
  ├── App.xaml.cs                      (Mutex, tray icon, splash screen, global exception handlers)
  ├── Data/ReadOnlyDbContext.cs
  ├── Views/LoadingWindow.xaml         (splash screen)
  ├── Views/MainWindow.xaml            (header with Restart/Stop/Mute buttons)
  ├── Views/ModemsOverviewPage.xaml
  ├── Views/ModemDetailPage.xaml
  └── Themes/Styles.xaml
```

**Dependency Graph:**
```
Desktop → Core
Hardware → Core, Infrastructure
Infrastructure → Core
```

**Concurrent Database Access:** Both `Hardware` and `Desktop` safely access the same SQLite database concurrently because SQLite is initialized with `PRAGMA journal_mode=WAL` and `busy_timeout=5000`. The Desktop app uses a Read-Only DbContext (`SaveChanges()` returns 0, physically cannot write). The `ReadOnlyDbContext` uses in-memory joins to avoid EF Core Include() issues on .NET 10.

**Single-Writer Pattern:** All SQLite writes are serialized through `DatabaseWriteChannel` using `Channel<T>`. The channel's `WriteOperation.Completed` (TaskCompletionSource) ensures callers can await write confirmation before proceeding.

**Async System:** ModemHandler uses proper async `Task` loops with `SemaphoreSlim` (prevents overlapping serial port access) and `CancellationTokenSource` (clean shutdown). No fire-and-forget `Timer+async` patterns.

**Force Modem Mode:** During init, ZTE modems receive `AT+ZCDRUN=2` and Huawei modems receive `AT^U2DIAG=0` to ensure they operate in modem-only mode (not storage/card-reader mode). Other modem types are unaffected.

**WinExe Mode:** Both Hardware and Desktop run as `OutputType=WinExe` — no console window appears. Detection via `Console.WindowWidth` throws in WinExe mode, so the console command loop is skipped gracefully.

**Single Instance:** Named Mutex `Global\FocusGate_Hardware` and `Global\FocusGate_Desktop` prevent multiple instances. Clicking FocusGate.exe while running sends a restart signal via named pipe `FocusGate_Restart`.

**System Tray:** Desktop minimizes to system tray (H.NotifyIcon). Close button minimizes to tray. Exit only via tray context menu with confirmation dialog.

---

## Database Schema

SQLite: `data/focusgate.db` — 8 tables with foreign keys. All tables have `UpdatedAt` (auto-stamped on every SaveChanges), `ArchivedAt` (soft-delete, excluded from all queries via EF Core `HasQueryFilter`), and `MachineId` (identity stamp for MongoDB sync isolation).

### Modems

| Column | Type | Description |
|--------|------|-------------|
| Id | INTEGER PK | Auto-increment |
| IMEI | TEXT(20) | Modem serial (unique index) |
| ComPort | TEXT? | COM port (NULL when offline) |
| Status | INTEGER | ModemStatus enum (0-7) |
| Brand | INTEGER | ModemBrand enum (0-99) |
| Manufacturer | TEXT? | From AT+CGMI |
| Model | TEXT? | From AT+CGMM |
| CreatedAt | DATETIME | UTC |
| UpdatedAt | DATETIME | UTC, auto-stamped |
| ArchivedAt | DATETIME? | Soft-delete (hidden from queries) |
| MachineId | TEXT | 16-char hex machine identity |

### SimCards

| Column | Type | Description |
|--------|------|-------------|
| Id | INTEGER PK | Auto-increment |
| ModemId | INTEGER FK | → Modems (cascade) |
| IMSI | TEXT(20) | SIM identity |
| PhoneNumber | INTEGER | Phone from *101# |
| Balance | DECIMAL | Current balance |
| VerifiedAt | DATETIME? | Last USSD verification |
| IsActive | BOOLEAN | Current active SIM |
| Status | INTEGER | SimStatus: Active=0, Replaced=1, Expired=2 |
| FirstSeen | DATETIME | First detection |
| LastSeen | DATETIME | Last activity |
| RemovedAt | DATETIME? | When replaced (NULL = active) |
| ReplacedAt | DATETIME? | When new SIM inserted |
| CreatedAt | DATETIME | UTC |
| UpdatedAt | DATETIME | UTC, auto-stamped |
| ArchivedAt | DATETIME? | Soft-delete |
| MachineId | TEXT | 16-char hex machine identity |

### SmsRecords

| Column | Type | Description |
|--------|------|-------------|
| Id | INTEGER PK | Auto-increment |
| SimCardId | INTEGER FK | → SimCards (cascade) |
| SenderNumber | TEXT(20) | Sender phone |
| Content | TEXT | SMS text |
| ReceivedAt | DATETIME | When received |
| ProcessedAt | DATETIME | When saved |
| UpdatedAt | DATETIME | UTC, auto-stamped |
| ArchivedAt | DATETIME? | Soft-delete |
| MachineId | TEXT | 16-char hex machine identity |

### Users

| Column | Type | Description |
|--------|------|-------------|
| Id | INTEGER PK | Auto-increment |
| Username | TEXT(50) | Login name (unique) |
| Password | TEXT(100) | Plain text (no hashing) |
| DisplayName | TEXT(100) | Display name |
| Role | INTEGER | UserRole: Admin=0, User=1 |
| Balance | DECIMAL | User wallet balance (auto-credited on credit SMS) |
| IsActive | BOOLEAN | Active flag |
| CreatedAt | DATETIME | UTC |
| UpdatedAt | DATETIME | UTC, auto-stamped |
| ArchivedAt | DATETIME? | Soft-delete |
| MachineId | TEXT | 16-char hex machine identity |

### UserModems

| Column | Type | Description |
|--------|------|-------------|
| Id | INTEGER PK | Auto-increment |
| UserId | INTEGER FK | → Users (cascade) |
| ModemId | INTEGER FK | → Modems (cascade) |
| AssignedAt | DATETIME | UTC |
| RemovedAt | DATETIME? | When unassigned (NULL = active) |
| UpdatedAt | DATETIME | UTC, auto-stamped |
| ArchivedAt | DATETIME? | Soft-delete |
| MachineId | TEXT | 16-char hex machine identity |

### BalanceHistories

| Column | Type | Description |
|--------|------|-------------|
| Id | INTEGER PK | Auto-increment |
| SimCardId | INTEGER FK? | → SimCards (cascade) — NULL for withdrawals |
| ModemId | INTEGER FK? | → Modems (cascade) — NULL for withdrawals |
| UserId | INTEGER FK? | → Users (cascade) |
| Balance | DECIMAL | Current balance at time of record |
| PreviousBalance | DECIMAL? | Balance before (NULL = first reading) |
| Source | INTEGER | BalanceSource: USSD=0, SMS=1, Settlement=2, Manual=3, Withdrawal=4 |
| RecordedAt | DATETIME | UTC |
| UpdatedAt | DATETIME | UTC, auto-stamped |
| ArchivedAt | DATETIME? | Soft-delete |
| MachineId | TEXT | 16-char hex machine identity |

**BalanceHistory is only inserted when balance actually changes** — no duplicate rows, clean history. Exception: credit transfer events ("montant de") are always recorded.

### WithdrawalRequests

| Column | Type | Description |
|--------|------|-------------|
| Id | INTEGER PK | Auto-increment |
| UserId | INTEGER FK | → Users (cascade) |
| Amount | DECIMAL | Requested amount |
| Status | INTEGER | WithdrawalStatus: Pending=0, Approved=1, Rejected=2 |
| Note | TEXT? | User note |
| AdminNote | TEXT? | Admin processing note |
| ProcessedByAdminId | INTEGER FK? | → Users (cascade) — NULL if unprocessed |
| RequestedAt | DATETIME | UTC |
| ProcessedAt | DATETIME? | When approved/rejected |
| UpdatedAt | DATETIME | UTC, auto-stamped |
| ArchivedAt | DATETIME? | Soft-delete |
| MachineId | TEXT | 16-char hex machine identity |

### UserBalanceHistories

| Column | Type | Description |
|--------|------|-------------|
| Id | INTEGER PK | Auto-increment |
| UserId | INTEGER FK | → Users (cascade) |
| Amount | DECIMAL | Transaction amount |
| BalanceAfter | DECIMAL | Balance after transaction |
| Type | TEXT | Transaction type (Credit, Debit, Withdrawal, Adjustment) |
| SimCardId | INTEGER FK? | → SimCards (cascade) — NULL for non-SIM transactions |
| Note | TEXT? | Description |
| RecordedAt | DATETIME | UTC |
| UpdatedAt | DATETIME | UTC, auto-stamped |
| ArchivedAt | DATETIME? | Soft-delete |
| MachineId | TEXT | 16-char hex machine identity |

### Database Indexes

| Table | Columns | Type |
|-------|---------|------|
| Modems | IMEI | Unique |
| SimCards | ModemId, IsActive | Composite |
| Users | Username | Unique |
| UserModems | UserId, ModemId | Unique composite |
| BalanceHistories | ModemId | Non-unique |
| BalanceHistories | UserId | Non-unique |
| BalanceHistories | RecordedAt | Non-unique |

---

## Startup Sequence

Every modem runs this sequence on startup (every restart, not just first run):

```
Step 1:  AT+CGSN → IMEI (abort if empty)
Step 2:  Force modem mode: ZTE → AT+ZCDRUN=2, Huawei → AT^U2DIAG=0
Step 3:  AT+CIMI → IMSI (abort if empty = no SIM)
Step 4:  AT+CREG? → Network registration (retry 5x, 3s between)
Step 5:  Wait 5s for network to settle
Step 6:  AT+CNUM → Phone number (fallback for modems that don't support USSD)
Step 7:  AT+CUSD=1,"*101#",15 → Phone number (if modem supports USSD via AT)
Step 8:  AT+CUSD=1,"*222#",15 → Balance (if modem supports USSD via AT)
Step 9:  UpsertSimCard in DB
Step 10: Configure SMS:
         AT+CMGF=1           (text mode)
         AT+CSCS="IRA"       (ZTE charset)
         AT+CPMS="SM","SM","SM"  (SMS storage)
         AT+CNMI=2,1,0,0,0   (route new SMS to serial)
Step 11: Read existing SMS → save to DB → delete from SIM
Step 12: Mark Online, start watchdog (30s) + poll (30s) + balance check (30min)
```

---

## Modem Detection

### ModemOrchestrator

BackgroundService. Scans every 30 seconds. Max 10 concurrent modems.

```
Every 30s:
  1. Get all COM port names
  2. Remove dead handlers → free their IMEIs
  3. Identify unhandled ports (up to MaxModems=10)
  4. Probe unhandled ports in parallel (8s timeout)
  5. Auto-detect baud rate (9600 → 115200 → 57600 → 19200)
  6. Detect manufacturer (AT+CGMI) and model (AT+CGMM)
  7. Identify brand (ZTE, Huawei, Quectel, SIMCom, Sierra, etc.)
  8. Check SIM PIN status (AT+CPIN?)
  9. Skip duplicate IMEIs
  10. Start ModemHandler for new modems
  11. Mark orphaned DB modems as Offline (AFTER probes complete)
```

### Multi-Brand Support

Works with **any COM port GSM/LTE modem** — not just ZTE. Tested brands:

| Brand | Force Mode Command | Notes |
|-------|-------------------|-------|
| ZTE | AT+ZCDRUN=2 | Primary test modem |
| Huawei | AT^U2DIAG=0 | Auto-detected |
| Quectel | (none needed) | Auto-detected |
| SIMCom | (none needed) | Auto-detected |
| Sierra Wireless | (none needed) | Auto-detected |
| Other | (none needed) | Brand logged, continues |

The system tries both ZTE and Huawei force-mode commands on every modem — errors are silently ignored for non-matching brands.

### Auto-Baud Detection

On probe, the system tries multiple baud rates to find the modem:
- 9600 (default for most modems)
- 115200 (some Huawei and high-speed modems)
- 57600
- 19200

Sends `AT` at each rate and checks for `OK` response.

### SMS Content Decoding

Multi-strategy decoding for any modem brand:

1. **UTF-16 hex** — 4 hex chars = 1 character (ZTE default when non-ASCII)
2. **GSM 7-bit packed** — Standard GSM SMS encoding (7 bits per character)
3. **ISO-8859-1 / IRA** — Raw byte decode (IRA charset on modems)

Garbage detection prevents false-positive decoding of already-readable text.

### Duplicate Detection

IMEI-based dedup across all brands:

```
COM8: IMEI=359905014822818 → handler starts
COM9: IMEI=359905014822818 → "Duplicate IMEI" → skipped
```

### Unplug / Replug

Unplug: IOException → DisconnectAsync → Offline → clear ComPort → freed IMEI
Replug: New probe → IMEI not active → fresh startup

### Orphan Race Condition Fix

Orphan check runs AFTER all probes complete, so `_activeImeis` is fully populated before marking modems offline.

### Watchdog Recovery

Watchdog AT command every 30s. If AT fails → `DisconnectAsync()` (sets Offline, clears ComPort, frees IMEI) → orchestrator immediately re-probes on next cycle.

---

## SMS Processing

### Polling (every 30s)

```
1. AT+CMGL="ALL" → read all SMS
2. Line-by-line parse (no regex):
   ParseCmglLine: extract index, status, sender from +CMGL: lines
   SplitCmglFields: quote-aware comma splitter
   Content: read all lines between +CMGL: entries
3. Decode SMS content (multi-strategy):
   - UTF-16 hex (4 hex chars = 1 char)
   - GSM 7-bit packed (7 bits per char)
   - ISO-8859-1 / IRA (raw byte)
4. Save each SMS to DB (with dedup) — AWAIT confirmation
5. Delete all from SIM (only after save confirmed)
```

### SMS Deduplication

Same SimCardId + SenderNumber + Content within 5 minutes → skipped.

### Two-Pass SMS Merge

**Pass 1 — UDH-based:** Parses concatenated SMS headers (IEI 0x00 or 0x08), groups by reference number, assembles parts in order.

**Pass 2 — Consecutive-index merge:** Mobilis SMSC splits long SMS without UDH headers. Groups by (Sender, same-second timestamp), sorts by index, merges consecutive indices into one SMS record.

```
[CMGL] Parsed 1 SMS messages (UDH=0, consecutive=1)
```

### SMS from Mobilis Service Number

Sender IDs with alphabetical letters are returned by modems as **Decimal ASCII strings**. For example, `7711198105108105115` decodes to `MOBILIS` (77=M, 111=o, 98=b...). The Desktop App automatically decodes these.

Three common SMS types from Mobilis:

| SMS Content | What Happens |
|-------------|-------------|
| "Solde X,XXDA" | Extract balance → update SimCard + BalanceHistory (Source=SMS) **only if balance changed** |
| "montant de X.XX DZD" | Credit transfer → BalanceHistory (Source=SMS) **always recorded** + auto-credit User.Balance |
| Plan activation ("Sama Mix 50...") | Saved. **Run *222#** |

### SMS Poll with *222# Logic

After ANY SMS is received (not just Mobilis):
```
If any SMS found AND 60s since last *222#:
  → Run *222# to check balance
```

---

## Balance Tracking

### How It Works

BalanceHistory is inserted **only when balance actually changes** — no duplicate rows, clean history.

**Sources:**

| Source | Value | When |
|--------|-------|------|
| USSD | 0 | `*222#` at startup or after SMS (if balance changed) |
| SMS | 1 | "Solde" in SMS content (if balance changed) or "montant de" credit (always) |
| Settlement | 2 | `settle` command (manual adjustment) |
| Manual | 3 | Reserved |
| Withdrawal | 4 | User withdrawal request processed |

### Balance from SMS Content

`ExtractBalanceFromContent()` in DatabaseWriteChannel:
- Finds "Solde" keyword in SMS text
- Extracts first decimal number after it (handles comma as decimal separator)
- Compares with current SimCard.Balance
- Only creates BalanceHistory if different

### Balance from USSD

`GetBalanceAsync()` in AtCommandService:
- Sends `AT+CUSD=1,"*222#",15`
- Decodes UTF-16 hex response
- Checks for "Solde"/"DA"/"DZD" keywords
- Extracts decimal amount
- Only creates BalanceHistory if different

**Note:** ZTE MF667 modem does NOT send `+CUSD:` URC via serial AT — USSD command returns `OK` but network response never arrives. Balance is obtained from SMS content parsing instead.

### Credit Transfer Detection

`ExtractCreditAmount()` in DatabaseWriteChannel:
- Finds "montant de " marker
- Extracts number before " DZD"
- Always creates BalanceHistory (Source=SMS) — it's an event worth recording
- Auto-credits User.Balance for the assigned user

### User Wallet Balance

`User.Balance` is separate from `SimCard.Balance`:
- `SimCard.Balance` = current Mobilis SIM credit
- `User.Balance` = user's earned/transferred wallet amount
- Auto-credited when credit transfer SMS detected for assigned SIM
- `UserBalanceHistory` records every wallet transaction

### Example BalanceHistory Rows

```
| Id | SimCardId | Balance | PreviousBalance | Source | RecordedAt          |
|----|-----------|---------|-----------------|--------|---------------------|
| 1  | 1         | 82.59   | NULL            | USSD   | 2026-06-20 00:36:33 |
| 2  | 1         | 85.59   | 82.59           | SMS    | 2026-06-20 01:15:00 |
```

Note: If balance is still 82.59 when checked again, no new row is created.

---

## USSD Protocol

### Mobilis Algeria

| Parameter | Value | Why |
|-----------|-------|-----|
| DCS | 15 | UTF-16 encoding required |
| Timeout | 10000ms (10s) | Fail-fast — ZTE MF667 doesn't send +CUSD URC |
| Format | `AT+CUSD=1,"*code#",15` | Third parameter mandatory |

### Codes

| Code | Purpose | Example Response |
|------|---------|-----------------|
| `*101#` | Phone number | `Cher client, votre numero est : 213674168034` |
| `*222#` | Balance | `Sama, Solde 82,59DA` or `Votre demande est prise en charge, un SMS vous sera envoye.` |

### ZTE MF667 USSD Limitation

**The ZTE MF667 modem does NOT send `+CUSD:` URC via serial AT.** The modem accepts `AT+CUSD` (returns `OK`) but the network response never arrives. This is a known firmware limitation.

**Workarounds implemented:**
- **Phone number:** Falls back to `AT+CNUM` command
- **Balance:** Extracted from SMS content parsing (`ExtractBalanceFromContent()`)
- **USSD timeout reduced to 10s** (fail-fast, not 30s wasted waiting)

### Response Handling

`*222#` often returns "Votre demande est prise en charge, un SMS vous sera envoye." instead of direct balance. The actual balance arrives via SMS from 7711198105108105115 — extracted by `ExtractBalanceFromContent()`.

### UTF-16 Decoding

Mobilis returns USSD as UTF-16BE hex. Auto-detected:

```
0053 = 'S', 0061 = 'a', 006D = 'm', 0061 = 'a'
→ "Sama"
```

If hex starts with "00" → UTF-16 (4 chars/char). Otherwise → ASCII (2 chars/char).

---

## MongoDB Cloud Sync

### Configuration

| Key | Default | Description |
|-----|---------|-------------|
| `mongodb.uri` | `mongodb+srv://admin:admin@cluster0.ldndrwe.mongodb.net/?appName=Cluster0` | MongoDB Atlas connection string |
| `mongodb.database` | `focusgate` | Database name |
| `sync.interval_seconds` | `30` | Sync cycle interval |

### Collections (8)

| SQLite Table | MongoDB Collection | Documents |
|-------------|-------------------|-----------|
| Modems | modems | Modem documents |
| SimCards | simcards | SimCard documents |
| SmsRecords | smsrecords | SmsRecord documents |
| BalanceHistories | balancehistories | BalanceHistory documents |
| Users | users | User documents |
| UserModems | usermodems | UserModem documents |
| WithdrawalRequests | withdrawalrequests | WithdrawalRequest documents |
| UserBalanceHistories | userbalancehistories | UserBalanceHistory documents |

### How It Works

`MongoSyncService` runs as a BackgroundService:

1. **First cycle:** Full sync — push all local records to MongoDB, pull all remote records to SQLite.
2. **Subsequent cycles:** Incremental sync — only records where `UpdatedAt > lastSyncAt` are transferred.
3. **Push:** SQLite → MongoDB using **compound filter** (`Id` + `MachineId`). Upsert with `IsUpsert=true`.
4. **Pull:** MongoDB → SQLite filtered by `MachineId` only. Insert or update using `IgnoreQueryFilters`.
5. **Conflict resolution:** Last-write-wins (comparing `UpdatedAt` timestamps).
6. **Duplicate key safety:** `SafeUpsertAsync` catches `MongoWriteException` (DuplicateKey) and falls back to overwrite-by-Id, claiming old records from other machines.
7. **Soft delete propagation:** `ArchivedAt` timestamp syncs across PCs via MongoDB.
8. **Offline handling:** If MongoDB is unreachable, retries every 60 seconds.

### MongoDB Schema

- **Database:** `focusgate`
- **Convention pack:** `CamelCaseElementNameConvention`, `IgnoreExtraElementsConvention(true)`
- **Class maps:** All 8 models mapped with `SetIgnoreExtraElements(true)`, navigation properties unmapped
- **`_id` field:** Maps to SQLite `Id` property
- **Compound key:** Every document has `MachineId` field for per-PC isolation

---

## Machine Identity & Licensing

### MachineInfoService

Generates a **persistent 16-character hex fingerprint** from hardware identifiers:

```
Input: hostname + username + MAC address + Windows registry MachineGuid
  ↓ SHA256 hash
Output: 16-char hex string (e.g., "d26b1c221259fb12")
```

This fingerprint is:
- **Unique per PC** — different hardware = different MachineId
- **Persistent** — same PC always generates the same ID
- **Logged on startup** — `Machine: d26b1c221259fb12 (WIN-L1KKATF985G@Administrator, Windows NT 10.0.26200.0)`

### MachineId Persistence

On first run, the generated MachineId is **saved to `data/config.json`** as `machine.id`. This ensures the ID never changes even if MAC address adapter order changes or Windows updates modify the registry GUID.

### MachineId on All Models

Every record in all 8 SQLite tables has a `MachineId` column. On insert, the DbContext stamps it with the current machine's ID.

### RSA License Verification

On every startup, `LicenseService.VerifyLicense()` checks:
1. `license.json` exists in `data/` directory
2. License is signed with RSA 2048-bit private key
3. `MachineId` matches current machine
4. License has **no expiry** (`ExpiresAt: null`) — anti-copy only

If verification fails → the application **auto-generates a new license** using the embedded private key. No manual license setup needed.

**License file:** `license.json` — auto-generated on first run. RSA private key embedded in `FocusGate.Core` assembly as resource for runtime generation.

**Developer contact:** License errors show: `Contact developer: ouguenfoude@gmail.com`

### MongoDB Isolation

**Push:** Compound filter `Filter.And(Id=x, MachineId=myId)` — records from different PCs never collide in MongoDB.

**Pull:** Filtered by `MachineId` — each PC only syncs its own records from the cloud.

### Config Override

Set `machine.id` in `data/config.json` to override the auto-generated ID. Useful for:
- Restoring from a backup on a new PC
- Sharing a machine identity across VMs

---

## Lifecycle Scenarios

### No Modem
Orchestrator scans → ports timeout → nothing happens.

### Modem Without SIM
IMEI read → IMSI empty → skipped. No DB row.

### Modem + SIM (Happy Path)
IMEI+IMSI → DB row → startup (*101# → *222# → SMS → timers) → Online.

### Duplicate Port (COM8 + COM9)
COM8 starts → COM9 gets "Duplicate IMEI" → skipped.

### Modem Unplugged
IOException → DisconnectAsync → Offline → ComPort=NULL → IMEI freed.

### Modem Replugged
Same IMEI detected → fresh startup → Online.

### SIM Changed
New IMSI → *101# → new phone → old SIM gets RemovedAt → new SimCard row.

### Multiple Modems (1-10)
Each gets own handler, timers, DB row. Parallel operation.

### Network Failure
CREG retries 5x → PendingNetwork → USSD may fail → Online anyway.

### Watchdog Dead
AT fails → DisconnectAsync → Offline → ComPort=NULL → IMEI freed → re-probe on next cycle.

### Update (Same PC)
Run new installer or copy new files over old (skip `data/`). Schema auto-migrates, config auto-merges, data preserved.

### New PC
Copy `dist/` or run installer → start → fresh DB created → license auto-generated → MachineId generated and persisted → MongoDB sync starts.

### Restore from Cloud
Set `machine.id` in config to match old PC → start → MongoDB pulls all old records.

### Click While Running
Click FocusGate.exe while already running → sends "restart" via named pipe → old instance dies → new instance starts fresh.

---

## FocusGate Desktop Dashboard

A modern, dark-themed WPF application providing a live dashboard of all connected modems.

**One command runs both apps:**
```powershell
dotnet run --project src\FocusGate.Hardware
```

Hardware auto-launches Desktop via `Process.Start` after database initialization.

### Key Features

- **Splash Screen:** LoadingWindow shows startup stages (waiting for DB, loading config, connecting to database)
- **WPF-UI Fluent Design:** Dark theme with Mica backdrop, `ui:FluentWindow`
- **Live Overview:** All modems displayed as a DataGrid with auto-refresh every 5 seconds
- **Filter Toggle:** All / Online / Offline filter buttons (green highlight for active filter)
- **Columns:** Row #, IMEI, Phone Number, Status (green/gray dot), Balance (DA), More button
- **Modem Detail:** Click "More" or double-click row → detail page with Phone, Balance, IMEI, Status, SMS list
- **SMS Decoding:** Sender IDs like `7711198105108105115` automatically decoded to `MOBILIS`
- **SMS Detail Dialog:** Double-click any SMS → popup with full content, scrollable
- **Sound Notification:** System beep when new SMS detected (compare count on each refresh)
- **Mute Toggle:** Speaker icon in header — toggle sound on/off (muted state shown in red)
- **Restart Button:** Sends restart signal to Hardware via named pipe, relaunches it
- **Stop Button:** Sends stop signal to Hardware via named pipe, both apps shut down with confirmation
- **System Tray:** Close minimizes to tray. Exit via tray context menu with "Exit FocusGate?" confirmation
- **Machine ID:** Shown in header for identification
- **Read-Only:** `SaveChanges()` returns 0 — Desktop physically cannot write to the database

### Theme

| Element | Color |
|---------|-------|
| Background | `#09090b` (near-black) |
| Surface / Cards | `#18181b` |
| Accent | `#10b981` (emerald green) |
| Accent Hover | `#059669` |
| Border | `#3f3f46` |
| Text Primary | `#ffffff` |
| Text Muted | `#a1a1aa` |
| Online | `#10b981` (green dot) |
| Offline | `#71717a` (gray dot) |
| Mute Icon (muted) | `#ef4444` (red) |
| Stop Button | `#ef4444` (red) |

### Desktop Config

Desktop reads `data/config.json` (same file as Hardware) to display the Machine ID. No separate config file needed.

### Desktop Log

File: `desktop.log` (in output directory) — errors only, auto-created.

---

## Console Commands

| Command | Description |
|---------|-------------|
| `help` | Show all commands |
| `status` | Dashboard: modems, online/offline count, total SMS, total balance, low-balance warnings |
| `modems` | List all modems: ID, IMEI, Status, Port, Balance |
| `modem <id>` | Modem detail: SIM info, SMS count, history |
| `sms [modemId] [days]` | Last 20 SMS records (filterable by modem and date range) |
| `sim <modemId>` | SIM history for a modem |
| `config` | Show all config values |
| `set-config <key> <value>` | Update config (writes to correct `data/config.json`) |
| `users` | List all users |
| `adduser <u> <p> [d]` | Create user (role=User) |
| `assign <uid> <mid>` | Assign modem to user |
| `unassign <uid> <mid>` | Unassign modem from user |
| `settle <modId> <amt> [note]` | Manual balance adjustment → BalanceHistory (Source=Settlement) |
| `report balance [id] [days]` | Balance history report with date range |
| `report sms [id] [days]` | SMS report: total, by-day breakdown, top-10 senders |
| `exit` | Shutdown |

---

## Configuration

File: `data/config.json`

### All Config Keys

| Key | Default | Used In |
|-----|---------|---------|
| `app.version` | `1.0.0` | ConfigMerger — version tracking |
| `gateway.name` | `FocusGate` | Display name |
| `gateway.admin.password` | `Admin@FocusGate2024` | Admin credentials |
| `machine.id` | `""` (auto) | MachineInfoService — override auto-generated ID |
| `modem.watchdog.interval` | `30` | ModemHandler — watchdog timer (seconds) |
| `modem.sms.poll.interval` | `30` | ModemHandler — SMS poll timer (seconds) |
| `modem.balance.poll.interval` | `30` | ModemHandler — periodic balance check (minutes) |
| `modem.ussd.phone_code` | `*101#` | USSD phone number code |
| `modem.ussd.balance_code` | `*222#` | USSD balance code |
| `modem.ussd.dcs` | `15` | USSD DCS parameter |
| `serial.read.timeout` | `5000` | AtCommandService — SerialPort.ReadTimeout (ms) |
| `mongodb.uri` | `mongodb+srv://admin:admin@...` | FocusGateMongoClient — Atlas connection |
| `mongodb.database` | `focusgate` | FocusGateMongoClient — database name |
| `sync.interval_seconds` | `30` | MongoSyncService — sync cycle interval |
| `alert.low_balance_threshold` | `10` | `status` command — low balance warning (DA) |

### ConfigMerger

On every startup, `ConfigMerger.EnsureConfig()` checks for missing config keys and adds them with default values. Existing values are **never overwritten**. This means updating to a new version automatically adds new config keys while preserving your settings.

---

## Deployment

### dist/ Folder Layout (Self-Contained)

```
dist/
  FocusGate.exe              ← entry point (launches both apps)
  FocusGate.Desktop.exe      ← WPF dashboard
  FocusGate-Setup.exe        ← Inno Setup installer
  *.dll                      (.NET runtime + all dependencies)
```

**Size:** ~169 MB (flat, all files in root)
**Target:** win-x64
**No .NET runtime needed** on the target PC.

### Installer Features

- Admin rights required
- Kills running FocusGate + FocusGate.Desktop processes before overwrite
- Desktop shortcut: `FocusGate`
- Start Menu group: FocusGate + Uninstall FocusGate
- Auto-start on boot: `HKCU\...\Run\FocusGate`
- Uninstall: cleans `{app}\data`
- Output: `dist/FocusGate-Setup.exe`

### Install on New PC (Option A: Installer)

1. Run `dist/FocusGate-Setup.exe`
2. Follow wizard — installs to `C:\Program Files\FocusGate\`
3. Done — desktop shortcut created, admin seeded, MongoDB sync starts

### Install on New PC (Option B: Portable)

1. Copy entire `dist/` folder to target PC
2. Double-click `FocusGate.exe`
3. Done — database created, license generated, admin seeded, MongoDB sync starts

### Update on Existing PC

1. Run new installer or copy new `dist/` files over old (skip `data/` folder)
2. Run `FocusGate.exe`
3. Schema auto-migrates, config auto-merges

### Build from Source

```powershell
# Debug build (for development)
dotnet build FocusGate.sln

# Release self-contained build (for deployment)
dotnet publish src\FocusGate.Hardware -c Release -r win-x64 --self-contained true -o dist
dotnet publish src\FocusGate.Desktop -c Release -r win-x64 --self-contained true -o dist

# Copy icon
Copy-Item src\FocusGate.Desktop\icon.ico dist\icon.ico

# Recompile installer
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" "installer\FocusGate.iss"
```

---

## Source Files

### FocusGate.Core (20 files)

| File | Description |
|------|-------------|
| Models/Modem.cs | Modem: Id, IMEI, ComPort, Status, Brand, Manufacturer, Model, CreatedAt, UpdatedAt, ArchivedAt, MachineId, SimCards |
| Models/SimCard.cs | SimCard: 16 fields, FK to Modem, navigation to Modem + SmsRecords |
| Models/SmsRecord.cs | SmsRecord: Id, SimCardId, SenderNumber, Content, ReceivedAt, ProcessedAt, UpdatedAt, ArchivedAt, MachineId |
| Models/User.cs | User: Id, Username, Password, DisplayName, Role, Balance, IsActive, CreatedAt, UpdatedAt, ArchivedAt, MachineId |
| Models/UserModem.cs | UserModem: UserId, ModemId, AssignedAt, RemovedAt, UpdatedAt, ArchivedAt, MachineId |
| Models/BalanceHistory.cs | BalanceHistory: SimCardId(long?), ModemId(int?), UserId, Balance, PreviousBalance, Source, RecordedAt, UpdatedAt, ArchivedAt, MachineId |
| Models/WithdrawalRequest.cs | WithdrawalRequest: UserId, Amount, Status, Note, AdminNote, ProcessedByAdminId, RequestedAt, ProcessedAt |
| Models/UserBalanceHistory.cs | UserBalanceHistory: UserId, Amount, BalanceAfter, Type, SimCardId, Note, RecordedAt |
| Enums/ModemStatus.cs | Unknown=0, Detected=1, Connecting=2, Initializing=3, Online=4, Offline=5, Error=6, PendingNetwork=7 |
| Enums/ModemBrand.cs | Unknown=0, ZTE=1, Huawei=2, Quectel=3, SIMCom=4, SierraWireless=5, Ericsson=6, MediaTek=7, Other=99 |
| Enums/SimStatus.cs | Active=0, Replaced=1, Expired=2 |
| Enums/BalanceSource.cs | USSD=0, SMS=1, Settlement=2, Manual=3, Withdrawal=4 |
| Enums/UserRole.cs | Admin=0, User=1 |
| Enums/NetworkRegistration.cs | NotRegistered=0, Registered=1, Denied=2, Unknown=3 |
| Enums/SmsParseStatus.cs | Pending=0, Parsed=1, ParseFailed=2, Rejected=3, Duplicate=4 |
| Enums/WithdrawalStatus.cs | Pending=0, Approved=1, Rejected=2 |
| DTOs/RawSmsMessage.cs | Index, Status, Sender, Content, ReceivedAt |
| DTOs/SmsParseResult.cs | SenderNumber (minimal, SMS parser removed) |
| Interfaces/IAtCommandService.cs | 14 members |
| Interfaces/IConfigProvider.cs | Get(string), Get\<T\>(string) |
| Interfaces/ISmsParser.cs | Empty (removed, raw storage only) |
| Services/LicenseService.cs | RSA verification + GenerateForMachine() using embedded private key |

### FocusGate.Infrastructure (9 files)

| File | Description |
|------|-------------|
| Data/FocusGateDbContext.cs | 8 DbSets, FK config, composite indexes, SaveChanges override stamps UpdatedAt + MachineId, HasQueryFilter(ArchivedAt) on all entities |
| Data/DatabaseInitializer.cs | EnsureCreated + PRAGMAs (WAL, busy_timeout=5000, synchronous=NORMAL, foreign_keys=ON) + MachineId + Brand/Manufacturer/Model + Balance column migrations |
| Data/FocusGateMongoClient.cs | MongoDB connection, 8 IMongoCollection\<T\> properties, BsonClassMap registration, IsConnected, TestConnectionAsync |
| Services/DatabaseWriteChannel.cs | Channel\<T\> single-writer, 11 operations (InsertModem with Brand/Manufacturer/Model), BalanceHistory only on change, credit detection, balance extraction from SMS, dedup, CreditUserBalance helper |
| Services/JsonConfigProvider.cs | JSON file config, hot-reload (5s stale check), type conversion |
| Services/MachineInfoService.cs | SHA256 fingerprint from hostname + username + MAC + Windows registry GUID |
| Services/MongoSyncService.cs | BackgroundService, bidirectional sync, compound MachineId filter, SafeUpsertAsync, ArchivedAt propagation |
| Services/SmsParser.cs | Empty (removed, raw storage only) |
| DependencyInjection.cs | Registers DbContext, WriteChannel, ConfigProvider, MachineInfoService, MongoSyncClient, MongoSyncService |

### FocusGate.Hardware (8 files)

| File | Description |
|------|-------------|
| Program.cs | Entry point, Serilog, Mutex (single-instance), license verification + auto-generation, data path resolution, ConfigMerger, DI setup, DB init, starts WriteChannel, auto-launches Desktop, restart-via-pipe if already running, ShowErrorDialog on startup failure |
| ConfigMerger.cs | Ensures all required config keys exist (28 keys), adds missing keys with defaults, uses JsonSerializer.Serialize for safe output, includes machine.id |
| DependencyInjection.cs | Registers ModemOrchestrator as Singleton |
| Services/AtCommandService.cs | Serial AT, auto-baud detection (9600/115200/57600/19200), USSD (10s fail-fast), SMS parse (line-by-line, no regex), multi-strategy decode (UTF-16/GSM 7-bit/ISO-8859-1), UDH merge, consecutive-index merge, SemaphoreSlim for thread safety |
| Services/ModemHandler.cs | Startup (14 steps: IMEI, CPIN check, manufacturer/model detect, signal check, force modem mode, network reg, phone/balance USSD, SIM upsert, charset fallback, SMS read, status update, watchdog/poll/balance loops), async Task loops with SemaphoreSlim (no Timer+async), watchdog (DisconnectAsync on failure), poll (*222# after any SMS), periodic balance check, CancellationToken support |
| Services/ModemOrchestrator.cs | COM scan (every 30s), auto-baud probe, manufacturer/model/brand detection, IMEI dedup, parallel probe (8s timeout), orphan check after probes, thread-safe _handlers/_activeImeis |
| Services/ConsoleCommandHandler.cs | 16 commands, admin auto-seed, scoped DbContext via IServiceScopeFactory, CancellationToken on all SaveChanges, config save to correct path, settlement, reports, low-balance alert, WinExe detection |
| Services/RestartService.cs | Named pipe IPC listener (`FocusGate_Restart`), handles "restart" and "stop" commands, calls IHostApplicationLifetime.StopApplication() |

### FocusGate.Desktop (15 files)

| File | Description |
|------|-------------|
| App.xaml | ResourceDictionary merging Styles.xaml + WPF-UI theme dictionaries |
| App.xaml.cs | Mutex `Global\FocusGate_Desktop`, tray icon (H.NotifyIcon), Exit confirmation dialog, global exception handlers, splash screen (LoadingWindow), PathService usage, error dialog |
| Data/ReadOnlyDbContext.cs | 3 DbSets (Modems, SimCards, SmsRecords), SaveChanges returns 0, NoTracking |
| ViewModels/ModemListItem.cs | Id, RowNumber, Imei, PhoneNumber, IsOnline, Balance, BalanceFormatted |
| ViewModels/SmsListItem.cs | Id, SenderNumber, Content, ContentPreview (40 chars), ReceivedAt, ReceivedAtFormatted |
| Themes/Styles.xaml | 15 named brushes, global Window/Page/Button styles, dark green theme |
| Views/LoadingWindow.xaml | Splash screen: FG logo, "FocusGate" title, status text, indeterminate progress bar (plain Window) |
| Views/LoadingWindow.xaml.cs | UpdateStatus() method for loading stages |
| Views/MainWindow.xaml | FluentWindow with Mica backdrop, header (FG logo, title, MachineId, Restart/Stop/Mute buttons), Frame navigation |
| Views/MainWindow.xaml.cs | RestartButton_Click (named pipe + relaunch), StopButton_Click (named pipe + shutdown), MuteButton_Click, OnClosing (minimize to tray), hardware path = FocusGate.exe |
| Views/ModemsOverviewPage.xaml | Filter buttons, DataGrid (#, IMEI, Phone, Status, Balance, More), empty state |
| Views/ModemsOverviewPage.xaml.cs | Auto-refresh 5s, filter state, SMS count comparison for sound notification, in-memory joins |
| Views/ModemDetailPage.xaml | Back button, detail card (Phone, Balance, IMEI, Status), SMS list |
| Views/ModemDetailPage.xaml.cs | Auto-refresh 5s, DecimalAscii decode for phone numbers, SMS sound notification |
| Views/SmsDetailDialog.xaml | Dark popup: sender, date, scrollable content, close button |
| Views/SmsDetailDialog.xaml.cs | Displays SmsListItem data |

**Total: 52 source files** (20 + 9 + 8 + 15)

---

## Real-World Test Results

### Tested Hardware

| Item | Value |
|------|-------|
| Modem | ZTE PID=0016 |
| COM Ports | COM8 (active) + COM9 (duplicate, skipped) |
| SIM | Mobilis Algeria |
| IMEI | 359905014822818 |
| IMSI | 603019053265152 |
| Phone | 213674168034 |
| Balance | 82.59 DZD |
| MachineId | d26b1c221259fb12 |

### Build Status

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Startup Log

```
[INF] Machine: d26b1c221259fb12 (WIN-L1KKATF985G@Administrator, Microsoft Windows NT 10.0.26200.0)
[INF] Database initialized (EnsureCreated + PRAGMAs + MachineId migration)
[INF] MachineId persisted to config: d26b1c221259fb12
[INF] License verified for machine d26b1c221259fb12
[INF] FocusGate started | DB: D:\FocusGate\dist\data\focusgate.db | Machine: d26b1c221259fb12
[INF] Desktop monitor launched
[INF] MongoDB connected: focusgate
[INF] Running in background mode (no console).
[INF] Probing 1 port(s) in parallel...
[INF] Default admin user seeded: admin/admin
[INF] Starting full sync for machine d26b1c221259fb12...
[INF] Pushed 1 records to MongoDB (skipped 0, machine: d26b1c221259fb12)
[INF] Pulled 1 records from MongoDB (machine: d26b1c221259fb12)
[INF] Full sync complete
[WRN] Opened COM3 at default 9600 baud (no AT response verified)
```

### SMS Reception Log

```
[INF] [CMGL] Parsed 1 SMS messages (UDH=0, consecutive=1)
[INF] SMS stored: SimCardId=1 Sender=7711198105108105115
[INF] Balance confirmed from SMS: Modem=1 82.59 DZD (no change)
[INF] Modem 1: 1 SMS saved to DB
[INF] [CMGD] Storage=SM Deleting 2 SMS
```

### USSD Raw Responses

**Phone (*101#):**
```
+CUSD: 0,"004300680065007200200063006C00690065006E0074002C00200076006F0074007200650020006E0075006D00E90072006F00200065007300740020003A0020003200310033003600370034003100360038003000330034",72
→ "Cher client, votre numero est : 213674168034"
```

**Balance (*222#):**
```
+CUSD: 0,"0056006F007400720065002000640065006D0061006E00640065002000650073007400200070007200690073006500200065006E0020006300680061007200670065002C00200075006E00200053004D005300200076006F007500730020007300650072006100200065006E0076006F007900E9002E",72
→ "Votre demande est prise en charge, un SMS vous sera envoye."
```

**Balance SMS (from 7711198105108105115):**
```
Sama, Solde 82,59DA, Sama Mix 50 500Mo valide au 20/06/2026, Bonus s50,00DA valable au 20/06/2026 a 22:50
```

---

## Known Limitations

### ZTE MF667 USSD
The ZTE MF667 modem does NOT send `+CUSD:` URC via serial AT commands. The modem accepts `AT+CUSD` (returns `OK`) but the network response never arrives. This is a known firmware limitation. Workaround: phone from CNUM, balance from SMS content.

### SerialPort.ReadLine() on USB
`SerialPort.ReadLine()` returns empty strings on USB serial adapters (known .NET bug on Windows). Workaround: use `ReadExisting()` with polling loop instead.

---

## Developer Contact

**Email:** ouguenfoude@gmail.com
