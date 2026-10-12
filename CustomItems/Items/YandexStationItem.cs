using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

namespace CustomItems.Items
{
    /// <summary>
    /// Умная колонка с часами (Яндекс Станция Мини). Это мебель корабля: покупается
    /// в разделе Decor терминала и ставится через режим строительства (B). Компоненты
    /// мебели игры (AutoParentToShip, PlaceableShipObject, InteractTrigger) мод
    /// навешивает на префаб при загрузке (Plugin.PrepareFurniture).
    ///
    /// На сегментном экране спереди горит внутриигровое время (24 часа), на орбите
    /// прочерки. Колонка отвечает фразами из <see cref="phraseSets"/>: на голос
    /// («Алиса, какая погода?», распознаёт плагин, см. AliceListener) и на E (случайная
    /// фраза). Пока говорит, на экране мордочка, кольцо Алисы пульсирует в такт.
    ///
    /// Фразы лежат по темам в подпапках Assets/Sources/Art/Audio/YandexStation/Phrases
    /// (Weather/Rainy, QuotaDone, Unknown...), список на префабе обновляется сам
    /// (Editor/ItemAssets.cs). Тему ответа выбирает <see cref="Topic"/> по тексту команды
    /// и состоянию игры (погода текущей луны, квота, дни до дедлайна).
    ///
    /// Сеть: тему и номер фразы выбирает тот, кто спросил, и рассылает через хост
    /// именованными сообщениями Netcode. NetcodePatcher не нужен.
    /// </summary>
    public class YandexStationItem : NetworkBehaviour
    {
        [Serializable]
        public class PhraseSet
        {
            [Tooltip("Тема: путь подпапки в Phrases, например Weather/Rainy")]
            public string name;
            public AudioClip[] clips = new AudioClip[0];
        }

        [Tooltip("Фразы по темам. Заполняется сам из подпапок Art/Audio/YandexStation/Phrases")]
        public PhraseSet[] phraseSets = new PhraseSet[0];

        [Tooltip("Цвет цифр и мордочки")]
        public Color displayColor = new Color(1f, 0.84f, 0.62f);
        [Tooltip("Яркость свечения цифр (множитель цвета свечения HDRP)")]
        public float displayGlow = 12f;
        [Tooltip("Цвет кольца Алисы")]
        public Color ringColor = new Color(0.58f, 0.36f, 1f);
        [Tooltip("Яркость кольца: молчит, слушает, говорит громче всего")]
        public float ringIdleGlow = 3f, ringListenGlow = 12f, ringTalkGlow = 25f;

        // ---- Настройки из конфига (Plugin) ----
        /// <summary>Слово, на которое колонка откликается.</summary>
        public static string WakeWord = "алиса";
        /// <summary>Как далеко от колонки игрок может к ней обращаться, м.</summary>
        public static float HearingRadius = 8f;
        /// <summary>Сколько секунд колонка ждёт команду после одного «Алиса».</summary>
        public static float ListenSeconds = 7f;
        public static string InteractTip = "Talk to Alice : [E]";

        /// <summary>Все колонки в мире: по ним плагин решает, слушать ли микрофон.</summary>
        public static readonly List<YandexStationItem> All = new List<YandexStationItem>();

        // Темы фраз. Свои темы просто добавляются папками, эти выбирает код.
        public const string TopicRandom = "Random", TopicListen = "Listen", TopicUnknown = "Unknown",
            TopicTime = "Time", TopicStop = "";

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
        private const long ColonBits = (1L << Colon) | (1L << (Colon + 1));

        private static CustomMessagingManager registeredOn;
        private static Material displayMaterial;
        private static AudioClip placeSfx;
        private static Sprite hoverIcon;

        private AudioSource audioSource;
        private GameObject[] segments;
        private Renderer[] segmentRenderers;
        private Material ringMaterial;
        private long shownMask = -1;

        private float talkUntil;
        private bool clockWhileTalking;
        private float listenUntil;
        private float answerListenAt;
        private string lastTopic;
        private int lastIndex = -1;
        private float nextBlink;
        private float blinkUntil;
        private float level;
        private readonly float[] samples = new float[256];

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            FindSegments();
        }

        private void OnEnable() => All.Add(this);

        private void OnDisable() => All.Remove(this);

        private void Start()
        {
            foreach (var set in phraseSets ?? new PhraseSet[0])
            {
                if (set != null && IsCustom(set.name))
                {
                    lock (CustomTopics)
                        CustomTopics.Add(set.name.ToLowerInvariant());
                }
            }
            RegisterMessages();
            SetupMaterials();
            SetupAudio();
            SetupFurniture();
            nextBlink = Time.time + Random.Range(2f, 4f);
        }

        // ---- Голос ----

        /// <summary>
        /// Распознанная фраза локального игрока (с главного потока). Колонка, к которой
        /// ближе всего, откликается на «Алиса, …», а после одного «Алиса» ждёт команду.
        /// </summary>
        public static void OnLocalSpeech(string text)
        {
            text = (text ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
            if (text.Length == 0)
                return;
            var station = Nearest(HearingRadius);
            if (station == null)
                return;

            var wake = text.IndexOf(WakeWord, StringComparison.Ordinal);
            string command;
            if (wake >= 0)
            {
                // «Алиса», «алисы», «алисонька»: пропускаем слово целиком
                var end = text.IndexOf(' ', wake);
                command = end < 0 ? "" : text.Substring(end + 1).Trim();
            }
            else if (Time.time < station.listenUntil)
            {
                command = text;
            }
            else
            {
                return; // говорят не с колонкой
            }

            if (command.Length == 0)
            {
                // Часто «Алиса, …» приходит двумя кусками: после имени пауза. Отзываемся
                // «Да?» не сразу, а если за секунду не пришла сама команда.
                station.listenUntil = Time.time + ListenSeconds;
                station.answerListenAt = Time.time + 1.2f;
                return;
            }
            station.listenUntil = 0f;
            station.answerListenAt = 0f;
            // [unk] — распознавание не разобрало слово: это вопрос, который колонка не поняла
            var known = command.Replace("[unk]", " ").Trim();
            var topic = known.Length == 0 ? TopicUnknown : Topic(known);
            if (topic == TopicUnknown)
                topic = station.CustomTopic(known) ?? topic;
            station.Say(topic);
        }

        /// <summary>Есть ли колонка рядом с локальным игроком (тогда плагин слушает микрофон).</summary>
        public static bool AnyNearLocalPlayer() => Nearest(HearingRadius) != null;

        private static YandexStationItem Nearest(float radius)
        {
            var player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null || player.isPlayerDead || All.Count == 0)
                return null;
            var pos = player.transform.position;
            YandexStationItem best = null;
            var bestDist = radius * radius;
            foreach (var s in All)
            {
                if (s == null || !s.isActiveAndEnabled)
                    continue;
                var d = (s.transform.position - pos).sqrMagnitude;
                if (d <= bestDist)
                {
                    best = s;
                    bestDist = d;
                }
            }
            return best;
        }

        /// <summary>
        /// Свои темы: подпапка Phrases, названная по-русски самой фразой («расскажи про компанию»),
        /// срабатывает, когда эта фраза есть в команде. Такие названия плагин добавляет в распознавание.
        /// </summary>
        public static readonly HashSet<string> CustomTopics = new HashSet<string>();

        private string CustomTopic(string command) =>
            phraseSets?.FirstOrDefault(s => s != null && IsCustom(s.name) && command.Contains(s.name.ToLowerInvariant()))?.name;

        private static bool IsCustom(string name) => name != null && name.Any(c => c >= 'а' && c <= 'я' || c >= 'А' && c <= 'Я');

        /// <summary>Тема ответа по тексту команды и состоянию игры.</summary>
        public static string Topic(string command)
        {
            bool Has(params string[] words) => words.Any(w => command.Contains(w));

            if (Has("стоп", "хватит", "замолчи", "тихо", "выключ"))
                return TopicStop;
            if (Has("погод", "дожд", "туман", "гроз"))
                return "Weather/" + WeatherName();
            // Дедлайн раньше квоты: «сколько дней до сдачи квоты» — про дни
            if (Has("дедлайн", "срок", "сколько дней", "дней остал", "до сдачи"))
            {
                var days = TimeOfDay.Instance != null ? TimeOfDay.Instance.daysUntilDeadline : 3;
                return "Deadline" + Mathf.Clamp(days, 0, 3);
            }
            if (Has("квот"))
            {
                var tod = TimeOfDay.Instance;
                return tod != null && tod.quotaFulfilled >= tod.profitQuota ? "QuotaDone" : "QuotaNotDone";
            }
            if (Has("врем", "который час", "сколько час"))
                return TopicTime;
            if (Has("привет", "здравств", "хай", "добр"))
                return "Greeting";
            if (Has("шутк", "пошути", "анекдот", "рассмеши"))
                return "Joke";
            if (Has("музык", "песн", "спой", "включи"))
                return "Music";
            if (Has("спасибо", "благодар", "молодец", "умница"))
                return "Thanks";
            if (Has("как дела", "как ты", "как жизнь"))
                return "HowAreYou";
            return TopicUnknown;
        }

        // Погода луны, на которой (или к которой) летит корабль: None → Clear
        private static string WeatherName()
        {
            var level = StartOfRound.Instance != null ? StartOfRound.Instance.currentLevel : null;
            var weather = level != null ? level.currentWeather : LevelWeatherType.None;
            return weather == LevelWeatherType.None ? "Clear" : weather.ToString();
        }

        // ---- Речь ----

        /// <summary>Сказать фразу на тему (с подменой отсутствующей темы на «не поняла») и разослать всем.</summary>
        public void Say(string topic)
        {
            askedLocally = true;
            if (topic == TopicStop)
            {
                Speak(TopicStop, -1);
                SendSpeak(TopicStop, -1);
                return;
            }

            var set = FindSet(topic) ?? FindSet(TopicUnknown) ?? FindSet(TopicRandom);
            if (set == null || set.clips.Length == 0)
                return;
            var index = Random.Range(0, set.clips.Length);
            if (set.clips.Length > 1 && set.name == lastTopic && index == lastIndex)
                index = (index + 1 + Random.Range(0, set.clips.Length - 1)) % set.clips.Length;
            Speak(set.name, index);
            SendSpeak(set.name, index);
        }

        private PhraseSet FindSet(string topic)
        {
            if (phraseSets == null)
                return null;
            foreach (var s in phraseSets)
            {
                if (s != null && s.clips != null && s.clips.Length > 0
                    && string.Equals(s.name, topic, StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            return null;
        }

        private bool IsTalking => Time.time < talkUntil && audioSource != null && audioSource.isPlaying;

        private void Speak(string topic, int index)
        {
            if (audioSource == null)
                return;
            if (topic == TopicStop)
            {
                audioSource.Stop();
                talkUntil = 0f;
                return;
            }
            var set = FindSet(topic);
            if (set == null || index < 0 || index >= set.clips.Length || set.clips[index] == null)
                return;

            var clip = set.clips[index];
            lastTopic = set.name;
            lastIndex = index;
            clockWhileTalking = set.name == TopicTime;
            audioSource.Stop();
            audioSource.clip = clip;
            audioSource.Play();
            WalkieTalkie.TransmitOneShotAudio(audioSource, clip);
            talkUntil = Time.time + clip.length;

            // Шум слышат монстры; считаем его только у того, кто спросил, чтобы не дублировать
            if (askedLocally)
            {
                RoundManager.Instance?.PlayAudibleNoise(transform.position, NoiseRange, NoiseLoudness, 0,
                    StartOfRound.Instance != null && StartOfRound.Instance.hangarDoorsClosed);
            }
        }

        private bool askedLocally;

        private void OnInteract(PlayerControllerB player)
        {
            if (player != null && player == GameNetworkManager.Instance?.localPlayerController && !IsTalking)
                Say(TopicRandom);
        }

        // ---- Экран ----

        private void Update()
        {
            if (segments == null)
                return;
            if (answerListenAt > 0f && Time.time > answerListenAt)
            {
                answerListenAt = 0f;
                if (!IsTalking)
                    Say(TopicListen);
            }
            var talking = IsTalking;
            UpdateLevel(talking);
            UpdateRing(talking);
            ShowMask(talking && !clockWhileTalking ? FaceMask() : ClockMask());
        }

        private long ClockMask()
        {
            var colonOn = Time.time % 1f < 0.5f;
            var tod = TimeOfDay.Instance;
            if (tod == null || !tod.currentDayTimeStarted)
                return Digit(0, Dash) | Digit(1, Dash) | Digit(2, Dash) | Digit(3, Dash) | (colonOn ? ColonBits : 0);

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
            var glow = talking ? Mathf.Lerp(ringIdleGlow * 2f, ringTalkGlow, level)
                : Time.time < listenUntil ? ringListenGlow * (0.8f + 0.2f * Mathf.Sin(Time.time * 6f))
                : ringIdleGlow;
            ringMaterial.SetColor("_EmissiveColor", ringColor * glow);
        }

        private static long Digit(int position, int segmentMask) => (long)segmentMask << (position * 7);

        private static int Mask(string segs)
        {
            var m = 0;
            foreach (var c in segs)
                m |= 1 << SegmentNames.IndexOf(c);
            return m;
        }

        private void ShowMask(long mask)
        {
            if (mask == shownMask)
                return;
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i] != null)
                    segments[i].SetActive((mask & (1L << i)) != 0);
            }
            shownMask = mask;
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
                var i = Array.IndexOf(Names, t.name);
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

        // Звук через тот же микшер, что у предметов игры (громкость из настроек, эффекты)
        private void SetupAudio()
        {
            if (audioSource == null)
                return;
            var items = StartOfRound.Instance?.allItemsList?.itemsList;
            var source = items?.Select(i => i != null && i.spawnPrefab != null ? i.spawnPrefab.GetComponent<AudioSource>() : null)
                .FirstOrDefault(a => a != null && a.outputAudioMixerGroup != null);
            if (source != null)
                audioSource.outputAudioMixerGroup = source.outputAudioMixerGroup;
        }

        // Мебель: иконка и подсказка E, звук установки, место по умолчанию на полу корабля
        private void SetupFurniture()
        {
            var trigger = GetComponentInChildren<InteractTrigger>(true);
            if (trigger != null)
            {
                if (hoverIcon == null)
                    hoverIcon = FindObjectsOfType<InteractTrigger>().Select(t => t.hoverIcon).FirstOrDefault(i => i != null);
                trigger.hoverIcon = hoverIcon;
                trigger.hoverTip = InteractTip;
                trigger.onInteract.AddListener(OnInteract);
            }

            var placeable = GetComponentInChildren<PlaceableShipObject>(true);
            var parent = GetComponent<AutoParentToShip>();
            if (placeable != null)
            {
                if (placeSfx == null)
                    placeSfx = FindObjectsOfType<PlaceableShipObject>().Select(p => p.placeObjectSFX).FirstOrDefault(c => c != null);
                placeable.placeObjectSFX = placeSfx;
                if (parent != null)
                    parent.unlockableID = placeable.unlockableID;
            }
            if (parent != null)
                StartCoroutine(DefaultSpot(parent));
        }

        // Купленная, но ещё не поставленная колонка: кладём на пол посреди корабля, а не в начало координат
        private IEnumerator DefaultSpot(AutoParentToShip parent)
        {
            yield return new WaitForSeconds(1f);
            var ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            if (ship == null || parent.positionOffset != Vector3.zero)
                yield break;
            var from = ship.position + ship.up * 1.5f;
            var mask = LayerMask.GetMask("Room", "Colliders");
            var floor = Physics.Raycast(from, -ship.up, out var hit, 5f, mask, QueryTriggerInteraction.Ignore)
                ? hit.point
                : ship.position;
            var placeable = GetComponentInChildren<PlaceableShipObject>(true);
            var lift = placeable != null ? placeable.yOffset : 0.05f;
            parent.positionOffset = ship.InverseTransformPoint(floor + ship.up * lift);
        }

        // ---- Синхронизация фразы: спросивший → хост → остальные ----

        private static void RegisterMessages()
        {
            var messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null || messaging == registeredOn)
                return;
            messaging.RegisterNamedMessageHandler(SpeakMessage, OnSpeakMessage);
            registeredOn = messaging;
        }

        private void SendSpeak(string topic, int index)
        {
            var nm = NetworkManager;
            if (nm == null || !nm.IsListening)
                return;
            if (IsServer)
                Broadcast(topic, index, except: nm.LocalClientId);
            else
                Send(topic, index, NetworkManager.ServerClientId);
        }

        private void Send(string topic, int index, ulong clientId)
        {
            using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(int) + 8 + topic.Length * 2, Allocator.Temp);
            writer.WriteValueSafe(NetworkObjectId);
            writer.WriteValueSafe(topic);
            writer.WriteValueSafe(index);
            NetworkManager.CustomMessagingManager.SendNamedMessage(SpeakMessage, clientId, writer, NetworkDelivery.Reliable);
        }

        private void Broadcast(string topic, int index, ulong except)
        {
            foreach (var clientId in NetworkManager.ConnectedClientsIds)
            {
                if (clientId != NetworkManager.ServerClientId && clientId != except)
                    Send(topic, index, clientId);
            }
        }

        // Хост получает фразу от спросившего, играет её и пересылает остальным;
        // клиенты получают её от хоста и просто играют.
        private static void OnSpeakMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong objectId);
            reader.ReadValueSafe(out string topic);
            reader.ReadValueSafe(out int index);
            var station = Find(objectId);
            if (station == null)
                return;
            station.askedLocally = false;
            station.Speak(topic, index);
            if (station.IsServer && sender != NetworkManager.ServerClientId)
                station.Broadcast(topic, index, except: sender);
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
