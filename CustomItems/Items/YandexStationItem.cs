using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        public AudioClip errorClip;
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
                talkUntil = 0f;
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
                topic = TopicError;
            station.Say(topic);
        }

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
            if (audioSource == null || !IsOn)
                return;
            switch (topic)
            {
                case TopicStop:
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

            var set = FindSet(topic);
            if (set == null || index < 0 || index >= set.clips.Length || set.clips[index] == null)
                return;
            var clip = set.clips[index];
            lastTopic = set.name;
            lastIndex = index;
            listenUntil = 0f;
            clockWhileTalking = set.name == TopicTime;

            // Ошибка: сначала сигнал сбоя, потом фраза
            var delay = set.name == TopicError && errorClip != null ? errorClip.length : 0f;
            audioSource.Stop();
            if (delay > 0f)
                audioSource.PlayOneShot(errorClip);
            audioSource.clip = clip;
            audioSource.PlayDelayed(delay);
            WalkieTalkie.TransmitOneShotAudio(audioSource, clip);
            talkUntil = Time.time + delay + clip.length;
            nextExpression = 0f;

            // Шум слышат монстры; считаем его только у того, кто спросил, чтобы не дублировать
            if (askedLocally)
            {
                RoundManager.Instance?.PlayAudibleNoise(transform.position, NoiseRange, NoiseLoudness, 0,
                    isInElevator && StartOfRound.Instance.hangarDoorsClosed);
            }
        }

        private void PlaySound(AudioClip clip)
        {
            if (audioSource != null && clip != null)
                audioSource.PlayOneShot(clip);
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
