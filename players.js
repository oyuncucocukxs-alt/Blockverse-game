'use strict';

const express = require('express');
const router  = express.Router();
const { Player, TradeLog, AntiCheatLog } = require('../models');
const { requireAuth, requireServerAuth } = require('../middleware/auth');

// ─────────────────────────────────────────────
// GET /v1/players/:playerId
// ─────────────────────────────────────────────

router.get('/:playerId', requireAuth, async (req, res, next) => {
  try {
    const player = await Player.findOne({ playerId: req.params.playerId })
      .select('-firebaseUid -email -purchaseHistory')
      .lean();
    if (!player) return res.status(404).json({ error: 'Player not found' });

    // Only return full data to self or admins
    if (req.params.playerId !== req.playerId && !req.isAdmin) {
      return res.json(publicProfile(player));
    }
    return res.json(player);
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// GET /v1/players/:playerId/inventory
// Only accessible by the player themselves or server
// ─────────────────────────────────────────────

router.get('/:playerId/inventory', requireAuth, async (req, res, next) => {
  try {
    if (req.params.playerId !== req.playerId && !req.isAdmin) {
      return res.status(403).json({ error: 'Cannot view other inventory' });
    }

    const player = await Player.findOne({ playerId: req.params.playerId })
      .select('inventory equipment gems premiumCurrency')
      .lean();

    if (!player) return res.status(404).json({ error: 'Player not found' });
    return res.json({
      inventory: player.inventory,
      equipment: player.equipment,
      gems: player.gems,
      premiumCurrency: player.premiumCurrency,
    });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// PUT /v1/players/:playerId/session  (game server → save player state)
// ─────────────────────────────────────────────

router.put('/:playerId/session', requireServerAuth, async (req, res, next) => {
  try {
    const {
      inventory, equipment, appearance, gems,
      lastWorldId, lastPosition, playtimeSeconds,
      level, xp,
    } = req.body;

    const updateFields = {};
    if (inventory)       updateFields.inventory       = inventory;
    if (equipment)       updateFields.equipment       = equipment;
    if (appearance)      updateFields.appearance      = appearance;
    if (gems !== undefined) updateFields.gems         = gems;
    if (lastWorldId)     updateFields.lastWorldId     = lastWorldId;
    if (lastPosition)    updateFields.lastPosition    = lastPosition;
    if (playtimeSeconds) updateFields.$inc            = { playtimeSeconds };
    if (level !== undefined) updateFields.level       = level;
    if (xp !== undefined) updateFields.xp             = xp;

    await Player.updateOne({ playerId: req.params.playerId }, { $set: updateFields });
    return res.json({ ok: true });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// PATCH /v1/players/me/appearance
// ─────────────────────────────────────────────

router.patch('/me/appearance', requireAuth, async (req, res, next) => {
  try {
    const allowed = ['hatItemId','shirtItemId','pantsItemId','shoeItemId',
                     'handItemId','backItemId','skinColor','eyeColor','hairColor','hairStyle'];
    const update = {};
    allowed.forEach(k => {
      if (req.body[k] !== undefined) update[`appearance.${k}`] = req.body[k];
    });

    await Player.updateOne({ playerId: req.playerId }, { $set: update });

    // Notify game server of appearance change via Redis
    global.redis.publish(`player:${req.playerId}:appearance`, JSON.stringify(update));

    return res.json({ message: 'Appearance updated' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// Friends system
// ─────────────────────────────────────────────

// GET /v1/players/me/friends
router.get('/me/friends', requireAuth, async (req, res, next) => {
  try {
    const player = await Player.findOne({ playerId: req.playerId })
      .select('friends').lean();
    if (!player) return res.status(404).json({ error: 'Player not found' });

    const friends = await Player.find({ playerId: { $in: player.friends } })
      .select('playerId username level appearance lastLoginAt')
      .lean();

    // Enrich with online status from Redis
    const onlineStatuses = await Promise.all(
      friends.map(f => global.redis.get(`online:${f.playerId}`))
    );

    const enriched = friends.map((f, i) => ({
      ...publicProfile(f),
      isOnline: !!onlineStatuses[i],
      currentWorld: onlineStatuses[i] || null,
    }));

    return res.json(enriched);
  } catch (err) { next(err); }
});

// POST /v1/players/me/friends/:targetId — send/accept friend request
router.post('/me/friends/:targetId', requireAuth, async (req, res, next) => {
  try {
    const { targetId } = req.params;
    if (targetId === req.playerId) return res.status(400).json({ error: 'Cannot friend yourself' });

    const [me, target] = await Promise.all([
      Player.findOne({ playerId: req.playerId }).select('friends'),
      Player.findOne({ playerId: targetId }).select('playerId username friends'),
    ]);

    if (!target) return res.status(404).json({ error: 'Player not found' });
    if (me.friends.includes(targetId)) return res.status(409).json({ error: 'Already friends' });

    // Mutual friend add
    await Promise.all([
      Player.updateOne({ playerId: req.playerId }, { $addToSet: { friends: targetId } }),
      Player.updateOne({ playerId: targetId },     { $addToSet: { friends: req.playerId } }),
    ]);

    // Notify target via Socket.IO if online
    global.io.to(`player:${targetId}`).emit('friendAdded', {
      playerId: req.playerId, username: me.username
    });

    return res.json({ message: 'Friend added' });
  } catch (err) { next(err); }
});

// DELETE /v1/players/me/friends/:targetId
router.delete('/me/friends/:targetId', requireAuth, async (req, res, next) => {
  try {
    const { targetId } = req.params;
    await Promise.all([
      Player.updateOne({ playerId: req.playerId }, { $pull: { friends: targetId } }),
      Player.updateOne({ playerId: targetId },     { $pull: { friends: req.playerId } }),
    ]);
    return res.json({ message: 'Friend removed' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// GET /v1/players/:playerId/trades
// ─────────────────────────────────────────────

router.get('/:playerId/trades', requireAuth, async (req, res, next) => {
  try {
    if (req.params.playerId !== req.playerId && !req.isAdmin) {
      return res.status(403).json({ error: 'Forbidden' });
    }

    const trades = await TradeLog.find({
      $or: [{ initiatorId: req.params.playerId }, { targetId: req.params.playerId }]
    })
      .sort({ completedAt: -1 })
      .limit(50)
      .lean();

    return res.json(trades);
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// POST /v1/players/anticheat/log  (game server only)
// ─────────────────────────────────────────────

router.post('/anticheat/log', requireServerAuth, async (req, res, next) => {
  try {
    const { playerId, violation, details, serverId } = req.body;
    await AntiCheatLog.create({ playerId, violation, details, serverId });
    return res.json({ ok: true });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// POST /v1/players/anticheat/autoban  (game server only)
// ─────────────────────────────────────────────

router.post('/anticheat/autoban', requireServerAuth, async (req, res, next) => {
  try {
    const { playerId, reason, durationSeconds } = req.body;
    const banExpiry = new Date(Date.now() + durationSeconds * 1000);

    await Player.updateOne({ playerId }, {
      $set: { isBanned: true, banExpiry, banReason: `[AutoBan] ${reason}` }
    });

    // Force disconnect from all game servers
    global.redis.publish('admin:ban', JSON.stringify({ playerId }));

    return res.json({ ok: true, banExpiry });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────

function publicProfile(p) {
  return {
    playerId:   p.playerId,
    username:   p.username,
    level:      p.level,
    appearance: p.appearance,
    guildId:    p.guildId,
    createdAt:  p.createdAt,
  };
}

module.exports = router;
