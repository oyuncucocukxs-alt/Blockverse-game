// ─── presenceSocket.js ────────────────────────────────────────────────────────
'use strict';

const { verifyAccessToken } = require('../middleware/auth');

/**
 * Socket.IO presence system.
 * Tracks which world each player is in, emits friend online/offline events.
 */
module.exports = function presenceSocket(io) {
  const ns = io.of('/presence');

  ns.use(async (socket, next) => {
    try {
      const token = socket.handshake.auth?.token;
      if (!token) return next(new Error('Auth required'));
      const payload = verifyAccessToken(token);
      socket.playerId  = payload.playerId;
      socket.username  = payload.username;
      next();
    } catch {
      next(new Error('Invalid token'));
    }
  });

  ns.on('connection', async socket => {
    const { playerId, username } = socket;

    // Store presence: playerId → current worldId
    await global.redis.set(`presence:${playerId}`, 'lobby', 'EX', 120);
    socket.join(`user:${playerId}`);

    // Notify friends this player is online
    await notifyFriends(playerId, username, 'online');

    socket.on('enter_world', async ({ worldId }) => {
      await global.redis.set(`presence:${playerId}`, worldId, 'EX', 120);
      await notifyFriends(playerId, username, 'in_world', { worldId });
    });

    socket.on('leave_world', async () => {
      await global.redis.set(`presence:${playerId}`, 'lobby', 'EX', 120);
      await notifyFriends(playerId, username, 'online');
    });

    socket.on('heartbeat', async ({ worldId }) => {
      await global.redis.set(`presence:${playerId}`, worldId || 'lobby', 'EX', 120);
    });

    socket.on('get_friend_presence', async ({ friendIds }, cb) => {
      const results = {};
      if (!Array.isArray(friendIds)) return cb({});
      const pipeline = global.redis.pipeline();
      friendIds.forEach(id => pipeline.get(`presence:${id}`));
      const responses = await pipeline.exec();
      friendIds.forEach((id, i) => {
        results[id] = responses[i][1] || 'offline';
      });
      if (cb) cb(results);
    });

    socket.on('disconnect', async () => {
      await global.redis.del(`presence:${playerId}`);
      await notifyFriends(playerId, username, 'offline');
    });
  });

  async function notifyFriends(playerId, username, status, extra = {}) {
    try {
      const { Player } = require('../models');
      const player = await Player.findOne({ playerId }).select('friends').lean();
      if (!player?.friends?.length) return;

      const payload = { playerId, username, status, ...extra };
      for (const friendId of player.friends) {
        ns.to(`user:${friendId}`).emit('friend_presence', payload);
      }
    } catch (err) {
      console.error('[Presence] notifyFriends error', err);
    }
  }
};
