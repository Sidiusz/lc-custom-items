using System.Collections;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Бутылка Егермейстера. В каждой бутылке 1–3 глотка (выпадает случайно и
    /// сохраняется вместе с кораблём), пополнить нельзя. Глоток (ЛКМ) на 90 секунд
    /// кружит голову и делает голос ниже, глотки складываются (см. DrunkEffects).
    ///
    /// Сам глоток синхронизирует игра (SyncUse), а число оставшихся глотков хранит
    /// хост и раздаёт через именованные сообщения Netcode, поэтому NetcodePatcher не нужен.
    ///
    /// Сквозь стекло видна жидкость из трёх слоёв (Liquid1..Liquid3 в модели), их
    /// столько, сколько осталось глотков.
    /// </summary>
    public class JagermeisterItem : CustomGrabbable
    {
        public const int MaxSips = 3;

        private const string SipsMessage = ModInfo.Guid + ".jagermeister.sips";
        private const string RequestMessage = ModInfo.Guid + ".jagermeister.request";

        // Глоток: бутылка подносится ко рту ванильной анимацией TZP-Inhalant, звук
        // начинается, когда бутылка у рта, эффект и уровень жидкости меняются в конце.
        private const string DrinkAnimBool = "useTZPItem";
        private const float SoundDelay = 0.3f;
        private const float DrinkDuration = 1.4f;

        [Tooltip("Глоток и выдох (Art/Audio/Jagermeister, собирается Tools/Jagermeister/sound.sh)")]
        public AudioClip drinkClip;

        private static CustomMessagingManager registeredOn;

        private int sipsLeft = -1; // -1: ещё не знаем (клиент ждёт ответа хоста)
        private GameObject[] liquidLayers;

        public override void Start()
        {
            base.Start();
            useCooldown = 3f; // звук глотка с выдохом ~2.8 с
            RegisterMessages();
            FindLiquid();

            if (IsServer)
                EnsureSips();
            else if (NetworkManager != null && NetworkManager.IsConnectedClient)
                RequestSips();
            UpdateLiquid();
        }

        public override int GetItemDataToSave()
        {
            EnsureSips();
            return sipsLeft;
        }

        public override void LoadItemSaveData(int saveData)
        {
            sipsLeft = Mathf.Clamp(saveData, 0, MaxSips);
            UpdateLiquid();
        }

        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            if (!buttonDown || playerHeldBy == null)
                return;

            if (sipsLeft == 0)
            {
                if (playerHeldBy == GameNetworkManager.Instance.localPlayerController)
                    HUDManager.Instance.DisplayTip(itemProperties.itemName, "The bottle is empty.");
                return;
            }

            // Пока хост не ответил, клиент всё равно даёт выпить: хост пришлёт точное число.
            if (sipsLeft > 0)
                sipsLeft--;

            StopDrinking();
            drinking = StartCoroutine(Drink(playerHeldBy));

            if (IsServer)
                BroadcastSips();
        }

        public override void PocketItem()
        {
            StopDrinking();
            base.PocketItem();
        }

        public override void DiscardItem()
        {
            StopDrinking();
            base.DiscardItem();
        }

        private Coroutine drinking;
        private PlayerControllerB animatedPlayer;

        // Идёт на всех клиентах (глоток синхронизирует игра). Анимацию ставит только
        // владелец: позу рук остальным раздаёт сама игра вместе с анимациями игрока.
        private IEnumerator Drink(PlayerControllerB player)
        {
            if (player == GameNetworkManager.Instance.localPlayerController)
            {
                animatedPlayer = player;
                player.activatingItem = true;
                player.playerBodyAnimator.SetBool(DrinkAnimBool, true);
            }

            yield return new WaitForSeconds(SoundDelay);
            if (audioSource != null && drinkClip != null)
            {
                audioSource.pitch = Random.Range(0.95f, 1.05f);
                audioSource.PlayOneShot(drinkClip);
                WalkieTalkie.TransmitOneShotAudio(audioSource, drinkClip);
            }

            yield return new WaitForSeconds(DrinkDuration - SoundDelay);
            ResetAnimation();
            UpdateLiquid();
            DrunkEffects.AddSip(player);
            drinking = null;
        }

        // Бутылку убрали или выбросили посреди глотка: опускаем руку сразу, а глоток
        // всё равно засчитан (число глотков уже уменьшено у всех).
        private void StopDrinking()
        {
            if (drinking == null)
                return;
            StopCoroutine(drinking);
            drinking = null;
            ResetAnimation();
            UpdateLiquid();
            if (playerHeldBy != null)
                DrunkEffects.AddSip(playerHeldBy);
        }

        private void ResetAnimation()
        {
            if (animatedPlayer == null)
                return;
            animatedPlayer.activatingItem = false;
            animatedPlayer.playerBodyAnimator.SetBool(DrinkAnimBool, false);
            animatedPlayer = null;
        }

        private void FindLiquid()
        {
            liquidLayers = new GameObject[MaxSips];
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                for (var i = 0; i < MaxSips; i++)
                {
                    if (t.name == "Liquid" + (i + 1))
                        liquidLayers[i] = t.gameObject;
                }
            }
        }

        // Слои снизу вверх: при двух глотках видны Liquid1 и Liquid2. Прячем через
        // SetActive, а не renderer.enabled: игра включает все рендереры, когда предмет
        // достают из кармана (EnableItemMeshes), а неактивные объекты не трогает.
        private void UpdateLiquid()
        {
            if (liquidLayers == null || sipsLeft < 0)
                return;
            for (var i = 0; i < liquidLayers.Length; i++)
            {
                if (liquidLayers[i] != null)
                    liquidLayers[i].SetActive(i < sipsLeft);
            }
        }

        private void EnsureSips()
        {
            if (sipsLeft < 0)
                sipsLeft = Random.Range(1, MaxSips + 1);
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

        // Хост: клиент только что увидел бутылку и спрашивает, сколько в ней осталось
        private static void OnRequestMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong objectId);
            var bottle = Find(objectId);
            if (bottle == null || !bottle.IsServer)
                return;
            bottle.EnsureSips();
            bottle.SendSips(sender);
        }

        // Клиент: хост прислал точное число глотков
        private static void OnSipsMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong objectId);
            reader.ReadValueSafe(out int sips);
            var bottle = Find(objectId);
            if (bottle != null && sender == NetworkManager.ServerClientId)
            {
                bottle.sipsLeft = Mathf.Clamp(sips, 0, MaxSips);
                bottle.UpdateLiquid();
            }
        }

        private static JagermeisterItem Find(ulong objectId)
        {
            var spawned = NetworkManager.Singleton?.SpawnManager?.SpawnedObjects;
            if (spawned == null || !spawned.TryGetValue(objectId, out var obj) || obj == null)
                return null;
            return obj.GetComponent<JagermeisterItem>();
        }
    }
}
