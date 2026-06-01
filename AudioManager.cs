using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace BlockVerse
{
    /// <summary>
    /// Centralized audio manager with pooled AudioSources,
    /// spatial SFX support, and per-category volume control.
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        [Header("Mixer")]
        [SerializeField] private AudioMixer mixer;
        [SerializeField] private AudioMixerGroup sfxGroup;
        [SerializeField] private AudioMixerGroup musicGroup;
        [SerializeField] private AudioMixerGroup uiGroup;

        [Header("Music Tracks")]
        [SerializeField] private AudioClip[] musicTracks;

        [Header("SFX Library")]
        [SerializeField] private SfxEntry[] sfxLibrary;

        [Header("Pool")]
        [SerializeField] private int sfxPoolSize = 20;
        [SerializeField] private float defaultMaxDistance = 30f;

        private AudioSource   _musicSource;
        private AudioSource   _ambienceSource;
        private Queue<AudioSource> _sfxPool = new();
        private Dictionary<string, SfxEntry> _sfxDict = new();
        private int _currentTrack = -1;

        // Volume prefs
        private float _masterVol = 1f;
        private float _sfxVol    = 1f;
        private float _musicVol  = 0.7f;

        // ─────────────────────────────────────────────
        #region Init

        private void Awake()
        {
            Instance = this;
        }

        public void Initialize()
        {
            // Build SFX dictionary
            foreach (var entry in sfxLibrary)
                _sfxDict[entry.Key] = entry;

            // Build SFX source pool
            var poolRoot = new GameObject("[AudioPool]");
            poolRoot.transform.SetParent(transform);
            for (int i = 0; i < sfxPoolSize; i++)
            {
                var src = poolRoot.AddComponent<AudioSource>();
                src.outputAudioMixerGroup = sfxGroup;
                src.spatialBlend = 0f; // default 2D
                src.playOnAwake  = false;
                _sfxPool.Enqueue(src);
            }

            // Music source
            _musicSource = gameObject.AddComponent<AudioSource>();
            _musicSource.outputAudioMixerGroup = musicGroup;
            _musicSource.loop = true;

            // Load volume prefs
            _masterVol = PlayerPrefs.GetFloat("vol_master", 1f);
            _sfxVol    = PlayerPrefs.GetFloat("vol_sfx",    1f);
            _musicVol  = PlayerPrefs.GetFloat("vol_music",  0.7f);

            ApplyVolumes();
            PlayMusic(0);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region SFX

        /// <summary>Play a 2D SFX by key.</summary>
        public void PlaySfx(string key)
        {
            if (!_sfxDict.TryGetValue(key, out var entry)) return;
            var clip = entry.GetRandomClip();
            if (clip == null) return;

            var src = GetPooledSource();
            src.clip         = clip;
            src.volume       = entry.Volume * _sfxVol;
            src.pitch        = entry.RandomPitch
                ? Random.Range(entry.PitchMin, entry.PitchMax) : 1f;
            src.spatialBlend = 0f; // 2D
            src.Play();

            StartCoroutine(ReturnAfterPlay(src, clip.length));
        }

        /// <summary>Play a 3D spatial SFX at a world position.</summary>
        public void PlaySfx(string key, Vector2 worldPos)
        {
            if (!_sfxDict.TryGetValue(key, out var entry)) return;
            var clip = entry.GetRandomClip();
            if (clip == null) return;

            // Cull if too far from camera
            float dist = Vector2.Distance(Camera.main.transform.position, worldPos);
            if (dist > defaultMaxDistance) return;

            var src = GetPooledSource();
            src.transform.position = new Vector3(worldPos.x, worldPos.y, 0);
            src.clip         = clip;
            src.volume       = entry.Volume * _sfxVol * Mathf.Clamp01(1f - dist / defaultMaxDistance);
            src.pitch        = entry.RandomPitch ? Random.Range(entry.PitchMin, entry.PitchMax) : 1f;
            src.spatialBlend = 0.5f;
            src.Play();

            StartCoroutine(ReturnAfterPlay(src, clip.length));
        }

        private AudioSource GetPooledSource()
        {
            if (_sfxPool.Count > 0) return _sfxPool.Dequeue();

            // Pool exhausted — create temporary source
            var go  = new GameObject("SFX_Overflow");
            go.transform.SetParent(transform);
            var src = go.AddComponent<AudioSource>();
            src.outputAudioMixerGroup = sfxGroup;
            return src;
        }

        private IEnumerator ReturnAfterPlay(AudioSource src, float duration)
        {
            yield return new WaitForSeconds(duration + 0.05f);
            src.Stop();
            src.clip = null;
            _sfxPool.Enqueue(src);
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Music

        public void PlayMusic(int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= musicTracks.Length) return;
            if (trackIndex == _currentTrack) return;

            _currentTrack = trackIndex;
            StartCoroutine(CrossFadeMusic(musicTracks[trackIndex]));
        }

        public void PlayRandomMusic()
        {
            if (musicTracks.Length == 0) return;
            PlayMusic(Random.Range(0, musicTracks.Length));
        }

        private IEnumerator CrossFadeMusic(AudioClip newTrack)
        {
            float fadeDur = 1.5f;

            // Fade out
            float startVol = _musicSource.volume;
            float t = 0;
            while (t < fadeDur)
            {
                _musicSource.volume = Mathf.Lerp(startVol, 0, t / fadeDur);
                t += Time.deltaTime;
                yield return null;
            }

            _musicSource.clip = newTrack;
            _musicSource.Play();

            // Fade in
            t = 0;
            while (t < fadeDur)
            {
                _musicSource.volume = Mathf.Lerp(0, _musicVol * _masterVol, t / fadeDur);
                t += Time.deltaTime;
                yield return null;
            }
        }

        public void StopMusic(float fadeTime = 1f)
        {
            StartCoroutine(FadeOutMusic(fadeTime));
        }

        private IEnumerator FadeOutMusic(float dur)
        {
            float start = _musicSource.volume;
            float t = 0;
            while (t < dur)
            {
                _musicSource.volume = Mathf.Lerp(start, 0, t / dur);
                t += Time.deltaTime;
                yield return null;
            }
            _musicSource.Stop();
        }

        #endregion

        // ─────────────────────────────────────────────
        #region Volume Control

        public void SetMasterVolume(float vol)
        {
            _masterVol = Mathf.Clamp01(vol);
            PlayerPrefs.SetFloat("vol_master", _masterVol);
            ApplyVolumes();
        }

        public void SetSfxVolume(float vol)
        {
            _sfxVol = Mathf.Clamp01(vol);
            PlayerPrefs.SetFloat("vol_sfx", _sfxVol);
            ApplyVolumes();
        }

        public void SetMusicVolume(float vol)
        {
            _musicVol = Mathf.Clamp01(vol);
            PlayerPrefs.SetFloat("vol_music", _musicVol);
            ApplyVolumes();
        }

        private void ApplyVolumes()
        {
            // Unity AudioMixer uses dB: 0 vol → -80dB, 1 vol → 0dB
            float ToDb(float linear) => linear > 0.001f
                ? Mathf.Log10(linear) * 20f
                : -80f;

            mixer.SetFloat("MasterVolume", ToDb(_masterVol));
            mixer.SetFloat("SFXVolume",    ToDb(_sfxVol));
            mixer.SetFloat("MusicVolume",  ToDb(_musicVol));
        }

        public (float master, float sfx, float music) GetVolumes()
            => (_masterVol, _sfxVol, _musicVol);

        #endregion
    }

    // ─────────────────────────────────────────────────────
    // SFX Entry (ScriptableObject per sound)
    // ─────────────────────────────────────────────────────

    [System.Serializable]
    public class SfxEntry
    {
        public string      Key;
        public AudioClip[] Clips;
        [Range(0f, 1f)]
        public float       Volume    = 1f;
        public bool        RandomPitch = true;
        [Range(0.5f, 2f)]
        public float       PitchMin  = 0.9f;
        [Range(0.5f, 2f)]
        public float       PitchMax  = 1.1f;

        public AudioClip GetRandomClip()
        {
            if (Clips == null || Clips.Length == 0) return null;
            return Clips[Random.Range(0, Clips.Length)];
        }
    }
}
