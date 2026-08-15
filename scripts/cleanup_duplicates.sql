-- FocusGate Duplicate Credit Cleanup Script
-- Run while gateway is STOPPED
-- Usage: sqlite3.exe focusgate.db < cleanup_duplicates.sql
--
-- This script:
--   1. Removes duplicate BalanceHistory records (MeetMob source)
--   2. Removes duplicate UserBalanceHistory records (MeetMob credits)
--   3. Recalculates user balances from remaining records

-- Preview duplicates before deleting (optional, run manually):
-- SELECT SimCardId, ROUND(Balance - PreviousBalance, 2) as Amount, RecordedAt, COUNT(*) as Cnt
-- FROM BalanceHistories WHERE Source = 5 AND ArchivedAt IS NULL AND PreviousBalance IS NOT NULL
-- GROUP BY SimCardId, Amount, RecordedAt HAVING Cnt > 1;

BEGIN TRANSACTION;

-- 1. Delete duplicate BalanceHistory records from MeetMob
--    Keep only the row with the lowest Id per (SimCardId, recharge_amount, RecordedAt)
DELETE FROM BalanceHistories
WHERE Id IN (
    SELECT Id FROM (
        SELECT Id,
               ROW_NUMBER() OVER (
                   PARTITION BY SimCardId, ROUND(Balance - PreviousBalance, 2), RecordedAt
                   ORDER BY Id
               ) as rn
        FROM BalanceHistories
        WHERE Source = 5
          AND ArchivedAt IS NULL
          AND PreviousBalance IS NOT NULL
    ) WHERE rn > 1
);

-- 2. Delete duplicate UserBalanceHistory records from MeetMob credits
--    Keep only the row with the lowest Id per (UserId, Amount, RecordedAt, SimCardId)
DELETE FROM UserBalanceHistories
WHERE Id IN (
    SELECT Id FROM (
        SELECT Id,
               ROW_NUMBER() OVER (
                   PARTITION BY UserId, Amount, RecordedAt, SimCardId
                   ORDER BY Id
               ) as rn
        FROM UserBalanceHistories
        WHERE SimCardId IS NOT NULL
          AND ArchivedAt IS NULL
          AND Amount > 0
          AND Note LIKE 'MeetMob%'
    ) WHERE rn > 1
);

-- 3. Recalculate user balances from remaining UserBalanceHistory
--    Balance = SUM of all UserBalanceHistory amounts (credits positive, withdrawals negative)
UPDATE Users
SET Balance = (
    SELECT COALESCE(SUM(ubh.Amount), 0)
    FROM UserBalanceHistories ubh
    WHERE ubh.UserId = Users.Id
      AND ubh.ArchivedAt IS NULL
)
WHERE Id IN (
    SELECT DISTINCT UserId FROM UserBalanceHistories WHERE ArchivedAt IS NULL
);

-- 4. Sync corrected user balances to MongoDB (if connected)
--    The gateway will handle this on next startup via MongoSyncService

COMMIT;

-- Verification queries (run after COMMIT to check results):
-- SELECT 'BalanceHistories remaining' as metric, COUNT(*) as cnt FROM BalanceHistories WHERE Source = 5 AND ArchivedAt IS NULL;
-- SELECT 'UserBalanceHistories remaining' as metric, COUNT(*) as cnt FROM UserBalanceHistories WHERE SimCardId IS NOT NULL AND ArchivedAt IS NULL;
-- SELECT Id, Balance FROM Users WHERE Id IN (SELECT DISTINCT UserId FROM UserBalanceHistories WHERE ArchivedAt IS NULL);
