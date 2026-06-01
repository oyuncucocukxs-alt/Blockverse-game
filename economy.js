'use strict';

const express = require('express');
const router  = express.Router();
const { v4: uuidv4 } = require('uuid');
const { MarketListing, Player, ShopItem, TradeLog } = require('../models');
const { requireAuth, requireServerAuth } = require('../middleware/auth');

const GEM_ITEM_ID = 1001; // Must match Unity ItemDatabase

// ─────────────────────────────────────────────
// MARKETPLACE
// ─────────────────────────────────────────────

// GET /v1/economy/market?itemId=&page=&sort=price
router.get('/market', requireAuth, async (req, res, next) => {
  try {
    const { itemId, page = 1, limit = 20, sort = 'price' } = req.query;
    const skip = (parseInt(page) - 1) * Math.min(parseInt(limit), 50);

    const filter = { isActive: true, expiresAt: { $gt: new Date() } };
    if (itemId) filter.itemId = parseInt(itemId);

    const sortMap = {
      price:    { priceEach: 1 },
      price_desc: { priceEach: -1 },
      newest:   { createdAt: -1 },
    };

    const [listings, total] = await Promise.all([
      MarketListing.find(filter)
        .sort(sortMap[sort] || sortMap.price)
        .skip(skip)
        .limit(50)
        .lean(),
      MarketListing.countDocuments(filter),
    ]);

    return res.json({ listings, total, page: parseInt(page) });
  } catch (err) { next(err); }
});

// POST /v1/economy/market — Create listing
router.post('/market', requireAuth, async (req, res, next) => {
  try {
    const { itemId, itemCount, priceEach } = req.body;

    if (!itemId || !itemCount || !priceEach) {
      return res.status(400).json({ error: 'itemId, itemCount, priceEach required' });
    }
    if (parseInt(priceEach) < 1 || parseInt(priceEach) > 1_000_000) {
      return res.status(400).json({ error: 'Price must be between 1 and 1,000,000 gems' });
    }
    if (parseInt(itemCount) < 1 || parseInt(itemCount) > 1000) {
      return res.status(400).json({ error: 'Count must be between 1 and 1000' });
    }

    // Check player has the items (via inventory snapshot)
    const player = await Player.findOne({ playerId: req.playerId })
      .select('inventory gems').lean();
    if (!player) return res.status(404).json({ error: 'Player not found' });

    const totalOwned = player.inventory
      .filter(s => s.itemId === parseInt(itemId))
      .reduce((sum, s) => sum + s.count, 0);

    if (totalOwned < parseInt(itemCount)) {
      return res.status(400).json({ error: 'Not enough items in inventory' });
    }

    // Count active listings (max 10 per player)
    const activeCount = await MarketListing.countDocuments({
      sellerId: req.playerId, isActive: true
    });
    if (activeCount >= 10) {
      return res.status(400).json({ error: 'Max 10 active listings per player' });
    }

    // Create listing — items are "locked" via game server notification
    const listing = await MarketListing.create({
      listingId: uuidv4(),
      sellerId:  req.playerId,
      sellerName: player.username, // denormalized for performance
      itemId:    parseInt(itemId),
      itemCount: parseInt(itemCount),
      priceEach: parseInt(priceEach),
      expiresAt: new Date(Date.now() + 7 * 24 * 3600 * 1000), // 7 days
    });

    // Notify game server to remove items from player inventory
    global.redis.publish('economy:listing_created', JSON.stringify({
      playerId: req.playerId,
      itemId: parseInt(itemId),
      itemCount: parseInt(itemCount),
      listingId: listing.listingId,
    }));

    return res.status(201).json({ listingId: listing.listingId, message: 'Listed' });
  } catch (err) { next(err); }
});

// POST /v1/economy/market/:listingId/buy
router.post('/market/:listingId/buy', requireAuth, async (req, res, next) => {
  try {
    const { quantity = 1 } = req.body;
    const qty = parseInt(quantity);

    // Use a database transaction for atomicity
    const session = await require('mongoose').startSession();
    session.startTransaction();

    try {
      const listing = await MarketListing.findOne({
        listingId: req.params.listingId,
        isActive: true,
        expiresAt: { $gt: new Date() }
      }).session(session);

      if (!listing) {
        await session.abortTransaction();
        return res.status(404).json({ error: 'Listing not found or expired' });
      }

      if (listing.sellerId === req.playerId) {
        await session.abortTransaction();
        return res.status(400).json({ error: 'Cannot buy your own listing' });
      }

      if (qty > listing.itemCount) {
        await session.abortTransaction();
        return res.status(400).json({ error: 'Not enough stock' });
      }

      const totalCost = qty * listing.priceEach;

      // Deduct gems from buyer
      const buyer = await Player.findOneAndUpdate(
        { playerId: req.playerId, gems: { $gte: totalCost } },
        { $inc: { gems: -totalCost } },
        { session, new: true }
      );

      if (!buyer) {
        await session.abortTransaction();
        return res.status(400).json({ error: 'Not enough gems' });
      }

      // Credit seller
      await Player.updateOne(
        { playerId: listing.sellerId },
        { $inc: { gems: totalCost } },
        { session }
      );

      // Update listing stock
      listing.itemCount -= qty;
      if (listing.itemCount <= 0) listing.isActive = false;
      await listing.save({ session });

      await session.commitTransaction();

      // Give items to buyer via game server
      global.redis.publish('economy:purchase', JSON.stringify({
        buyerId: req.playerId,
        sellerId: listing.sellerId,
        itemId: listing.itemId,
        quantity: qty,
        totalGems: totalCost,
      }));

      return res.json({ message: 'Purchase successful', gemsSpent: totalCost });
    } catch (err) {
      await session.abortTransaction();
      throw err;
    } finally {
      session.endSession();
    }
  } catch (err) { next(err); }
});

// DELETE /v1/economy/market/:listingId — Cancel listing
router.delete('/market/:listingId', requireAuth, async (req, res, next) => {
  try {
    const listing = await MarketListing.findOne({
      listingId: req.params.listingId,
      sellerId: req.playerId,
      isActive: true,
    });

    if (!listing) return res.status(404).json({ error: 'Listing not found' });

    listing.isActive = false;
    await listing.save();

    // Return items to seller
    global.redis.publish('economy:listing_cancelled', JSON.stringify({
      playerId: req.playerId,
      itemId: listing.itemId,
      itemCount: listing.itemCount,
    }));

    return res.json({ message: 'Listing cancelled, items returned' });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// COSMETIC SHOP
// ─────────────────────────────────────────────

// GET /v1/economy/shop
router.get('/shop', async (_req, res, next) => {
  try {
    const items = await ShopItem.find({ isActive: true })
      .sort({ isLimited: -1, priceUSD: 1 })
      .lean();
    return res.json(items);
  } catch (err) { next(err); }
});

// POST /v1/economy/shop/:shopItemId/buy  (premium currency)
router.post('/shop/:shopItemId/buy', requireAuth, async (req, res, next) => {
  try {
    const shopItem = await ShopItem.findOne({
      shopItemId: req.params.shopItemId,
      isActive: true,
    });

    if (!shopItem) return res.status(404).json({ error: 'Shop item not found' });
    if (!shopItem.pricePremium) {
      return res.status(400).json({ error: 'Item not available for premium currency' });
    }

    // Check limited stock
    if (shopItem.isLimited && shopItem.soldCount >= shopItem.limitedCount) {
      return res.status(400).json({ error: 'Item sold out' });
    }

    const player = await Player.findOneAndUpdate(
      { playerId: req.playerId, premiumCurrency: { $gte: shopItem.pricePremium } },
      {
        $inc: { premiumCurrency: -shopItem.pricePremium },
        $push: { purchaseHistory: { itemId: shopItem.shopItemId, amount: shopItem.pricePremium, date: new Date() } }
      },
      { new: true }
    );

    if (!player) return res.status(400).json({ error: 'Not enough premium currency' });

    await ShopItem.updateOne({ shopItemId: req.params.shopItemId }, { $inc: { soldCount: 1 } });

    // Grant item to player
    global.redis.publish('economy:shop_purchase', JSON.stringify({
      playerId: req.playerId,
      gameItemId: shopItem.gameItemId,
      quantity: 1,
    }));

    return res.json({ message: 'Purchase successful', gameItemId: shopItem.gameItemId });
  } catch (err) { next(err); }
});

// ─────────────────────────────────────────────
// TRADE LOG (game server submits completed trades)
// ─────────────────────────────────────────────

router.post('/trade-log', requireServerAuth, async (req, res, next) => {
  try {
    const trade = req.body;
    await TradeLog.create({
      tradeId: trade.tradeId,
      initiatorId: trade.initiatorId,
      targetId:    trade.targetId,
      initiatorItems: trade.initiatorItems,
      targetItems:    trade.targetItems,
      initiatorCurrency: trade.initiatorCurrency,
      targetCurrency:    trade.targetCurrency,
      worldId: trade.worldId,
    });
    return res.json({ ok: true });
  } catch (err) { next(err); }
});

module.exports = router;
