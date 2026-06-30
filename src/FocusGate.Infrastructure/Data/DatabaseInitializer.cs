using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Data;

public static class DatabaseInitializer
{
    public static void Initialize(FocusGateDbContext context, ILogger logger)
    {
        context.Database.EnsureCreated();

        using var cmd = context.Database.GetDbConnection().CreateCommand();
        context.Database.OpenConnection();

        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();

        AddColumnIfMissing(context, "Modems", "MachineId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(context, "SimCards", "MachineId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(context, "SmsRecords", "MachineId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(context, "BalanceHistories", "MachineId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(context, "Users", "MachineId", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(context, "UserModems", "MachineId", "TEXT NOT NULL DEFAULT ''");

        AddColumnIfMissing(context, "Users", "Balance", "DECIMAL NOT NULL DEFAULT 0");

        AddColumnIfMissing(context, "SimCards", "Status", "INTEGER NOT NULL DEFAULT 0");

        AddColumnIfMissing(context, "Modems", "Brand", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(context, "Modems", "Manufacturer", "TEXT");
        AddColumnIfMissing(context, "Modems", "Model", "TEXT");

        RecreateBalanceHistoriesIfUserIdNotNull(context);

        ExecuteSql(@"
            CREATE TABLE IF NOT EXISTS WithdrawalRequests (
                Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId             INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                Amount             DECIMAL NOT NULL,
                Status             INTEGER NOT NULL DEFAULT 0,
                Note               TEXT,
                AdminNote          TEXT,
                ProcessedByAdminId INTEGER REFERENCES Users(Id) ON DELETE SET NULL,
                RequestedAt        DATETIME NOT NULL,
                ProcessedAt        DATETIME,
                CreatedAt          DATETIME NOT NULL,
                UpdatedAt          DATETIME NOT NULL,
                ArchivedAt         DATETIME,
                MachineId          TEXT NOT NULL DEFAULT ''
            )", context);

        ExecuteSql("CREATE INDEX IF NOT EXISTS IX_WithdrawalRequests_UserId ON WithdrawalRequests(UserId)", context);
        ExecuteSql("CREATE INDEX IF NOT EXISTS IX_WithdrawalRequests_Status ON WithdrawalRequests(Status)", context);
        ExecuteSql("CREATE INDEX IF NOT EXISTS IX_WithdrawalRequests_RequestedAt ON WithdrawalRequests(RequestedAt)", context);

        ExecuteSql(@"
            CREATE TABLE IF NOT EXISTS UserBalanceHistories (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId      INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                Amount      DECIMAL NOT NULL,
                BalanceAfter DECIMAL NOT NULL,
                Type        INTEGER NOT NULL,
                SimCardId   INTEGER REFERENCES SimCards(Id) ON DELETE SET NULL,
                Note        TEXT,
                RecordedAt  DATETIME NOT NULL,
                UpdatedAt   DATETIME NOT NULL,
                ArchivedAt  DATETIME,
                MachineId   TEXT NOT NULL DEFAULT ''
            )", context);

        ExecuteSql("CREATE INDEX IF NOT EXISTS IX_UserBalanceHistories_UserId ON UserBalanceHistories(UserId)", context);
        ExecuteSql("CREATE INDEX IF NOT EXISTS IX_UserBalanceHistories_RecordedAt ON UserBalanceHistories(RecordedAt)", context);

        context.Database.CloseConnection();
        logger.LogInformation("Database initialized (EnsureCreated + PRAGMAs + MachineId migration)");
    }

    private static void AddColumnIfMissing(FocusGateDbContext context, string table, string column, string columnType)
    {
        using var checkCmd = context.Database.GetDbConnection().CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = checkCmd.ExecuteReader();
        bool exists = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == column)
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            using var alterCmd = context.Database.GetDbConnection().CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnType}";
            alterCmd.ExecuteNonQuery();
        }
    }

    private static void RecreateBalanceHistoriesIfUserIdNotNull(FocusGateDbContext context)
    {
        if (!IsColumnNotNull(context, "BalanceHistories", "UserId")) return;

        ExecuteSql("ALTER TABLE BalanceHistories RENAME TO BalanceHistories_old", context);

        ExecuteSql(@"
            CREATE TABLE BalanceHistories (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                SimCardId       INTEGER REFERENCES SimCards(Id) ON DELETE SET NULL,
                ModemId         INTEGER REFERENCES Modems(Id) ON DELETE SET NULL,
                UserId          INTEGER REFERENCES Users(Id) ON DELETE SET NULL,
                Balance         DECIMAL NOT NULL,
                PreviousBalance DECIMAL,
                Source          INTEGER NOT NULL DEFAULT 0,
                RecordedAt      DATETIME NOT NULL,
                UpdatedAt       DATETIME NOT NULL,
                ArchivedAt      DATETIME,
                MachineId       TEXT NOT NULL DEFAULT ''
            )", context);

        ExecuteSql(@"
            INSERT INTO BalanceHistories
                (Id, SimCardId, ModemId, UserId, Balance, PreviousBalance, Source, RecordedAt, UpdatedAt, ArchivedAt, MachineId)
            SELECT Id, SimCardId, ModemId, UserId, Balance, PreviousBalance, Source, RecordedAt, UpdatedAt, ArchivedAt, MachineId
            FROM BalanceHistories_old", context);

        ExecuteSql("DROP TABLE BalanceHistories_old", context);

        ExecuteSql("CREATE INDEX IF NOT EXISTS IX_BalanceHistories_ModemId ON BalanceHistories(ModemId)", context);
        ExecuteSql("CREATE INDEX IF NOT EXISTS IX_BalanceHistories_UserId ON BalanceHistories(UserId)", context);
        ExecuteSql("CREATE INDEX IF NOT EXISTS IX_BalanceHistories_RecordedAt ON BalanceHistories(RecordedAt)", context);
    }

    private static bool IsColumnNotNull(FocusGateDbContext context, string table, string column)
    {
        using var cmd = context.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == column)
            {
                return reader.GetString(3) == "1";
            }
        }
        return false;
    }

    private static void ExecuteSql(string sql, FocusGateDbContext context)
    {
        using var cmd = context.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
