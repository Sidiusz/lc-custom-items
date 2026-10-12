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
    /// </summary>
    public class JagermeisterItem : CustomGrabbable
    {
        public const int MaxSips = 3;

        private const string SipsMessage = Plugin.PluginGuid + ".jagermeister.sips";
        private const string RequestMessage = Plugin.PluginGuid + ".jagermeister.request";

        private static CustomMessagingManager registeredOn;
        private static AudioClip gulpClip;

        private int sipsLeft = -1; // -1: ещё не знаем (клиент ждёт ответа хоста)

        public override void Start()
        {
            base.Start();
            useCooldown = 1.5f;
            RegisterMessages();

            if (IsServer)
                EnsureSips();
            else if (NetworkManager != null && NetworkManager.IsConnectedClient)
                RequestSips();
        }

        public override int GetItemDataToSave()
        {
            EnsureSips();
            return sipsLeft;
        }

        public override void LoadItemSaveData(int saveData)
        {
            sipsLeft = Mathf.Clamp(saveData, 0, MaxSips);
        }

        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            if (!buttonDown || playerHeldBy == null)
                return;

            if (sipsLeft == 0)
            {
                if (playerHeldBy == GameNetworkManager.Instance.localPlayerController)
                    HUDManager.Instance.DisplayTip(itemProperties.itemName, Lang.T("The bottle is empty.", "Бутылка пуста."));
                return;
            }

            // Пока хост не ответил, клиент всё равно даёт выпить: хост пришлёт точное число.
            if (sipsLeft > 0)
                sipsLeft--;

            PlayGulp();
            DrunkEffects.AddSip(playerHeldBy);

            if (IsServer)
                BroadcastSips();
        }

        private void EnsureSips()
        {
            if (sipsLeft < 0)
                sipsLeft = Random.Range(1, MaxSips + 1);
        }

        private void PlayGulp()
        {
            if (audioSource == null)
                return;
            gulpClip ??= MakeGulpClip();
            audioSource.pitch = Random.Range(0.9f, 1.1f);
            audioSource.PlayOneShot(gulpClip);
            WalkieTalkie.TransmitOneShotAudio(audioSource, gulpClip);
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
                bottle.sipsLeft = Mathf.Clamp(sips, 0, MaxSips);
        }

        private static JagermeisterItem Find(ulong objectId)
        {
            var spawned = NetworkManager.Singleton?.SpawnManager?.SpawnedObjects;
            if (spawned == null || !spawned.TryGetValue(objectId, out var obj) || obj == null)
                return null;
            return obj.GetComponent<JagermeisterItem>();
        }

        // ---- Звук глотка: несколько «бульков», синтезируются один раз ----

        private static AudioClip MakeGulpClip()
        {
            const int rate = 44100;
            const int gulps = 3;
            const float gulpLength = 0.16f;
            const float gap = 0.26f;
            var total = (int)(rate * (gap * (gulps - 1) + gulpLength + 0.05f));
            var data = new float[total];
            var rng = new System.Random(1337);

            for (var g = 0; g < gulps; g++)
            {
                var start = (int)(rate * g * gap);
                var length = (int)(rate * gulpLength);
                var phase = 0.0;
                var noise = 0f;
                for (var i = 0; i < length && start + i < total; i++)
                {
                    var t = (float)i / length;
                    // Пузырь: тон быстро съезжает вниз, мягкая атака и затухание
                    var freq = Mathf.Lerp(340f, 140f, Mathf.Sqrt(t)) * (1f - 0.08f * g);
                    phase += 2 * System.Math.PI * freq / rate;
                    var env = Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.6f)) * (1f - t);
                    noise = Mathf.Lerp(noise, (float)(rng.NextDouble() * 2 - 1), 0.08f);
                    data[start + i] += ((float)System.Math.Sin(phase) * 0.8f + noise * 0.5f) * env * 0.7f;
                }
            }

            var clip = AudioClip.Create("JagermeisterGulp", total, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
