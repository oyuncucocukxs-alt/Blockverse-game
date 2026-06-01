'use strict';

const { ChatLog } = require('../models');
const { verifyAccessToken } = require('../middleware/auth');

/**
 * Socket.IO chat handler
 * Handles global chat relay, online presence, and whispers.
 * World chat is handled directly by the dedicated game server (Mirror).
 */
module.exports = function (io) {
  const chatNamespace = io.of('/chat');

  chatNamespace.use(async (socket, next) => {
    const token = socket.handshake.auth?.token;
    if (!token) return next(new Error('Authentication required'));

    try {
      const payload = verifyAccessToken(token);
      socket.playerId  = payload.playerId;
      socket.username  = payload.username;
      next();
    } catch {
      next(new Error('Invalid token'));
    }
  });

  chatNamespace.on('connection', async (socket) => {
    const { playerId, username } = socket;
    console.log(`[Chat] ${username} connected`);

    // Track online presence
    await global.redis.set(`online:${playerId}`, socket.id, 'EX', 120);

    // Join personal room for DMs
    socket.join(`player:${playerId}`);

    // Emit recent global messages
    const recent = await ChatLog.find({ channel: 1 }) // Global channel
      .sort({ timestamp: -1 })
      .limit(30)
      .lean();
    socket.emit('chat_history', recent.reverse());

    // ── Global Chat ─────────────────────────────────

    socket.on('global_chat', async (data) => {
      try {
        const text = sanitize(data?.text, 200);
        if (!text) return;

        // Rate limit: max 1 message per second
        const rateLimitKey = `chat_rate:${playerId}`;
        const requests = await global.redis.incr(rateLimitKey);
        if (requests === 1) await global.redis.expire(rateLimitKey, 1);
        if (requests > 1) {
          socket.emit('error', { message: 'Please slow down' });
          return;
        }

        const msg = {
          senderId: playerId,
          senderName: username,
          text,
          channel: 1,
          timestamp: new Date(),
        };

        // Broadcast to all connected global chat clients
        chatNamespace.emit('global_message', msg);

        // Persist
        ChatLog.create(msg).catch(console.error);
      } catch (err) {
        console.error('[Chat] global_chat error', err);
      }
    });

    // ── Private Message (Whisper) ─────────────────────

    socket.on('whisper', async (data) => {
      try {
        const { targetPlayerId, text } = data;
        if (!targetPlayerId || !text) return;
        const clean = sanitize(text, 200);
        if (!clean) return;

        // Find target's socket
        const targetSocketId = await global.redis.get(`online:${targetPlayerId}`);
        if (!targetSocketId) {
          socket.emit('whisper_error', { message: 'Player is offline' });
          return;
        }

        const msg = {
          senderId: playerId,
          senderName: username,
          targetPlayerId,
          text: clean,
          channel: 2, // Whisper
          timestamp: new Date(),
        };

        chatNamespace.to(`player:${targetPlayerId}`).emit('whisper_received', msg);
        socket.emit('whisper_sent', msg);
      } catch (err) {
        console.error('[Chat] whisper error', err);
      }
    });

    // ── Disconnect ────────────────────────────────────

    socket.on('disconnect', async () => {
      console.log(`[Chat] ${username} disconnected`);
      await global.redis.del(`online:${playerId}`);
      chatNamespace.emit('player_offline', { playerId, username });
    });

    // ── Heartbeat to keep online status fresh ─────────

    socket.on('heartbeat', async (data) => {
      await global.redis.set(`online:${playerId}`, data?.worldId || '1', 'EX', 120);
    });

    // Announce join
    chatNamespace.emit('player_online', { playerId, username });
  });
};

function sanitize(text, maxLen) {
  if (!text || typeof text !== 'string') return '';
  const trimmed = text.trim().slice(0, maxLen);
  // Remove HTML and control characters
  return trimmed.replace(/<[^>]*>/g, '').replace(/[\x00-\x1F\x7F]/g, '');
}
