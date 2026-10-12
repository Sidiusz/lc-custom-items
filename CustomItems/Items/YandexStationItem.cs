using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

namespace CustomItems.Items
{
    /// <summary>
    /// Умная колонка с часами (Яндекс Станция Мини). Продаётся в магазине терминала,
    /// носится в руке и ставится куда угодно (просто положить). ЛКМ включает и
    /// выключает, E меняет цвет корпуса. Работает от батареи (полного заряда хватает
    /// на 30 минут работы, задаётся Battery Usage в Item), заряжается на зарядке корабля.
    ///
    /// Включённая колонка показывает внутриигровое время (24 часа, на орбите прочерки)
    /// и отвечает на голос из голосового чата: «Алиса, какая погода?» (распознаёт
    /// плагин, см. AliceListener). Пока говорит, на экране глаза: моргает, щурится,
    /// подмигивает, смотрит по сторонам. Пока не договорит, новых вопросов не слушает
    /// (<see cref="AllowInterrupt"/>). При погоде из <see cref="JammingWeathers"/>
    /// (солнечная вспышка) на луне колонка сбоит: экран мигает мусором, вместо ответа ошибка.
    ///
    /// Фразы лежат по темам в подпапках Assets/Sources/Art/Audio/YandexStation/Phrases
    /// (Weather/Rainy, QuotaDone, Unknown...), список на префабе обновляется сам
    /// (Editor/ItemAssets.cs). Тему выбирает <see cref="Topic"/> по тексту вопроса и
    /// состоянию игры. Погода модов берётся у WeatherRegistry, если он стоит.
    ///
    /// Сеть: вкл/выкл и цвет синхронизирует игра (Sync Use / Sync Interact LR), а
    /// фразу выбирает тот, кто спросил, и рассылает через хост именованными сообщениями
    /// Netcode. Опоздавшим хост присылает цвет и состояние. NetcodePatcher не нужен.
    /// </summary>
    public class YandexStationItem : CustomGrabbable
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

        [Header("Звуки Алисы (Art/Audio/YandexStation/Sounds)")]
        public AudioClip listenClip;
        public AudioClip listenTimeoutClip;
        public AudioClip turnOnClip;
        public AudioClip turnOffClip;

        [Header("Экран и цвет")]
        [Tooltip("Цвет цифр и глаз")]
        public Color displayColor = new Color(1f, 0.84f, 0.62f);
        [Tooltip("Яркость свечения цифр (множитель цвета свечения HDRP)")]
        public float displayGlow = 12f;
        [Tooltip("Цвет кольца Алисы")]
        public Color ringColor = new Color(0.58f, 0.36f, 1f);
        [Tooltip("Яркость кольца: включена, слушает, говорит громче всего")]
        public float ringIdleGlow = 3f, ringListenGlow = 12f, ringTalkGlow = 25f;
        [Tooltip("Цвета корпуса по E: серая, голубая, розовая, чёрная")]
        public Color[] bodyColors =
        {
            new Color(0.30f, 0.31f, 0.34f), new Color(0.36f, 0.53f, 0.72f),
            new Color(0.78f, 0.47f, 0.55f), new Color(0.045f, 0.045f, 0.05f),
        };
        [Tooltip("Цвета ножки, по одному на цвет корпуса")]
        public Color[] trimColors =
        {
            new Color(0.10f, 0.10f, 0.11f), new Color(0.12f, 0.18f, 0.26f),
            new Color(0.36f, 0.20f, 0.25f), new Color(0.02f, 0.02f, 0.022f),
        };

        // ---- Настройки из конфига (Plugin) ----
        public static string WakeWord = "алиса";
        public static float HearingRadius = 8f;
        public static float ListenSeconds = 6f;
        /// <summary>Можно ли задать новый вопрос, пока колонка ещё отвечает.</summary>
        public static bool AllowInterrupt;
        /// <summary>Погоды (названия как у игры или WeatherRegistry), при которых колонка сбоит.</summary>
        public static string[] JammingWeathers = { "Solar Flare" };
        public static string OnOffTip = "Turn on/off : [LMB]";
        public static string TalkTip = "Talk : [E]";

        /// <summary>Все колонки в мире: по ним плагин решает, слушать ли голос.</summary>
        public static readonly List<YandexStationItem> All = new List<YandexStationItem>();

        public const string TopicGoodbye = "Goodbye", TopicRepeat = "Repeat", TopicCount = "#count",
            TopicNumbers = "Numbers/", TopicTimeIntro = "TimeIntro", TopicTimeError = "TimeError",
            TopicGameAsk = "Game/Ask", TopicGameUnknown = "Game/Unknown";
        /// <summary>Сколько секунд после «В какую игру хотите поиграть?» колонка ждёт ответ без «Алиса».</summary>
        public static float GameAnswerSeconds = 10f;
        public const string TopicNames = "Names/", TopicMyName = "#myname", TopicBattery = "#battery",
            TopicColor = "#color", TopicAgree = "Agree", TopicRefuse = "Refuse", TopicOff = "Off";

        // Записи цветов по порядку bodyColors (серая, голубая, розовая, чёрная): своя папка, иначе ближайшая
        private static readonly string[][] ColorFolders =
        {
            new[] { "Color/Grey", "Color/White" },
            new[] { "Color/Blue", "Color/DarkBlue" },
            new[] { "Color/Pink" },
            new[] { "Color/Black" },
        };

        /// <summary>
        /// Друзья по нику Steam: ник (без регистра и значков) → папка Names/&lt;Папка&gt;.
        /// Задаётся в конфиге строкой «ник,ник=Папка; ...».
        /// </summary>
        public static readonly Dictionary<string, string> FriendNames = new Dictionary<string, string>();

        public static void SetNames(string table)
        {
            FriendNames.Clear();
            foreach (var entry in (table ?? "").Split(';'))
            {
                var eq = entry.IndexOf('=');
                if (eq <= 0)
                    continue;
                var folder = entry.Substring(eq + 1).Trim();
                foreach (var nick in entry.Substring(0, eq).Split(','))
                {
                    var key = NormalizeNick(nick);
                    if (key.Length > 0 && folder.Length > 0)
                        FriendNames[key] = folder;
                }
            }
        }

        private static string NormalizeNick(string nick) =>
            new string((nick ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>Папка имени по нику: точное совпадение, иначе ник начинается с известного («Sidiusz*»).</summary>
        public static string NameFolder(string nick)
        {
            var key = NormalizeNick(nick);
            if (key.Length == 0)
                return null;
            if (FriendNames.TryGetValue(key, out var folder))
                return folder;
            return FriendNames.Where(n => key.StartsWith(n.Key)).OrderByDescending(n => n.Key.Length)
                .Select(n => n.Value).FirstOrDefault();
        }

        /// <summary>Тот же вопрос в течение стольких секунд — колонка отвечает фразой из Repeat.</summary>
        public static float RepeatSeconds = 60f;

        public const string TopicRandom = "Random", TopicUnknown = "Unknown", TopicError = "Error",
            TopicTime = "Time", TopicStop = "#stop", SoundListen = "#listen", SoundTimeout = "#timeout";

        private const string SpeakMessage = ModInfo.Guid + ".yandexstation.speak";
        private const string StateMessage = ModInfo.Guid + ".yandexstation.state";
        private const string RequestMessage = ModInfo.Guid + ".yandexstation.request";
        private const float NoiseRange = 12f;
        private const float NoiseLoudness = 0.5f;

        // Сегменты: 4 цифры по 7 (a верх, b верх-право, c низ-право, d низ, e низ-лево,
        // f верх-лево, g середина), затем двоеточие
        private const string SegmentNames = "abcdefg";
        private const int Colon = 28;
        private static readonly string[] Names = BuildNames();

        private static readonly int[] DigitMasks =
        {
            Mask("abcdef"), Mask("bc"), Mask("abdeg"), Mask("abcdg"), Mask("bcfg"),
            Mask("acdfg"), Mask("acdefg"), Mask("abc"), Mask("abcdefg"), Mask("abcdfg"),
        };
        private static readonly int Dash = Mask("g");
        private const long ColonBits = (1L << Colon) | (1L << (Colon + 1));

        // Глаза из сегментов цифр: (позиция левого глаза, правого, левый, правый)
        private static readonly int Open = Mask("abfg"), Closed = Mask("g"), Happy = Mask("abf"),
            Squint = Mask("ag"), Wide = Mask("abcdef"), Low = Mask("cdeg");
        private static readonly (int left, int right, int l, int r)[] Expressions =
        {
            (0, 3, Open, Open),     // смотрит
            (0, 3, Open, Open),
            (0, 3, Happy, Happy),   // улыбается глазами ^ ^
            (0, 3, Squint, Squint), // щурится
            (0, 3, Open, Closed),   // подмигивает
            (0, 3, Closed, Open),
            (0, 2, Open, Open),     // смотрит влево
            (1, 3, Open, Open),     // смотрит вправо
            (0, 3, Low, Low),       // смотрит вниз
            (0, 3, Wide, Wide),     // удивляется
        };
        private const int HappyFace = 2, WideFace = 9;
        private static readonly (int left, int right, int l, int r) ListenFace = (0, 3, Wide, Wide);

        private static CustomMessagingManager registeredOn;
        private static Material displayMaterial;
        private static MethodInfo weatherRegistryGet;
        private static bool weatherRegistryChecked;

        private GameObject[] segments;
        private Renderer[] segmentRenderers;
        private Material ringMaterial, bodyMaterial, trimMaterial;
        private long shownMask = -1;
        private bool shownPocketed;

        private bool isOn;
        private int goodbyes;
        private int colorIndex = -1; // -1: ещё не знаем (клиент ждёт хоста)
        private float talkUntil;
        private bool clockWhileTalking;
        private float listenUntil;
        private string lastTopic;
        private int lastIndex = -1;
        private int expression;
        private float nextExpression, blinkUntil, nextBlink;
        private float level;
        private bool askedLocally;
        private readonly float[] samples = new float[256];

        public bool IsOn => isOn && (insertedBattery == null || !insertedBattery.empty);

        public override void Start()
        {
            base.Start();
            useCooldown = 0.4f;
            RegisterMessages();
            FindSegments();
            SetupMaterials();
            if (IsServer)
                EnsureColor();
            else if (NetworkManager != null && NetworkManager.IsConnectedClient)
                RequestState();
            ApplyColor();
        }

        private void OnEnable() => All.Add(this);

        private void OnDisable() => All.Remove(this);

        // Цвет (случайный при покупке) и «включена» в одном числе: сохраняются вместе с кораблём
        public override int GetItemDataToSave()
        {
            EnsureColor();
            return colorIndex | (isOn ? 16 : 0);
        }

        public override void LoadItemSaveData(int saveData)
        {
            colorIndex = Mathf.Clamp(saveData & 15, 0, bodyColors.Length - 1);
            isOn = (saveData & 16) != 0;
            isBeingUsed = isOn;
            ApplyColor();
        }

        public override void SetControlTipsForItem()
        {
            HUDManager.Instance.ChangeControlTipMultiple(new[] { OnOffTip, TalkTip }, holdingItem: true, itemProperties);
        }

        // ЛКМ. Item.automaticallySetUsingPower: игра сама переключает isBeingUsed и не даёт
        // включить с пустой батареей, а сюда у всех приходит новое состояние в used.
        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            SwitchOn(used);
        }

        private void SwitchOn(bool on)
        {
            isBeingUsed = on;
            if (on == isOn)
                return;
            isOn = on;
            listenUntil = 0f;
            if (!on && audioSource != null)
            {
                audioSource.Stop();
                queued.Clear();
                talkUntil = 0f;
                // Выключают — прощается. Номер по счётчику выключений, чтобы у всех одна фраза
                var off = FindSet(TopicOff);
                var goodbye = FindSet(TopicGoodbye);
                var bye = off != null && goodbye != null ? (goodbyes % 2 == 0 ? off : goodbye) : off ?? goodbye;
                if (bye != null)
                {
                    audioSource.clip = bye.clips[goodbyes++ / 2 % bye.clips.Length];
                    audioSource.Play();
                    return;
                }
            }
            PlaySound(on ? turnOnClip : turnOffClip);
        }

        // Батарея села: гаснет
        public override void UseUpBatteries()
        {
            base.UseUpBatteries();
            SwitchOn(false);
        }

        // E: поболтать — случайная фраза. Выбирает держащий, остальным уходит по сети.
        public override void ItemInteractLeftRight(bool right)
        {
            base.ItemInteractLeftRight(right);
            if (!right || !IsOwner || !IsOn || IsTalking)
                return;
            Say(IsJammed ? TopicError : TopicRandom);
        }

        private void EnsureColor()
        {
            if (colorIndex >= 0)
                return;
            colorIndex = Random.Range(0, bodyColors.Length);
        }

        private void ApplyColor()
        {
            if (colorIndex < 0 || bodyMaterial == null)
                return;
            var i = colorIndex % bodyColors.Length;
            bodyMaterial.SetColor("_BaseColor", bodyColors[i]);
            if (trimMaterial != null && i < trimColors.Length)
                trimMaterial.SetColor("_BaseColor", trimColors[i]);
        }

        // ---- Голос ----

        /// <summary>
        /// Распознанная фраза локального игрока из голосового чата (с главного потока).
        /// Отвечает ближайшая включённая колонка: на «Алиса, …» сразу, после одного
        /// «Алиса» — сигнал и несколько секунд ждёт вопрос.
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
                var end = text.IndexOf(' ', wake);
                command = end < 0 ? "" : text.Substring(end + 1).Trim();
            }
            else if (Time.time < station.listenUntil)
            {
                command = text;
            }
            else if (Time.time < station.gameAnswerUntil && !station.IsTalking)
            {
                // Ответ на «В какую игру хотите поиграть?»: таких игр она не знает
                station.gameAnswerUntil = 0f;
                station.Say(station.IsJammed ? TopicError : TopicGameUnknown);
                return;
            }
            else
            {
                return; // говорят не с колонкой
            }

            // [unk] — распознавание не разобрало слово: вопрос есть, но непонятный
            var known = command.Replace("[unk]", " ").Trim();
            var topic = command.Length == 0 ? null : known.Length == 0 ? TopicUnknown : Topic(known);

            // Пока отвечает, других не слушает (кроме «стоп»)
            if (station.IsTalking && !AllowInterrupt && topic != TopicStop)
                return;

            if (topic == null)
            {
                station.Say(SoundListen);
                return;
            }
            if (topic == TopicUnknown)
                topic = station.CustomTopic(known) ?? topic;
            if (station.IsJammed && topic != TopicStop)
                topic = topic == TopicTime && station.FindSet(TopicTimeError) != null ? TopicTimeError : TopicError;

            // Тот же вопрос второй раз за минуту: «Я уже отвечала»
            if (topic != TopicStop && topic == station.lastAsked && Time.time - station.lastAskedAt < RepeatSeconds
                && station.FindSet(TopicRepeat) != null)
            {
                station.lastAskedAt = Time.time;
                station.Say(TopicRepeat);
                return;
            }
            station.lastAsked = topic;
            station.lastAskedAt = Time.time;

            if (topic == TopicGameAsk)
            {
                station.Say(TopicGameAsk);
                station.gameAnswerUntil = station.talkUntil + GameAnswerSeconds;
            }
            else if (topic == TopicMyName)
            {
                // Спрашивает локальный игрок: распознавание идёт у него
                var me = GameNetworkManager.Instance?.localPlayerController;
                var folder = me != null ? NameFolder(me.playerUsername) : null;
                station.Say(folder != null && station.FindSet(TopicNames + folder) != null ? TopicNames + folder : TopicNames + "Unknown");
            }
            else if (topic == TopicCount)
                station.SayCount(PlayersNear(station, 15f));
            else if (topic == TopicBattery)
                station.Say(station.BatteryTopic() ?? TopicUnknown);
            else if (topic == TopicColor)
                station.Say(station.ColorTopic() ?? TopicUnknown);
            else if (topic == TopicRefuse)
            {
                // «Не хочу» вперемешку с «Хочу!»
                var agree = station.FindSet(TopicAgree) != null && (station.FindSet(TopicRefuse) == null || Random.value < 0.5f);
                station.Say(agree ? TopicAgree : TopicRefuse);
            }
            else if (topic == TopicTime && station.SayTime())
                return;
            else
                station.Say(topic);
        }

        private string lastAsked;
        private float gameAnswerUntil;
        private float lastAskedAt;

        /// <summary>Есть ли рядом с локальным игроком включённая колонка (тогда плагин слушает голос).</summary>
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
                if (s == null || !s.IsOn)
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
        /// срабатывает, когда эта фраза есть в вопросе. Такие названия плагин добавляет в распознавание.
        /// </summary>
        public static readonly HashSet<string> CustomTopics = new HashSet<string>();

        private string CustomTopic(string command) =>
            phraseSets?.FirstOrDefault(s => s != null && IsCustom(s.name) && command.Contains(s.name.ToLowerInvariant()))?.name;

        private static bool IsCustom(string name) => name != null && name.Any(c => c >= 'а' && c <= 'я' || c >= 'А' && c <= 'Я');

        /// <summary>Тема ответа по тексту вопроса и состоянию игры.</summary>
        public static string Topic(string command)
        {
            bool Has(params string[] words) => words.Any(w => command.Contains(w));

            if (Has("стоп", "хватит", "замолчи", "тихо", "выключ"))
                return TopicStop;
            if (Has("погод", "дожд", "туман", "гроз"))
                return "Weather/" + (WeatherName() ?? "Other");
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
            if (Has("как меня зовут", "кто я", "мое имя", "знаешь меня"))
                return TopicMyName;
            if (Has("сколько нас", "нас сколько", "сколько игроков"))
                return TopicCount;
            if (Has("поиграем", "сыграем", "поиграть", "сыграть", "игру"))
                return TopicGameAsk;
            if (Has("любишь", "люблю", "нравлюсь", "нравится"))
                return "Love";
            if (Has("заряд", "батаре"))
                return TopicBattery;
            if (Has("цвет"))
                return TopicColor;
            if (Has("сделай", "принеси", "помоги", "открой", "танцуй", "найди", "дай", "хочешь", "будешь"))
                return TopicRefuse;
            if (Has("хорошо", "ладно", "понятно", "ясно", "окей") || Regex.IsMatch(command, @"^(я|мы|мне|нам)\s"))
                return "Ok";
            if (Has("до свидания", "до встречи", "прощай", "увидимся") || Regex.IsMatch(command, @"(^|\s)пока(\s|$)"))
                return TopicGoodbye;
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

        /// <summary>
        /// Погода луны, на которой (или к которой летит) корабль: название из WeatherRegistry,
        /// если он стоит (так видна и погода модов), иначе ванильное. Ясно → Clear, null — не знаем.
        /// </summary>
        public static string WeatherName()
        {
            var level = StartOfRound.Instance != null ? StartOfRound.Instance.currentLevel : null;
            if (level == null)
                return null;
            var name = WeatherRegistryName(level);
            if (string.IsNullOrEmpty(name))
            {
                var weather = level.currentWeather;
                name = Enum.IsDefined(typeof(LevelWeatherType), weather) ? weather.ToString() : null;
            }
            return name == "None" ? "Clear" : name;
        }

        private static string WeatherRegistryName(SelectableLevel level)
        {
            if (!weatherRegistryChecked)
            {
                weatherRegistryChecked = true;
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("WeatherRegistry.WeatherManager", false)).FirstOrDefault(t => t != null);
                weatherRegistryGet = type?.GetMethod("GetCurrentWeather", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(SelectableLevel) }, null);
            }
            if (weatherRegistryGet == null)
                return null;
            try
            {
                var weather = weatherRegistryGet.Invoke(null, new object[] { level });
                return weather?.GetType().GetProperty("Name")?.GetValue(weather) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Корабль сел на луну с «глушащей» погодой (солнечная вспышка): колонка сбоит.</summary>
        public bool IsJammed
        {
            get
            {
                var round = StartOfRound.Instance;
                if (round == null || round.inShipPhase || JammingWeathers.Length == 0)
                    return false;
                var name = WeatherName();
                return name != null && JammingWeathers.Any(w => string.Equals(w.Trim(), name, StringComparison.OrdinalIgnoreCase));
            }
        }

        // ---- Речь ----

        /// <summary>Сказать фразу на тему (с подменой отсутствующей темы) и разослать всем.</summary>
        public void Say(string topic)
        {
            askedLocally = true;
            if (topic.StartsWith("#"))
            {
                Speak(topic, 0);
                SendSpeak(topic, 0);
                return;
            }

            var set = FindSet(topic)
                      ?? (topic.StartsWith("Weather/") ? FindSet("Weather/Other") : null)
                      ?? FindSet(topic == TopicError ? TopicError : TopicUnknown)
                      ?? FindSet(TopicRandom);
            SayFrom(set);
        }

        private void SayFrom(PhraseSet set)
        {
            if (set == null || set.clips.Length == 0)
                return;
            askedLocally = true;
            var index = Random.Range(0, set.clips.Length);
            if (set.clips.Length > 1 && set.name == lastTopic && index == lastIndex)
                index = (index + 1 + Random.Range(0, set.clips.Length - 1)) % set.clips.Length;
            Speak(set.name, index);
            SendSpeak(set.name, index);
        }

        // Фраза из кусочков: «сейчас» + «один» «четыре» + «три» «пять». Уходит одной строкой
        // «#seq» с парами тема/номер, у всех играет одинаково.
        private const string SeqPrefix = "#seq\n";

        private void SaySequence(List<string> topics)
        {
            var parts = new List<string>();
            foreach (var t in topics)
            {
                var set = FindSet(t);
                if (set != null)
                    parts.Add(set.name + "\t" + Random.Range(0, set.clips.Length));
            }
            if (parts.Count == 0)
                return;
            askedLocally = true;
            var topic = SeqPrefix + string.Join("\n", parts);
            Speak(topic, 0);
            SendSpeak(topic, 0);
        }

        /// <summary>Число словами из папок Numbers/0 … Numbers/10; больше десяти — по цифрам.</summary>
        private bool AddNumber(List<string> topics, int n)
        {
            if (n < 0)
                return false;
            if (FindSet(TopicNumbers + n) != null)
            {
                topics.Add(TopicNumbers + n);
                return true;
            }
            var digits = n.ToString();
            if (digits.Length == 1 || digits.Any(d => FindSet(TopicNumbers + d) == null))
                return false;
            topics.AddRange(digits.Select(d => TopicNumbers + d));
            return true;
        }

        /// <summary>«Который час»: время с экрана числами. false — чисел нет, ответит фраза из Time.</summary>
        private bool SayTime()
        {
            var tod = TimeOfDay.Instance;
            if (tod == null || !tod.currentDayTimeStarted)
                return false;
            var minutes = (int)(tod.normalizedTimeOfDay * (60f * tod.numberOfHours)) + 360;
            // Целой фразой на этот час: Time/15 — «Три часа дня»
            var hourTopic = TopicTime + "/" + minutes / 60 % 24;
            if (FindSet(hourTopic) != null)
            {
                Say(hourTopic);
                return true;
            }
            var topics = new List<string> { TopicTimeIntro };
            if (!AddNumber(topics, minutes / 60 % 24) || !AddNumber(topics, minutes % 60))
                return false;
            SaySequence(topics);
            return true;
        }

        /// <summary>«Сколько нас»: Count/1, Count/2, Count/3, Count/Many, иначе число.</summary>
        private void SayCount(int players)
        {
            var topic = CountTopic(players);
            if (topic != null)
            {
                Say(topic);
                return;
            }
            var topics = new List<string>();
            if (AddNumber(topics, players))
                SaySequence(topics);
            else
                Say(TopicUnknown);
        }

        /// <summary>«Какой заряд»: Battery/0 … Battery/100 (по 10%) или словами (Battery/Full …).</summary>
        private string BatteryTopic()
        {
            var charge = insertedBattery != null ? Mathf.Clamp01(insertedBattery.charge) : 1f;
            var percent = "Battery/" + Mathf.RoundToInt(charge * 10f) * 10;
            var words = "Battery/" + (charge >= 0.95f ? "Full" : charge >= 0.75f ? "AlmostFull" : charge >= 0.5f ? "MoreHalf"
                : charge >= 0.2f ? "LessHalf" : "AlmostEmpty");
            var hasPercent = FindSet(percent) != null;
            var hasWords = FindSet(words) != null;
            if (hasPercent && hasWords)
                return Random.value < 0.5f ? percent : words;
            return hasPercent ? percent : hasWords ? words : null;
        }

        /// <summary>«Какого ты цвета»: запись своего цвета или ближайшего, иначе null.</summary>
        private string ColorTopic()
        {
            EnsureColor();
            var folders = ColorFolders[colorIndex % ColorFolders.Length];
            return folders.FirstOrDefault(f => FindSet(f) != null);
        }

        private string CountTopic(int players)
        {
            var exact = "Count/" + players;
            if (FindSet(exact) != null)
                return exact;
            return players >= 4 && FindSet("Count/Many") != null ? "Count/Many" : null;
        }

        private static int PlayersNear(YandexStationItem station, float radius)
        {
            var round = StartOfRound.Instance;
            if (round == null)
                return 0;
            var count = 0;
            foreach (var p in round.allPlayerScripts)
            {
                if (p != null && p.isPlayerControlled && !p.isPlayerDead
                    && Vector3.Distance(p.transform.position, station.transform.position) < radius)
                    count++;
            }
            return count;
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

        private readonly Queue<AudioClip> queued = new Queue<AudioClip>();

        // Следующий кусочек фразы, как только доиграл предыдущий
        private void PlayQueued()
        {
            if (queued.Count == 0 || audioSource == null || audioSource.isPlaying)
                return;
            if (!IsOn)
            {
                queued.Clear();
                return;
            }
            var clip = queued.Dequeue();
            audioSource.clip = clip;
            audioSource.Play();
            WalkieTalkie.TransmitOneShotAudio(audioSource, clip);
        }

        private bool IsTalking => Time.time < talkUntil && audioSource != null && audioSource.isPlaying;

        private void Speak(string topic, int index)
        {
            if (audioSource == null || !IsOn)
                return;
            switch (topic)
            {
                case TopicStop:
                    queued.Clear();
                    audioSource.Stop();
                    talkUntil = 0f;
                    return;
                case SoundListen:
                    listenUntil = Time.time + ListenSeconds;
                    PlaySound(listenClip);
                    return;
                case SoundTimeout:
                    listenUntil = 0f;
                    PlaySound(listenTimeoutClip);
                    return;
            }

            queued.Clear();
            if (topic.StartsWith(SeqPrefix))
            {
                foreach (var part in topic.Substring(SeqPrefix.Length).Split('\n'))
                {
                    var pair = part.Split('\t');
                    var s = pair.Length == 2 ? FindSet(pair[0]) : null;
                    if (s != null && int.TryParse(pair[1], out var i) && i >= 0 && i < s.clips.Length && s.clips[i] != null)
                        queued.Enqueue(s.clips[i]);
                }
                if (queued.Count == 0)
                    return;
                topic = TopicNumbers;
            }

            var set = queued.Count > 0 ? null : FindSet(topic);
            if (queued.Count == 0 && (set == null || index < 0 || index >= set.clips.Length || set.clips[index] == null))
                return;
            var clip = queued.Count > 0 ? queued.Dequeue() : set.clips[index];
            var setName = set != null ? set.name : TopicNumbers;
            lastTopic = setName;
            lastIndex = index;
            listenUntil = 0f;
            clockWhileTalking = setName == TopicTime || setName == TopicNumbers;

            // Ошибка (солнечная вспышка) — обычная фраза из папки Error, голосом Алисы
            audioSource.Stop();
            audioSource.clip = clip;
            audioSource.Play();
            WalkieTalkie.TransmitOneShotAudio(audioSource, clip);
            talkUntil = Time.time + clip.length + queued.Sum(c => c.length);
            nextExpression = 0f;

            // Шум слышат монстры; считаем его только у того, кто спросил, чтобы не дублировать
            if (askedLocally)
            {
                RoundManager.Instance?.PlayAudibleNoise(transform.position, noiseRange > 0f ? noiseRange : NoiseRange,
                    NoiseLoudness, 0, isInElevator && StartOfRound.Instance.hangarDoorsClosed);
            }
            noiseRange = 0f;
        }

        private void PlaySound(AudioClip clip)
        {
            if (audioSource != null && clip != null)
                audioSource.PlayOneShot(clip);
        }

        // ---- События: колонка сама реагирует на то, что вокруг ----

        public static bool EventsEnabled = true;
        /// <summary>Шанс, что колонка скажет что-то на повод (монстр подошёл, посадка...).</summary>
        public static float EventChance = 0.35f;
        /// <summary>Пауза (с) после реплики колонки, прежде чем она снова заговорит сама.</summary>
        public static float EventCooldown = 45f;
        /// <summary>Одна и та же тема события не чаще, чем раз в столько секунд (на все колонки).</summary>
        public static float EventTopicCooldown = 180f;
        /// <summary>С какого расстояния (м) колонка замечает монстра.</summary>
        public static float EventRadius = 15f;
        /// <summary>Как далеко монстры слышат реплику на событие (м): громко — может приманить.</summary>
        public static float EventNoiseRange = 25f;
        /// <summary>Раз во сколько секунд колонка может поболтать сама, если рядом кто-то есть (0 — никогда).</summary>
        public static float IdleSeconds = 120f;

        public const string EventsFolder = "Events/";
        private const float EventCheckSeconds = 1.5f;

        // Общие на все колонки: две колонки не говорят про одно и то же разом
        private static readonly Dictionary<string, float> topicFreeAt = new Dictionary<string, float>();
        private static float anyEventFreeAt;
        private static readonly HashSet<string> LoggedEnemies = new HashSet<string>();

        private float noiseRange;
        private float nextEventCheck, eventFreeAt, nextIdle;
        private bool eventsReady, wasLanded, wasLeaving, wasHeld, wasNight, lowBatteryTold;
        private int deadPlayers, playersNear, sipsSeen = -1, shipScrap = -1, credits = -1;
        private bool wasLate, wasMusic, wasWalkie, wasWinston;
        private int squeaksSeen = -1;
        private readonly HashSet<ulong> itemsNear = new HashSet<ulong>();
        private bool itemsSeeded;
        private float nextScan;
        private readonly Dictionary<ulong, int> suits = new Dictionary<ulong, int>();
        private static Terminal terminal;
        /// <summary>Столько скрапа на корабле — «ой, сколько предметов».</summary>
        public static int ManyItems = 20;
        private readonly HashSet<ulong> deadIds = new HashSet<ulong>();
        private readonly HashSet<ulong> seenEnemies = new HashSet<ulong>();
        private readonly List<string> reasons = new List<string>();

        // Решает хост, остальным уходит выбранная фраза, как при ответе на вопрос
        private void UpdateEvents()
        {
            if (!EventsEnabled || !IsServer || Time.time < nextEventCheck)
                return;
            nextEventCheck = Time.time + EventCheckSeconds;

            reasons.Clear();
            CollectReasons();
            if (!eventsReady)
            {
                eventsReady = true; // первый проход только запоминает, что уже есть
                return;
            }
            if (reasons.Count == 0 || !IsOn || IsTalking || isPocketed || IsJammed
                || Time.time < eventFreeAt || Time.time < anyEventFreeAt || listenUntil > 0f)
                return;

            foreach (var topic in reasons)
            {
                if (topicFreeAt.TryGetValue(topic, out var free) && Time.time < free)
                    continue;
                var set = FindSet(topic);
                if (set == null || Random.value > EventChance)
                    continue;
                topicFreeAt[topic] = Time.time + EventTopicCooldown;
                eventFreeAt = Time.time + EventCooldown;
                anyEventFreeAt = Time.time + 3f;
                noiseRange = EventNoiseRange;
                SayFrom(set);
                return;
            }
        }

        // Поводы появляются один раз, в момент перемены (сел корабль, подошёл монстр...)
        private void Reason(string topic) => reasons.Add(EventsFolder + topic);

        private void CollectReasons()
        {
            var round = StartOfRound.Instance;
            if (round == null)
                return;

            if (round.shipHasLanded && !wasLanded)
                Reason("Landed");
            wasLanded = round.shipHasLanded;
            if (round.shipIsLeaving && !wasLeaving)
                Reason("Leaving");
            wasLeaving = round.shipIsLeaving;

            var dead = 0;
            foreach (var p in round.allPlayerScripts)
            {
                if (p == null || !p.isPlayerDead)
                {
                    if (p != null)
                        deadIds.Remove(p.playerClientId);
                    continue;
                }
                dead++;
                // Умер именно этот: сначала его личная тема PlayerDied/<Имя>, потом общая
                if (deadIds.Add(p.playerClientId) && !round.inShipPhase)
                {
                    var folder = NameFolder(p.playerUsername);
                    if (folder != null)
                        Reason("PlayerDied/" + folder);
                    Reason("PlayerDied");
                }
            }
            deadPlayers = dead;

            var tod = TimeOfDay.Instance;
            var night = tod != null && !round.inShipPhase && tod.normalizedTimeOfDay > 0.75f;
            if (night && !wasNight)
                Reason("Night");
            wasNight = night;

            if (isHeld && !wasHeld)
                Reason("PickedUp");
            wasHeld = isHeld;

            if (insertedBattery != null)
            {
                if (insertedBattery.charge < 0.15f && !lowBatteryTold && IsOn)
                {
                    Reason("LowBattery");
                    lowBatteryTold = true;
                }
                else if (insertedBattery.charge > 0.5f)
                    lowBatteryTold = false;
            }

            var enemies = RoundManager.Instance != null ? RoundManager.Instance.SpawnedEnemies : null;
            if (enemies != null)
            {
                var pos = transform.position;
                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.enemyType == null)
                        continue;
                    var id = enemy.NetworkObjectId;
                    var dist = Vector3.Distance(enemy.transform.position, pos);
                    if (!enemy.isEnemyDead && dist < EventRadius)
                    {
                        if (seenEnemies.Add(id))
                        {
                            Reason("Enemy/" + enemy.enemyType.enemyName);
                            if (LoggedEnemies.Add(enemy.enemyType.enemyName))
                                Debug.Log($"[Алиса] рядом монстр, папка фраз: Phrases/{EventsFolder}Enemy/{enemy.enemyType.enemyName}");
                        }
                    }
                    else if (dist > EventRadius * 1.5f || enemy.isEnemyDead)
                        seenEnemies.Remove(id);
                }
            }

            var late = tod != null && !round.inShipPhase && tod.normalizedTimeOfDay > 0.9f;
            if (late && !wasLate)
                Reason("LateDay");
            wasLate = late;

            // Сколько нас: компания рядом выросла
            var near = PlayersNear(this, 8f);
            if (near > playersNear && near > 0)
            {
                var count = CountTopic(near);
                if (count != null)
                    reasons.Add(count);
            }
            // Все ушли от колонки: «А можно с вами?»
            if (near == 0 && playersNear > 0)
                Reason("LeftBehind");
            playersNear = near;

            // Кто-то рядом отпил энергетик или Егермейстер
            if (DrinkAnimation.Sips != sipsSeen)
            {
                if (sipsSeen >= 0 && Vector3.Distance(DrinkAnimation.LastSipAt, transform.position) < EventRadius)
                    Reason("Drink");
                sipsSeen = DrinkAnimation.Sips;
            }

            // Пищали Уинстоном рядом
            if (WinstonItem.Squeaks != squeaksSeen)
            {
                if (squeaksSeen >= 0 && Vector3.Distance(WinstonItem.LastSqueakAt, transform.position) < EventRadius)
                    Reason("Winston");
                squeaksSeen = WinstonItem.Squeaks;
            }

            // Смена костюма у игрока рядом
            foreach (var p in round.allPlayerScripts)
            {
                if (p == null || !p.isPlayerControlled)
                    continue;
                var id = p.playerClientId;
                if (suits.TryGetValue(id, out var suit) && suit != p.currentSuitID
                    && Vector3.Distance(p.transform.position, transform.position) < EventRadius)
                    Reason("Suit");
                suits[id] = p.currentSuitID;
            }

            // Дорогое — раз в несколько секунд: поиск объектов по сцене
            if (Time.time > nextScan)
            {
                nextScan = Time.time + 5f;
                ScanScene(round);
            }

            if (IdleSeconds > 0f && Time.time > nextIdle)
            {
                if (nextIdle > 0f && PlayerNear(this, 8f))
                    Reason("Idle");
                nextIdle = Time.time + IdleSeconds * Random.Range(0.7f, 1.3f);
            }
        }

        private void ScanScene(StartOfRound round)
        {
            if (terminal == null)
                terminal = FindObjectOfType<Terminal>();
            if (terminal != null)
            {
                if (credits >= 0 && terminal.groupCredits > credits)
                    Reason("Money");
                credits = terminal.groupCredits;
            }

            var scrap = 0;
            var music = false;
            var walkie = false;
            var winston = false;
            var pos = transform.position;
            foreach (var item in FindObjectsOfType<GrabbableObject>())
            {
                if (item == null || item.itemProperties == null)
                    continue;
                if (item.isInShipRoom && item.itemProperties.isScrap && !item.isHeld)
                    scrap++;
                var close = Vector3.Distance(item.transform.position, pos) < EventRadius;
                if (close && item is BoomboxItem boombox && boombox.isPlayingMusic)
                    music = true;
                if (close && item is WalkieTalkie && item.isBeingUsed && item.isHeld)
                    walkie = true;
                if (close && item is WinstonItem)
                    winston = true;

                // Поднесли или бросили рядом скрап: «Что это?»
                var id = item.NetworkObjectId;
                var dist = Vector3.Distance(item.transform.position, pos);
                if (item != this && item.itemProperties.isScrap && dist < 2.5f)
                {
                    if (itemsNear.Add(id) && itemsSeeded)
                        Reason("WhatIsThis");
                }
                else if (dist > 5f)
                    itemsNear.Remove(id);
            }
            if (shipScrap >= 0 && scrap > shipScrap && round.shipHasLanded)
                Reason(scrap >= ManyItems && shipScrap < ManyItems ? "ManyItems" : "Work");
            shipScrap = scrap;
            if (music && !wasMusic)
                Reason("Music");
            wasMusic = music;
            if (walkie && !wasWalkie)
                Reason("Walkie");
            wasWalkie = walkie;
            if (winston && !wasWinston)
                Reason("Winston");
            wasWinston = winston;
            itemsSeeded = true;
        }

        private static bool PlayerNear(YandexStationItem station, float radius)
        {
            var round = StartOfRound.Instance;
            foreach (var p in round.allPlayerScripts)
            {
                if (p != null && p.isPlayerControlled && !p.isPlayerDead
                    && Vector3.Distance(p.transform.position, station.transform.position) < radius)
                    return true;
            }
            return false;
        }

        // ---- Экран ----

        public override void Update()
        {
            base.Update();
            if (segments == null)
                return;

            // Позвали «Алиса» и замолчали: сигнал конца прослушивания (решает тот, кто звал)
            if (listenUntil > 0f && Time.time > listenUntil)
            {
                listenUntil = 0f;
                if (askedLocally && IsOn)
                    Say(SoundTimeout);
            }

            PlayQueued();
            UpdateEvents();
            var talking = IsTalking;
            UpdateLevel(talking);
            UpdateRing(talking);
            long mask;
            if (!IsOn)
                mask = 0;
            else if (IsJammed)
                mask = GlitchMask();
            else if (talking && !clockWhileTalking)
                mask = FaceMask();
            else if (Time.time < listenUntil)
                mask = Eyes(ListenFace);
            else
                mask = ClockMask();
            ShowMask(mask);
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

        // Пока говорит: каждые полсекунды-секунду новое выражение глаз, плюс моргание
        private long FaceMask()
        {
            if (Time.time > nextExpression)
            {
                // На громких местах чаще довольные или широко открытые глаза
                expression = level > 0.4f && Random.value < 0.5f
                    ? (Random.value < 0.5f ? HappyFace : WideFace)
                    : Random.Range(0, Expressions.Length);
                nextExpression = Time.time + Random.Range(0.5f, 1.2f);
            }
            if (Time.time > nextBlink)
            {
                blinkUntil = Time.time + 0.13f;
                nextBlink = Time.time + Random.Range(2f, 4.5f);
            }
            var e = Expressions[expression];
            return Time.time < blinkUntil ? Eyes((e.left, e.right, Closed, Closed)) : Eyes(e);
        }

        private static long Eyes((int left, int right, int l, int r) e) => Digit(e.left, e.l) | Digit(e.right, e.r);

        // Солнечная вспышка: «Err» вперемешку со случайными сегментами
        private long GlitchMask()
        {
            if (shownMask >= 0 && Random.value < 0.9f)
                return shownMask;
            if (Random.value < 0.5f)
                return Digit(0, Mask("adefg")) | Digit(1, Mask("eg")) | Digit(2, Mask("eg"));
            long mask = 0;
            for (var i = 0; i < Colon + 2; i++)
            {
                if (Random.value < 0.3f)
                    mask |= 1L << i;
            }
            return mask;
        }

        // Громкость голоса 0..1, сглаженная: по ней выбираются глаза и светится кольцо
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
            var glow = !IsOn ? 0f
                : IsJammed ? (Random.value < 0.3f ? ringTalkGlow : 0f)
                : talking ? Mathf.Lerp(ringIdleGlow * 2f, ringTalkGlow, level)
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
            var names = new string[30];
            for (var d = 0; d < 4; d++)
                for (var s = 0; s < 7; s++)
                    names[d * 7 + s] = "Seg" + d + SegmentNames[s];
            names[Colon] = "ColonTop";
            names[Colon + 1] = "ColonBottom";
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

            // Свои экземпляры: кольцо пульсирует и цвет корпуса у каждой колонки свой
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                switch (r.name)
                {
                    case "Ring":
                        ringMaterial = r.material;
                        ringMaterial.SetColor("_BaseColor", ringColor);
                        break;
                    case "Body":
                        bodyMaterial = r.material;
                        break;
                    case "Trim":
                        trimMaterial = r.material;
                        break;
                }
            }

            foreach (var set in phraseSets ?? new PhraseSet[0])
            {
                if (set != null && IsCustom(set.name))
                {
                    lock (CustomTopics)
                        CustomTopics.Add(set.name.ToLowerInvariant());
                }
            }
        }

        // ---- Сеть ----

        private static void RegisterMessages()
        {
            var messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null || messaging == registeredOn)
                return;
            messaging.RegisterNamedMessageHandler(SpeakMessage, OnSpeakMessage);
            messaging.RegisterNamedMessageHandler(StateMessage, OnStateMessage);
            messaging.RegisterNamedMessageHandler(RequestMessage, OnRequestMessage);
            registeredOn = messaging;
        }

        // Фраза: спросивший → хост → остальные
        private void SendSpeak(string topic, int index)
        {
            var nm = NetworkManager;
            if (nm == null || !nm.IsListening)
                return;
            if (IsServer)
                BroadcastSpeak(topic, index, except: nm.LocalClientId);
            else
                SendSpeakTo(topic, index, NetworkManager.ServerClientId);
        }

        private void SendSpeakTo(string topic, int index, ulong clientId)
        {
            using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(int) + 8 + topic.Length * 2, Allocator.Temp);
            writer.WriteValueSafe(NetworkObjectId);
            writer.WriteValueSafe(topic);
            writer.WriteValueSafe(index);
            NetworkManager.CustomMessagingManager.SendNamedMessage(SpeakMessage, clientId, writer, NetworkDelivery.Reliable);
        }

        private void BroadcastSpeak(string topic, int index, ulong except)
        {
            foreach (var clientId in NetworkManager.ConnectedClientsIds)
            {
                if (clientId != NetworkManager.ServerClientId && clientId != except)
                    SendSpeakTo(topic, index, clientId);
            }
        }

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
                station.BroadcastSpeak(topic, index, except: sender);
        }

        // Тот, кто только что увидел колонку, спрашивает у хоста цвет и «включена»
        private void RequestState()
        {
            using var writer = new FastBufferWriter(sizeof(ulong), Allocator.Temp);
            writer.WriteValueSafe(NetworkObjectId);
            NetworkManager.CustomMessagingManager.SendNamedMessage(RequestMessage, NetworkManager.ServerClientId, writer,
                NetworkDelivery.Reliable);
        }

        private static void OnRequestMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong objectId);
            var station = Find(objectId);
            if (station == null || !station.IsServer)
                return;
            using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(int), Allocator.Temp);
            writer.WriteValueSafe(objectId);
            writer.WriteValueSafe(station.GetItemDataToSave());
            station.NetworkManager.CustomMessagingManager.SendNamedMessage(StateMessage, sender, writer, NetworkDelivery.Reliable);
        }

        private static void OnStateMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong objectId);
            reader.ReadValueSafe(out int state);
            var station = Find(objectId);
            if (station != null && sender == NetworkManager.ServerClientId)
                station.LoadItemSaveData(state);
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
