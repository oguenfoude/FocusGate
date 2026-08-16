// FocusGate MongoDB Balance DRY RUN — shows what WOULD change, touches nothing
// Usage: node scripts/fix-mongodb-balances-dryrun.js

const mongoose = require('mongoose');

const MONGODB_URI = process.env.MONGODB_URI;
if (!MONGODB_URI) {
  console.error('Set MONGODB_URI env var first.');
  process.exit(1);
}

async function main() {
  console.log('Connecting to MongoDB (READ ONLY)...');
  await mongoose.connect(MONGODB_URI, {
    bufferCommands: false,
    maxPoolSize: 5,
    serverSelectionTimeoutMS: 10000,
  });
  console.log('Connected.\n');

  const db = mongoose.connection.db;

  // --- Step 1: Find duplicates ---
  console.log('=== STEP 1: Duplicate UserBalanceHistories ===\n');

  const allUbh = await db.collection('userbalancehistories')
    .find({ archivedAt: null })
    .sort({ userId: 1, recordedAt: 1, _id: 1 })
    .toArray();

  console.log(`Total records (non-archived): ${allUbh.length}`);

  const groups = new Map();
  for (const doc of allUbh) {
    const key = `${doc.userId}|${doc.amount}|${doc.recordedAt}|${doc.simCardId || ''}`;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(doc);
  }

  let dupeCount = 0;
  for (const [key, docs] of groups) {
    if (docs.length > 1) {
      dupeCount += docs.length - 1;
      const [userId, amount, recordedAt, simCardId] = key.split('|');
      console.log(`  DUPE: user=${userId} amount=${amount} simCardId=${simCardId || 'null'} recordedAt=${recordedAt} (${docs.length} copies, keeping lowest _id)`);
      for (const d of docs) {
        console.log(`    _id=${d._id} type=${d.type} note="${d.note || ''}" balanceAfter=${d.balanceAfter}`);
      }
    }
  }

  if (dupeCount === 0) console.log('  No duplicates found.');
  console.log(`\nWould remove: ${dupeCount} duplicate records\n`);

  // --- Step 2: Recalculate balances ---
  console.log('=== STEP 2: Balance Recalculation ===\n');

  const aggResult = await db.collection('userbalancehistories').aggregate([
    { $match: { archivedAt: null } },
    {
      $addFields: {
        amountNum: { $toDouble: '$amount' },
      }
    },
    {
      $group: {
        _id: '$userId',
        correctBalance: { $sum: '$amountNum' },
        credits: { $sum: { $cond: [{ $eq: ['$type', 0] }, '$amountNum', 0] } },
        debits: { $sum: { $cond: [{ $eq: ['$type', 1] }, { $abs: '$amountNum' }, 0] } },
        recordCount: { $sum: 1 },
      }
    }
  ]).toArray();

  const correctBalances = new Map(aggResult.map(r => [r._id, r]));

  const userIds = [...correctBalances.keys()];
  const users = await db.collection('users').find({ _id: { $in: userIds } }).toArray();
  const currentBalances = new Map(users.map(u => [u._id, u]));

  let fixed = 0;
  let ok = 0;

  for (const [userId, agg] of correctBalances) {
    const user = currentBalances.get(userId);
    if (!user) continue;
    if (user.role === 0) continue; // skip system admin

    const current = Number(user.balance) || 0;
    const correct = Number(agg.correctBalance);
    const diff = Math.round((correct - current) * 100) / 100;

    if (Math.abs(diff) < 0.01) {
      ok++;
      continue;
    }

    fixed++;
    console.log(`  FIX  user=${userId} (${user.username}): ${current.toFixed(2)} -> ${correct.toFixed(2)}  [diff=${diff >= 0 ? '+' : ''}${diff.toFixed(2)}]  (${Number(agg.credits).toFixed(2)} credits - ${Number(agg.debits).toFixed(2)} debits, ${agg.recordCount} txns)`);
  }

  // Show all user balances for overview
  console.log('\n=== ALL USER BALANCES ===\n');
  const allUsers = await db.collection('users').find({ archivedAt: null }).sort({ _id: 1 }).toArray();
  for (const u of allUsers) {
    const agg = correctBalances.get(u._id);
    const historyBalance = agg ? agg.correctBalance : 0;
    const current = Number(u.balance) || 0;
    const diff = Math.abs(current - historyBalance);
    const flag = diff >= 0.01 ? ' *** MISMATCH ***' : '';
    console.log(`  user=${u._id} (${u.username}): balance=${current.toFixed(2)} history=${historyBalance.toFixed(2)}${flag}`);
  }

  console.log(`\n=== SUMMARY ===`);
  console.log(`Users: ${allUsers.length}`);
  console.log(`Records: ${allUbh.length}`);
  console.log(`Duplicates to remove: ${dupeCount}`);
  console.log(`Balances to fix: ${fixed}`);
  console.log(`Balances OK: ${ok}`);
  console.log(`\nNO CHANGES MADE (dry run)`);

  await mongoose.disconnect();
}

main().catch(err => {
  console.error('FATAL:', err);
  process.exit(1);
});
