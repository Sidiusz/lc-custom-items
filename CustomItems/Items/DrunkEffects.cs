using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Эффект от глотка Егермейстера: каждый глоток действует 90 секунд, глотки складываются.
    /// Голова кружится только у выпившего (камера покачивается), а голос становится ниже
    /// для всех, кто его слышит. Работает на каждом клиенте: глоток приходит всем через
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
                SetVoicePitch(player, VoicePitch(strength));
                if (player == local)
                    localStrength = strength;
            }

            foreach (var player in finished)
            {
                sips.Remove(player);
                if (player != null)
                    SetVoicePitch(player, 1f);
            }

            SwayCamera(local, localStrength);
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
