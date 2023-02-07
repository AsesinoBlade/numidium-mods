using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DynamicMusic
{
    [RequireComponent(typeof(DaggerfallSongPlayer))]
    public sealed class DynamicMusic : MonoBehaviour
    {
        public static DynamicMusic Instance { get; private set; }
        private static Mod mod;
        private SongManager songManager;
        private DaggerfallSongPlayer combatSongPlayer;
        private GameManager gameManager;
        private float stateChangeInterval;
        private float stateCheckDelta;
        private float fadeOutLength;
        private float fadeInLength;
        private float fadeOutTime;
        private float fadeInTime;
        private byte taperOffLength;
        private byte taperFadeStart;
        private byte taperOff;
        private SongFiles[] defaultSongs;
        private List<string> combatPlaylist;
        private byte playlistIndex;
        private string musicPath;
        private bool combatMusicIsMidi;

        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            mod = initParams.Mod;
            var go = new GameObject(mod.Title);
            Instance = go.AddComponent<DynamicMusic>();
            SaveLoadManager.OnLoad += SaveLoadManager_OnLoad;
            //mod.LoadSettingsCallback = Instance.LoadSettings;
        }

        private void Awake()
        {
            //var settings = mod.GetSettings();
            //LoadSettings(settings, new ModSettingsChange());
            combatSongPlayer = GetComponent<DaggerfallSongPlayer>();
            musicPath = Path.Combine(Application.streamingAssetsPath, "Sound", "DynMusic_Combat");
            var fileNames = Directory.GetFiles(musicPath, "*.ogg");
            combatPlaylist = new List<string>(fileNames.Length);
            foreach (var fileName in fileNames)
                combatPlaylist.Add(fileName);
            Debug.Log("Dynamic Music initialized.");
            mod.IsReady = true;
        }

        // Load settings that can change during runtime.
        /*
        private void LoadSettings(ModSettings settings, ModSettingsChange change)
        {
        }
        */

        private void Start()
        {
            gameManager = GameManager.Instance;
            stateChangeInterval = 3f;
            taperOffLength = 5;
            taperFadeStart = 1;
            fadeOutLength = 1f;
            fadeInLength = 3f;
            defaultSongs = new SongFiles[]
            {
                SongFiles.song_17, // fighter trainers
                SongFiles.song_30  // unused sneaking (?) theme
            };

            playlistIndex = (byte)Random.Range(0, combatPlaylist.Count);
            PlayerEnterExit.OnTransitionInterior += OnTransitionInterior;
            PlayerEnterExit.OnTransitionExterior += OnTransitionExterior;
            PlayerEnterExit.OnTransitionDungeonInterior += OnTransitionDungeonInterior;
            PlayerEnterExit.OnTransitionDungeonExterior += OnTransitionDungeonExterior;
            gameManager.PlayerEntity.OnDeath += OnDeath;
        }

        private void Update()
        {
            if (!songManager)
                return;
            stateCheckDelta += Time.deltaTime;
            // Fade out combat music.
            if (fadeOutTime > 0f)
            {
                // Fade out combat music during taper time.
                fadeOutTime += Time.deltaTime;
                combatSongPlayer.AudioSource.volume = Mathf.Lerp(DaggerfallUnity.Settings.MusicVolume, 0f, fadeOutTime / fadeOutLength);
                // End fade when time elapsed.
                if (fadeOutTime >= fadeOutLength)
                {
                    StopCombatMusic();
                    // Start normal music fade-in.
                    fadeInTime = Time.deltaTime;
                }
            }
            else if (combatMusicIsMidi && !combatSongPlayer.IsPlaying)
                combatSongPlayer.Play(); // Loop combat music if MIDI.
            // Fade in normal music.
            var songPlayer = songManager.SongPlayer;
            if (fadeInTime > 0f && songPlayer.AudioSource.isPlaying)
            {
                fadeInTime += Time.deltaTime;
                if (songPlayer.enabled)
                    songPlayer.enabled = false; // Stop SongPlayer from controlling its own volume.
                songPlayer.AudioSource.volume = Mathf.Lerp(0f, DaggerfallUnity.Settings.MusicVolume, fadeInTime / fadeInLength);
                if (fadeInTime >= fadeInLength)
                {
                    fadeInTime = 0f; // End fade when time elapsed.
                    songPlayer.enabled = true; // Resume updates in SongPlayer.
                }
            }

            // Only perform state check once per assigned interval.
            if (stateCheckDelta < stateChangeInterval)
                return;
            if (!gameManager.PlayerDeath.DeathInProgress && IsPlayerDetected())
            {
                // Switch to combat music if not tapering or already playing.
                if (taperOff == 0 || !IsCombatMusicPlaying())
                {
                    songManager.StopPlaying();
                    songManager.enabled = false;
                    songPlayer.enabled = false;
                    combatSongPlayer.AudioSource.volume = DaggerfallUnity.Settings.MusicVolume;
                    combatSongPlayer.AudioSource.loop = true;
                    int playlistCount;
                    if (combatPlaylist.Count > 0 && TryLoadSong(musicPath, combatPlaylist[playlistIndex], out var song))
                    {
                        combatSongPlayer.AudioSource.clip = song;
                        combatSongPlayer.AudioSource.Play();
                        playlistCount = combatPlaylist.Count;
                        combatMusicIsMidi = false;
                    }
                    else
                    {
                        var songFile = defaultSongs[playlistIndex % defaultSongs.Length];
                        combatSongPlayer.Play(songFile);
                        combatSongPlayer.Song = songFile;
                        playlistCount = defaultSongs.Length;
                        combatMusicIsMidi = combatSongPlayer.AudioSource.clip == null;
                    }

                    playlistIndex += (byte)Random.Range(1, playlistCount - 1);
                    playlistIndex %= (byte)playlistCount;
                }

                taperOff = taperOffLength;
            }
            else if (taperOff <= taperFadeStart && combatSongPlayer.AudioSource.isPlaying || (combatMusicIsMidi && combatSongPlayer.IsPlaying))
            {
                // Begin fading after taper ends.
                fadeOutTime = Time.deltaTime;
            }
            else if (taperOff > 0 && --taperOff == 0)
            {
                // Re-enable vanilla music system on taper end.
                songManager.SongPlayer.enabled = true;
                songManager.enabled = true;
                songManager.StartPlaying();
            }

            #if UNITY_EDITOR
            if (taperOff > 0)
                Debug.Log("DynamicMusic: taperOff = " + taperOff);
            #endif
            stateCheckDelta = 0f;
        }

        private bool IsPlayerDetected()
        {
            var entityBehaviours = FindObjectsOfType<DaggerfallEntityBehaviour>();
            foreach (var entityBehaviour in entityBehaviours)
            {
                if (entityBehaviour.EntityType == EntityTypes.EnemyMonster || entityBehaviour.EntityType == EntityTypes.EnemyClass)
                {
                    var enemySenses = entityBehaviour.GetComponent<EnemySenses>();
                    if (enemySenses && enemySenses.Target == gameManager.PlayerEntityBehaviour && enemySenses.DetectedTarget && enemySenses.TargetInSight)
                        return true;
                }
            }

            return false;
        }

        private void LoadSongManager()
        {
            var go = GameObject.Find("SongPlayer");
            songManager = go.GetComponent<SongManager>();
            taperOff = 0; // Don't continue tapering if we have a freshly loaded player.
            songManager.SongPlayer.enabled = true;
            songManager.enabled = true;
            if (!songManager.SongPlayer.IsPlaying)
                songManager.StartPlaying();
        }

        private bool TryLoadSong(string soundPath, string name, out AudioClip audioClip)
        {
            string path = Path.Combine(soundPath, name);
            if (File.Exists(path))
            {
                var www = new WWW("file://" + path); // the "non-deprecated" class gives me compiler errors so it can suck it
                audioClip = www.GetAudioClip(true, true);
                return audioClip != null;
            }

            audioClip = null;
            return false;
        }

        private void StopCombatMusic()
        {
            fadeOutTime = 0f;
            combatSongPlayer.AudioSource.loop = false;
            combatSongPlayer.Stop(); // stop midi in case it's playing
            combatMusicIsMidi = false;
            combatSongPlayer.AudioSource.Stop();
        }

        private bool IsCombatMusicPlaying()
        {
            return (combatMusicIsMidi && combatSongPlayer.IsPlaying) ||
                (!combatMusicIsMidi && combatSongPlayer.AudioSource.isPlaying);
        }

        // Load new location's song player when player moves into it.
        private void OnTransitionInterior(PlayerEnterExit.TransitionEventArgs args)
        {
            LoadSongManager();
        }

        private void OnTransitionExterior(PlayerEnterExit.TransitionEventArgs args)
        {
            LoadSongManager();
        }

        private void OnTransitionDungeonInterior(PlayerEnterExit.TransitionEventArgs args)
        {
            LoadSongManager();
        }

        private void OnTransitionDungeonExterior(PlayerEnterExit.TransitionEventArgs args)
        {
            LoadSongManager();
        }

        private static void SaveLoadManager_OnLoad(SaveData_v1 saveData)
        {
            Instance.LoadSongManager();
        }

        private void OnDeath(DaggerfallEntity entity)
        {
            // Fade out on death.
            if (IsCombatMusicPlaying())
                fadeOutTime = Time.deltaTime;
        }
    }
}
