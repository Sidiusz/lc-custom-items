using System.Collections;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

namespace CustomItems.Items
{
    /// <summary>
    /// Вейп-игрушка (три модели: PYXL, VAPEFIT, OXYA; отличаются моделью и экраном, поведение одно).
    ///
    /// Держишь ЛКМ — затягиваешься (рука подносит вейп ко рту, как TZP), трещит испаритель,
    /// на экране растёт нагрев. Отпустил — выдыхаешь облако пара: чем дольше тянул, тем больше
    /// облако, и висит оно долго (VapeVapor). Держишь дольше OverheatSeconds — вейп перегревается:
    /// пищит, шипит, обжигает на OverheatDamage и всё равно выпускает большое облако.
    /// В этот момент с шансом ExplodeChance % он взрывается: урон ExplodeDamage (при полном
    /// здоровье не убивает), вейп пропадает, а у погибшего на теле «затянулся мама не горюй».
    ///
    /// На экране заряд батареи (заряжается на зарядке корабля, как фонарик; одноразовый OXYA —
    /// нет, см. rechargeable и VapeCharger в плагине), нагрев и у OXYA счётчик затяжек (сохраняется с кораблём). Экран горит, пока вейп в руке, и пару секунд после.
    ///
    /// Затяжку решает тот, кто держит вейп: события (начал, выдохнул, перегрел, взорвал) уходят
    /// хосту и от него всем именованными сообщениями Netcode, и каждый клиент сам играет звук,
    /// анимацию и рисует пар. Урон получает только сам курильщик, смерть синхронизирует игра.
    /// </summary>
    public class VapeItem : CustomGrabbable
    {
        // Задаются из конфига в Plugin
        public static float OverheatSeconds = 6f;
        public static int OverheatDamage = 10;
        public static float ExplodeChance = 1f;
        public static int ExplodeDamage = 50;
        public static float PuffSecondsPerCharge = 240f;
        public static float VaporSeconds = 35f;

        public const string DeathCause = "Затянулся мама не горюй";
        private const string AnimBool = "useTZPItem"; // поза TZP-Inhalant: предмет у рта
        private const float HotFrom = 0.65f;          // с этой доли OverheatSeconds пищит и мигает
        private const float ScreenAfterDrop = 3f;
        private const string EventMessage = ModInfo.Guid + ".vape";
        private const byte KindStart = 0, KindExhale = 1, KindOverheat = 2, KindExplode = 3, KindStop = 4;

        [Tooltip("Какой экран рисовать на объекте Screen модели")]
        public VapeScreen.Style screenStyle = VapeScreen.Style.Pixl;

        [Tooltip("Заряжается на зарядке корабля. Выключено — одноразовый: сел, и всё (OXYA)")]
        public bool rechargeable = true;

        [Tooltip("Яркость экрана (HDRP emissive)")]
        public float screenGlow = 6f;

        [Tooltip("Цвет пара")]
        public Color vaporColor = new Color(0.93f, 0.95f, 0.98f);

        private static CustomMessagingManager registeredOn;

        private AudioSource loop;
        private VapeScreen screen;
        private Transform mouth;
        private bool inhaling;
        private float inhaleStart, heatAfter, heatAfterAt;
        private int puffs;
        private bool warned, finished;
        private float screenUntil;
        private PlayerControllerB smoker;

        private float Hold => inhaling ? Time.time - inhaleStart : 0f;
        private float Charge => insertedBattery != null ? Mathf.Clamp01(insertedBattery.charge) : 1f;

        public override void Start()
        {
            if (templateItemName == "Rubber Ducky")
                templateItemName = "TZP-Inhalant";
            // Пока ассет Item не настроен в Unity, нужное включаем отсюда
            itemProperties.holdButtonUse = true;
            itemProperties.syncUseFunction = false;
            itemProperties.requiresBattery = true;
            itemProperties.batteryUsage = PuffSecondsPerCharge;
            itemProperties.automaticallySetUsingPower = false; // ЛКМ держат, а не включают
            itemProperties.saveItemVariable = true;
            base.Start();
            // Предмет из кода (VapeRegistration) приходит без оффсетов руки: берём их у TZP,
            // дальше их можно подогнать окном настройки (F8)
            if (itemProperties.positionOffset == Vector3.zero && itemProperties.rotationOffset == Vector3.zero)
            {
                var template = FindTemplate();
                if (template != null)
                {
                    itemProperties.positionOffset = template.positionOffset;
                    itemProperties.rotationOffset = template.rotationOffset;
                }
            }
            // Отпускание ЛКМ проходит через ту же паузу, что и нажатие: она должна быть короткой
            useCooldown = 0.05f;
            if (insertedBattery == null)
                insertedBattery = new Battery(false, 1f);

            loop = gameObject.AddComponent<AudioSource>();
            loop.clip = EmbeddedAudio.Get("VapeInhale");
            loop.loop = true;
            loop.playOnAwake = false;
            loop.spatialBlend = 1f;
            loop.rolloffMode = AudioRolloffMode.Linear;
            loop.maxDistance = 14f;
            loop.volume = 0.7f;
            if (audioSource != null)
                loop.outputAudioMixerGroup = audioSource.outputAudioMixerGroup;

            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Mouth")
                    mouth = t;
                else if (t.name == "Screen" && t.TryGetComponent<Renderer>(out var r))
                    screen = new VapeScreen(screenStyle, r, screenGlow);
            }
            RegisterMessages();
        }

        public override int GetItemDataToSave() => puffs;

        public override void LoadItemSaveData(int saveData) => puffs = Mathf.Max(0, saveData);

        // ---------------- ввод (только у того, кто держит)

        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            if (playerHeldBy == null || !IsLocal(playerHeldBy) || finished)
                return;
            if (buttonDown)
            {
                if (inhaling || playerHeldBy.isPlayerDead)
                    return;
                if (insertedBattery != null && (insertedBattery.empty || insertedBattery.charge <= 0f))
                {
                    HUDManager.Instance.DisplayTip(itemProperties.itemName,
                        rechargeable ? "The vape is dead." : "The disposable vape is empty.");
                    isBeingUsed = false;
                    return;
                }
                isBeingUsed = true;
                Send(KindStart, 0f);
            }
            else if (inhaling)
            {
                Release();
            }
        }

        private void Release()
        {
            isBeingUsed = false;
            Send(KindExhale, Hold);
        }

        public override void UseUpBatteries()
        {
            base.UseUpBatteries();
            if (inhaling && playerHeldBy != null && IsLocal(playerHeldBy))
                Release();
        }

        public override void PocketItem()
        {
            if (inhaling && playerHeldBy != null && IsLocal(playerHeldBy))
                Release();
            base.PocketItem();
        }

        public override void DiscardItem()
        {
            if (inhaling && playerHeldBy != null && IsLocal(playerHeldBy))
                Release();
            screenUntil = Time.time + ScreenAfterDrop;
            base.DiscardItem();
        }

        public override void Update()
        {
            base.Update();
            var holder = playerHeldBy;
            if (inhaling && holder != null && IsLocal(holder) && !finished)
            {
                // Отпускание ЛКМ могло потеряться (пауза, меню): проверяем кнопку сами
                var action = IngamePlayerSettings.Instance?.playerInput?.actions?.FindAction("ActivateItem", false);
                if (holder.isPlayerDead || (action != null && !action.IsPressed()))
                    Release();
                else if (Hold >= OverheatSeconds)
                {
                    isBeingUsed = false;
                    if (Random.value * 100f < ExplodeChance)
                        Explode(holder);
                    else
                        Send(KindOverheat, Hold);
                }
            }
            else if (inhaling && (holder == null || holder.isPlayerDead) && smoker != null && !IsLocal(smoker))
            {
                // Курильщик пропал, а события не пришло: тихо гасим у себя
                Apply(smoker, KindStop, 0f, transform.position, puffs, Charge);
            }

            if (inhaling)
            {
                var heat = Hold / OverheatSeconds;
                loop.pitch = 1f + 0.3f * heat;
                loop.volume = 0.55f + 0.35f * heat;
                if (!warned && heat >= HotFrom)
                {
                    warned = true;
                    PlayClip("VapeBeep", 0.5f);
                }
            }
            DrawScreen();
        }

        // ---------------- экран

        private void DrawScreen()
        {
            if (screen == null || isPocketed)
                return;
            var on = isHeld || playerHeldBy != null || Time.time < screenUntil;
            var heat = inhaling ? Mathf.Clamp01(Hold / OverheatSeconds)
                : heatAfter * Mathf.Clamp01(1f - (Time.time - heatAfterAt) / 3f);
            // Перегревается: экран мигает
            var blink = inhaling && heat >= HotFrom && Mathf.Repeat(Time.time * 6f, 1f) < 0.3f;
            screen.Draw(on && !blink, Charge, heat, puffs, inhaling);
        }

        // ---------------- взрыв (решает курильщик)

        private void Explode(PlayerControllerB holder)
        {
            finished = true;
            Send(KindExplode, Hold);
            StartCoroutine(HurtAndVanish(holder));
        }

        private IEnumerator HurtAndVanish(PlayerControllerB holder)
        {
            yield return null;
            HUDManager.Instance.ShakeCamera(ScreenShakeType.Big);
            if (holder.health <= ExplodeDamage)
                holder.KillPlayer(holder.gameplayCamera.transform.forward * -3f + Vector3.up * 2f, spawnBody: true,
                    causeOfDeath: CauseOfDeath.Blast);
            else
                holder.DamagePlayer(ExplodeDamage, hasDamageSFX: true, callRPC: true, causeOfDeath: CauseOfDeath.Blast);

            // Вейпа больше нет: убираем из слота у всех. Погибший его уронил — тогда убирает хост
            var slot = System.Array.IndexOf(holder.ItemSlots, this);
            if (slot >= 0 && !holder.isPlayerDead)
                holder.DestroyItemInSlotAndSync(slot);
        }

        private IEnumerator DespawnIfDropped()
        {
            yield return new WaitForSeconds(1f);
            if (NetworkObject != null && NetworkObject.IsSpawned && (playerHeldBy == null || playerHeldBy.isPlayerDead))
                NetworkObject.Despawn();
        }

        // ---------------- события у всех клиентов

        private void Apply(PlayerControllerB player, byte kind, float hold, Vector3 at, int newPuffs, float charge)
        {
            if (insertedBattery != null && player != null && !IsLocal(player))
                insertedBattery.charge = charge;
            switch (kind)
            {
                case KindStart:
                    inhaling = true;
                    warned = false;
                    smoker = player;
                    inhaleStart = Time.time;
                    SetPose(player, true);
                    if (loop.clip != null)
                    {
                        loop.time = Random.Range(0f, loop.clip.length);
                        loop.Play();
                    }
                    return;

                case KindStop:
                case KindExhale:
                case KindOverheat:
                case KindExplode:
                    if (inhaling)
                    {
                        heatAfter = Mathf.Clamp01(hold / OverheatSeconds);
                        heatAfterAt = Time.time;
                    }
                    inhaling = false;
                    loop.Stop();
                    SetPose(player, false);
                    smoker = null;
                    break;
            }
            if (kind == KindStop)
                return;

            puffs = Mathf.Max(puffs, newPuffs);
            switch (kind)
            {
                case KindExplode:
                    finished = true;
                    if (IsServer)
                        StartCoroutine(DespawnIfDropped());
                    break;
                case KindExhale:
                    if (hold > 0.25f)
                        PlayClip("VapeExhale", Mathf.Clamp(0.3f + hold * 0.15f, 0.3f, 1f), 0.35f);
                    VapeVapor.Exhale(player, hold, vaporColor, VaporSeconds);
                    break;
                case KindOverheat:
                    PlayClip("VapeSizzle", 1f);
                    PlayClip("VapeExhale", 1f, 0.3f);
                    VapeVapor.Exhale(player, hold * 1.3f, vaporColor, VaporSeconds);
                    VapeVapor.Burst(MouthPosition(), 1f, vaporColor, VaporSeconds * 0.5f);
                    if (player != null && IsLocal(player))
                    {
                        HUDManager.Instance.DisplayTip(itemProperties.itemName, "The vape overheated!", isWarning: true);
                        HUDManager.Instance.ShakeCamera(ScreenShakeType.Small);
                        player.DamagePlayer(OverheatDamage, hasDamageSFX: true, callRPC: true, causeOfDeath: CauseOfDeath.Burning);
                    }
                    break;
            }
        }

        private Vector3 MouthPosition() => mouth != null ? mouth.position : transform.position + Vector3.up * 0.1f;

        // Поза «у рта» — та же булева анимации тела, что включает TZP-Inhalant
        private static void SetPose(PlayerControllerB player, bool on)
        {
            if (player != null && player.playerBodyAnimator != null)
                player.playerBodyAnimator.SetBool(AnimBool, on);
        }

        private void PlayClip(string name, float volume, float delay = 0f)
        {
            var clip = EmbeddedAudio.Get(name);
            if (clip == null || audioSource == null)
                return;
            if (delay <= 0f)
            {
                audioSource.PlayOneShot(clip, volume);
                WalkieTalkie.TransmitOneShotAudio(audioSource, clip, volume);
            }
            else
            {
                StartCoroutine(PlayLater(clip, volume, delay));
            }
        }

        private IEnumerator PlayLater(AudioClip clip, float volume, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (audioSource != null)
                audioSource.PlayOneShot(clip, volume);
        }

        private static bool IsLocal(PlayerControllerB player) =>
            player != null && player == GameNetworkManager.Instance?.localPlayerController;

        // ---------------- сеть: курильщик -> хост -> все остальные

        private void Send(byte kind, float hold)
        {
            var player = playerHeldBy;
            if (kind != KindStart && kind != KindStop)
                puffs++;
            var at = kind == KindExplode ? MouthPosition() : transform.position;
            Apply(player, kind, hold, at, puffs, Charge);
            if (kind == KindExplode)
                Blast(player, at);

            var net = NetworkManager.Singleton;
            if (net == null || !net.IsListening || net.CustomMessagingManager == null || player == null)
                return;
            var e = new Event
            {
                ObjectId = NetworkObjectId, PlayerId = player.playerClientId, Kind = kind, Hold = hold,
                At = at, Puffs = puffs, Charge = Charge,
            };
            if (net.IsServer)
                Relay(net, e, net.LocalClientId);
            else
                Write(net, NetworkManager.ServerClientId, e);
        }

        private struct Event
        {
            public ulong ObjectId, PlayerId;
            public byte Kind;
            public float Hold, Charge;
            public Vector3 At;
            public int Puffs;
        }

        private static void RegisterMessages()
        {
            var messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null || messaging == registeredOn)
                return;
            messaging.RegisterNamedMessageHandler(EventMessage, OnEventMessage);
            registeredOn = messaging;
        }

        private static void Relay(NetworkManager net, Event e, ulong except)
        {
            foreach (var clientId in net.ConnectedClientsIds)
            {
                if (clientId != NetworkManager.ServerClientId && clientId != except)
                    Write(net, clientId, e);
            }
        }

        private static void Write(NetworkManager net, ulong to, Event e)
        {
            using var writer = new FastBufferWriter(64, Allocator.Temp);
            writer.WriteValueSafe(e.ObjectId);
            writer.WriteValueSafe(e.PlayerId);
            writer.WriteValueSafe(e.Kind);
            writer.WriteValueSafe(e.Hold);
            writer.WriteValueSafe(e.At);
            writer.WriteValueSafe(e.Puffs);
            writer.WriteValueSafe(e.Charge);
            net.CustomMessagingManager.SendNamedMessage(EventMessage, to, writer, NetworkDelivery.Reliable);
        }

        private static void OnEventMessage(ulong sender, FastBufferReader reader)
        {
            var e = new Event();
            reader.ReadValueSafe(out e.ObjectId);
            reader.ReadValueSafe(out e.PlayerId);
            reader.ReadValueSafe(out e.Kind);
            reader.ReadValueSafe(out e.Hold);
            reader.ReadValueSafe(out e.At);
            reader.ReadValueSafe(out e.Puffs);
            reader.ReadValueSafe(out e.Charge);
            var net = NetworkManager.Singleton;
            if (net == null)
                return;
            if (net.IsServer)
                Relay(net, e, sender);
            else if (sender != NetworkManager.ServerClientId)
                return;

            var players = StartOfRound.Instance?.allPlayerScripts;
            var player = players != null && e.PlayerId < (ulong)players.Length ? players[(int)e.PlayerId] : null;
            var spawned = net.SpawnManager?.SpawnedObjects;
            VapeItem vape = null;
            if (spawned != null && spawned.TryGetValue(e.ObjectId, out var obj) && obj != null)
                vape = obj.GetComponent<VapeItem>();
            if (vape != null)
                vape.Apply(player, e.Kind, e.Hold, e.At, e.Puffs, e.Charge);
            // Взрыв рисуем и без вейпа: его могли уже убрать
            if (e.Kind == KindExplode)
                Blast(player, e.At);
        }

        /// <summary>Взрыв у всех: вспышка и звук ванильной мины без урона, облако пара, надпись на теле.</summary>
        private static void Blast(PlayerControllerB player, Vector3 at)
        {
            Landmine.SpawnExplosion(at, spawnExplosionEffect: true, killRange: 0f, damageRange: 0f, nonLethalDamage: 0,
                physicsForce: 0f);
            var clip = EmbeddedAudio.Get("VapeSizzle");
            if (clip != null)
                AudioSource.PlayClipAtPoint(clip, at, 1f);
            VapeVapor.Burst(at, 2.5f, new Color(0.85f, 0.86f, 0.88f), VaporSeconds);
            if (player != null)
                VapeVapor.Run(MarkBody(player));
        }

        // Если курильщик погиб, на его теле при сканировании вместо «Blast» — своя причина
        private static IEnumerator MarkBody(PlayerControllerB player)
        {
            for (var t = 0f; t < 5f; t += 0.25f)
            {
                yield return new WaitForSeconds(0.25f);
                if (player == null)
                    yield break;
                if (!player.isPlayerDead || player.deadBody == null)
                    continue;
                var node = player.deadBody.GetComponentInChildren<ScanNodeProperties>(true);
                if (node != null)
                    node.subText = "Причина смерти: " + DeathCause;
                yield break;
            }
        }
    }
}
