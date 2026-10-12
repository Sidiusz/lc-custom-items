using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Эффект от глотка Егермейстера: каждый глоток действует 90 секунд, глотки складываются.
    /// У выпившего кружится голова (камера покачивается) и голоса остальных звучат
    /// искажённо, а его собственный голос все слышат низким. Работает на каждом клиенте: глоток приходит всем через
    /// синхронизацию использования предмета.
    /// </summary>
    internal class DrunkEffects : MonoBehaviour
    {
        public const float SipDuration = 90f;

        private const float FadeIn = 3f;
        private const float FadeOut = 10f;
        private const int MaxStacks = 4;

        // Покачивание камеры на один глоток, в градусах
        private const float RollPerSip = 3.5f;
        private const float YawPerSip = 2f;

        // Высота голоса: 1 глоток 0.85, 2 глотка 0.75, 3 и больше 0.65
        private const float MinVoicePitch = 0.65f;

        private static DrunkEffects instance;

        private readonly Dictionary<PlayerControllerB, List<float>> sips = new Dictionary<PlayerControllerB, List<float>>();
        private readonly Dictionary<PlayerControllerB, float> ownPitch = new Dictionary<PlayerControllerB, float>();
        private readonly HashSet<PlayerControllerB> touched = new HashSet<PlayerControllerB>();
        private readonly List<PlayerControllerB> finished = new List<PlayerControllerB>();
        private bool cameraTilted;

        public static void AddSip(PlayerControllerB player)
        {
            if (instance == null)
            {
                var go = new GameObject("CustomItems.DrunkEffects");
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                instance = go.AddComponent<DrunkEffects>();
            }
            if (!instance.sips.TryGetValue(player, out var list))
                instance.sips[player] = list = new List<float>();
            list.Add(Time.time);
        }

        /// <summary>Сила эффекта: число действующих глотков с плавным началом и концом.</summary>
        private static float Strength(List<float> starts)
        {
            var now = Time.time;
            var strength = 0f;
            foreach (var start in starts)
            {
                var age = now - start;
                strength += Mathf.Clamp01(age / FadeIn) * Mathf.Clamp01((SipDuration - age) / FadeOut);
            }
            return Mathf.Min(strength, MaxStacks);
        }

        private static float VoicePitch(float strength)
        {
            var pitch = 1f - 0.15f * Mathf.Min(strength, 1f) - 0.1f * Mathf.Max(strength - 1f, 0f);
            return Mathf.Max(MinVoicePitch, pitch);
        }

        private void LateUpdate()
        {
            var local = GameNetworkManager.Instance?.localPlayerController;
            var localStrength = 0f;
            finished.Clear();

            // Свой голос: у каждого выпившего ниже для всех
            foreach (var pair in sips)
            {
                var player = pair.Key;
                var list = pair.Value;
                list.RemoveAll(start => Time.time - start >= SipDuration);

                if (player == null || list.Count == 0 || player.isPlayerDead || !player.isPlayerControlled)
                {
                    finished.Add(player);
                    continue;
                }

                var strength = Strength(list);
                ownPitch[player] = VoicePitch(strength);
                if (player == local)
                    localStrength = strength;
            }
            foreach (var player in finished)
            {
                sips.Remove(player);
                ownPitch.Remove(player);
            }

            UpdateVoices(local, localStrength);
            SwayCamera(local, localStrength);
        }

        // Голоса, которые слышит этот клиент. Высота = собственный низкий голос говорящего
        // (если он выпил), а если выпили мы сами, то ещё плавание высоты и искажение у всех
        // остальных. Кого эффект отпустил, тому возвращаем обычный голос.
        private void UpdateVoices(PlayerControllerB local, float localStrength)
        {
            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return;

            var dizzy = Mathf.Min(localStrength, 2f);
            foreach (var player in players)
            {
                if (player == null)
                    continue;
                var hasOwn = ownPitch.TryGetValue(player, out var pitch);
                var distort = dizzy > 0f && player != local && player.isPlayerControlled;
                if (!hasOwn)
                    pitch = 1f;

                if (distort)
                {
                    var phase = player.playerClientId * 1.7f;
                    pitch *= 1f + (Mathf.PerlinNoise(Time.time * 0.8f, phase) * 2f - 1f) * 0.12f * dizzy;
                }

                SetDistortion(player, distort ? 0.3f * dizzy : 0f);
                if (hasOwn || distort)
                {
                    SetVoicePitch(player, pitch);
                    touched.Add(player);
                }
                else if (touched.Remove(player))
                {
                    SetVoicePitch(player, 1f);
                }
            }
        }

        private void SetDistortion(PlayerControllerB player, float level)
        {
            var source = player.currentVoiceChatAudioSource;
            if (source == null)
                return;
            var filter = source.GetComponent<AudioDistortionFilter>();
            if (level <= 0f)
            {
                if (filter != null && filter.enabled)
                    filter.enabled = false;
                return;
            }
            if (filter == null)
                filter = source.gameObject.AddComponent<AudioDistortionFilter>();
            filter.enabled = true;
            filter.distortionLevel = Mathf.Clamp01(level);
        }

        // Камеру качаем только по Y и Z и задаём их целиком, а не прибавляем: X (взгляд
        // вверх-вниз) принадлежит игре, а абсолютные значения не накапливаются между кадрами.
        private void SwayCamera(PlayerControllerB local, float strength)
        {
            var camera = local != null ? local.gameplayCamera : null;
            if (camera == null)
                return;
            if (strength <= 0f)
            {
                if (cameraTilted)
                {
                    var e = camera.transform.localEulerAngles;
                    camera.transform.localEulerAngles = new Vector3(e.x, 0f, 0f);
                    cameraTilted = false;
                }
                return;
            }

            var t = Time.time;
            var roll = (Mathf.PerlinNoise(t * 0.35f, 0.1f) * 2f - 1f) * RollPerSip * strength
                       + Mathf.Sin(t * 0.9f) * RollPerSip * 0.5f * strength;
            var yaw = (Mathf.PerlinNoise(0.7f, t * 0.3f) * 2f - 1f) * YawPerSip * strength;

            var angles = camera.transform.localEulerAngles;
            camera.transform.localEulerAngles = new Vector3(angles.x, yaw, roll);
            cameraTilted = true;
        }

        // Голос игрока проходит через микшер SoundManager. Выставляем и цель, и текущее
        // значение, чтобы SoundManager не тянул высоту обратно, и сразу пишем в микшер.
        private static void SetVoicePitch(PlayerControllerB player, float pitch)
        {
            var sound = SoundManager.Instance;
            var id = (int)player.playerClientId;
            if (sound == null || sound.playerVoicePitchTargets == null || id >= sound.playerVoicePitchTargets.Length)
                return;
            sound.playerVoicePitchTargets[id] = pitch;
            sound.playerVoicePitches[id] = pitch;
            sound.SetPlayerPitch(pitch, id);
        }
    }
}
