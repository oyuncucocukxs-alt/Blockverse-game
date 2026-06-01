// docker-entrypoint-initdb.d/init.js
// Runs once when MongoDB container is first created.

print('BlockVerse MongoDB Init Script');

// Switch to blockverse db
db = db.getSiblingDB('blockverse');

// ─── Collections & Indexes ────────────────────────────────────────────────────

// Players
db.createCollection('players');
db.players.createIndex({ playerId: 1 },    { unique: true });
db.players.createIndex({ firebaseUid: 1 }, { unique: true });
db.players.createIndex({ username: 1 },    { unique: true, collation: { locale: 'en', strength: 2 } });
db.players.createIndex({ gems: -1 });
db.players.createIndex({ level: -1, xp: -1 });
db.players.createIndex({ lastLoginAt: -1 });
db.players.createIndex({ isBanned: 1 });
db.players.createIndex({ username: 'text' });

// Worlds
db.createCollection('worlds');
db.worlds.createIndex({ worldId: 1 },    { unique: true });
db.worlds.createIndex({ ownerId: 1 });
db.worlds.createIndex({ visitCount: -1 });
db.worlds.createIndex({ likeCount: -1 });
db.worlds.createIndex({ lastVisited: -1 });
db.worlds.createIndex({ name: 'text' });

// Chunks (compound index for world+position lookups)
db.createCollection('chunks');
db.chunks.createIndex({ worldId: 1, chunkX: 1, chunkY: 1 }, { unique: true });
db.chunks.createIndex({ worldId: 1, lastSaved: -1 });

// Chat logs (TTL index: auto-expire after 30 days)
db.createCollection('chatlogs');
db.chatlogs.createIndex({ worldId: 1, timestamp: -1 });
db.chatlogs.createIndex({ senderId: 1 });
db.chatlogs.createIndex({ timestamp: 1 }, { expireAfterSeconds: 2592000 });

// Trade logs
db.createCollection('tradelogs');
db.tradelogs.createIndex({ tradeId: 1 }, { unique: true });
db.tradelogs.createIndex({ initiatorId: 1, completedAt: -1 });
db.tradelogs.createIndex({ targetId: 1, completedAt: -1 });

// AntiCheat logs (TTL: 90 days)
db.createCollection('anticheatlog');
db.anticheatlog.createIndex({ playerId: 1, timestamp: -1 });
db.anticheatlog.createIndex({ timestamp: 1 }, { expireAfterSeconds: 7776000 });

// Market listings
db.createCollection('marketlistings');
db.marketlistings.createIndex({ listingId: 1 }, { unique: true });
db.marketlistings.createIndex({ sellerId: 1, isActive: 1 });
db.marketlistings.createIndex({ itemId: 1, isActive: 1, priceEach: 1 });
db.marketlistings.createIndex({ expiresAt: 1 }, { expireAfterSeconds: 0 });

// Guilds
db.createCollection('guilds');
db.guilds.createIndex({ guildId: 1 }, { unique: true });
db.guilds.createIndex({ name: 1 }, { unique: true });
db.guilds.createIndex({ xp: -1, memberCount: -1 });
db.guilds.createIndex({ name: 'text' });

// Game servers
db.createCollection('gameservers');
db.gameservers.createIndex({ serverId: 1 }, { unique: true });
db.gameservers.createIndex({ worldId: 1, isOnline: 1 });
db.gameservers.createIndex({ lastPing: 1 }, { expireAfterSeconds: 60 });

// Shop items
db.createCollection('shopitems');
db.shopitems.createIndex({ shopItemId: 1 }, { unique: true });
db.shopitems.createIndex({ isActive: 1 });

// ─── Seed: Default Shop Items ─────────────────────────────────────────────────

db.shopitems.insertMany([
  {
    shopItemId:   'shop_wing_angel',
    gameItemId:   2001,
    name:         'Angel Wings',
    description:  'Soar above the world with divine wings.',
    type:         'wearable',
    pricePremium: 800,
    priceUSD:     9.99,
    isLimited:    false,
    isActive:     true,
    isFeatured:   true,
    soldCount:    0,
  },
  {
    shopItemId:   'shop_hat_wizard',
    gameItemId:   2002,
    name:         'Wizard Hat',
    description:  'Channel your inner mage.',
    type:         'wearable',
    pricePremium: 300,
    priceUSD:     null,
    isLimited:    false,
    isActive:     true,
    isFeatured:   false,
    soldCount:    0,
  },
  {
    shopItemId:   'shop_emote_dance',
    gameItemId:   3001,
    name:         'Dance Emote',
    description:  'Show off your moves!',
    type:         'emote',
    pricePremium: 200,
    priceUSD:     null,
    isLimited:    false,
    isActive:     true,
    isFeatured:   false,
    soldCount:    0,
  },
]);

// ─── Seed: Hub World ──────────────────────────────────────────────────────────

const hubExists = db.worlds.findOne({ worldId: 'HUB' });
if (!hubExists) {
  db.worlds.insertOne({
    worldId:     'HUB',
    name:        'HUB',
    ownerId:     'system',
    width:       30000,
    height:      360,
    spawnX:      15000,
    spawnY:      200,
    isLocked:    true,
    isPrivate:   false,
    description: 'The main hub world. Welcome to BlockVerse!',
    visitCount:  0,
    likeCount:   0,
    playerCount: 0,
    banList:     [],
    permissions: [],
    farmingEntries: [],
    createdAt:   new Date(),
    lastVisited: new Date(),
  });
  print('Hub world created.');
}

print('BlockVerse MongoDB init complete.');
