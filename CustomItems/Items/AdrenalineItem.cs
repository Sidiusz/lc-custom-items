using System;
using System.Collections;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.UI;

namespace CustomItems.Items
{
    /// <summary>
    /// Энергетик Adrenaline Game Fuel. ЛКМ: выпить (одна банка на один раз, пустую можно продать).
    /// Обычный игрок получает полную стамину и быстрее её восстанавливает.
    /// Игроки из списка SleepyPlayers в конфиге, наоборот, засыпают: глаза моргают,
    /// потом на несколько секунд темнота, приглушённый звук и нельзя двигаться.
    /// Использование синхронизирует игра (SyncUse), эффекты считаются только у того, кто пьёт.
    /// </summary>
    public class AdrenalineItem : CustomGrabbable
    {
        /// <summary>Ники через запятую, без учёта регистра. Задаётся из конфига в Plugin.</summary>
        public static string[] SleepyPlayers = { "Sidiusz" };

        public static float BoostDuration = 30f;
        public static float SleepDuration = 10f;

        private bool empty;

        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            if (!buttonDown || empty || playerHeldBy == null)
                return;

            empty = true;
            if (audioSource != null)
            {
                audioSource.PlayOneShot(DrinkSound.Clip);
                WalkieTalkie.TransmitOneShotAudio(audioSource, DrinkSound.Clip);
            }

            var drinker = playerHeldBy;
            if (drinker != GameNetworkManager.Instance.localPlayerController)
                return;

            if (IsSleepy(drinker.playerUsername))
                AdrenalineEffects.Run(AdrenalineEffects.FallAsleep(drinker, SleepDuration));
            else
                AdrenalineEffects.Run(AdrenalineEffects.StaminaBoost(drinker, BoostDuration));
        }

        public override int GetItemDataToSave() => empty ? 1 : 0;

        public override void LoadItemSaveData(int saveData) => empty = saveData == 1;

        private static bool IsSleepy(string name) =>
            !string.IsNullOrEmpty(name) &&
            SleepyPlayers.Any(n => string.Equals(n.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Эффекты живут на отдельном объекте, а не на банке: банку можно выбросить или продать,
    /// а сон и ускорение должны доиграть до конца.
    /// </summary>
    internal class AdrenalineEffects : MonoBehaviour
    {
        private static AdrenalineEffects host;
        private static int boostToken;

        public static void Run(IEnumerator routine)
        {
            if (host == null)
            {
                var go = new GameObject("AdrenalineEffects");
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                host = go.AddComponent<AdrenalineEffects>();
            }
            host.StartCoroutine(routine);
        }

        // ---------------- стамина

        public static IEnumerator StaminaBoost(PlayerControllerB player, float duration)
        {
            var token = ++boostToken; // повторная банка продлевает эффект, а не складывается
            player.sprintMeter = 1f;
            player.isExhausted = false;

            for (var t = 0f; t < duration && token == boostToken; t += Time.deltaTime)
            {
                if (player == null || player.isPlayerDead)
                    yield break;
                // Сверх ванильного восстановления: полная шкала примерно за 3 секунды
                if (!player.isSprinting)
                    player.sprintMeter = Mathf.Min(1f, player.sprintMeter + Time.deltaTime / 3f);
                yield return null;
            }
        }

        // ---------------- сон

        public static IEnumerator FallAsleep(PlayerControllerB player, float sleepSeconds)
        {
            var lids = Eyelids.Create();
            var listener = player.activeAudioListener != null
                ? player.activeAudioListener
                : StartOfRound.Instance?.audioListener;
            AudioLowPassFilter muffle = null;
            var volume = AudioListener.volume;
            var quieter = false;
            var crouched = false;

            try
            {
                // Клонит в сон: качает как от TZP, глаза моргают всё дольше
                player.drunkness = Mathf.Max(player.drunkness, 0.6f);
                yield return new WaitForSeconds(1.5f);
                foreach (var (close, hold) in new[] { (0.15f, 0.05f), (0.25f, 0.2f), (0.35f, 0.5f), (0.5f, 0.9f) })
                {
                    yield return lids.Animate(0f, 0.85f, close);
                    yield return lids.Animate(0.85f, 1f, close);
                    yield return new WaitForSeconds(hold);
                    yield return lids.Animate(1f, 0f, close * 1.5f);
                    yield return new WaitForSeconds(0.6f);
                }

                // Отрубается
                yield return lids.Animate(0f, 1f, 0.6f);
                if (player == null || player.isPlayerDead)
                    yield break;
                player.disableMoveInput = true;
                player.disableLookInput = true;
                if (!player.isCrouching)
                {
                    player.Crouch(true);
                    crouched = true;
                }
                if (listener != null)
                {
                    muffle = listener.gameObject.AddComponent<AudioLowPassFilter>();
                    muffle.cutoffFrequency = 500f;
                }
                AudioListener.volume = volume * 0.5f;
                quieter = true;

                for (var t = 0f; t < sleepSeconds; t += Time.deltaTime)
                {
                    if (player == null || player.isPlayerDead)
                        break;
                    yield return null;
                }

                AudioListener.volume = volume;
                quieter = false;
                if (muffle != null)
                    Destroy(muffle);
                if (player != null)
                    player.drunkness = 0f;
                yield return lids.Animate(1f, 0.6f, 0.8f);
                yield return lids.Animate(0.6f, 0.9f, 0.4f);
                yield return lids.Animate(0.9f, 0f, 1.2f);
            }
            finally
            {
                if (player != null)
                {
                    player.disableMoveInput = false;
                    player.disableLookInput = false;
                    if (crouched && player.isCrouching && !player.isPlayerDead)
                        player.Crouch(false);
                }
                if (muffle != null)
                    Destroy(muffle);
                if (quieter)
                    AudioListener.volume = volume;
                lids.Destroy();
            }
        }

        /// <summary>Два чёрных века поверх экрана: 0 = глаза открыты, 1 = закрыты.</summary>
        private class Eyelids
        {
            private GameObject root;
            private RectTransform top, bottom;

            public static Eyelids Create()
            {
                var e = new Eyelids { root = new GameObject("AdrenalineEyelids") };
                DontDestroyOnLoad(e.root);
                var canvas = e.root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 1000;
                e.top = e.Lid(new Vector2(0, 1));
                e.bottom = e.Lid(new Vector2(0, 0));
                e.Set(0f);
                return e;
            }

            private RectTransform Lid(Vector2 pivot)
            {
                var go = new GameObject("Lid", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(root.transform, false);
                go.GetComponent<Image>().color = Color.black;
                var rt = (RectTransform)go.transform;
                rt.anchorMin = new Vector2(0, pivot.y);
                rt.anchorMax = new Vector2(1, pivot.y);
                rt.pivot = new Vector2(0.5f, pivot.y);
                return rt;
            }

            public void Set(float closed)
            {
                if (root == null)
                    return;
                // Каждое веко закрывает чуть больше половины экрана, чтобы не было щели
                var h = Mathf.Clamp01(closed) * 0.52f;
                top.anchorMin = new Vector2(0, 1 - h);
                top.anchorMax = new Vector2(1, 1);
                bottom.anchorMin = new Vector2(0, 0);
                bottom.anchorMax = new Vector2(1, h);
                top.offsetMin = top.offsetMax = bottom.offsetMin = bottom.offsetMax = Vector2.zero;
            }

            public IEnumerator Animate(float from, float to, float seconds)
            {
                for (var t = 0f; t < seconds; t += Time.deltaTime)
                {
                    Set(Mathf.SmoothStep(from, to, t / seconds));
                    yield return null;
                }
                Set(to);
            }

            public void Destroy()
            {
                if (root != null)
                    UnityEngine.Object.Destroy(root);
                root = null;
            }
        }
    }

    /// <summary>Звук «пшш» и три глотка, собранный кодом, чтобы не тащить аудио в бандл.</summary>
    internal static class DrinkSound
    {
        private static AudioClip clip;

        public static AudioClip Clip => clip ??= Build();

        private static AudioClip Build()
        {
            const int rate = 44100;
            var data = new float[(int)(rate * 1.6f)];
            var rng = new System.Random(449);

            // Открывание банки: щелчок и шипение
            float prev = 0;
            for (var i = 0; i < rate * 0.45f; i++)
            {
                var t = i / (float)rate;
                var n = (float)(rng.NextDouble() * 2 - 1);
                var hiss = n - prev; // грубый фильтр верхних частот
                prev = n;
                var env = t < 0.01f ? t / 0.01f : Mathf.Exp(-(t - 0.01f) * 7f);
                data[i] += hiss * env * 0.35f;
                if (t < 0.006f)
                    data[i] += (float)(rng.NextDouble() * 2 - 1) * 0.8f; // щелчок язычка
            }

            // Глотки
            foreach (var start in new[] { 0.65f, 0.95f, 1.25f })
            {
                var s = (int)(start * rate);
                var len = (int)(0.14f * rate);
                var phase = 0.0;
                for (var i = 0; i < len && s + i < data.Length; i++)
                {
                    var t = i / (float)len;
                    var freq = 180 - 90 * t;
                    phase += 2 * Math.PI * freq / rate;
                    var env = Mathf.Sin(Mathf.PI * t);
                    data[s + i] += (float)Math.Sin(phase) * env * env * 0.5f;
                }
            }

            var c = AudioClip.Create("AdrenalineDrink", data.Length, 1, rate, false);
            c.SetData(data, 0);
            return c;
        }
    }
}
