'use strict';

const mongoose = require('mongoose');
const { Schema } = mongoose;

// ─────────────────────────────────────────────
// Player
// ─────────────────────────────────────────────

const InventorySlotSchema = new Schema({
  itemId:   { type: Number, default: 0 },
  count:    { type: Number, default: 0 },
  metadata: { type: String, default: null },
}, { _id: false });

const AppearanceSchema = new Schema({
  hatItemId:   { type: Number, default: 0 },
  shirtItemId: { type: Number, default: 0 },
  pantsItemId: { type: Number, default: 0 },
  shoeItemId:  { type: Number, default: 0 },
  handItemId:  { type: Number, default: 0 },
  backItemId:  { type: Number, default: 0 },
  skinColor:   { type: Number, default: 0 },
  eyeColor:    { type: Number, default: 0 },
  hairColor:   { type: Number, default: 0 },
  hairStyle:   { type: Number, default: 0 },
}, { _id: false });

const PlayerSchema = new Schema({
  playerId:     { type: String, required: true, unique: true, index: true },
  firebaseUid:  { type: String, required: true, unique: true, index: true },
  username:     { type: String, required: true, unique: true, trim: true, minlength: 3, maxlength: 20 },
  email:        { type: String, required: true, lowercase: true },

  // Stats
  level:        { type: Number, default: 1 },
  xp:           { type: Number, default: 0 },
  playtimeSeconds: { type: Number, default: 0 },

  // Economy
  gems:         { type: Number, default: 100 },
  premiumCurrency: { type: Number, default: 0 },

  // Inventory (36 slots)
  inventory:    { type: [InventorySlotSchema], default: () => Array(36).fill({ itemId: 0, count: 0 }) },

  // Equipment
  equipment: {
    hatItemId:   { type: Number, default: 0 },
    shirtItemId: { type: Number, default: 0 },
    pantsItemId: { type: Number, default: 0 },
    shoeItemId:  { type: Number, default: 0 },
    handItemId:  { type: Number, default: 0 },
    backItemId:  { type: Number, default: 0 },
  },

  appearance:   { type: AppearanceSchema, default: () => ({}) },

  // Social
  friends:      [{ type: String }],
  blockedPlayers: [{ type: String }],
  guildId:      { type: String, default: null },

  // World
  ownedWorlds:  [{ type: String }],
  lastWorldId:  { type: String, default: 'hub' },
  lastPosition: { x: { type: Number, default: 0 }, y: { type: Number, default: 0 } },

  // Moderation
  isBanned:     { type: Boolean, default: false },
  isMuted:      { type: Boolean, default: false },
  banExpiry:    { type: Date, default: null },
  muteExpiry:   { type: Date, default: null },
  banReason:    { type: String, default: '' },
  isAdmin:      { type: Boolean, default: false },
  isModerator:  { type: Boolean, default: false },

  // Monetization
  isPremium:    { type: Boolean, default: false },
  premiumExpiry: { type: Date, default: null },
  purchaseHistory: [{ itemId: String, amount: Number, date: Date }],

  // Timestamps
  createdAt:    { type: Date, default: Date.now },
  lastLoginAt:  { type: Date, default: Date.now },
}, {
  timestamps: true,
  versionKey: '__v',
});

PlayerSchema.index({ username: 'text' });
PlayerSchema.index({ gems: -1 });
PlayerSchema.index({ level: -1 });

// ─────────────────────────────────────────────
// World
// ─────────────────────────────────────────────

const TileDataSchema = new Schema({
  itemId:    { type: Number, default: 0 },
  health:    { type: Number, default: 0 },
  placedBy:  { type: String, default: null },
  placedAt:  { type: Number, default: 0 }, // unix timestamp
}, { _id: false });

const FarmingTileSchema = new Schema({
  seedItemId: { type: Number, default: 0 },
  growthStage: { type: Number, default: 0 },
  plantedAt:  { type: Number, default: 0 },
  plantedBy:  { type: String, default: null },
}, { _id: false });

const ChunkSchema = new Schema({
  worldId:   { type: String, required: true, index: true },
  chunkX:    { type: Number, required: true },
  chunkY:    { type: Number, required: true },
  foreground: { type: Buffer, required: true }, // binary-encoded tile layer
  background: { type: Buffer, required: true },
  farming:   { type: Buffer, default: null },
  isDirty:   { type: Boolean, default: false },
  lastSaved: { type: Date, default: Date.now },
}, { timestamps: true });

ChunkSchema.index({ worldId: 1, chunkX: 1, chunkY: 1 }, { unique: true });

const WorldPermissionSchema = new Schema({
  playerId:   { type: String, required: true },
  level:      { type: Number, default: 1 }, // 1=build, 2=admin
}, { _id: false });

const WorldSchema = new Schema({
  worldId:    { type: String, required: true, unique: true, index: true },
  name:       { type: String, required: true, trim: true, minlength: 1, maxlength: 24 },
  ownerId:    { type: String, required: true, index: true },
  width:      { type: Number, required: true },
  height:     { type: Number, required: true },
  spawnX:     { type: Number, default: 0 },
  spawnY:     { type: Number, default: 0 },

  // Access
  isLocked:   { type: Boolean, default: false },
  isPrivate:  { type: Boolean, default: false },
  banList:    [{ type: String }],
  permissions: [WorldPermissionSchema],

  // Metadata
  description: { type: String, default: '', maxlength: 200 },
  tags:        [{ type: String }],
  visitCount:  { type: Number, default: 0 },
  likeCount:   { type: Number, default: 0 },

  // Farming data (lightweight, tiles store heavy data in chunks)
  farmingEntries: [{
    tileX: Number, tileY: Number,
    seedItemId: Number, plantedBy: String,
    plantedAt: Number, growthStage: Number,
    totalGrowthTime: Number,
  }],

  // Server assignment
  serverId:   { type: String, default: null },
  playerCount: { type: Number, default: 0 },

  createdAt:  { type: Date, default: Date.now },
  lastVisited: { type: Date, default: Date.now },
}, { timestamps: true });

WorldSchema.index({ name: 'text' });
WorldSchema.index({ visitCount: -1 });
WorldSchema.index({ likeCount: -1 });

// ─────────────────────────────────────────────
// Chat Log
// ─────────────────────────────────────────────

const ChatLogSchema = new Schema({
  worldId:    { type: String, index: true },
  senderId:   { type: String, required: true },
  senderName: { type: String, required: true },
  text:       { type: String, required: true },
  channel:    { type: Number, default: 0 }, // ChatChannel enum
  timestamp:  { type: Date, default: Date.now, index: true },
}, { timestamps: false });

ChatLogSchema.index({ worldId: 1, timestamp: -1 });
// Auto-expire chat logs after 30 days
ChatLogSchema.index({ timestamp: 1 }, { expireAfterSeconds: 30 * 24 * 3600 });

// ─────────────────────────────────────────────
// Trade Log
// ─────────────────────────────────────────────

const TradeLogSchema = new Schema({
  tradeId:          { type: String, required: true, unique: true },
  initiatorId:      { type: String, required: true },
  targetId:         { type: String, required: true },
  initiatorItems:   [{ itemId: Number, count: Number }],
  targetItems:      [{ itemId: Number, count: Number }],
  initiatorCurrency: { type: Number, default: 0 },
  targetCurrency:   { type: Number, default: 0 },
  worldId:          { type: String },
  completedAt:      { type: Date, default: Date.now },
}, { timestamps: false });

TradeLogSchema.index({ initiatorId: 1, completedAt: -1 });
TradeLogSchema.index({ targetId:    1, completedAt: -1 });

// ─────────────────────────────────────────────
// AntiCheat Log
// ─────────────────────────────────────────────

const AntiCheatLogSchema = new Schema({
  playerId:  { type: String, required: true, index: true },
  violation: { type: String, required: true },
  details:   { type: String },
  serverId:  { type: String },
  timestamp: { type: Date, default: Date.now },
});

AntiCheatLogSchema.index({ playerId: 1, timestamp: -1 });
AntiCheatLogSchema.index({ timestamp: 1 }, { expireAfterSeconds: 90 * 24 * 3600 }); // 90 days

// ─────────────────────────────────────────────
// Marketplace Listing
// ─────────────────────────────────────────────

const MarketListingSchema = new Schema({
  listingId:  { type: String, required: true, unique: true },
  sellerId:   { type: String, required: true, index: true },
  sellerName: { type: String, required: true },
  itemId:     { type: Number, required: true, index: true },
  itemCount:  { type: Number, required: true, min: 1 },
  priceEach:  { type: Number, required: true, min: 1 },
  currency:   { type: String, default: 'gem' },
  isActive:   { type: Boolean, default: true, index: true },
  expiresAt:  { type: Date, required: true },
  createdAt:  { type: Date, default: Date.now },
});

MarketListingSchema.index({ itemId: 1, isActive: 1, priceEach: 1 });
MarketListingSchema.index({ expiresAt: 1 }, { expireAfterSeconds: 0 }); // Auto-expire

// ─────────────────────────────────────────────
// Guild
// ─────────────────────────────────────────────

const GuildSchema = new Schema({
  guildId:     { type: String, required: true, unique: true },
  name:        { type: String, required: true, unique: true, minlength: 3, maxlength: 20 },
  ownerId:     { type: String, required: true },
  description: { type: String, default: '', maxlength: 200 },
  members:     [{ playerId: String, role: { type: String, enum: ['member', 'officer', 'owner'], default: 'member' }, joinedAt: Date }],
  memberCount: { type: Number, default: 1 },
  maxMembers:  { type: Number, default: 50 },
  xp:          { type: Number, default: 0 },
  level:       { type: Number, default: 1 },
  createdAt:   { type: Date, default: Date.now },
});

GuildSchema.index({ name: 'text' });

// ─────────────────────────────────────────────
// Shop / IAP
// ─────────────────────────────────────────────

const ShopItemSchema = new Schema({
  shopItemId:    { type: String, required: true, unique: true },
  gameItemId:    { type: Number, required: true },
  name:          { type: String, required: true },
  description:   { type: String },
  priceUSD:      { type: Number },
  pricePremium:  { type: Number }, // Premium currency price
  isLimited:     { type: Boolean, default: false },
  limitedCount:  { type: Number, default: 0 },
  soldCount:     { type: Number, default: 0 },
  isActive:      { type: Boolean, default: true },
  expiresAt:     { type: Date, default: null },
});

// ─────────────────────────────────────────────
// Server Registry (game servers for matchmaking)
// ─────────────────────────────────────────────

const GameServerSchema = new Schema({
  serverId:    { type: String, required: true, unique: true },
  address:     { type: String, required: true },
  port:        { type: Number, required: true },
  region:      { type: String, required: true },
  worldId:     { type: String, default: null, index: true },
  playerCount: { type: Number, default: 0 },
  maxPlayers:  { type: Number, default: 50 },
  isOnline:    { type: Boolean, default: true },
  lastPing:    { type: Date, default: Date.now },
  startedAt:   { type: Date, default: Date.now },
});

GameServerSchema.index({ worldId: 1, isOnline: 1 });
GameServerSchema.index({ lastPing: 1 }, { expireAfterSeconds: 60 }); // Servers auto-expire if no ping

// ─────────────────────────────────────────────
// Compile & Export Models
// ─────────────────────────────────────────────

module.exports = {
  Player:       mongoose.model('Player',       PlayerSchema),
  Chunk:        mongoose.model('Chunk',        ChunkSchema),
  World:        mongoose.model('World',        WorldSchema),
  ChatLog:      mongoose.model('ChatLog',      ChatLogSchema),
  TradeLog:     mongoose.model('TradeLog',     TradeLogSchema),
  AntiCheatLog: mongoose.model('AntiCheatLog', AntiCheatLogSchema),
  MarketListing: mongoose.model('MarketListing', MarketListingSchema),
  Guild:        mongoose.model('Guild',        GuildSchema),
  ShopItem:     mongoose.model('ShopItem',     ShopItemSchema),
  GameServer:   mongoose.model('GameServer',   GameServerSchema),
};
