'use strict';

const express = require('express');
const router  = express.Router();
const { v4: uuidv4 } = require('uuid');
const rateLimit = require('express-rate-limit');
const { Player } = require('../models');
const {
  verifyFirebaseToken,
  signAccessToken,
  signRefreshToken,
  verifyRefreshToken,
  requireAuth,
} = require('../middleware/auth');

// Strict rate limit for auth endpoints
const authLimiter = rateLimit({
  windowMs: 15 * 60 * 1000, // 15 min
  max: 20,
  message: { error: 'Too many auth requests, try again later' },
});

// ─────────────────────────────────────────────
// POST /v1/auth/login
// Body: { firebaseIdToken }
// Returns: { accessToken, refreshToken, player }
// ─────────────────────────────────────────────

router.post('/login', authLimiter, async (req, res, next) => {
  try {
    const { firebaseIdToken } = req.body;
    if (!firebaseIdToken) return res.status(400).json({ error: 'firebaseIdToken required' });

    // Verify Firebase token
    const fbUser = await verifyFirebaseToken(firebaseIdToken);

    // Find or create player
    let player = await Player.findOne({ firebaseUid: fbUser.firebaseUid });

    if (!player) {
      // New player — needs username registration
      return res.status(200).json({
        isNewPlayer: true,
        firebaseUid: fbUser.firebaseUid,
        email: fbUser.email,
        message: 'New player: provide username to complete registration',
      });
    }

    // Update last login
    await Player.updateOne({ _id: player._id }, { lastLoginAt: new Date() });

    const tokenPayload = {
      playerId: player.playerId,
      firebaseUid: player.firebaseUid,
      username: player.username,
      isAdmin: player.isAdmin,
    };

    return res.json({
      accessToken:  signAccessToken(tokenPayload),
      refreshToken: signRefreshToken(tokenPayload),
      player: sanitizePlayer(player),
    });
  } catch (err) {
    next(err);
  }
});

// ─────────────────────────────────────────────
// POST /v1/auth/register
// Body: { firebaseIdToken, username }
// ─────────────────────────────────────────────

router.post('/register', authLimiter, async (req, res, next) => {
  try {
    const { firebaseIdToken, username } = req.body;
    if (!firebaseIdToken || !username) {
      return res.status(400).json({ error: 'firebaseIdToken and username required' });
    }

    // Validate username
    const usernameRegex = /^[a-zA-Z0-9_]{3,20}$/;
    if (!usernameRegex.test(username)) {
      return res.status(400).json({
        error: 'Username must be 3-20 characters: letters, numbers, underscores only'
      });
    }

    const fbUser = await verifyFirebaseToken(firebaseIdToken);

    // Check uniqueness
    const existing = await Player.findOne({
      $or: [{ firebaseUid: fbUser.firebaseUid }, { username }]
    });

    if (existing) {
      if (existing.firebaseUid === fbUser.firebaseUid)
        return res.status(409).json({ error: 'Account already registered' });
      return res.status(409).json({ error: 'Username already taken' });
    }

    // Create player
    const playerId = uuidv4();
    const player = await Player.create({
      playerId,
      firebaseUid: fbUser.firebaseUid,
      username,
      email: fbUser.email,
      gems: 100, // Starting gems
      inventory: Array(36).fill({ itemId: 0, count: 0, metadata: null }),
    });

    const tokenPayload = { playerId, firebaseUid: fbUser.firebaseUid, username, isAdmin: false };

    return res.status(201).json({
      accessToken:  signAccessToken(tokenPayload),
      refreshToken: signRefreshToken(tokenPayload),
      player: sanitizePlayer(player),
    });
  } catch (err) {
    next(err);
  }
});

// ─────────────────────────────────────────────
// POST /v1/auth/refresh
// Body: { refreshToken }
// ─────────────────────────────────────────────

router.post('/refresh', async (req, res, next) => {
  try {
    const { refreshToken } = req.body;
    if (!refreshToken) return res.status(400).json({ error: 'refreshToken required' });

    let payload;
    try {
      payload = verifyRefreshToken(refreshToken);
    } catch {
      return res.status(401).json({ error: 'Invalid or expired refresh token' });
    }

    const player = await Player.findOne({ playerId: payload.playerId })
      .select('playerId firebaseUid username isAdmin isBanned')
      .lean();

    if (!player || player.isBanned) {
      return res.status(401).json({ error: 'Account not found or banned' });
    }

    const tokenPayload = {
      playerId: player.playerId,
      firebaseUid: player.firebaseUid,
      username: player.username,
      isAdmin: player.isAdmin,
    };

    return res.json({
      accessToken:  signAccessToken(tokenPayload),
      refreshToken: signRefreshToken(tokenPayload),
    });
  } catch (err) {
    next(err);
  }
});

// ─────────────────────────────────────────────
// POST /v1/auth/validate-token (called by game server)
// Header: x-server-secret
// Body: { token }
// ─────────────────────────────────────────────

router.post('/validate-token', require('../middleware/auth').requireServerAuth, async (req, res, next) => {
  try {
    const { token } = req.body;
    if (!token) return res.status(400).json({ error: 'token required' });

    const result = await require('../middleware/auth').validateGameToken(token);
    return res.json(result);
  } catch (err) {
    next(err);
  }
});

// ─────────────────────────────────────────────
// GET /v1/auth/me
// ─────────────────────────────────────────────

router.get('/me', requireAuth, async (req, res, next) => {
  try {
    const player = await Player.findOne({ playerId: req.playerId }).lean();
    if (!player) return res.status(404).json({ error: 'Player not found' });
    return res.json(sanitizePlayer(player));
  } catch (err) {
    next(err);
  }
});

// ─────────────────────────────────────────────
// POST /v1/auth/check-username
// ─────────────────────────────────────────────

router.post('/check-username', authLimiter, async (req, res, next) => {
  try {
    const { username } = req.body;
    const regex = /^[a-zA-Z0-9_]{3,20}$/;
    if (!regex.test(username)) return res.json({ available: false, reason: 'Invalid format' });

    const exists = await Player.exists({ username });
    return res.json({ available: !exists });
  } catch (err) {
    next(err);
  }
});

// ─────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────

function sanitizePlayer(p) {
  return {
    playerId:    p.playerId,
    username:    p.username,
    level:       p.level,
    xp:          p.xp,
    gems:        p.gems,
    premiumCurrency: p.premiumCurrency,
    isPremium:   p.isPremium,
    appearance:  p.appearance,
    equipment:   p.equipment,
    ownedWorlds: p.ownedWorlds,
    lastWorldId: p.lastWorldId,
    guildId:     p.guildId,
    isAdmin:     p.isAdmin,
    createdAt:   p.createdAt,
    lastLoginAt: p.lastLoginAt,
  };
}

module.exports = router;
