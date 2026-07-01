# FocusGate MongoDB Schema Reference

> Reference document for the 8 MongoDB Atlas collections synced by `MongoSyncService`.
> Used by the Next.js cloud dashboard.

## Conventions

| Convention | Value |
|------------|-------|
| **Sync direction** | Bidirectional (push + pull) |
| **Sync interval** | 30 seconds (configurable via `sync.interval_seconds`) |
| **Machine scoping** | Every document has `MachineId` field — isolates one gateway machine's data |
| **Soft delete** | `ArchivedAt: null` = active; `ArchivedAt: ISODate(...)` = archived |
| **Upsert key** | Compound: `{ _id, MachineId }` |
| **Date format** | All dates stored as MongoDB `ISODate` (UTC) |
| **Enum storage** | Stored as integers (C# enum backing type) |
| **_id type** | **Number (long)** — NOT ObjectId. The C# `Id` property maps directly to MongoDB `_id` via `BsonClassMap.MapIdMember`. Never use MongoDB auto-generated ObjectId. |

---

## Collections

### 1. `modems`

| Field | Type | Description |
|-------|------|-------------|
| `_id` | Number (long) | Primary key — C# `Modem.Id` maps here |
| `IMEI` | string | Unique IMEI, max 20 chars |
| `ComPort` | string | COM port (AT modems) or null |
| `Status` | int | 0=Offline, 1=Online, 2=Pending, etc. |
| `Brand` | int | 0=Unknown, 1=Huawei, 2=ZTE, etc. |
| `Manufacturer` | string | Optional |
| `Model` | string | Optional |
| `CreatedAt` | ISODate | UTC |
| `UpdatedAt` | ISODate | UTC — used for incremental sync |
| `ArchivedAt` | ISODate | null = active, date = archived |
| `MachineId` | string | Gateway machine fingerprint |

---

### 2. `simcards`

| Field | Type | Description |
|-------|------|-------------|
| `_id` | Number (long) | Primary key — C# `SimCard.Id` maps here |
| `ModemId` | int | FK → modems.Id |
| `IMSI` | string | Max 20 chars |
| `PhoneNumber` | long | Phone number (no country code) |
| `Balance` | decimal | Current balance in DZD |
| `VerifiedAt` | ISODate | When balance was last verified via USSD |
| `IsActive` | bool | true = currently active SIM in modem |
| `Status` | int | 0=Active, 1=Inactive, 2=Removed |
| `FirstSeen` | ISODate | First time this SIM was detected |
| `LastSeen` | ISODate | Last time this SIM was active |
| `RemovedAt` | ISODate | When SIM was physically removed |
| `ReplacedAt` | ISODate | When SIM was replaced with different IMSI |
| `CreatedAt` | ISODate | UTC |
| `UpdatedAt` | ISODate | UTC |
| `ArchivedAt` | ISODate | null = active |
| `MachineId` | string | Gateway machine fingerprint |

---

### 3. `smsrecords`

| Field | Type | Description |
|-------|------|-------------|
| `_id` | Number (long) | Primary key — C# `SmsRecord.Id` maps here |
| `SimCardId` | long | FK → simcards.Id |
| `SenderNumber` | string | Sender phone/shortcode, max 20 chars |
| `Content` | string | Full SMS text |
| `ReceivedAt` | ISODate | When SMS was received by modem |
| `ProcessedAt` | ISODate | When SMS was processed by system |
| `UpdatedAt` | ISODate | UTC |
| `ArchivedAt` | ISODate | null = active |
| `MachineId` | string | Gateway machine fingerprint |

---

### 4. `balancehistories`

| Field | Type | Description |
|-------|------|-------------|
| `_id` | Number (long) | Primary key — C# `BalanceHistory.Id` maps here |
| `SimCardId` | long | FK → simcards.Id (nullable) |
| `ModemId` | int | FK → modems.Id (nullable) |
| `UserId` | long | FK → users.Id (nullable) |
| `Balance` | decimal | Balance at time of recording |
| `PreviousBalance` | decimal | Previous balance (nullable) |
| `Source` | int | 0=USSD, 1=SMS, 2=Settlement, 3=Manual, 4=Withdrawal |
| `RecordedAt` | ISODate | When balance was recorded |
| `UpdatedAt` | ISODate | UTC |
| `ArchivedAt` | ISODate | null = active |
| `MachineId` | string | Gateway machine fingerprint |

---

### 5. `users`

| Field | Type | Description |
|-------|------|-------------|
| `_id` | Number (long) | Primary key — C# `User.Id` maps here |
| `Username` | string | Unique, max 50 chars |
| `Password` | string | Plain text password, max 100 chars |
| `DisplayName` | string | Max 100 chars |
| `Role` | int | 0=User, 1=Admin |
| `IsActive` | bool | Account active flag |
| `Balance` | decimal | Current wallet balance in DZD |
| `CreatedAt` | ISODate | UTC |
| `UpdatedAt` | ISODate | UTC |
| `ArchivedAt` | ISODate | null = active |
| `MachineId` | string | Gateway machine fingerprint |

---

### 6. `usermodems`

| Field | Type | Description |
|-------|------|-------------|
| `_id` | Number (long) | Primary key — C# `UserModem.Id` maps here |
| `UserId` | long | FK → users.Id |
| `ModemId` | int | FK → modems.Id |
| `AssignedAt` | ISODate | When modem was assigned to user |
| `RemovedAt` | ISODate | When assignment was removed (nullable) |
| `UpdatedAt` | ISODate | UTC |
| `ArchivedAt` | ISODate | null = active |
| `MachineId` | string | Gateway machine fingerprint |

---

### 7. `withdrawalrequests`

| Field | Type | Description |
|-------|------|-------------|
| `_id` | Number (long) | Primary key — C# `WithdrawalRequest.Id` maps here |
| `UserId` | long | FK → users.Id |
| `Amount` | decimal | Withdrawal amount in DZD |
| `Status` | int | 0=Pending, 1=Approved, 2=Rejected |
| `Note` | string | User-provided note (nullable) |
| `AdminNote` | string | Admin note (nullable) |
| `ProcessedByAdminId` | long | FK → users.Id (nullable) |
| `RequestedAt` | ISODate | When request was created |
| `ProcessedAt` | ISODate | When request was approved/rejected (nullable) |
| `CreatedAt` | ISODate | UTC |
| `UpdatedAt` | ISODate | UTC |
| `ArchivedAt` | ISODate | null = active |
| `MachineId` | string | Gateway machine fingerprint |

---

### 8. `userbalancehistories`

| Field | Type | Description |
|-------|------|-------------|
| `_id` | Number (long) | Primary key — C# `UserBalanceHistory.Id` maps here |
| `UserId` | long | FK → users.Id |
| `Amount` | decimal | Amount (positive=credit, negative=withdrawal) |
| `BalanceAfter` | decimal | User balance after this transaction |
| `Type` | int | 0=Credit, 1=Debit/Withdrawal |
| `SimCardId` | long | FK → simcards.Id (nullable) |
| `Note` | string | Description (nullable) |
| `RecordedAt` | ISODate | When transaction was recorded |
| `UpdatedAt` | ISODate | UTC |
| `ArchivedAt` | ISODate | null = active |
| `MachineId` | string | Gateway machine fingerprint |

---

## Relationships

```
users 1──N usermodems N──1 modems
users 1──N balancehistories
users 1──N withdrawalrequests
users 1──N userbalancehistories
modems 1──N simcards
simcards 1──N smsrecords
simcards 1──N balancehistories
```

## Querying Tips

- Always filter by `MachineId` to scope to a specific gateway
- Use `ArchivedAt: null` to get only active records
- Use `UpdatedAt` for incremental sync (only changed records)
- Balance changes are tracked in both `balancehistories` (SIM-level) and `userbalancehistories` (user-level)
