'use strict';

const express = require('express');
const router  = express.Router();
const { v4: uuidv4 } = require('uuid');
const rateLimit = require('express-rate-limit');
const { World, Chunk, Player } = require('../models');
const { requireAuth, requireServerAuth } = require('../middleware/auth');

const createWorldLimiter = rateLimit({
  windowMs: 60 * 60 * 1000, // 1 hour
  max: 5,
  keyGenerator: (req) => req.playerId,
  message: { error: 'World creation limit reached. Try again later.' },
});

// ─────────────────────────────────────────────
// GET /v1/worlds?search=&page=&limit=
// ─────────────────────────────────────────────

router.get('/', requireAuth, async (req, res, next) => {
  try {
    const { search, page = 1, limit = 20, sort = 'visitCount' } = req.query;
    const skip = (parseInt(page) - 1) * parseInt(limit);
    const cap  = Math.min(parseInt(limit), 50);

    const filter = { isPrivate: false };
    if (search) filter.$text = { $search: search };

    const sortOptions = {
      visitCount: { visitCount: -1 },
      likeCount:  { likeCount:  -1 },
      newest:     { createdAt:  -1 },
    };

    const [worlds, total] = await Promise.all([
      World.find(filter)
        .select('worldId name ownerId visitCount likeCount playerCount description isLocked')
        .sort(sortOptions[sort] || sortOptions.visitCount)
        .skip(skip)
        .limit(cap)
        .lean(),
      World.countDocuments(filter),
    ]);

    return res.json({ worlds, total, page: parseInt(page), pages: Math.ceil(total / cap) });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// GET /v1/worlds/:worldId
// ─────────────────────────────────────────────

router.get('/:worldId', requireAuth, async (req, res, next) => {
  try {
    const world = await World.findOne({ worldId: req.params.worldId }).lean();
    if (!world) return res.status(404).json({ error: 'World not found' });

    // Check ban
    if (world.banList.includes(req.playerId)) {
      return res.status(403).json({ error: 'You are banned from this world' });
    }

    // Increment visit count
    World.updateOne({ worldId: world.worldId }, { $inc: { visitCount: 1 }, lastVisited: new Date() })
      .catch(() => {});

    return res.json(world);
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// POST /v1/worlds — Create world
// ─────────────────────────────────────────────

router.post('/', requireAuth, createWorldLimiter, async (req, res, next) => {
  try {
    const { name, isLocked = false, isPrivate = false, description = '' } = req.body;

    if (!name || typeof name !== 'string') return res.status(400).json({ error: 'name required' });
    if (!/^[a-zA-Z0-9 _-]{1,24}$/.test(name)) {
      return res.status(400).json({ error: 'Invalid world name. Letters, numbers, spaces, _ - only.' });
    }

    const existing = await World.findOne({ name: name.toUpperCase() });
    if (existing) return res.status(409).json({ error: 'World name already taken' });

    const worldId = name.toUpperCase().replace(/\s+/g, '_');
    const width   = 300 * 100; // config.WorldWidth * config.ChunkWidth
    const height  = 6   * 60;

    const world = await World.create({
      worldId,
      name: name.toUpperCase(),
      ownerId: req.playerId,
      width, height,
      spawnX: Math.floor(width  / 2),
      spawnY: Math.floor(height / 2),
      isLocked, isPrivate,
      description: description.slice(0, 200),
    });

    // Add world to player's owned worlds
    await Player.updateOne({ playerId: req.playerId }, { $push: { ownedWorlds: worldId } });

    return res.status(201).json({ worldId: world.worldId, message: 'World created' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// PATCH /v1/worlds/:worldId — Update world settings
// ─────────────────────────────────────────────

router.patch('/:worldId', requireAuth, async (req, res, next) => {
  try {
    const world = await World.findOne({ worldId: req.params.worldId });
    if (!world) return res.status(404).json({ error: 'World not found' });
    if (world.ownerId !== req.playerId && !req.isAdmin) {
      return res.status(403).json({ error: 'Only the world owner can edit settings' });
    }

    const allowed = ['isLocked', 'isPrivate', 'description', 'tags'];
    allowed.forEach(key => {
      if (req.body[key] !== undefined) world[key] = req.body[key];
    });

    await world.save();
    return res.json({ message: 'World updated' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// GET /v1/worlds/:worldId/chunks/:cx/:cy
// ─────────────────────────────────────────────

router.get('/:worldId/chunks/:cx/:cy', requireAuth, async (req, res, next) => {
  try {
    const { worldId, cx, cy } = req.params;
    const chunk = await Chunk.findOne({
      worldId,
      chunkX: parseInt(cx),
      chunkY: parseInt(cy),
    }).lean();

    if (!chunk) return res.status(404).json({ error: 'Chunk not found' });

    // Return as binary buffer
    res.set('Content-Type', 'application/octet-stream');
    // Combine fore, back, farming into single binary response
    const fgLen = Buffer.alloc(4); fgLen.writeUInt32LE(chunk.foreground.length);
    const bgLen = Buffer.alloc(4); bgLen.writeUInt32LE(chunk.background.length);
    const farmLen = chunk.farming ? Buffer.alloc(4) : Buffer.alloc(4);
    if (chunk.farming) farmLen.writeUInt32LE(chunk.farming.length);

    const payload = Buffer.concat([
      fgLen, chunk.foreground,
      bgLen, chunk.background,
      farmLen, chunk.farming || Buffer.alloc(0),
    ]);
    return res.send(payload);
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// PUT /v1/worlds/:worldId/chunks/:cx/:cy
// Called by game server to persist chunk
// ─────────────────────────────────────────────

router.put('/:worldId/chunks/:cx/:cy', requireServerAuth, async (req, res, next) => {
  try {
    const { worldId, cx, cy } = req.params;
    const { foreground, background, farming } = req.body;

    if (!foreground || !background) {
      return res.status(400).json({ error: 'foreground and background required' });
    }

    await Chunk.findOneAndUpdate(
      { worldId, chunkX: parseInt(cx), chunkY: parseInt(cy) },
      {
        $set: {
          foreground: Buffer.from(foreground, 'base64'),
          background: Buffer.from(background, 'base64'),
          farming: farming ? Buffer.from(farming, 'base64') : null,
          lastSaved: new Date(),
          isDirty: false,
        }
      },
      { upsert: true }
    );

    return res.json({ ok: true });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// POST /v1/worlds/:worldId/ban
// ─────────────────────────────────────────────

router.post('/:worldId/ban', requireAuth, async (req, res, next) => {
  try {
    const { targetPlayerId, reason } = req.body;
    const world = await World.findOne({ worldId: req.params.worldId });
    if (!world) return res.status(404).json({ error: 'World not found' });

    const perm = world.permissions.find(p => p.playerId === req.playerId);
    const isWorldAdmin = world.ownerId === req.playerId ||
      (perm && perm.level >= 2) || req.isAdmin;

    if (!isWorldAdmin) return res.status(403).json({ error: 'Insufficient permissions' });
    if (world.ownerId === targetPlayerId) return res.status(400).json({ error: 'Cannot ban world owner' });

    if (!world.banList.includes(targetPlayerId)) {
      world.banList.push(targetPlayerId);
      await world.save();
    }

    // Notify game server to kick player (via Redis pub/sub)
    global.redis.publish(`world:${world.worldId}:kick`, JSON.stringify({
      targetPlayerId, reason: reason || 'Banned from world'
    }));

    return res.json({ message: 'Player banned from world' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// POST /v1/worlds/:worldId/unban
// ─────────────────────────────────────────────

router.post('/:worldId/unban', requireAuth, async (req, res, next) => {
  try {
    const { targetPlayerId } = req.body;
    const world = await World.findOne({ worldId: req.params.worldId });
    if (!world) return res.status(404).json({ error: 'World not found' });
    if (world.ownerId !== req.playerId && !req.isAdmin) {
      return res.status(403).json({ error: 'Only owner can unban' });
    }

    world.banList = world.banList.filter(id => id !== targetPlayerId);
    await world.save();
    return res.json({ message: 'Player unbanned' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// POST /v1/worlds/:worldId/permissions
// ─────────────────────────────────────────────

router.post('/:worldId/permissions', requireAuth, async (req, res, next) => {
  try {
    const { targetPlayerId, level } = req.body; // level: 0=none,1=build,2=admin
    const world = await World.findOne({ worldId: req.params.worldId });
    if (!world) return res.status(404).json({ error: 'World not found' });
    if (world.ownerId !== req.playerId && !req.isAdmin) {
      return res.status(403).json({ error: 'Only world owner can set permissions' });
    }

    const existing = world.permissions.find(p => p.playerId === targetPlayerId);
    if (existing) {
      existing.level = level;
    } else {
      world.permissions.push({ playerId: targetPlayerId, level });
    }

    await world.save();

    // Notify game server
    global.redis.publish(`world:${world.worldId}:permission`, JSON.stringify({
      targetPlayerId, level
    }));

    return res.json({ message: 'Permission updated' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// POST /v1/worlds/:worldId/like
// ─────────────────────────────────────────────

router.post('/:worldId/like', requireAuth, async (req, res, next) => {
  try {
    const likeKey = `like:${req.params.worldId}:${req.playerId}`;
    const alreadyLiked = await global.redis.get(likeKey);

    if (alreadyLiked) return res.status(409).json({ error: 'Already liked' });

    await World.updateOne({ worldId: req.params.worldId }, { $inc: { likeCount: 1 } });
    await global.redis.set(likeKey, '1', 'EX', 86400 * 30); // Cache for 30 days

    return res.json({ message: 'Liked' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// GET /v1/worlds/my/owned
// ─────────────────────────────────────────────

router.get('/my/owned', requireAuth, async (req, res, next) => {
  try {
    const worlds = await World.find({ ownerId: req.playerId })
      .select('worldId name visitCount likeCount playerCount isLocked isPrivate createdAt')
      .lean();
    return res.json(worlds);
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// PUT /v1/worlds/:worldId/farming  (called by game server)
// ─────────────────────────────────────────────

router.put('/:worldId/farming', requireServerAuth, async (req, res, next) => {
  try {
    const { farmingEntries } = req.body;
    await World.updateOne(
      { worldId: req.params.worldId },
      { $set: { farmingEntries } }
    );
    return res.json({ ok: true });
  } catch (err) { next(err); }
});

module.exports = router;
