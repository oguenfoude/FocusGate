# FocusGate — Improvement Ideas

Ideas for making FocusGate better. Organized by priority.

---

## High Priority

### 1. Desktop Shows Actual Sync Status

The green status bar currently always says "Connected." It should show:
- MongoDB connection status (Connected / Disconnected)
- Last sync time (e.g., "Last sync: 2 min ago")
- Modem online/offline count
- Any active errors

**Action:** Add a `SyncStatus` read-out. Since Desktop is read-only and can't access the MongoSyncService directly, options are:
- Write sync status to a small `status.json` file that Desktop reads
- Or add a `SyncStatus` table to SQLite (Hardware writes, Desktop reads)

---

### 2. Version Display in Desktop

The config has `app.version: 1.0.0` but the Desktop doesn't show it. Users can't tell what version they're running.

**Action:** Read `app.version` from `data/config.json` in `App.xaml.cs`, display it in the header next to MachineId.

---

### 3. Password Hashing

Currently passwords are stored in plain text. Even for a local-only system, this is a risk.

**Action:** Add SHA256 hashing on user creation and login verification. Existing plain-text passwords can be migrated on first run by checking if the stored password looks like a hash (64 hex chars) vs plain text.

---

## Medium Priority

### 4. Config Validation on Startup

Some config keys could have invalid values (e.g., negative intervals, empty MongoDB URI). Currently the app just uses whatever is in the config.

**Action:** Add validation in `ConfigMerger.EnsureConfig()`:
- `modem.watchdog.interval` must be >= 5
- `modem.sms.poll.interval` must be >= 10
- `mongodb.uri` must not be empty
- `serial.read.timeout` must be >= 1000

---

### 5. Log Rotation Cleanup

Logs roll daily but the 30-day retention limit is only in Serilog. Old `desktop.log` files accumulate forever.

**Action:** Add cleanup on startup — delete `desktop.log` files older than 30 days.

---

### 6. Desktop: Send SMS (Testing)

The Desktop is currently read-only. For testing purposes, a "Send SMS" button on the detail page would be useful.

**Action:** This would require Desktop to have write access, which breaks the read-only architecture. Better approach: add a `send` console command that sends via the Hardware's ModemHandler. The Desktop already shows the result when the SMS arrives.

---

### 7. Multi-SIM Support per Modem

Some modems have dual-SIM slots. Currently we assume one SIM per modem.

**Action:** This requires hardware-level support (AT commands for SIM slot switching) which varies by modem manufacturer. Low priority unless specifically needed.

---

### 8. Backup / Restore Commands

No way to manually backup or restore the database.

**Action:** Add console commands:
- `backup` — copies `focusgate.db` to `data/backups/focusgate-YYYYMMDD-HHMMSS.db`
- `restore <file>` — stops sync, replaces DB, restarts

---

### 9. Alert Notifications

Low-balance alerts only show in the `status` command. Could be more visible.

**Action:** Options:
- Desktop toast notification (Windows toast API)
- Log warning every N minutes when balance is low
- Write to `data/alerts.log` for external monitoring

---

### 10. SMS Search

No way to search SMS content.

**Action:** Add console command `search <query>` that searches SMS content. Desktop could add a search box.

---

## Low Priority

### 11. Web Dashboard

A lightweight web UI accessible from other devices on the same network.

**Action:** Backend tables (WithdrawalRequests, UserBalanceHistories, User.Balance) are already in place. Could add a separate `FocusGate.Web` project that reads the same SQLite DB.

---

### 12. Export to CSV/Excel

No way to export balance history or SMS records.

**Action:** Add console commands:
- `export balance [modemId] [days]` → CSV file
- `export sms [modemId] [days]` → CSV file

---

### 13. i18n — Multi-Language Support

Currently UI is English only. French and Arabic would be useful for Algeria.

**Action:** Add resource files for en/fr/ar. Desktop XAML uses DynamicResource for all text strings. RTL layout support needed for Arabic.

---

### 14. Statistics Dashboard

The Desktop shows current state but no historical trends.

**Action:** Add a charts page showing:
- Balance over time (line chart)
- SMS per day (bar chart)
- Modem uptime percentage

This would require a charting library (e.g., LiveCharts2 for WPF).

---

### 15. Rate Limiting

No protection against rapid SMS flood or USSD spam.

**Action:** Add configurable rate limits:
- Max SMS per minute per modem
- Max USSD requests per hour per modem
- Cooldown period after rapid activity

---

## Completed Features (Reference)

These are already implemented:

### Core
- [x] Modem detection (COM port probe)
- [x] IMEI deduplication
- [x] Auto-baud detection (9600/115200/57600/19200)
- [x] Multi-brand support (ZTE, Huawei, Quectel, SIMCom, Sierra, etc.)
- [x] Brand detection and logging (AT+CGMI, AT+CGMM)
- [x] AT command service (SemaphoreSlim thread safety)
- [x] SMS polling (30s)
- [x] UDH merge + consecutive-index merge
- [x] Multi-strategy SMS decoding (UTF-16/GSM 7-bit/ISO-8859-1)
- [x] Credit transfer detection
- [x] Balance extraction from SMS + USSD
- [x] BalanceHistory (only when balance changes, nullable FKs for withdrawals)
- [x] SQLite database (8 tables: Modems, SimCards, SmsRecords, Users, UserModems, BalanceHistories, WithdrawalRequests, UserBalanceHistories)
- [x] Schema auto-migration (EnsureCreated + AddColumnIfMissing)
- [x] Config auto-merge on update (ConfigMerger, 28 keys)
- [x] SIM PIN check (AT+CPIN?)
- [x] Signal quality check (AT+CSQ)
- [x] Charset fallback (IRA → GSM → UCS2)
- [x] User wallet balance (User.Balance separate from SimCard.Balance)
- [x] Auto-credit user on credit transfer SMS
- [x] Withdrawal request model (WithdrawalStatus: Pending/Approved/Rejected)
- [x] UserBalanceHistory tracking

### Infrastructure
- [x] MongoDB sync (bidirectional, 8 collections, MachineId compound filter)
- [x] Machine identity (SHA256 fingerprint, persisted to config.json)
- [x] RSA license verification (2048-bit, machine-locked, no expiry, auto-generate on mismatch)
- [x] DatabaseWriteChannel (Channel<T> single-writer, TaskCompletionSource)
- [x] Async Task loops (no Timer+async, SemaphoreSlim concurrency)
- [x] CancellationToken support throughout
- [x] LicenseService.GenerateForMachine() (embedded private key)
- [x] MongoSyncService (8 collections: modems, simcards, smsrecords, balancehistories, users, usermodems, withdrawalrequests, userbalancehistories)

### Desktop
- [x] WPF Desktop (dark theme, auto-refresh 5s)
- [x] WPF-UI 4.3.0 Fluent design (FluentWindow, Mica backdrop)
- [x] Splash/loading screen (LoadingWindow)
- [x] SMS sound notification + mute toggle (green/red color indicator)
- [x] Online/Offline/All filter
- [x] Modem detail page (Phone, Balance, IMEI, Status, SMS list)
- [x] SMS detail dialog (scrollable content popup, fixed layout)
- [x] System tray (H.NotifyIcon, close minimizes to tray, proper icon)
- [x] Restart button (named pipe IPC)
- [x] Stop button (sends stop via pipe, Hardware marks modems Offline)
- [x] Single instance (named Mutex `Global\FocusGate_Desktop`)
- [x] Read-only DbContext (SaveChanges returns 0)
- [x] Global exception handlers
- [x] Header buttons with proper colors (Restart=white, Stop=red, Mute=green/red)
- [x] Error dialog on startup failure

### Deployment
- [x] Self-contained build (no .NET needed, ~169 MB)
- [x] WinExe mode (no terminal window)
- [x] Inno Setup installer (admin rights, desktop shortcut, auto-start, kills processes)
- [x] Data lives next to exe ({baseDir}/data/)
- [x] Update mechanism (installer or copy over, skip data/)
- [x] Single-instance restart (click FocusGate.exe while running)
- [x] Named pipe IPC (restart + stop commands)
- [x] License auto-recovery (mismatch → auto-generate for current machine)
- [x] Error dialog on startup failure (contact developer: ouguenfoude@gmail.com)

### Hardware
- [x] Force modem mode (ZTE AT+ZCDRUN=2 + Huawei AT^U2DIAG=0)
- [x] Orphan race condition fix
- [x] Double-dispose crash fix
- [x] Watchdog recovery (DisconnectAsync → re-probe)
- [x] SimCardId resolution (30x500ms retry)
- [x] ConsoleCommandHandler scoped DbContext
- [x] Global exception handlers (AppDomain + TaskScheduler)
- [x] _disposed checks after lock (NRE fix)
- [x] USSD fail-fast (10s timeout for ZTE MF667 limitation)
- [x] CNUM fallback for phone detection
- [x] ReadExisting() polling for USSD (SerialPort.ReadLine() broken on USB)
