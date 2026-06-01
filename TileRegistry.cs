using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using BlockVerse.Items;

namespace BlockVerse.World
{
    /// <summary>
    /// Maps ItemId → Unity RuleTile/AnimatedTile for the tilemap renderer.
    /// Also provides farming growth stage tiles.
    /// Loaded from Addressables at boot; accessed synchronously during gameplay.
    /// </summary>
    public class TileRegistry : MonoBehaviour
    {
        public static TileRegistry Instance { get; private set; }

        [Header("Collision")]
        [SerializeField] private TileBase collisionTile;
        public static TileBase CollisionTile => Instance.collisionTile;

        [Header("Tile Entries")]
        [SerializeField] private TileEntry[] foregroundEntries;
        [SerializeField] private TileEntry[] backgroundEntries;

        [Header("Farming Stages")]
        [SerializeField] private FarmingTileEntry[] farmingEntries;

        [Header("Liquid")]
        [SerializeField] private AnimatedTile waterAnimTile;
        [SerializeField] private AnimatedTile lavaAnimTile;

        // Lookup tables
        private static readonly Dictionary<int, TileBase> _fgTiles  = new();
        private static readonly Dictionary<int, TileBase> _bgTiles  = new();
        private static readonly Dictionary<(int itemId, byte stage), TileBase> _farmingTiles = new();

        // Default tile when sprite is missing (hot-pink checkerboard for debug)
        private static TileBase _missingTile;

        private void Awake()
        {
            Instance = this;
            BuildLookupTables();
        }

        private void BuildLookupTables()
        {
            _fgTiles.Clear();
            _bgTiles.Clear();
            _farmingTiles.Clear();

            foreach (var e in foregroundEntries)
                if (e.Tile != null) _fgTiles[e.ItemId] = e.Tile;

            foreach (var e in backgroundEntries)
                if (e.Tile != null) _bgTiles[e.ItemId] = e.Tile;

            foreach (var e in farmingEntries)
                for (byte s = 0; s < e.Stages.Length; s++)
                    if (e.Stages[s] != null)
                        _farmingTiles[(e.SeedItemId, s)] = e.Stages[s];

            Debug.Log($"[TileRegistry] {_fgTiles.Count} fg tiles, {_bgTiles.Count} bg tiles, {_farmingTiles.Count} farming tiles.");
        }

        // ─────────────────────────────────────────────
        #region Public API

        public static TileBase GetRenderTile(int itemId, TileLayer layer)
        {
            var dict = layer == TileLayer.Foreground ? _fgTiles : _bgTiles;
            if (dict.TryGetValue(itemId, out var tile)) return tile;

            // Fallback: try building tile from item definition sprite
            var def = ItemDatabase.Instance.GetItem(itemId);
            if (def?.WorldSprite != null)
            {
                var t = ScriptableObject.CreateInstance<Tile>();
                t.sprite = def.WorldSprite;
                t.colliderType = Tile.ColliderType.None;
                dict[itemId] = t;
                return t;
            }

            return _missingTile;
        }

        public static TileBase GetFarmingTile(int seedItemId, byte growthStage)
        {
            if (_farmingTiles.TryGetValue((seedItemId, growthStage), out var tile))
                return tile;

            // Fall back to stage 0 if stage not found
            _farmingTiles.TryGetValue((seedItemId, 0), out var fallback);
            return fallback;
        }

        public static TileBase GetLiquidTile(int itemId)
        {
            // Item ID 7 = water, 6 = lava (match WorldGenerator constants)
            return itemId switch
            {
                7 => Instance.waterAnimTile,
                6 => Instance.lavaAnimTile,
                _ => null
            };
        }

        public static bool IsTransparent(int itemId)
        {
            var def = ItemDatabase.Instance.GetItem(itemId);
            return def == null || def.IsTransparent || def.IsLiquid;
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // Data entries (configured in Inspector)
    // ─────────────────────────────────────────────────────

    [System.Serializable]
    public class TileEntry
    {
        public int      ItemId;
        public TileBase Tile;
    }

    [System.Serializable]
    public class FarmingTileEntry
    {
        public int        SeedItemId;
        public TileBase[] Stages; // index = growth stage (0-7)
    }

    // ─────────────────────────────────────────────────────
    // ItemDefinition extension for transparency/liquid flags
    // (add these fields to ItemDefinition.cs if needed)
    // ─────────────────────────────────────────────────────

    public static class ItemDefinitionExtensions
    {
        private static readonly HashSet<int> _transparentIds = new() { 10, 11, 12 }; // leaves, glass, etc.
        private static readonly HashSet<int> _liquidIds      = new() { 6, 7 };

        public static bool IsTransparent(this ItemDefinition def)
            => def != null && _transparentIds.Contains(def.ItemId);

        public static bool IsLiquidTile(this ItemDefinition def)
            => def != null && _liquidIds.Contains(def.ItemId);
    }
}
