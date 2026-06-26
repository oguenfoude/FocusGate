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

### 2. Version Display in Desktop

The config has `app.version: 1.0.0` but the Desktop doesn't show it.

### 3. Backup / Restore Commands

No way to manually backup or restore the database. Add console commands:
- `backup` — copies `focusgate.db` to `data/backups/focusgate-YYYYMMDD-HHMMSS.db`
- `restore <file>` — stops sync, replaces DB, restart

---

## Medium Priority

### 4. Config Validation on Startup

Some config keys could have invalid values. Add validation in `ConfigMerger.EnsureConfig()`.

### 5. Log Rotation Cleanup

Logs roll daily but old `desktop.log` files accumulate forever. Add cleanup on startup.

### 6. SMS Search

No way to search SMS content. Add `search <query>` console command.

### 7. Alert Notifications

Low-balance alerts only show in `status` command. Could use Windows toast API or log warnings.

### 8. Export to CSV/Excel

Add `export balance [modemId] [days]` and `export sms [modemId] [days]` console commands.

---

## Low Priority

### 9. Web Dashboard

A lightweight web UI accessible from other devices on the same network. Could be a separate `FocusGate.Web` project reading the same SQLite DB.

### 10. i18n — Multi-Language Support

French and Arabic support for Algeria. Add resource files for en/fr/ar.

### 11. Statistics Dashboard

Add charts page with balance over time, SMS per day, modem uptime. Requires charting library.

### 12. Rate Limiting

Configurable rate limits for SMS flood or USSD spam protection.

---

## Completed Features

### Core
- [x] Modem detection (COM port + HiLink HTTP)
- [x] IMEI deduplication (cross AT/HiLink)
- [x] Auto-baud detection (9600/115200/57600/19200)
- [x] Multi-brand support (ZTE, Huawei, Quectel, SIMCom, Sierra, etc.)
- [x] AT command service (SemaphoreSlim thread safety)
- [x] HiLink HTTP API service (SesTokInfo auth, USSD, SMS, device info)
- [x] HiLink network auto-detection (NetworkInterface gateway scan)
- [x] SMS polling (30s) with UDH merge + consecutive-index merge
- [x] Multi-strategy SMS decoding (UTF-16/GSM 7-bit/ISO-8859-1)
- [x] Credit transfer detection
- [x] Balance extraction from SMS + USSD
- [x] BalanceHistory (only when balance changes)
- [x] SQLite database (8 tables)
- [x] Schema auto-migration (EnsureCreated + AddColumnIfMissing)
- [x] Config auto-merge on update (ConfigMerger)
- [x] User wallet balance (auto-credit on transfer SMS)
- [x] Two separate executables (AT + HiLink)

### Infrastructure
- [x] MongoDB sync (bidirectional, 8 collections, MachineId compound filter)
- [x] Machine identity (SHA256 fingerprint, persisted to config)
- [x] DatabaseWriteChannel (Channel\<T\> single-writer)
- [x] Async Task loops (SemaphoreSlim, CancellationToken)
- [x] MongoSyncService (non-fatal, returns immediately when unreachable)

### Hardware
- [x] Force modem mode (ZTE AT+ZCDRUN=2 + Huawei AT^U2DIAG=0)
- [x] Watchdog recovery (DisconnectAsync → re-probe)
- [x] SimCardId resolution (30x500ms retry)
- [x] ConsoleCommandHandler scoped DbContext
- [x] USSD fail-fast (10s timeout for ZTE MF667)
- [x] CNUM fallback for phone detection
- [x] SHA256 password hashing

### Desktop
- [x] WPF Desktop (dark theme, auto-refresh 5s)
- [x] WPF-UI Fluent design (FluentWindow, Mica backdrop)
- [x] Splash screen
- [x] SMS sound notification + mute toggle
- [x] Online/Offline/All filter
- [x] Modem detail page
- [x] SMS detail dialog
- [x] System tray
- [x] Restart/Stop buttons (named pipe IPC)
- [x] Read-only DbContext

### Deployment
- [x] Self-contained build (no .NET needed)
- [x] WinExe mode (no terminal window)
- [x] Config auto-merge on update
- [x] Database auto-migration
- [x] Single-instance restart (named pipe)
