// FocusGate MongoDB User Balance Reconciliation Script
// =====================================================
// Usage: node scripts/fix-mongodb-balances.js
//
// What it does:
//   1. Reads ALL userbalancehistories (non-archived) for each user
//   2. Recalculates correct balance = SUM(credits) - SUM(debits)
//   3. Removes duplicate UserBalanceHistory records (same userId, amount, recordedAt, simCardId)
//   4. Updates users.balance in MongoDB to match recalculated total
//   5. Reports every change made
//
// Safe to run multiple times — idempotent (dedup removes dupes, balance recalc is deterministic)

const mongoose = require('mongoose');

const MONGODB_URI = process.env.MONGODB_URI;
if (!MONGODB_URI) {
  console.error('Set MONGODB_URI env var first. Example:');
  console.error('  $env:MONGODB_URI="mongodb://..."; node scripts/fix-mongodb-balances.js');
  process.exit(1);
}

async function main() {
  console.log('Connecting to MongoDB...');
  await mongoose.connect(MONGODB_URI, {
    bufferCommands: false,
    maxPoolSize: 10,
    serverSelectionTimeoutMS: 10000,
  });
  console.log('Connected.\n');

  const db = mongoose.connection.db;

  // --- Step 1: Find and remove duplicate UserBalanceHistory records ---
  console.log('=== STEP 1: Deduplicate UserBalanceHistories ===');

  const allUbh = await db.collection('userbalancehistories')
    .find({ archivedAt: null })
    .sort({ userId: 1, recordedAt: 1, _id: 1 })
    .toArray();

  console.log(`Total UserBalanceHistory records (non-archived): ${allUbh.length}`);

  // Group by (userId, amount, recordedAt, simCardId) for dedup
  const groups = new Map();
  for (const doc of allUbh) {
    const key = `${doc.userId}|${doc.amount}|${doc.recordedAt}|${doc.simCardId || ''}`;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(doc);
  }

  const dupeIds = [];
  let dupeCount = 0;
  for (const [key, docs] of groups) {
    if (docs.length > 1) {
      // Keep the first (lowest _id), mark rest for deletion
      for (let i = 1; i < docs.length; i++) {
        dupeIds.push(docs[i]._id);
        dupeCount++;
      }
    }
  }

  if (dupeIds.length > 0) {
    console.log(`Found ${dupeCount} duplicate records to remove:`);

    // Print details of dupes being removed
    const dupes = allUbh.filter(d => dupeIds.includes(d._id));
    for (const d of dupes.slice(0, 20)) {
      console.log(`  REMOVE _id=${d._id} user=${d.userId} amount=${d.amount} type=${d.type} recordedAt=${d.recordedAt} note=${d.note || ''}`);
    }
    if (dupes.length > 20) console.log(`  ... and ${dupes.length - 20} more`);

    const result = await db.collection('userbalancehistories').deleteMany({ _id: { $in: dupeIds } });
    console.log(`Deleted ${result.deletedCount} duplicate records.\n`);
  } else {
    console.log('No duplicates found.\n');
  }

  // --- Step 2: Recalculate balances from remaining records ---
  console.log('=== STEP 2: Recalculate User Balances ===');

  // Aggregate correct balance per user from userbalancehistories
  const aggResult = await db.collection('userbalancehistories').aggregate([
    { $match: { archivedAt: null } },
    {
      $group: {
        _id: '$userId',
        correctBalance: { $sum: '$amount' },  // type 0 = +amount, type 1 = -amount
        recordCount: { $sum: 1 },
      }
    }
  ]).toArray();

  const correctBalances = new Map(aggResult.map(r => [r._id, { balance: r.correctBalance, count: r.recordCount }]));
  console.log(`Users with balance history: ${correctBalances.size}`);

  // Load current user balances
  const userIds = [...correctBalances.keys()];
  const users = await db.collection('users').find({ _id: { $in: userIds } }).toArray();
  const currentBalances = new Map(users.map(u => [u._id, u.balance || 0]));

  // Compare and fix
  let fixed = 0;
  let alreadyOk = 0;
  const updates = [];

  for (const [userId, { balance: correctBalance, count }] of correctBalances) {
    const current = currentBalances.get(userId) || 0;
    const diff = Math.round((correctBalance - current) * 100) / 100;

    if (Math.abs(diff) < 0.01) {
      alreadyOk++;
      continue;
    }

    const user = users.find(u => u._id === userId);
    const username = user ? user.username : `unknown(${userId})`;

    console.log(`  FIX user ${userId} (${username}): ${current.toFixed(2)} -> ${correctBalance.toFixed(2)} (diff=${diff >= 0 ? '+' : ''}${diff.toFixed(2)}) [${count} transactions]`);

    updates.push({
      updateOne: {
        filter: { _id: userId },
        update: {
          $set: {
            balance: correctBalance,
            updatedAt: new Date(),
          }
        }
      }
    });
    fixed++;
  }

  // Also check users with NO history but non-zero balance
  const usersWithNoHistory = users.filter(u => !correctBalances.has(u._id));
  for (const u of usersWithNoHistory) {
    if (u.role === 1) continue; // skip admin
    if (u.archivedAt) continue;
    if ((u.balance || 0) !== 0) {
      console.log(`  WARN user ${u._id} (${u.username}): has balance ${u.balance} but no history records — left unchanged`);
    }
  }

  if (updates.length > 0) {
    const result = await db.collection('users').bulkWrite(updates, { ordered: false });
    console.log(`\nUpdated ${result.modifiedCount} user balances in MongoDB.`);
  }

  console.log(`\nSummary: ${fixed} fixed, ${alreadyOk} already correct, ${dupeCount} duplicates removed`);
  console.log('\n=== DONE ===');

  await mongoose.disconnect();
}

main().catch(err => {
  console.error('FATAL:', err);
  process.exit(1);
});
