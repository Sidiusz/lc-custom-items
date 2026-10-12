using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Умная колонка с часами (Яндекс Станция Мини). На сегментном экране спереди
    /// горит внутриигровое время (как часы на HUD, только в 24-часовом формате),
    /// пока корабль на орбите — прочерки. По ЛКМ колонка говорит случайную фразу из
    /// <see cref="phrases"/>, и пока говорит, вместо часов на экране мордочка: глаза
    /// моргают, рот открывается в такт голосу, кольцо Алисы на крышке пульсирует.
    ///
    /// Фразы: все .wav из Assets/Sources/Art/Audio/YandexStation/Phrases, список на
    /// префабе обновляется сам (Editor/ItemAssets.cs) при открытии проекта и сборке бандла.
    ///
    /// Сеть: нажатие синхронизирует игра (Sync Use Function), но случайную фразу
    /// выбирает только владелец и рассылает номер через хост именованными сообщениями
    /// Netcode, чтобы все слышали одно и то же. NetcodePatcher не нужен.
    /// </summary>
    public class YandexStationItem : CustomGrabbable
    {
        [Tooltip("Фразы, из которых колонка выбирает случайную. Заполняется сам из Art/Audio/YandexStation/Phrases")]
        public AudioClip[] phrases = new AudioClip[0];

        [Tooltip("Цвет цифр и мордочки")]
        public Color displayColor = new Color(1f, 0.84f, 0.62f);
        [Tooltip("Яркость свечения цифр (множитель цвета свечения HDRP)")]
        public float displayGlow = 12f;
        [Tooltip("Цвет кольца Алисы")]
        public Color ringColor = new Color(0.58f, 0.36f, 1f);
        [Tooltip("Яркость кольца, когда колонка молчит и когда говорит громче всего")]
        public float ringIdleGlow = 3f, ringTalkGlow = 25f;

        private const string SpeakMessage = ModInfo.Guid + ".yandexstation.speak";
        private const float NoiseRange = 12f;
        private const float NoiseLoudness = 0.5f;

        // Сегменты: 4 цифры по 7 (a верх, b верх-право, c низ-право, d низ, e низ-лево,
        // f верх-лево, g середина), затем двоеточие, затем рты
        private const string SegmentNames = "abcdefg";
        private const int Colon = 28;
        private const int MouthSmile = 30, MouthHalf = 31, MouthOpen = 32;
        private static readonly string[] Names = BuildNames();

        private static readonly int[] DigitMasks =
        {
            Mask("abcdef"), Mask("bc"), Mask("abdeg"), Mask("abcdg"), Mask("bcfg"),
            Mask("acdfg"), Mask("acdefg"), Mask("abc"), Mask("abcdefg"), Mask("abcdfg"),
        };
        private static readonly int EyeOpen = Mask("abfg");
        private static readonly int EyeClosed = Mask("g");
        private static readonly int Dash = Mask("g");

        private static CustomMessagingManager registeredOn;
        private static Material displayMaterial;

        private GameObject[] segments;
        private Renderer[] segmentRenderers;
        private Material ringMaterial;
        private long shownMask = -1;
        private bool shownPocketed;

        private float talkUntil;
        private int lastPhrase = -1;
        private float nextBlink;
        private float blinkUntil;
        private float level;
        private readonly float[] samples = new float[256];

        public override void Start()
        {
            base.Start();
            RegisterMessages();
            FindSegments();
            SetupMaterials();
            nextBlink = Time.time + Random.Range(2f, 4f);
        }

        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            // Игра вызывает это у всех, но фразу выбирает владелец и рассылает сам
            if (!buttonDown || !IsOwner || phrases == null || phrases.Length == 0 || IsTalking)
                return;

            var index = Random.Range(0, phrases.Length);
            if (phrases.Length > 1 && index == lastPhrase)
                index = (index + 1 + Random.Range(0, phrases.Length - 1)) % phrases.Length;
            Speak(index);
            SendSpeak(index);
        }

        private bool IsTalking => Time.time < talkUntil && audioSource != null && audioSource.isPlaying;

        private void Speak(int index)
        {
            if (audioSource == null || phrases == null || index < 0 || index >= phrases.Length || phrases[index] == null)
                return;
            var clip = phrases[index];
            lastPhrase = index;
            audioSource.Stop();
            audioSource.pitch = 1f;
            audioSource.clip = clip;
            audioSource.Play();
            WalkieTalkie.TransmitOneShotAudio(audioSource, clip);
            talkUntil = Time.time + clip.length;

            // Шум слышат монстры; считаем его только на владельце, чтобы не дублировать
            if (IsOwner)
            {
                RoundManager.Instance.PlayAudibleNoise(transform.position, NoiseRange, NoiseLoudness,
                    0, isInElevator && StartOfRound.Instance.hangarDoorsClosed);
            }
        }

        public override void Update()
        {
            base.Update();
            if (segments == null)
                return;

            var talking = IsTalking;
            UpdateLevel(talking);
            UpdateRing(talking);
            ShowMask(talking ? FaceMask() : ClockMask());
        }

        // ---- Экран ----

        private long ClockMask()
        {
            var colonOn = Time.time % 1f < 0.5f;
            var tod = TimeOfDay.Instance;
            if (tod == null || !tod.currentDayTimeStarted)
                return Digit(1, Dash) | Digit(2, Dash) | Digit(3, Dash) | Digit(0, Dash) | (colonOn ? ColonBits : 0);

            // Как HUDManager.SetClock: день начинается в 6:00 и длится numberOfHours часов
            var minutes = (int)(tod.normalizedTimeOfDay * (60f * tod.numberOfHours)) + 360;
            var hours = minutes / 60 % 24;
            minutes %= 60;
            long mask = (hours >= 10 ? Digit(0, DigitMasks[hours / 10]) : 0)
                        | Digit(1, DigitMasks[hours % 10])
                        | Digit(2, DigitMasks[minutes / 10])
                        | Digit(3, DigitMasks[minutes % 10]);
            return mask | (colonOn ? ColonBits : 0);
        }

        private long FaceMask()
        {
            if (Time.time > nextBlink)
            {
                blinkUntil = Time.time + 0.15f;
                nextBlink = Time.time + Random.Range(2f, 5f);
            }
            var eye = Time.time < blinkUntil ? EyeClosed : EyeOpen;
            var mouth = level > 0.35f ? MouthOpen : level > 0.12f ? MouthHalf : MouthSmile;
            return Digit(0, eye) | Digit(3, eye) | (1L << mouth);
        }

        // Громкость голоса 0..1, сглаженная: по ней открывается рот и светится кольцо
        private void UpdateLevel(bool talking)
        {
            var target = 0f;
            if (talking)
            {
                audioSource.GetOutputData(samples, 0);
                var sum = 0f;
                foreach (var s in samples)
                    sum += s * s;
                target = Mathf.Clamp01(Mathf.Sqrt(sum / samples.Length) * 4f);
            }
            level = Mathf.Lerp(level, target, Time.deltaTime * (target > level ? 25f : 10f));
        }

        private void UpdateRing(bool talking)
        {
            if (ringMaterial == null)
                return;
            var glow = talking ? Mathf.Lerp(ringIdleGlow * 2f, ringTalkGlow, level) : ringIdleGlow;
            ringMaterial.SetColor("_EmissiveColor", ringColor * glow);
        }

        private const long ColonBits = (1L << Colon) | (1L << (Colon + 1));

        private static long Digit(int position, int segmentMask) => (long)segmentMask << (position * 7);

        private static int Mask(string segs)
        {
            var m = 0;
            foreach (var c in segs)
                m |= 1 << SegmentNames.IndexOf(c);
            return m;
        }

        // Прячем через SetActive, как жидкость у Егермейстера: игра включает все
        // рендереры при доставании из кармана, а неактивные объекты не трогает.
        // В кармане рендереры выключены игрой, и экран не трогаем совсем.
        private void ShowMask(long mask)
        {
            if (isPocketed)
            {
                shownPocketed = true;
                return;
            }
            if (mask == shownMask && !shownPocketed)
                return;
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i] == null)
                    continue;
                var on = (mask & (1L << i)) != 0;
                segments[i].SetActive(on);
                if (on && segmentRenderers[i] != null)
                    segmentRenderers[i].enabled = true;
            }
            shownMask = mask;
            shownPocketed = false;
        }

        private static string[] BuildNames()
        {
            var names = new string[33];
            for (var d = 0; d < 4; d++)
                for (var s = 0; s < 7; s++)
                    names[d * 7 + s] = "Seg" + d + SegmentNames[s];
            names[Colon] = "ColonTop";
            names[Colon + 1] = "ColonBottom";
            names[MouthSmile] = "MouthSmile";
            names[MouthHalf] = "MouthHalf";
            names[MouthOpen] = "MouthOpen";
            return names;
        }

        private void FindSegments()
        {
            segments = new GameObject[Names.Length];
            segmentRenderers = new Renderer[Names.Length];
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                var i = System.Array.IndexOf(Names, t.name);
                if (i < 0)
                    continue;
                segments[i] = t.gameObject;
                segmentRenderers[i] = t.GetComponent<Renderer>();
            }
        }

        // Свечение задаём из кода, чтобы цвет и яркость правились полями на префабе.
        // У HDRP/Lit _EmissiveColor работает без ключевых слов шейдера.
        private void SetupMaterials()
        {
            foreach (var r in segmentRenderers)
            {
                if (r == null)
                    continue;
                if (displayMaterial == null)
                {
                    displayMaterial = new Material(r.sharedMaterial) { name = "YandexStationDisplay (glow)" };
                    displayMaterial.SetColor("_BaseColor", displayColor);
                    displayMaterial.SetColor("_EmissiveColor", displayColor * displayGlow);
                }
                r.sharedMaterial = displayMaterial;
            }

            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.name != "Ring")
                    continue;
                ringMaterial = r.material; // свой экземпляр: кольцо пульсирует у каждой колонки отдельно
                ringMaterial.SetColor("_BaseColor", ringColor);
            }
        }

        // ---- Синхронизация фразы: владелец → хост → остальные ----

        private static void RegisterMessages()
        {
            var messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null || messaging == registeredOn)
                return;
            messaging.RegisterNamedMessageHandler(SpeakMessage, OnSpeakMessage);
            registeredOn = messaging;
        }

        private void SendSpeak(int index)
        {
            var nm = NetworkManager;
            if (nm == null || !nm.IsListening)
                return;
            if (IsServer)
                Broadcast(index, except: nm.LocalClientId);
            else
                Send(index, NetworkManager.ServerClientId);
        }

        private void Send(int index, ulong clientId)
        {
            using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(int), Allocator.Temp);
            writer.WriteValueSafe(NetworkObjectId);
            writer.WriteValueSafe(index);
            NetworkManager.CustomMessagingManager.SendNamedMessage(SpeakMessage, clientId, writer, NetworkDelivery.Reliable);
        }

        private void Broadcast(int index, ulong except)
        {
            foreach (var clientId in NetworkManager.ConnectedClientsIds)
            {
                if (clientId != NetworkManager.ServerClientId && clientId != except)
                    Send(index, clientId);
            }
        }

        // Хост получает фразу от владельца, играет её и пересылает остальным;
        // клиенты получают её от хоста и просто играют.
        private static void OnSpeakMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong objectId);
            reader.ReadValueSafe(out int index);
            var speaker = Find(objectId);
            if (speaker == null)
                return;
            speaker.Speak(index);
            if (speaker.IsServer && sender != NetworkManager.ServerClientId)
                speaker.Broadcast(index, except: sender);
        }

        private static YandexStationItem Find(ulong objectId)
        {
            var spawned = NetworkManager.Singleton?.SpawnManager?.SpawnedObjects;
            if (spawned == null || !spawned.TryGetValue(objectId, out var obj) || obj == null)
                return null;
            return obj.GetComponent<YandexStationItem>();
        }
    }
}
