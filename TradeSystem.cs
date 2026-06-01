using System;
using System.Collections.Generic;
using UnityEngine;
using BlockVerse.Core;
using BlockVerse.Inventory;
using BlockVerse.Network;

namespace BlockVerse.Economy
{
    /// <summary>
    /// Server-authoritative trade system.
    /// Two-phase commit: offer → confirm → execute.
    /// Prevents duplication via atomic swap.
    /// </summary>
    public class TradeSystem : MonoBehaviour
    {
        public static TradeSystem Instance { get; private set; }

        private readonly Dictionary<string, TradeSession> _activeTrades = new();
        private readonly Dictionary<string, string> _playerToTrade = new(); // playerId → tradeId

        private const float TRADE_TIMEOUT = 60f;
        private const int MAX_TRADE_SLOTS = 9;
        private const int MAX_CURRENCY_PER_TRADE = 1_000_000;

        private void Awake() => Instance = this;

        // ─────────────────────────────────────────────
        #region Trade Initiation

        public void HandleTradeRequest(PlayerServerState requester, TradeRequestMessage msg,
            Dictionary<int, PlayerServerState> allPlayers)
        {
            // Validation
            if (requester.PlayerId == msg.TargetPlayerId) return;
            if (_playerToTrade.ContainsKey(requester.PlayerId)) return; // Already in trade

            // Find target player
            PlayerServerState target = null;
            foreach (var p in allPlayers.Values)
                if (p.PlayerId == msg.TargetPlayerId) { target = p; break; }

            if (target == null) return;
            if (_playerToTrade.ContainsKey(target.PlayerId)) return;

            // Distance check (must be within 5 tiles)
            float dist = Vector2.Distance(requester.Position, target.Position);
            if (dist > 5f)
            {
                requester.Connection.Send(new ServerErrorMessage
                {
                    Code = ErrorCode.InvalidAction,
                    Message = "Too far away to trade."
                });
                return;
            }

            string tradeId = Guid.NewGuid().ToString("N");

            var session = new TradeSession
            {
                TradeId = tradeId,
                InitiatorId = requester.PlayerId,
                TargetId = target.PlayerId,
                Initiator = requester,
                Target = target,
                CreatedAt = Time.time,
                State = TradeState.Pending
            };

            _activeTrades[tradeId] = session;
            _playerToTrade[requester.PlayerId] = tradeId;
            _playerToTrade[target.PlayerId] = tradeId;

            // Notify target of incoming trade request
            target.Connection.Send(new TradeResponseMessage
            {
                TradeId = tradeId,
                Accepted = false // Initial request, not a response
            });

            Debug.Log($"[Trade] {requester.Username} → {target.Username} | id={tradeId}");

            // Start timeout
            StartCoroutine(TradeTimeout(tradeId));
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Offer Management

        public void HandleTradeOffer(PlayerServerState player, string tradeId,
            InventorySlotData[] items, int currency)
        {
            if (!_activeTrades.TryGetValue(tradeId, out var session)) return;
            if (session.State != TradeState.Active) return;

            // Validate ownership
            bool isInitiator = session.InitiatorId == player.PlayerId;

            // Cap currency
            currency = Mathf.Min(currency, MAX_CURRENCY_PER_TRADE);

            // Validate player has all offered items
            foreach (var slot in items)
            {
                if (slot.ItemId == 0) continue;
                if (!player.Inventory.HasItem(slot.ItemId, slot.Count))
                {
                    player.Connection.Send(new ServerErrorMessage
                    {
                        Code = ErrorCode.NotEnoughItems,
                        Message = "You don't have enough of an offered item."
                    });
                    return;
                }
            }

            // Validate currency
            if (currency > player.Inventory.GetItemCount(CurrencySystem.GEM_ITEM_ID))
            {
                player.Connection.Send(new ServerErrorMessage
                {
                    Code = ErrorCode.NotEnoughCurrency,
                    Message = "Not enough gems."
                });
                return;
            }

            if (isInitiator)
            {
                session.InitiatorOffer = items;
                session.InitiatorCurrency = currency;
                session.InitiatorConfirmed = false;
            }
            else
            {
                session.TargetOffer = items;
                session.TargetCurrency = currency;
                session.TargetConfirmed = false;
            }

            // Notify both parties of updated offer
            BroadcastTradeState(session);
        }

        public void HandleTradeConfirm(PlayerServerState player, string tradeId, bool confirmed)
        {
            if (!_activeTrades.TryGetValue(tradeId, out var session)) return;
            if (session.State != TradeState.Active) return;

            bool isInitiator = session.InitiatorId == player.PlayerId;

            if (confirmed)
            {
                if (isInitiator) session.InitiatorConfirmed = true;
                else session.TargetConfirmed = true;
            }
            else
            {
                // Cancel trade
                CancelTrade(tradeId, "Trade cancelled.");
                return;
            }

            // Both confirmed → execute
            if (session.InitiatorConfirmed && session.TargetConfirmed)
                ExecuteTrade(session);
            else
                BroadcastTradeState(session);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Trade Execution (Atomic Swap)

        private void ExecuteTrade(TradeSession session)
        {
            session.State = TradeState.Executing;

            // ── Validate both sides one final time ────────────
            if (!ValidateTradeItems(session.Initiator, session.InitiatorOffer, session.InitiatorCurrency) ||
                !ValidateTradeItems(session.Target, session.TargetOffer, session.TargetCurrency))
            {
                CancelTrade(session.TradeId, "Items changed during trade. Trade cancelled.");
                return;
            }

            // ── Atomic removal from both ──────────────────────
            RemoveTradeItems(session.Initiator, session.InitiatorOffer, session.InitiatorCurrency);
            RemoveTradeItems(session.Target, session.TargetOffer, session.TargetCurrency);

            // ── Atomic give to both ───────────────────────────
            GiveTradeItems(session.Target, session.InitiatorOffer, session.InitiatorCurrency);
            GiveTradeItems(session.Initiator, session.TargetOffer, session.TargetCurrency);

            // ── Sync inventories ──────────────────────────────
            session.Initiator.Connection.Send(new InventorySyncMessage
            {
                Slots = session.Initiator.Inventory.Serialize()
            });
            session.Target.Connection.Send(new InventorySyncMessage
            {
                Slots = session.Target.Inventory.Serialize()
            });

            // ── Notify completion ─────────────────────────────
            session.Initiator.Connection.Send(new TradeCompleteMessage
            {
                TradeId = session.TradeId,
                Success = true,
                ReceivedItems = session.TargetOffer
            });
            session.Target.Connection.Send(new TradeCompleteMessage
            {
                TradeId = session.TradeId,
                Success = true,
                ReceivedItems = session.InitiatorOffer
            });

            // ── Log transaction ───────────────────────────────
            TradeLogger.Log(session);

            CleanupTrade(session.TradeId);
            Debug.Log($"[Trade] Completed: {session.InitiatorId} ↔ {session.TargetId}");
        }

        private bool ValidateTradeItems(PlayerServerState player, InventorySlotData[] items, int currency)
        {
            foreach (var slot in items)
            {
                if (slot.ItemId == 0) continue;
                if (!player.Inventory.HasItem(slot.ItemId, slot.Count)) return false;
            }

            if (currency > 0 && !player.Inventory.HasItem(CurrencySystem.GEM_ITEM_ID, currency))
                return false;

            return true;
        }

        private void RemoveTradeItems(PlayerServerState player, InventorySlotData[] items, int currency)
        {
            foreach (var slot in items)
            {
                if (slot.ItemId == 0) continue;
                player.Inventory.RemoveItem(slot.ItemId, slot.Count);
            }

            if (currency > 0)
                player.Inventory.RemoveItem(CurrencySystem.GEM_ITEM_ID, currency);
        }

        private void GiveTradeItems(PlayerServerState player, InventorySlotData[] items, int currency)
        {
            foreach (var slot in items)
            {
                if (slot.ItemId == 0) continue;
                player.Inventory.AddItem(slot.ItemId, slot.Count);
            }

            if (currency > 0)
                player.Inventory.AddItem(CurrencySystem.GEM_ITEM_ID, currency);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Cancel / Cleanup

        public void CancelTrade(string tradeId, string reason)
        {
            if (!_activeTrades.TryGetValue(tradeId, out var session)) return;

            session.Initiator.Connection.Send(new TradeCancelMessage { TradeId = tradeId });
            session.Target.Connection.Send(new TradeCancelMessage { TradeId = tradeId });

            CleanupTrade(tradeId);
        }

        private void CleanupTrade(string tradeId)
        {
            if (!_activeTrades.TryGetValue(tradeId, out var session)) return;

            _playerToTrade.Remove(session.InitiatorId);
            _playerToTrade.Remove(session.TargetId);
            _activeTrades.Remove(tradeId);
        }

        private System.Collections.IEnumerator TradeTimeout(string tradeId)
        {
            yield return new WaitForSeconds(TRADE_TIMEOUT);
            if (_activeTrades.ContainsKey(tradeId))
                CancelTrade(tradeId, "Trade timed out.");
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Helpers

        private void BroadcastTradeState(TradeSession session)
        {
            var msgToInitiator = new TradeOfferMessage
            {
                TradeId = session.TradeId,
                OfferedItems = session.TargetOffer,
                OfferedCurrency = session.TargetCurrency
            };
            var msgToTarget = new TradeOfferMessage
            {
                TradeId = session.TradeId,
                OfferedItems = session.InitiatorOffer,
                OfferedCurrency = session.InitiatorCurrency
            };

            session.Initiator.Connection.Send(msgToInitiator);
            session.Target.Connection.Send(msgToTarget);
        }

        #endregion
    }

    // ─────────────────────────────────────────────
    // Supporting types
    // ─────────────────────────────────────────────

    public enum TradeState { Pending, Active, Executing, Complete, Cancelled }

    public class TradeSession
    {
        public string TradeId;
        public string InitiatorId;
        public string TargetId;
        public PlayerServerState Initiator;
        public PlayerServerState Target;
        public float CreatedAt;
        public TradeState State;

        public InventorySlotData[] InitiatorOffer = Array.Empty<InventorySlotData>();
        public InventorySlotData[] TargetOffer = Array.Empty<InventorySlotData>();
        public int InitiatorCurrency;
        public int TargetCurrency;
        public bool InitiatorConfirmed;
        public bool TargetConfirmed;
    }
}
