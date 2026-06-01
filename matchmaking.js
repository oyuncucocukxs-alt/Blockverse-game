'use strict';

const express = require('express');
const router  = express.Router();
const { v4: uuidv4 } = require('uuid');
const { GameServer, World } = require('../models');
const { requireAuth, requireServerAuth } = require('../middleware/auth');

// ─────────────────────────────────────────────
// POST /v1/matchmaking/world/:worldId
// Client requests server address for a world
// ─────────────────────────────────────────────

router.post('/world/:worldId', requireAuth, async (req, res, next) => {
  try {
    const { worldId } = req.params;

    // Verify world exists
    const world = await World.findOne({ worldId }).lean();
    if (!world) return res.status(404).json({ error: 'World not found' });

    // Check ban
    if (world.banList.includes(req.playerId)) {
      return res.status(403).json({ error: 'You are banned from this world' });
    }

    // Check private world access
    if (world.isPrivate) {
      const hasPermission = world.ownerId === req.playerId ||
        world.permissions.some(p => p.playerId === req.playerId);
      if (!hasPermission) {
        return res.status(403).json({ error: 'This world is private' });
      }
    }

    // Try Redis cache first (currently active servers for this world)
    const cached = await global.redis.get(`server:world:${worldId}`);
    if (cached) {
      const serverInfo = JSON.parse(cached);
      // Verify server is still online
      const alive = await GameServer.findOne({ serverId: serverInfo.serverId, isOnline: true });
      if (alive && alive.playerCount < alive.maxPlayers) {
        return res.json({ address: alive.address, port: alive.port, worldId });
      }
    }

    // Find existing server hosting this world
    let server = await GameServer.findOne({
      worldId,
      isOnline: true,
      $expr: { $lt: ['$playerCount', '$maxPlayers'] }
    }).sort({ playerCount: -1 }); // prefer more populated for social experience

    if (!server) {
      // Assign world to an available server
      server = await GameServer.findOneAndUpdate(
        {
          worldId: null,
          isOnline: true,
          $expr: { $lt: ['$playerCount', '$maxPlayers'] }
        },
        { $set: { worldId } },
        { new: true, sort: { playerCount: 1 } }
      );
    }

    if (!server) {
      return res.status(503).json({
        error: 'No servers available. Please try again in a moment.',
        retryAfter: 10,
      });
    }

    // Cache server assignment
    await global.redis.set(
      `server:world:${worldId}`,
      JSON.stringify({ serverId: server.serverId }),
      'EX', 300 // 5 min cache
    );

    return res.json({
      address: server.address,
      port:    server.port,
      worldId,
      serverId: server.serverId,
    });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// POST /v1/matchmaking/register  (game server self-registers)
// ─────────────────────────────────────────────

router.post('/register', requireServerAuth, async (req, res, next) => {
  try {
    const { serverId, address, port, region, maxPlayers = 50 } = req.body;
    if (!serverId || !address || !port) {
      return res.status(400).json({ error: 'serverId, address, port required' });
    }

    await GameServer.findOneAndUpdate(
      { serverId },
      {
        $set: {
          address, port, region, maxPlayers,
          isOnline: true, lastPing: new Date(), playerCount: 0,
          worldId: null,
        }
      },
      { upsert: true }
    );

    return res.json({ ok: true, serverId });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// POST /v1/matchmaking/heartbeat  (game server keeps alive)
// ─────────────────────────────────────────────

router.post('/heartbeat', requireServerAuth, async (req, res, next) => {
  try {
    const { serverId, playerCount, worldId } = req.body;

    await GameServer.updateOne(
      { serverId },
      { $set: { playerCount, worldId, lastPing: new Date(), isOnline: true } }
    );

    // Refresh Redis cache
    if (worldId) {
      await global.redis.set(
        `server:world:${worldId}`,
        JSON.stringify({ serverId }),
        'EX', 300
      );
    }

    // Update world player count
    if (worldId) {
      await World.updateOne({ worldId }, { $set: { playerCount } });
    }

    // Check for any admin commands (kick, ban etc.) to relay
    const commands = await global.redis.lrange(`server:${serverId}:commands`, 0, -1);
    if (commands.length > 0) {
      await global.redis.del(`server:${serverId}:commands`);
      return res.json({ ok: true, commands: commands.map(c => JSON.parse(c)) });
    }

    return res.json({ ok: true, commands: [] });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// POST /v1/matchmaking/deregister  (game server shutting down)
// ─────────────────────────────────────────────

router.post('/deregister', requireServerAuth, async (req, res, next) => {
  try {
    const { serverId } = req.body;
    const server = await GameServer.findOneAndUpdate(
      { serverId },
      { $set: { isOnline: false, worldId: null, playerCount: 0 } }
    );

    if (server?.worldId) {
      await global.redis.del(`server:world:${server.worldId}`);
    }

    return res.json({ ok: true });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// GET /v1/matchmaking/servers  (admin view)
// ─────────────────────────────────────────────

router.get('/servers', require('../middleware/auth').requireAdmin, async (req, res, next) => {
  try {
    const servers = await GameServer.find({ isOnline: true }).lean();
    return res.json(servers);
  } catch (err) { next(err); }
});

module.exports = router;
