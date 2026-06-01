'use strict';

// ─── GUILD ROUTES ─────────────────────────────────────────────────────────────

const express      = require('express');
const guildRouter  = express.Router();
const { v4: uuid } = require('uuid');
const { Guild, Player } = require('../models');
const { requireAuth } = require('../middleware/auth');

// POST /v1/guild — Create guild
guildRouter.post('/', requireAuth, async (req, res, next) => {
  try {
    const { name, description = '' } = req.body;

    if (!name || name.length < 3 || name.length > 20)
      return res.status(400).json({ error: 'Guild name must be 3-20 characters' });

    if (!/^[a-zA-Z0-9 _-]+$/.test(name))
      return res.status(400).json({ error: 'Invalid characters in guild name' });

    const player = await Player.findOne({ playerId: req.playerId }).select('guildId');
    if (player?.guildId)
      return res.status(409).json({ error: 'Already in a guild. Leave first.' });

    const exists = await Guild.exists({ name: new RegExp(`^${name}$`, 'i') });
    if (exists)
      return res.status(409).json({ error: 'Guild name already taken' });

    const guildId = uuid();
    const guild = await Guild.create({
      guildId,
      name,
      description: description.slice(0, 200),
      ownerId: req.playerId,
      members: [{ playerId: req.playerId, role: 'owner', joinedAt: new Date() }],
      memberCount: 1,
    });

    await Player.updateOne({ playerId: req.playerId }, { $set: { guildId } });
    return res.status(201).json({ guildId: guild.guildId });
  } catch (err) { next(err); }
});

// GET /v1/guild/:guildId
guildRouter.get('/:guildId', requireAuth, async (req, res, next) => {
  try {
    const guild = await Guild.findOne({ guildId: req.params.guildId }).lean();
    if (!guild) return res.status(404).json({ error: 'Guild not found' });
    return res.json(guild);
  } catch (err) { next(err); }
});

// POST /v1/guild/:guildId/members — add member
guildRouter.post('/:guildId/members', async (req, res, next) => {
  try {
    const { playerId } = req.body;
    await Guild.updateOne(
      { guildId: req.params.guildId },
      {
        $push: { members: { playerId, role: 'member', joinedAt: new Date() } },
        $inc:  { memberCount: 1 }
      }
    );
    await Player.updateOne({ playerId }, { $set: { guildId: req.params.guildId } });
    return res.json({ ok: true });
  } catch (err) { next(err); }
});

// DELETE /v1/guild/:guildId/members/:playerId
guildRouter.delete('/:guildId/members/:playerId', async (req, res, next) => {
  try {
    await Guild.updateOne(
      { guildId: req.params.guildId },
      {
        $pull: { members: { playerId: req.params.playerId } },
        $inc:  { memberCount: -1 }
      }
    );
    await Player.updateOne({ playerId: req.params.playerId }, { $unset: { guildId: '' } });
    return res.json({ ok: true });
  } catch (err) { next(err); }
});

// PATCH /v1/guild/:guildId/members/:playerId
guildRouter.patch('/:guildId/members/:playerId', async (req, res, next) => {
  try {
    const { role } = req.body;
    await Guild.updateOne(
      { guildId: req.params.guildId, 'members.playerId': req.params.playerId },
      { $set: { 'members.$.role': role } }
    );
    return res.json({ ok: true });
  } catch (err) { next(err); }
});

// DELETE /v1/guild/:guildId — Disband
guildRouter.delete('/:guildId', async (req, res, next) => {
  try {
    const guild = await Guild.findOne({ guildId: req.params.guildId });
    if (!guild) return res.status(404).json({ error: 'Not found' });

    const memberIds = guild.members.map(m => m.playerId);
    await Player.updateMany({ playerId: { $in: memberIds } }, { $unset: { guildId: '' } });
    await Guild.deleteOne({ guildId: req.params.guildId });

    return res.json({ ok: true });
  } catch (err) { next(err); }
});

// GET /v1/guild?search=
guildRouter.get('/', requireAuth, async (req, res, next) => {
  try {
    const { search, page = 1 } = req.query;
    const skip = (parseInt(page) - 1) * 20;
    const filter = {};
    if (search) filter.$text = { $search: search };

    const guilds = await Guild.find(filter)
      .sort({ memberCount: -1 })
      .skip(skip).limit(20).lean();

    return res.json(guilds);
  } catch (err) { next(err); }
});

// ─── LEADERBOARD ROUTES ───────────────────────────────────────────────────────

const lbRouter = express.Router();

// GET /v1/leaderboard/gems
lbRouter.get('/gems', async (_req, res, next) => {
  try {
    const key = 'lb:gems';
    const cached = await global.redis.get(key);
    if (cached) return res.json(JSON.parse(cached));

    const top = await Player.find({ isBanned: false })
      .sort({ gems: -1 }).limit(100)
      .select('playerId username gems level appearance')
      .lean();

    await global.redis.set(key, JSON.stringify(top), 'EX', 300); // 5 min cache
    return res.json(top);
  } catch (err) { next(err); }
});

// GET /v1/leaderboard/level
lbRouter.get('/level', async (_req, res, next) => {
  try {
    const key = 'lb:level';
    const cached = await global.redis.get(key);
    if (cached) return res.json(JSON.parse(cached));

    const top = await Player.find({ isBanned: false })
      .sort({ level: -1, xp: -1 }).limit(100)
      .select('playerId username level xp appearance')
      .lean();

    await global.redis.set(key, JSON.stringify(top), 'EX', 300);
    return res.json(top);
  } catch (err) { next(err); }
});

// GET /v1/leaderboard/worlds
lbRouter.get('/worlds', async (_req, res, next) => {
  try {
    const { World } = require('../models');
    const key = 'lb:worlds';
    const cached = await global.redis.get(key);
    if (cached) return res.json(JSON.parse(cached));

    const top = await World.find({ isPrivate: false })
      .sort({ visitCount: -1 }).limit(50)
      .select('worldId name ownerId visitCount likeCount playerCount')
      .lean();

    await global.redis.set(key, JSON.stringify(top), 'EX', 300);
    return res.json(top);
  } catch (err) { next(err); }
});

// GET /v1/leaderboard/guilds
lbRouter.get('/guilds', async (_req, res, next) => {
  try {
    const key = 'lb:guilds';
    const cached = await global.redis.get(key);
    if (cached) return res.json(JSON.parse(cached));

    const top = await Guild.find()
      .sort({ xp: -1, memberCount: -1 }).limit(50)
      .select('guildId name level xp memberCount ownerId')
      .lean();

    await global.redis.set(key, JSON.stringify(top), 'EX', 600);
    return res.json(top);
  } catch (err) { next(err); }
});

// GET /v1/leaderboard/me/rank — player's own rank
lbRouter.get('/me/rank', require('../middleware/auth').requireAuth, async (req, res, next) => {
  try {
    const [gemsRank, levelRank] = await Promise.all([
      Player.countDocuments({ gems: { $gt: (await Player.findOne({ playerId: req.playerId }).select('gems').lean())?.gems ?? 0 } }),
      Player.countDocuments({ level: { $gt: (await Player.findOne({ playerId: req.playerId }).select('level').lean())?.level ?? 0 } }),
    ]);
    return res.json({ gemsRank: gemsRank + 1, levelRank: levelRank + 1 });
  } catch (err) { next(err); }
});

// ─── IAP VALIDATION ROUTE ─────────────────────────────────────────────────────

const iapRouter = express.Router();

const CRYSTAL_GRANTS = {
  'blockverse.crystals.80':    80,
  'blockverse.crystals.500':   500,
  'blockverse.crystals.1200':  1200,
  'blockverse.crystals.2800':  2800,
  'blockverse.crystals.8000':  8000,
  'blockverse.crystals.20000': 20000,
};

// POST /v1/economy/iap/validate
iapRouter.post('/validate', require('../middleware/auth').requireAuth, async (req, res, next) => {
  try {
    const { productId, receipt, platform } = req.body;

    if (!productId || !receipt)
      return res.status(400).json({ error: 'productId and receipt required' });

    const crystalsToGrant = CRYSTAL_GRANTS[productId];
    if (!crystalsToGrant)
      return res.status(400).json({ error: 'Unknown product' });

    // Idempotency key: prevent double-grant
    const idempotencyKey = `iap:${req.playerId}:${require('crypto').createHash('sha256').update(receipt).digest('hex').slice(0, 16)}`;
    const alreadyProcessed = await global.redis.get(idempotencyKey);
    if (alreadyProcessed)
      return res.json({ valid: true, grantedCrystals: 0, message: 'Already processed' });

    // In production: verify with Apple/Google receipt validation API
    // For now: trust client receipt (implement proper validation for production)
    let receiptValid = true;

    if (platform === 'ios') {
      // receiptValid = await verifyAppleReceipt(receipt, productId);
    } else if (platform === 'android') {
      // receiptValid = await verifyGoogleReceipt(receipt, productId);
    }

    if (!receiptValid)
      return res.json({ valid: false, grantedCrystals: 0 });

    // Grant crystals
    await Player.updateOne(
      { playerId: req.playerId },
      {
        $inc:  { premiumCurrency: crystalsToGrant },
        $push: { purchaseHistory: { itemId: productId, amount: crystalsToGrant, date: new Date() } }
      }
    );

    // Cache idempotency key for 30 days
    await global.redis.set(idempotencyKey, '1', 'EX', 30 * 24 * 3600);

    // Notify game server to update player's crystal display
    global.redis.publish('economy:crystal_grant', JSON.stringify({
      playerId: req.playerId,
      crystals: crystalsToGrant,
    }));

    return res.json({ valid: true, grantedCrystals: crystalsToGrant });
  } catch (err) { next(err); }
});

module.exports = { guildRouter, lbRouter, iapRouter };
