using System;
using System.Collections.Generic;
using UnityEngine;

namespace BlockVerse.Utils
{
    // ─────────────────────────────────────────────────────
    // Generic Object Pool
    // ─────────────────────────────────────────────────────

    public class ObjectPool<T> where T : Component
    {
        private readonly T                  _prefab;
        private readonly Transform          _parent;
        private readonly Queue<T>           _pool = new();
        private readonly List<T>            _active = new();
        private readonly Func<T, T>         _onGet;
        private readonly Action<T>          _onReturn;

        public int ActiveCount => _active.Count;
        public int PoolCount   => _pool.Count;

        public ObjectPool(T prefab, int initialSize, Transform parent = null,
            Func<T, T> onGet = null, Action<T> onReturn = null)
        {
            _prefab   = prefab;
            _parent   = parent;
            _onGet    = onGet;
            _onReturn = onReturn;

            for (int i = 0; i < initialSize; i++)
                ReturnToPool(CreateNew());
        }

        public T Get()
        {
            T obj = _pool.Count > 0 ? _pool.Dequeue() : CreateNew();
            obj.gameObject.SetActive(true);
            _active.Add(obj);
            return _onGet != null ? _onGet(obj) : obj;
        }

        public T Get(Vector3 position, Quaternion rotation)
        {
            T obj = Get();
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            return obj;
        }

        public void Return(T obj)
        {
            if (!_active.Remove(obj)) return;
            _onReturn?.Invoke(obj);
            ReturnToPool(obj);
        }

        public void ReturnAll()
        {
            foreach (var obj in _active.ToArray())
                Return(obj);
        }

        private T CreateNew()
        {
            var go  = UnityEngine.Object.Instantiate(_prefab, _parent);
            go.name = $"{_prefab.name}_pooled";
            return go;
        }

        private void ReturnToPool(T obj)
        {
            obj.gameObject.SetActive(false);
            if (_parent != null) obj.transform.SetParent(_parent);
            _pool.Enqueue(obj);
        }
    }

    // ─────────────────────────────────────────────────────
    // Pool Manager — central registry for all game pools
    // ─────────────────────────────────────────────────────

    public class PoolManager : MonoBehaviour
    {
        public static PoolManager Instance { get; private set; }

        [Header("Prefabs")]
        [SerializeField] private GameObject damageNumberPrefab;
        [SerializeField] private GameObject worldItemPrefab;
        [SerializeField] private GameObject tileDamageOverlayPrefab;
        [SerializeField] private GameObject notificationPrefab;
        [SerializeField] private GameObject chatMessagePrefab;
        [SerializeField] private GameObject particleExplosionPrefab;

        [Header("Pool Sizes")]
        [SerializeField] private int damageNumberPoolSize    = 30;
        [SerializeField] private int worldItemPoolSize       = 100;
        [SerializeField] private int overlayPoolSize         = 50;
        [SerializeField] private int notificationPoolSize    = 10;
        [SerializeField] private int chatMessagePoolSize     = 100;
        [SerializeField] private int particlePoolSize        = 20;

        // Pools
        private ObjectPool<DamageNumber>      _damageNumbers;
        private ObjectPool<WorldItemObject>   _worldItems;
        private ObjectPool<TileDamageOverlay> _damageOverlays;
        private ObjectPool<NotificationUI>    _notifications;
        private ObjectPool<UI.ChatMessageUI>  _chatMessages;
        private ObjectPool<ParticleSystem>    _particles;

        private void Awake()
        {
            Instance = this;
            InitializePools();
        }

        private void InitializePools()
        {
            var poolRoot = new GameObject("[Pools]").transform;
            poolRoot.SetParent(transform);

            _damageNumbers  = new ObjectPool<DamageNumber>(
                damageNumberPrefab.GetComponent<DamageNumber>(),
                damageNumberPoolSize,
                CreatePoolParent("DamageNumbers", poolRoot)
            );

            _worldItems = new ObjectPool<WorldItemObject>(
                worldItemPrefab.GetComponent<WorldItemObject>(),
                worldItemPoolSize,
                CreatePoolParent("WorldItems", poolRoot)
            );

            _damageOverlays = new ObjectPool<TileDamageOverlay>(
                tileDamageOverlayPrefab.GetComponent<TileDamageOverlay>(),
                overlayPoolSize,
                CreatePoolParent("DamageOverlays", poolRoot)
            );

            _notifications = new ObjectPool<NotificationUI>(
                notificationPrefab.GetComponent<NotificationUI>(),
                notificationPoolSize,
                CreatePoolParent("Notifications", poolRoot)
            );

            _particles = new ObjectPool<ParticleSystem>(
                particleExplosionPrefab.GetComponent<ParticleSystem>(),
                particlePoolSize,
                CreatePoolParent("Particles", poolRoot)
            );
        }

        private static Transform CreatePoolParent(string name, Transform root)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root);
            return go.transform;
        }

        // ── Public API ──────────────────────────────────────

        public DamageNumber GetDamageNumber(Vector3 pos)
            => _damageNumbers.Get(pos, Quaternion.identity);

        public void ReturnDamageNumber(DamageNumber n) => _damageNumbers.Return(n);

        public WorldItemObject GetWorldItem(Vector3 pos)
            => _worldItems.Get(pos, Quaternion.identity);

        public void ReturnWorldItem(WorldItemObject item) => _worldItems.Return(item);

        public TileDamageOverlay GetDamageOverlay(Vector3 pos)
            => _damageOverlays.Get(pos, Quaternion.identity);

        public void ReturnDamageOverlay(TileDamageOverlay overlay)
            => _damageOverlays.Return(overlay);

        public NotificationUI GetNotification() => _notifications.Get();
        public void ReturnNotification(NotificationUI n) => _notifications.Return(n);

        public ParticleSystem GetParticle(Vector3 pos)
        {
            var p = _particles.Get(pos, Quaternion.identity);
            p.Play();
            return p;
        }

        public void ReturnParticle(ParticleSystem p) => _particles.Return(p);
    }

    // ─────────────────────────────────────────────────────
    // Damage Number (floating combat text)
    // ─────────────────────────────────────────────────────

    public class DamageNumber : MonoBehaviour
    {
        [SerializeField] private TMPro.TextMeshPro text;
        [SerializeField] private AnimationCurve scaleCurve;
        [SerializeField] private AnimationCurve alphaCurve;
        [SerializeField] private float lifetime = 1.2f;
        [SerializeField] private float floatSpeed = 1.5f;

        private float _timer;

        private void OnEnable()
        {
            _timer = 0;
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            float t = _timer / lifetime;

            transform.position += Vector3.up * floatSpeed * Time.deltaTime;
            text.alpha          = alphaCurve.Evaluate(t);
            float scale         = scaleCurve.Evaluate(t);
            transform.localScale = Vector3.one * scale;

            if (_timer >= lifetime)
                PoolManager.Instance.ReturnDamageNumber(this);
        }

        public void Setup(int damage, bool isCrit, Color color)
        {
            text.text  = isCrit ? $"<b>{damage}</b>!" : damage.ToString();
            text.color = color;

            if (isCrit)
            {
                transform.localScale = Vector3.one * 1.5f;
                AudioManager.Instance.PlaySfx("crit_hit");
            }
        }
    }

    // ─────────────────────────────────────────────────────
    // Tile Damage Overlay
    // ─────────────────────────────────────────────────────

    public class TileDamageOverlay : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private Sprite[]       crackSprites; // 0-8 crack stages

        public void SetProgress(float progress)
        {
            // 0 = fresh, 1 = about to break
            int stage = Mathf.Clamp(Mathf.FloorToInt(progress * crackSprites.Length),
                0, crackSprites.Length - 1);
            spriteRenderer.sprite = crackSprites[stage];
        }
    }

    // Static accessor helpers referenced in WorldEngine.cs
    public static class DamageOverlayPool
    {
        public static TileDamageOverlay Get(Vector3 pos)
            => PoolManager.Instance.GetDamageOverlay(pos);

        public static void Return(TileDamageOverlay overlay)
            => PoolManager.Instance.ReturnDamageOverlay(overlay);
    }
}
