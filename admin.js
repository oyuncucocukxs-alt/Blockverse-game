'use strict';

const express = require('express');
const router  = express.Router();
const { Player, World, ChatLog, AntiCheatLog, GameServer } = require('../models');
const { requireAdmin } = require('../middleware/auth');

// All admin routes require admin auth
router.use(requireAdmin);

// ─────────────────────────────────────────────
// Player Management
// ─────────────────────────────────────────────

// GET /v1/admin/players?search=&page=
router.get('/players', async (req, res, next) => {
  try {
    const { search, page = 1, limit = 25, filter } = req.query;
    const skip = (parseInt(page) - 1) * parseInt(limit);

    const query = {};
    if (search) {
      const regex = new RegExp(search, 'i');
      query.$or = [{ username: regex }, { playerId: regex }, { email: regex }];
    }
    if (filter === 'banned')  query.isBanned = true;
    if (filter === 'muted')   query.isMuted  = true;
    if (filter === 'premium') query.isPremium = true;
    if (filter === 'admin')   query.isAdmin  = true;

    const [players, total] = await Promise.all([
      Player.find(query)
        .select('playerId username email level gems isBanned isMuted isAdmin createdAt lastLoginAt')
        .sort({ createdAt: -1 })
        .skip(skip)
        .limit(parseInt(limit))
        .lean(),
      Player.countDocuments(query),
    ]);

    return res.json({ players, total, page: parseInt(page) });
  } catch (err) { next(err); }
});

// GET /v1/admin/players/:playerId/full
router.get('/players/:playerId/full', async (req, res, next) => {
  try {
    const player = await Player.findOne({ playerId: req.params.playerId }).lean();
    if (!player) return res.status(404).json({ error: 'Player not found' });

    const anticheat = await AntiCheatLog.find({ playerId: req.params.playerId })
      .sort({ timestamp: -1 }).limit(20).lean();

    return res.json({ player, antiCheatHistory: anticheat });
  } catch (err) { next(err); }
});

// POST /v1/admin/players/:playerId/ban
router.post('/players/:playerId/ban', async (req, res, next) => {
  try {
    const { reason, durationHours } = req.body;
    if (!reason) return res.status(400).json({ error: 'reason required' });

    const banExpiry = durationHours
      ? new Date(Date.now() + parseInt(durationHours) * 3600 * 1000)
      : null; // null = permanent

    await Player.updateOne(
      { playerId: req.params.playerId },
      { $set: { isBanned: true, banExpiry, banReason: reason } }
    );

    // Force disconnect from game servers
    global.redis.publish('admin:ban', JSON.stringify({ playerId: req.params.playerId, reason }));

    return res.json({ message: 'Player banned', banExpiry });
  } catch (err) { next(err); }
});

// POST /v1/admin/players/:playerId/unban
router.post('/players/:playerId/unban', async (req, res, next) => {
  try {
    await Player.updateOne(
      { playerId: req.params.playerId },
      { $set: { isBanned: false, banExpiry: null, banReason: '' } }
    );
    return res.json({ message: 'Player unbanned' });
  } catch (err) { next(err); }
});

// POST /v1/admin/players/:playerId/mute
router.post('/players/:playerId/mute', async (req, res, next) => {
  try {
    const { durationMinutes = 60 } = req.body;
    const muteExpiry = new Date(Date.now() + parseInt(durationMinutes) * 60 * 1000);

    await Player.updateOne(
      { playerId: req.params.playerId },
      { $set: { isMuted: true, muteExpiry } }
    );

    global.redis.publish('admin:mute', JSON.stringify({
      playerId: req.params.playerId, muteExpiry
    }));

    return res.json({ message: 'Player muted', muteExpiry });
  } catch (err) { next(err); }
});

// POST /v1/admin/players/:playerId/unmute
router.post('/players/:playerId/unmute', async (req, res, next) => {
  try {
    await Player.updateOne(
      { playerId: req.params.playerId },
      { $set: { isMuted: false, muteExpiry: null } }
    );
    return res.json({ message: 'Player unmuted' });
  } catch (err) { next(err); }
});

// POST /v1/admin/players/:playerId/kick
router.post('/players/:playerId/kick', async (req, res, next) => {
  try {
    const { reason = 'Kicked by admin' } = req.body;
    global.redis.publish('admin:kick', JSON.stringify({
      playerId: req.params.playerId, reason
    }));
    return res.json({ message: 'Kick signal sent' });
  } catch (err) { next(err); }
});

// POST /v1/admin/players/:playerId/give-item
router.post('/players/:playerId/give-item', async (req, res, next) => {
  try {
    const { itemId, count = 1 } = req.body;
    if (!itemId) return res.status(400).json({ error: 'itemId required' });

    global.redis.publish('admin:give_item', JSON.stringify({
      playerId: req.params.playerId, itemId: parseInt(itemId), count: parseInt(count)
    }));

    return res.json({ message: 'Item grant signal sent' });
  } catch (err) { next(err); }
});

// POST /v1/admin/players/:playerId/set-admin
router.post('/players/:playerId/set-admin', async (req, res, next) => {
  try {
    const { isAdmin } = req.body;
    await Player.updateOne({ playerId: req.params.playerId }, { $set: { isAdmin: !!isAdmin } });
    return res.json({ message: 'Admin status updated' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// World Management
// ─────────────────────────────────────────────

// DELETE /v1/admin/worlds/:worldId
router.delete('/worlds/:worldId', async (req, res, next) => {
  try {
    const { reason } = req.body;
    await World.updateOne(
      { worldId: req.params.worldId },
      { $set: { isPrivate: true, name: `[DELETED]${req.params.worldId}` } }
    );
    global.redis.publish('admin:world_close', JSON.stringify({
      worldId: req.params.worldId, reason: reason || 'World removed by admin'
    }));
    return res.json({ message: 'World disabled' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// Chat Moderation
// ─────────────────────────────────────────────

// GET /v1/admin/chat?worldId=&playerId=&page=
router.get('/chat', async (req, res, next) => {
  try {
    const { worldId, playerId, page = 1 } = req.query;
    const skip = (parseInt(page) - 1) * 50;

    const filter = {};
    if (worldId)  filter.worldId  = worldId;
    if (playerId) filter.senderId = playerId;

    const logs = await ChatLog.find(filter)
      .sort({ timestamp: -1 })
      .skip(skip)
      .limit(50)
      .lean();

    return res.json(logs);
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// Analytics Dashboard
// ─────────────────────────────────────────────

// GET /v1/admin/analytics
router.get('/analytics', async (_req, res, next) => {
  try {
    const now   = new Date();
    const day   = new Date(now.getTime() - 24 * 3600 * 1000);
    const week  = new Date(now.getTime() - 7  * 24 * 3600 * 1000);
    const month = new Date(now.getTime() - 30 * 24 * 3600 * 1000);

    const [
      totalPlayers,
      dau, wau, mau,
      totalWorlds,
      activeServers,
      anticheatViolations,
      newPlayersToday,
    ] = await Promise.all([
      Player.countDocuments(),
      Player.countDocuments({ lastLoginAt: { $gte: day   } }),
      Player.countDocuments({ lastLoginAt: { $gte: week  } }),
      Player.countDocuments({ lastLoginAt: { $gte: month } }),
      World.countDocuments(),
      GameServer.countDocuments({ isOnline: true }),
      AntiCheatLog.countDocuments({ timestamp: { $gte: day } }),
      Player.countDocuments({ createdAt: { $gte: day } }),
    ]);

    // Online players (from Redis)
    const onlineKeys = await global.redis.keys('online:*');
    const onlinePlayers = onlineKeys.length;

    return res.json({
      players: { total: totalPlayers, dau, wau, mau, online: onlinePlayers, newToday: newPlayersToday },
      worlds:  { total: totalWorlds, activeServers },
      security: { anticheatViolationsToday: anticheatViolations },
      timestamp: now,
    });
  } catch (err) { next(err); }
});

// GET /v1/admin/anticheat?page=
router.get('/anticheat', async (req, res, next) => {
  try {
    const { page = 1, playerId } = req.query;
    const skip = (parseInt(page) - 1) * 50;

    const filter = {};
    if (playerId) filter.playerId = playerId;

    const [logs, total] = await Promise.all([
      AntiCheatLog.find(filter).sort({ timestamp: -1 }).skip(skip).limit(50).lean(),
      AntiCheatLog.countDocuments(filter),
    ]);

    return res.json({ logs, total });
  } catch (err) { next(err); }
});

// POST /v1/admin/broadcast — Send system message to all worlds
router.post('/broadcast', async (req, res, next) => {
  try {
    const { message } = req.body;
    if (!message) return res.status(400).json({ error: 'message required' });

    global.io.emit('system_message', { text: message, timestamp: Date.now() });
    global.redis.publish('admin:broadcast', JSON.stringify({ message }));

    return res.json({ message: 'Broadcast sent' });
  } catch (err) { next(err); }
});

module.exports = router;
