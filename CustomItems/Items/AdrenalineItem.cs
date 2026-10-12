using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace CustomItems.Items
{
    /// <summary>
    /// Энергетик Adrenaline Game Fuel. В банке 3 глотка (ЛКМ), выпитая банка мнётся
    /// (в модели два объекта: Can и Crushed) и остаётся скрапом.
    ///
    /// Каждый глоток: обычный игрок получает полную стамину и быстрее её восстанавливает.
    /// Игрок из списка SleepyPlayers с шансом SleepChance вместо этого засыпает: глаза моргают,
    /// потом он лежит, ничего не видит, звук приглушён и двигаться нельзя.
    /// У любого пьющего может случиться сердечный приступ: шанс = глотков за день × HeartAttackChancePerSip %.
    ///
    /// Глоток синхронизирует игра (SyncUse), поэтому код ниже выполняется у всех. Число глотков
    /// в банке хранит хост и раздаёт через именованные сообщения Netcode (как у Егермейстера).
    /// Уснёт ли игрок, все клиенты считают одинаково из общего зерна, чтобы все видели его лежащим;
    /// приступ решает только сам пьющий, а смерть синхронизирует игра.
    /// </summary>
    public class AdrenalineItem : CustomGrabbable
    {
        public const int MaxSips = 3;

        // Задаются из конфига в Plugin
        public static string[] SleepyPlayers = { "Sidiusz" };
        public static float SleepChance = 20f;
        public static float SleepDuration = 10f;
        public static float BoostDuration = 30f;
        public static float HeartAttackChancePerSip = 1f;

        private const int SaveBase = 10;
        private const string SipsMessage = ModInfo.Guid + ".adrenaline.sips";
        private const string RequestMessage = ModInfo.Guid + ".adrenaline.request";

        private static CustomMessagingManager registeredOn;

        private int sipsLeft = MaxSips;
        private GameObject fullModel, crushedModel;

        public override void Start()
        {
            base.Start();
            useCooldown = 1.5f;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Can") fullModel = t.gameObject;
                else if (t.name == "Crushed") crushedModel = t.gameObject;
            }
            RegisterMessages();
            if (!IsServer && NetworkManager != null && NetworkManager.IsConnectedClient)
                RequestSips();
            UpdateModel();
        }

        // 10 + глотки; старые версии сохраняли 0 = целая, 1 = пустая
        public override int GetItemDataToSave() => SaveBase + sipsLeft;

        public override void LoadItemSaveData(int saveData)
        {
            sipsLeft = saveData >= SaveBase ? Mathf.Clamp(saveData - SaveBase, 0, MaxSips)
                : saveData == 1 ? 0 : MaxSips;
            UpdateModel();
        }

        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            if (!buttonDown || playerHeldBy == null)
                return;

            var drinker = playerHeldBy;
            var isLocal = drinker == GameNetworkManager.Instance.localPlayerController;
            if (AdrenalineEffects.IsAsleep(drinker))
                return; // во сне не пьют
            if (sipsLeft <= 0)
            {
                if (isLocal)
                    HUDManager.Instance.DisplayTip(itemProperties.itemName, "The can is empty.");
                return;
            }

            var first = sipsLeft == MaxSips;
            sipsLeft--;
            UpdateModel();
            if (IsServer)
                BroadcastSips();

            if (audioSource != null)
            {
                var clip = DrinkSound.Get(first);
                audioSource.PlayOneShot(clip);
                WalkieTalkie.TransmitOneShotAudio(audioSource, clip);
            }
            DrinkAnimation.Play(drinker);

            var sleeps = IsSleepy(drinker.playerUsername) && SharedRoll(drinker) < SleepChance / 100f;
            if (sleeps)
                AdrenalineEffects.Run(AdrenalineEffects.FallAsleep(drinker, SleepDuration, isLocal));
            if (!isLocal)
                return;

            if (!sleeps)
                AdrenalineEffects.Run(AdrenalineEffects.StaminaBoost(drinker, BoostDuration));
            var chance = AdrenalineEffects.CountSip() * HeartAttackChancePerSip;
            if (Random.value * 100f < chance)
                AdrenalineEffects.Run(AdrenalineEffects.HeartAttack(drinker));
        }

        // Одинаковое у всех клиентов число 0..1: зависит от банки, глотка, игрока и сида луны
        private float SharedRoll(PlayerControllerB drinker)
        {
            unchecked
            {
                var seed = (int)NetworkObjectId * 73856093 ^ sipsLeft * 19349663 ^ (int)drinker.playerClientId * 83492791
                           ^ (StartOfRound.Instance != null ? StartOfRound.Instance.randomMapSeed : 0);
                return (float)new System.Random(seed).NextDouble();
            }
        }

        // Ник сравниваем без регистра и без значков: «Sidiusz*» в Steam тоже Sidiusz
        private static bool IsSleepy(string name)
        {
            var me = Normalize(name);
            return me.Length > 0 && SleepyPlayers.Any(n => Normalize(n) == me);
        }

        private static string Normalize(string s) =>
            new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        // Целая банка, пока есть хоть глоток, потом мятая. SetActive, а не renderer.enabled:
        // игра включает все рендереры, когда предмет достают из кармана.
        private void UpdateModel()
        {
            if (fullModel == null || crushedModel == null)
                return;
            fullModel.SetActive(sipsLeft > 0);
            crushedModel.SetActive(sipsLeft <= 0);
            mainObjectRenderer = (sipsLeft > 0 ? fullModel : crushedModel).GetComponent<MeshRenderer>();
        }

        // ---- Синхронизация числа глотков ----

        private static void RegisterMessages()
        {
            var messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null || messaging == registeredOn)
                return;
            messaging.RegisterNamedMessageHandler(SipsMessage, OnSipsMessage);
            messaging.RegisterNamedMessageHandler(RequestMessage, OnRequestMessage);
            registeredOn = messaging;
        }

        private void RequestSips()
        {
            using var writer = new FastBufferWriter(sizeof(ulong), Allocator.Temp);
            writer.WriteValueSafe(NetworkObjectId);
            NetworkManager.CustomMessagingManager.SendNamedMessage(RequestMessage, NetworkManager.ServerClientId, writer,
                NetworkDelivery.Reliable);
        }

        private void SendSips(ulong clientId)
        {
            using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(int), Allocator.Temp);
            writer.WriteValueSafe(NetworkObjectId);
            writer.WriteValueSafe(sipsLeft);
            NetworkManager.CustomMessagingManager.SendNamedMessage(SipsMessage, clientId, writer, NetworkDelivery.Reliable);
        }

        private void BroadcastSips()
        {
            foreach (var clientId in NetworkManager.ConnectedClientsIds)
            {
                if (clientId != NetworkManager.ServerClientId)
                    SendSips(clientId);
            }
        }

        private static void OnRequestMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong objectId);
            var can = Find(objectId);
            if (can != null && can.IsServer)
                can.SendSips(sender);
        }

        private static void OnSipsMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong objectId);
            reader.ReadValueSafe(out int sips);
            var can = Find(objectId);
            if (can != null && sender == NetworkManager.ServerClientId)
            {
                can.sipsLeft = Mathf.Clamp(sips, 0, MaxSips);
                can.UpdateModel();
            }
        }

        private static AdrenalineItem Find(ulong objectId)
        {
            var spawned = NetworkManager.Singleton?.SpawnManager?.SpawnedObjects;
            if (spawned == null || !spawned.TryGetValue(objectId, out var obj) || obj == null)
                return null;
            return obj.GetComponent<AdrenalineItem>();
        }
    }

    /// <summary>
    /// Эффекты живут на отдельном объекте, а не на банке: банку можно выбросить или продать,
    /// а сон и ускорение должны доиграть до конца.
    /// </summary>
    internal class AdrenalineEffects : MonoBehaviour
    {
        private static AdrenalineEffects host;
        private static int boostToken;
        private static readonly HashSet<PlayerControllerB> Sleeping = new HashSet<PlayerControllerB>();

        // Глотки за день у локального игрока, для шанса приступа
        private static int sipsToday;
        private static string sipsDay;

        public static bool IsAsleep(PlayerControllerB player) => Sleeping.Contains(player);

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

        /// <summary>Засчитать глоток локальному игроку; возвращает, который это глоток за день.</summary>
        public static int CountSip()
        {
            var round = StartOfRound.Instance;
            var day = round == null ? "" : round.GetInstanceID() + ":" + (round.gameStats?.daysSpent ?? 0);
            if (day != sipsDay)
            {
                sipsDay = day;
                sipsToday = 0;
            }
            return ++sipsToday;
        }

        // ---------------- стамина

        public static IEnumerator StaminaBoost(PlayerControllerB player, float duration)
        {
            var token = ++boostToken; // следующий глоток продлевает эффект, а не складывается
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

        // ---------------- сердечный приступ

        public static IEnumerator HeartAttack(PlayerControllerB player)
        {
            yield return new WaitForSeconds(2f);
            if (player == null || player.isPlayerDead)
                yield break;
            HUDManager.Instance.ShakeCamera(ScreenShakeType.Big);
            HUDManager.Instance.DisplayTip("Heart attack", "Too much Adrenaline.", isWarning: true);
            yield return new WaitForSeconds(0.6f);
            if (player != null && !player.isPlayerDead)
                player.KillPlayer(Vector3.zero, spawnBody: true, causeOfDeath: CauseOfDeath.Unknown);
        }

        // ---------------- сон
        // Идёт у всех клиентов: у спящего моргают глаза и темнеет экран, остальные видят,
        // как он лежит. Сначала DrowsySeconds клонит в сон, потом sleepSeconds лежит.

        private const float DrowsySeconds = 5f;
        private static readonly (float close, float hold)[] Blinks = { (0.15f, 0.05f), (0.3f, 0.3f), (0.45f, 0.7f) };

        public static IEnumerator FallAsleep(PlayerControllerB player, float sleepSeconds, bool isLocal)
        {
            if (player == null || !Sleeping.Add(player))
                yield break; // уже спит

            Eyelids lids = null;
            AudioLowPassFilter muffle = null;
            var volume = AudioListener.volume;
            var quieter = false;
            var lying = false;
            var model = LyingModel(player);
            var modelRotation = model != null ? model.localRotation : Quaternion.identity;
            var modelPosition = model != null ? model.localPosition : Vector3.zero;

            try
            {
                // Клонит в сон: качает как от TZP, глаза моргают всё дольше
                var start = Time.time;
                if (isLocal)
                {
                    lids = Eyelids.Create();
                    player.drunkness = Mathf.Max(player.drunkness, 0.5f);
                    yield return new WaitForSeconds(0.8f);
                    foreach (var (close, hold) in Blinks)
                    {
                        yield return lids.Animate(0f, 0.85f, close);
                        yield return lids.Animate(0.85f, 1f, close);
                        yield return new WaitForSeconds(hold);
                        yield return lids.Animate(1f, 0f, close * 1.5f);
                        yield return new WaitForSeconds(0.4f);
                    }
                    yield return lids.Animate(0f, 1f, 0.5f);
                }
                var rest = DrowsySeconds - (Time.time - start);
                if (rest > 0f)
                    yield return new WaitForSeconds(rest);
                if (player == null || player.isPlayerDead)
                    yield break;

                // Отрубается: модель ложится на спину (её видят остальные)
                if (model != null)
                {
                    model.localRotation = modelRotation * Quaternion.Euler(-90f, 0f, 0f);
                    model.localPosition = modelPosition + Vector3.up * 0.2f;
                    lying = true;
                }

                var spot = player.transform.position;
                if (isLocal)
                {
                    var listener = player.activeAudioListener != null
                        ? player.activeAudioListener
                        : StartOfRound.Instance?.audioListener;
                    if (listener != null)
                    {
                        muffle = listener.gameObject.AddComponent<AudioLowPassFilter>();
                        muffle.cutoffFrequency = 500f;
                    }
                    AudioListener.volume = volume * 0.5f;
                    quieter = true;
                }

                for (var t = 0f; t < sleepSeconds; t += Time.deltaTime)
                {
                    if (player == null || player.isPlayerDead)
                        break;
                    if (isLocal)
                        HoldStill(player, spot);
                    yield return null;
                }

                if (isLocal)
                {
                    AudioListener.volume = volume;
                    quieter = false;
                    if (muffle != null)
                        Destroy(muffle);
                    player.drunkness = 0f;
                    // Глаза открываются медленно, ещё с полсекунды лежим
                    yield return lids.Animate(1f, 0.6f, 0.8f);
                    HoldStill(player, spot);
                    yield return lids.Animate(0.6f, 0.9f, 0.4f);
                }
                RestoreModel(model, modelRotation, modelPosition, ref lying);
                if (isLocal)
                    yield return lids.Animate(0.9f, 0f, 1.2f);
            }
            finally
            {
                Sleeping.Remove(player);
                RestoreModel(model, modelRotation, modelPosition, ref lying);
                if (isLocal && player != null)
                {
                    player.disableMoveInput = false;
                    player.disableLookInput = false;
                }
                if (muffle != null)
                    Destroy(muffle);
                if (quieter)
                    AudioListener.volume = volume;
                lids?.Destroy();
            }
        }

        // Ходьба и обзор выключены, а если игрок всё-таки сдвинулся (прыжок, толчок),
        // возвращаем его на место
        private static void HoldStill(PlayerControllerB player, Vector3 spot)
        {
            player.disableMoveInput = true;
            player.disableLookInput = true;
            var offset = player.transform.position - spot;
            offset.y = 0f;
            if (offset.sqrMagnitude > 0.05f * 0.05f)
                player.TeleportPlayer(new Vector3(spot.x, player.transform.position.y, spot.z));
        }

        // Объект с аниматором тела: его поворот игра не трогает, поэтому модель можно положить.
        // Корень игрока не годится: он двигает камеру и контроллер.
        private static Transform LyingModel(PlayerControllerB player)
        {
            var t = player.playerBodyAnimator != null ? player.playerBodyAnimator.transform : null;
            return t == null || t == player.transform || t == player.thisPlayerBody ? null : t;
        }

        private static void RestoreModel(Transform model, Quaternion rotation, Vector3 position, ref bool lying)
        {
            if (!lying || model == null)
                return;
            model.localRotation = rotation;
            model.localPosition = position;
            lying = false;
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


    /// <summary>Звук «пшш» с глотками и просто глотки, собранные кодом, чтобы не тащить аудио в бандл.</summary>
    internal static class DrinkSound
    {
        private static AudioClip openClip, sipClip;

        /// <summary>first: первый глоток, банку только что открыли.</summary>
        public static AudioClip Get(bool first) => first ? openClip ??= Build(true) : sipClip ??= Build(false);

        private static AudioClip Build(bool open)
        {
            const int rate = 44100;
            var lead = open ? 0.65f : 0.1f;
            var data = new float[(int)(rate * (lead + 0.95f))];
            var rng = new System.Random(449);

            // Открывание банки: щелчок и шипение
            if (open)
            {
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
            }

            // Глотки
            foreach (var offset in new[] { 0f, 0.3f, 0.6f })
            {
                var s = (int)((lead + offset) * rate);
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

            var c = AudioClip.Create(open ? "AdrenalineOpen" : "AdrenalineSip", data.Length, 1, rate, false);
            c.SetData(data, 0);
            return c;
        }
    }
}
