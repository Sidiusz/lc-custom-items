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
    /// Пока действует бодрость, едва слышно стучит сердце.
    /// Игрок из списка SleepyPlayers с шансом SleepChance вместо этого засыпает: глаза моргают
    /// и закрываются, он падает на спину, спит SleepDuration секунд и встаёт.
    /// У любого пьющего может случиться сердечный приступ: шанс = глотков за день × HeartAttackChancePerSip %
    /// (1-й глоток за день 1 %, 2-й 2 %...). Сердце колотится всё быстрее, экран краснеет и плывёт,
    /// потом удар на половину здоровья, а у кого здоровья половина или меньше, смерть.
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
            useCooldown = 1.7f; // первый глоток с открыванием банки ~1.6 с
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

            var clip = EmbeddedAudio.Get(first ? "AdrenalineOpen" : "AdrenalineSip");
            if (audioSource != null && clip != null)
            {
                audioSource.PlayOneShot(clip);
                WalkieTalkie.TransmitOneShotAudio(audioSource, clip);
            }
            DrinkAnimation.Play(drinker, first ? 1.6f : 1.2f);

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
    /// а сон, бодрость и приступ должны доиграть до конца.
    /// </summary>
    internal class AdrenalineEffects : MonoBehaviour
    {
        private static AdrenalineEffects host;
        private static int boostToken;
        private static readonly Dictionary<PlayerControllerB, SleepPose> Sleeping = new Dictionary<PlayerControllerB, SleepPose>();

        // Глотки за день у локального игрока, для шанса приступа
        private static int sipsToday;
        private static string sipsDay;

        // Сердце стучит только у самого выпившего, звук плоский (не из точки в мире)
        private static AudioSource heart;
        private static bool attackRunning;

        public static bool IsAsleep(PlayerControllerB player) => Sleeping.ContainsKey(player);

        public static void Run(IEnumerator routine)
        {
            if (host == null)
            {
                var go = new GameObject("AdrenalineEffects");
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                host = go.AddComponent<AdrenalineEffects>();
                heart = go.AddComponent<AudioSource>();
                heart.playOnAwake = false;
                heart.spatialBlend = 0f;
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

        private static void Beat(float volume, float pitch = 1f)
        {
            var clip = EmbeddedAudio.Get("AdrenalineHeartbeat");
            if (heart == null || clip == null)
                return;
            heart.pitch = pitch;
            heart.PlayOneShot(clip, volume);
        }

        // ---------------- бодрость

        private const float CalmBpm = 88f;
        private const float CalmVolume = 0.1f; // едва слышно

        public static IEnumerator StaminaBoost(PlayerControllerB player, float duration)
        {
            var token = ++boostToken; // следующий глоток продлевает эффект, а не складывается
            player.sprintMeter = 1f;
            player.isExhausted = false;
            var nextBeat = 0.6f;

            for (var t = 0f; t < duration && token == boostToken; t += Time.deltaTime)
            {
                if (player == null || player.isPlayerDead)
                    yield break;
                // Сверх ванильного восстановления: полная шкала примерно за 3 секунды
                if (!player.isSprinting)
                    player.sprintMeter = Mathf.Min(1f, player.sprintMeter + Time.deltaTime / 3f);

                // Сердцебиение: плавно появляется и последние 5 секунд стихает
                if (t >= nextBeat)
                {
                    nextBeat += 60f / CalmBpm;
                    var fade = Mathf.Clamp01(t / 2f) * Mathf.Clamp01((duration - t) / 5f);
                    if (!attackRunning && !IsAsleep(player))
                        Beat(CalmVolume * fade);
                }
                yield return null;
            }
        }

        // ---------------- сердечный приступ
        // Только у самого выпившего: сердце разгоняется, экран краснеет и плывёт, потом удар.
        // Урон и смерть синхронизирует игра.

        private const int HalfHealth = 50; // половина от 100, ванильного максимума
        private const float AttackSeconds = 7f;

        public static IEnumerator HeartAttack(PlayerControllerB player)
        {
            yield return new WaitForSeconds(1f);
            if (player == null || player.isPlayerDead || attackRunning)
                yield break;

            attackRunning = true;
            var red = RedScreen.Create();
            try
            {
                var nextBeat = 0f;
                var pulse = 0f;
                for (var t = 0f; t < AttackSeconds; t += Time.deltaTime)
                {
                    if (player == null || player.isPlayerDead)
                        yield break;
                    var k = t / AttackSeconds;
                    if (t >= nextBeat)
                    {
                        // 100 ударов в минуту в начале, 200 в конце, всё громче
                        nextBeat += 60f / Mathf.Lerp(100f, 200f, k * k);
                        Beat(Mathf.Lerp(0.45f, 1f, k), 1f + 0.12f * k);
                        pulse = 1f;
                        if (k > 0.5f)
                            HUDManager.Instance.ShakeCamera(ScreenShakeType.Small);
                    }
                    pulse = Mathf.Max(0f, pulse - Time.deltaTime * 4f);
                    red.Set(0.15f + 0.65f * k + 0.2f * pulse * (0.4f + k), 0.45f * k * k + 0.1f * pulse * k);

                    // Плывёт в глазах: качка как от TZP и запотевший шлем
                    player.drunkness = Mathf.Max(player.drunkness, Mathf.Lerp(0.15f, 1f, k));
                    HUDManager.Instance.increaseHelmetCondensation = true;
                    yield return null;
                }

                // Сердце замирает
                red.Set(1f, 0.6f);
                yield return new WaitForSeconds(0.4f);
                if (player == null || player.isPlayerDead)
                    yield break;

                HUDManager.Instance.ShakeCamera(ScreenShakeType.Big);
                if (player.health <= HalfHealth)
                {
                    player.KillPlayer(Vector3.zero, spawnBody: true, causeOfDeath: CauseOfDeath.Unknown);
                    yield break;
                }
                player.DamagePlayer(HalfHealth, hasDamageSFX: true, callRPC: true, causeOfDeath: CauseOfDeath.Unknown);

                // Выжил: ещё пара тяжёлых ударов, краснота уходит
                var slowBeat = 0.5f;
                for (var t = 0f; t < 2.5f; t += Time.deltaTime)
                {
                    if (t >= slowBeat)
                    {
                        slowBeat += 60f / 70f;
                        Beat(0.6f * (1f - t / 2.5f), 0.95f);
                    }
                    var k = 1f - t / 2.5f;
                    red.Set(k, 0.6f * k * k);
                    yield return null;
                }
            }
            finally
            {
                attackRunning = false;
                red.Destroy();
            }
        }

        /// <summary>Красная виньетка и заливка поверх экрана.</summary>
        private class RedScreen
        {
            private static Texture2D vignetteTexture;
            private GameObject root;
            private RawImage vignette, tint;

            public static RedScreen Create()
            {
                var r = new RedScreen { root = Overlay.Canvas("AdrenalineHeartAttack", 990) };
                r.tint = Overlay.Image(r.root, null);
                r.vignette = Overlay.Image(r.root, vignetteTexture ??= Overlay.Vignette());
                r.Set(0f, 0f);
                return r;
            }

            /// <summary>edges: краснота по краям 0..1, fill: красный поверх всего экрана 0..1.</summary>
            public void Set(float edges, float fill)
            {
                if (root == null)
                    return;
                vignette.color = new Color(0.55f, 0f, 0f, Mathf.Clamp01(edges));
                tint.color = new Color(0.45f, 0f, 0f, Mathf.Clamp01(fill));
            }

            public void Destroy()
            {
                if (root != null)
                    UnityEngine.Object.Destroy(root);
                root = null;
            }
        }

        // ---------------- сон
        // Идёт у всех клиентов в одном темпе: у спящего моргают и закрываются глаза, остальные
        // видят, как он падает на спину и потом встаёт.

        private static readonly (float close, float hold)[] Blinks = { (0.12f, 0.05f), (0.25f, 0.3f), (0.4f, 0.7f) };
        private const float BlinkOpenSlower = 1.5f, BlinkPause = 0.4f, FirstBlink = 0.8f, FinalClose = 1.2f;
        private const float FallSeconds = 0.7f, StandSeconds = 0.8f;
        private const float WakeOpen = 1f, WakeLook = 1.4f; // открывает глаза лёжа и смотрит в потолок

        // Сколько клонит в сон до падения: все моргания и закрытие глаз
        private static float DrowsySeconds()
        {
            var total = FirstBlink + FinalClose;
            foreach (var (close, hold) in Blinks)
                total += close * 2f + hold + close * BlinkOpenSlower + BlinkPause;
            return total;
        }

        public static IEnumerator FallAsleep(PlayerControllerB player, float sleepSeconds, bool isLocal)
        {
            if (player == null || Sleeping.ContainsKey(player))
                yield break; // уже спит

            var pose = SleepPose.Create(player, isLocal);
            Sleeping[player] = pose;
            Eyelids lids = null;
            AudioLowPassFilter muffle = null;
            var volume = AudioListener.volume;
            var quieter = false;

            try
            {
                // Клонит в сон: качает как от TZP, глаза моргают всё дольше и закрываются
                if (isLocal)
                {
                    lids = Eyelids.Create();
                    player.drunkness = Mathf.Max(player.drunkness, 0.5f);
                    yield return new WaitForSeconds(FirstBlink);
                    foreach (var (close, hold) in Blinks)
                    {
                        yield return lids.Animate(0f, 0.8f, close);
                        yield return lids.Animate(0.8f, 1f, close);
                        yield return new WaitForSeconds(hold);
                        yield return lids.Animate(1f, 0f, close * BlinkOpenSlower);
                        yield return new WaitForSeconds(BlinkPause);
                    }
                    yield return lids.Animate(0f, 1f, FinalClose);
                }
                else
                {
                    yield return new WaitForSeconds(DrowsySeconds());
                }
                if (player == null || player.isPlayerDead)
                    yield break;

                // Отрубается и падает на спину
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
                for (var t = 0f; t < FallSeconds; t += Time.deltaTime)
                {
                    pose.Lie = t / FallSeconds * (t / FallSeconds); // падает с ускорением
                    if (isLocal)
                        HoldStill(player, spot);
                    yield return null;
                }
                pose.Lie = 1f;

                // Спит
                for (var t = 0f; t < sleepSeconds; t += Time.deltaTime)
                {
                    if (player == null || player.isPlayerDead)
                        yield break;
                    if (isLocal)
                        HoldStill(player, spot);
                    yield return null;
                }

                // Просыпается: открывает глаза лёжа, моргает, встаёт
                if (isLocal)
                {
                    AudioListener.volume = volume;
                    quieter = false;
                    if (muffle != null)
                        Destroy(muffle);
                    player.drunkness = 0f;
                }
                for (var t = 0f; t < WakeOpen + WakeLook; t += Time.deltaTime)
                {
                    if (isLocal)
                    {
                        lids.Set(t < WakeOpen ? Mathf.SmoothStep(1f, 0.45f, t / WakeOpen)
                            : 0.45f + 0.45f * Mathf.Sin(Mathf.PI * Mathf.Clamp01((t - WakeOpen - 0.5f) / 0.4f)));
                        HoldStill(player, spot);
                    }
                    yield return null;
                }
                for (var t = 0f; t < StandSeconds; t += Time.deltaTime)
                {
                    var k = t / StandSeconds;
                    pose.Lie = 1f - Mathf.SmoothStep(0f, 1f, k);
                    if (isLocal)
                    {
                        lids.Set(0.45f * (1f - k));
                        HoldStill(player, spot);
                    }
                    yield return null;
                }
                pose.Lie = 0f;
                if (isLocal)
                    lids.Set(0f);
            }
            finally
            {
                Sleeping.Remove(player);
                pose.Restore();
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

        // Позу ставим каждый кадр после аниматора и игры, чтобы её ничто не перезаписало
        private void LateUpdate()
        {
            foreach (var pose in Sleeping.Values)
                pose.Apply();
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

        /// <summary>
        /// Спящий лежит на спине: модель тела поворачивается вокруг ступней. Камера спящего обычно
        /// висит на той же модели и ложится вместе с ней; если нет, кладём её так же отдельно.
        /// </summary>
        private class SleepPose
        {
            public float Lie; // 0 стоит, 1 лежит

            private Transform model, camera, pivot;
            private Quaternion modelRotation, cameraRotation;
            private Vector3 modelPosition, cameraPosition;

            public static SleepPose Create(PlayerControllerB player, bool isLocal)
            {
                var p = new SleepPose { pivot = player.transform, model = LyingModel(player) };
                if (p.model != null)
                {
                    p.modelRotation = p.model.localRotation;
                    p.modelPosition = p.model.localPosition;
                }
                var cam = isLocal && player.gameplayCamera != null ? player.gameplayCamera.transform : null;
                if (cam != null && (p.model == null || !cam.IsChildOf(p.model)))
                {
                    p.camera = cam;
                    p.cameraRotation = cam.localRotation;
                    p.cameraPosition = cam.localPosition;
                }
                return p;
            }

            public void Apply()
            {
                if (model != null)
                {
                    model.localRotation = modelRotation * Quaternion.Euler(-90f * Lie, 0f, 0f);
                    model.localPosition = modelPosition + Vector3.up * (0.2f * Lie);
                }
                if (camera != null && pivot != null)
                {
                    camera.localRotation = cameraRotation;
                    camera.localPosition = cameraPosition;
                    var turn = Quaternion.AngleAxis(-90f * Lie, pivot.right);
                    var feet = pivot.position;
                    camera.position = feet + turn * (camera.position - feet) + Vector3.up * (0.2f * Lie);
                    camera.rotation = turn * camera.rotation;
                }
            }

            public void Restore()
            {
                Lie = 0f;
                Apply();
            }
        }

        // Объект с аниматором тела: его поворот игра не трогает, поэтому модель можно положить.
        // Корень игрока не годится: он двигает камеру и контроллер.
        private static Transform LyingModel(PlayerControllerB player)
        {
            var t = player.playerBodyAnimator != null ? player.playerBodyAnimator.transform : null;
            return t == null || t == player.transform || t == player.thisPlayerBody ? null : t;
        }

        /// <summary>
        /// Два чёрных века поверх экрана с размытым краем: 0 = глаза открыты, 1 = закрыты.
        /// Когда веки сомкнулись, экран чёрный целиком.
        /// </summary>
        private class Eyelids
        {
            private static Texture2D lidTexture;
            private GameObject root;
            private RawImage top, bottom, dark;

            public static Eyelids Create()
            {
                var e = new Eyelids { root = Overlay.Canvas("AdrenalineEyelids", 1000) };
                lidTexture ??= Overlay.SoftEdge();
                e.top = e.Lid(1f);
                e.bottom = e.Lid(0f);
                e.dark = Overlay.Image(e.root, null);
                e.dark.color = Color.black;
                e.Set(0f);
                return e;
            }

            // Текстура: внизу прозрачная, вверху чёрная. Нижнему веку она переворачивается.
            private RawImage Lid(float edge)
            {
                var img = Overlay.Image(root, lidTexture);
                img.color = Color.black;
                img.uvRect = edge > 0.5f ? new Rect(0, 0, 1, 1) : new Rect(0, 1, 1, -1);
                var rt = img.rectTransform;
                rt.pivot = new Vector2(0.5f, edge);
                return img;
            }

            public void Set(float closed)
            {
                if (root == null)
                    return;
                closed = Mathf.Clamp01(closed);
                // Каждое веко закрывает до 80 % экрана: размытые края перекрываются без щели
                var h = closed * 0.8f;
                Place(top.rectTransform, 1f - h, 1f);
                Place(bottom.rectTransform, 0f, h);
                dark.color = new Color(0f, 0f, 0f, Mathf.Clamp01((closed - 0.85f) / 0.15f));
            }

            private static void Place(RectTransform rt, float from, float to)
            {
                rt.anchorMin = new Vector2(0, from);
                rt.anchorMax = new Vector2(1, to);
                rt.offsetMin = rt.offsetMax = Vector2.zero;
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

        /// <summary>Полноэкранные картинки поверх всего (холст ScreenSpaceOverlay).</summary>
        private static class Overlay
        {
            public static GameObject Canvas(string name, int order)
            {
                var root = new GameObject(name);
                DontDestroyOnLoad(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = order;
                return root;
            }

            public static RawImage Image(GameObject root, Texture texture)
            {
                var go = new GameObject("Image", typeof(RectTransform), typeof(RawImage));
                go.transform.SetParent(root.transform, false);
                var img = go.GetComponent<RawImage>();
                img.texture = texture;
                img.raycastTarget = false;
                var rt = img.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                return img;
            }

            // Белая полоса с прозрачностью: нижние 40 % плавно проявляются, выше непрозрачно
            public static Texture2D SoftEdge()
            {
                const int n = 128;
                var tex = new Texture2D(1, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                for (var y = 0; y < n; y++)
                    tex.SetPixel(0, y, new Color(1, 1, 1, Mathf.SmoothStep(0f, 1f, y / (n * 0.4f))));
                tex.Apply();
                return tex;
            }

            // Белое кольцо: прозрачная середина, к краям непрозрачно
            public static Texture2D Vignette()
            {
                const int n = 128;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var d = new Vector2(x / (n - 1f) - 0.5f, y / (n - 1f) - 0.5f).magnitude / 0.7071f;
                    tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.SmoothStep(0f, 1f, (d - 0.3f) / 0.7f)));
                }
                tex.Apply();
                return tex;
            }
        }
    }
}
