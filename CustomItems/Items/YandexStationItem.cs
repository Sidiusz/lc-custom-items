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
    public partial class YandexStationItem : CustomGrabbable
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
        public static float ListenSilence = 3f, ListenMax = 12f;
        /// <summary>Сколько секунд после её вопроса («Вы уверены?») ждёт ответ без «Алиса».</summary>
        public static float AnswerSeconds = 6f;
        /// <summary>Сколько длится пробуждение (сигнал и глаза), прежде чем она начнёт «думать».</summary>
        public static float WakeSeconds = 0.8f;
        /// <summary>Можно ли задать новый вопрос, пока колонка ещё отвечает.</summary>
        public static bool AllowInterrupt;
        /// <summary>Погоды (названия как у игры или WeatherRegistry), при которых колонка сбоит.</summary>
        public static string[] JammingWeathers = { "Solar Flare" };
        public static string OnOffTip = "Turn on/off : [LMB]";
        public static string TalkTip = "Talk : [E]";

        /// <summary>Все колонки в мире: по ним плагин решает, слушать ли голос.</summary>
        public static readonly List<YandexStationItem> All = new List<YandexStationItem>();

        public const string TopicGoodbye = "Goodbye", TopicRepeat = "Repeat", TopicCount = "#count",
            TopicNumbers = "Numbers/", TopicAnd = "And", TopicTimeIntro = "TimeIntro", TopicTimeError = "TimeError",
            TopicGameAsk = "Game/Ask", TopicGameUnknown = "Game/Unknown", TopicBored = "Game/Bored",
            TopicEnemies = "#enemies", TopicEnemiesNear = "EnemiesNear", TopicAllClear = "AllClear",
            TopicWhoAreYou = "WhoAreYou", TopicShush = "Shush", TopicSorry = "Sorry", TopicInsult = "Insult",
            TopicMumble = "Mumble", TopicWhereIs = "#whereis", TopicRend = "Game/Rend", TopicCancel = "Game/Cancel",
            TopicFavoriteColor = "FavoriteColor", TopicRoute = "#route";

        /// <summary>
        /// Ставит маршрут корабля по тексту (плагин, AliceRoute) и возвращает темы ответа по порядку:
        /// говорится первая, у которой есть записи.
        /// </summary>
        public static Func<string, string[]> RouteHandler;

        /// <summary>Забыть, что игрок наговорил распознаванию (ставит плагин), чтобы фразы не склеивались.</summary>
        public static Action<GameNetcodeStuff.PlayerControllerB> ResetHearing;

        /// <summary>Рядом дунули в рожок или горн (вызывает патч плагина) — Events/Horn.</summary>
        public static void OnHorn(Vector3 position)
        {
            foreach (var s in All)
            {
                if (s != null && Vector3.Distance(s.transform.position, position) < EventRadius)
                    s.hornHeard = true;
            }
        }

        private bool hornHeard;
        /// <summary>Столько секунд после «замолчи» колонка не болтает сама.</summary>
        public static float ShushSeconds = 60f;

        /// <summary>Как друзей называют вслух: папка → имена («Andrey=андрей,андрюха;…»), падежи добавляются сами.</summary>
        public static readonly Dictionary<string, string[]> SpokenNames = new Dictionary<string, string[]>();

        /// <summary>Папка друга, если в тексте есть его имя в любой форме (Пашей, Пашка, Павел).</summary>
        public static string FriendNamed(string command) => AliceIntents.NameIn(command);

        public static void SetSpokenNames(string table)
        {
            SpokenNames.Clear();
            foreach (var entry in (table ?? "").Split(';'))
            {
                var parts = entry.Split('=');
                if (parts.Length != 2 || parts[0].Trim().Length == 0)
                    continue;
                SpokenNames[parts[0].Trim()] = parts[1].Split(',').Select(w => w.Trim().ToLowerInvariant().Replace('ё', 'е'))
                    .Where(w => w.Length > 0).ToArray();
            }
            AliceIntents.SetNames(SpokenNames);
        }
        /// <summary>Сколько секунд после «В какую игру хотите поиграть?» колонка ждёт ответ без «Алиса».</summary>
        public static float GameAnswerSeconds = 10f;
        public const string TopicNames = "Names/", TopicMyName = "#myname", TopicMyRole = "#myrole", TopicRoles = "Roles/", TopicBattery = "#battery",
            TopicColor = "#color", TopicAgree = "Agree", TopicRefuse = "Refuse", TopicOff = "Off";

        // Записи цветов по порядку bodyColors (серая, голубая, розовая, чёрная): своя папка, иначе ближайшая
        private static readonly string[][] ColorFolders =
        {
            new[] { "Color/Grey", "Color/White" },
            new[] { "Color/Blue", "Color/DarkBlue" },
            new[] { "Color/Pink", "Color/Purple" },
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
        /// <summary>Шанс «Я уже отвечала» на тот же вопрос (иначе отвечает снова, например новый анекдот).</summary>
        public static float RepeatChance = 0.5f;
        /// <summary>Время числами из кусков («На часах пятнадцать часов тридцать минут»), если все куски записаны.</summary>
        public static bool TimeInNumbers = true;
        /// <summary>Шанс на «Алиса» ответить голосом «Слушаю!» (папка Listen), а не сигналом.</summary>
        public static float ListenVoiceChance = 0.3f;
        public const string TopicLoot = "#loot", TopicLootIntro = "Loot/Intro", TopicWait = "Wait", TopicListenVoice = "Listen",
            TopicCall = "Call/", TopicMyNameIntro = "MyName/Intro", TopicLightsOn = "#lightson", TopicLightsOff = "#lightsoff",
            TopicJoke = "Joke", TopicJokeNext = "Joke/Next", TopicWhoDied = "#whodied";

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
        private float listenUntil, listenStartedAt;
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
            ApplyReuse();
            RegisterMessages();
            FindSegments();
            SetupMaterials();
            if (IsServer)
                EnsureColor();
            else if (NetworkManager != null && NetworkManager.IsConnectedClient)
                RequestState();
            ApplyColor();
            ApplyVolume();
        }

        private void OnEnable() => All.Add(this);

        private void OnDisable() => All.Remove(this);

        // Цвет (случайный при покупке), «включена» и громкость в одном числе: сохраняются вместе с кораблём.
        // Громкость хранится как «на сколько шагов тише 100%», чтобы в старых сохранениях было 100%
        public override int GetItemDataToSave()
        {
            EnsureColor();
            return colorIndex | (isOn ? 16 : 0) | (10 - volumeStep) << 5;
        }

        public override void LoadItemSaveData(int saveData)
        {
            colorIndex = Mathf.Clamp(saveData & 15, 0, bodyColors.Length - 1);
            isOn = (saveData & 16) != 0;
            isBeingUsed = isOn;
            volumeStep = Mathf.Clamp(10 - ((saveData >> 5) & 15), MinVolumeStep, 10);
            ApplyColor();
            ApplyVolume();
        }

        // ---- Громкость: «Алиса, громче / тише», шаг 10% ----

        public const string TopicLouder = "#louder", TopicQuieter = "#quieter", TopicVolume = "#volume";
        private const string VolumePrefix = "#vol:";
        private const int MinVolumeStep = 1;
        /// <summary>Сколько секунд на экране проценты громкости.</summary>
        public static float VolumeShowSeconds = 2.5f;
        private int volumeStep = 10;
        private float baseVolume = -1f, volumeShownUntil;

        private void ApplyVolume()
        {
            if (audioSource == null)
                return;
            if (baseVolume < 0f)
                baseVolume = audioSource.volume;
            audioSource.volume = baseVolume * volumeStep / 10f;
        }

        // Решает тот, кто услышал; всем уходит «#vol:N», как фраза
        private void ChangeVolume(int delta)
        {
            var step = Mathf.Clamp(volumeStep + delta, MinVolumeStep, 10);
            Speak(VolumePrefix + step, 0);
            SendSpeak(VolumePrefix + step, 0);
        }

        // Проценты: «60» и значок процента из двух кружков, «100» и нижний кружок
        private long VolumeMask()
        {
            var pct = volumeStep * 10;
            int upper = Mask("abfg"), lower = Mask("cdeg");
            if (pct >= 100)
                return Digit(0, DigitMasks[1]) | Digit(1, DigitMasks[0]) | Digit(2, DigitMasks[0]) | Digit(3, lower);
            return Digit(0, DigitMasks[pct / 10]) | Digit(1, DigitMasks[0]) | Digit(2, upper) | Digit(3, lower);
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
                talkUntil = 0f; // выключают — только звук выключения, без «до свидания»
            }
            PlaySound(on ? turnOnClip : turnOffClip);
            // Через раз после сигнала включения «Включаюсь» (TurnOn): у всех одинаково, без сети
            if (on && FindSet(TopicTurnOn) is { } hello && switchOns++ % 2 == 0)
            {
                queued.Clear();
                queued.Enqueue(hello.clips[switchOns / 2 % hello.clips.Length]);
                turnOnSpeechAt = Time.time + (turnOnClip != null ? turnOnClip.length : 0.5f);
            }
        }

        private const string TopicTurnOn = "TurnOn";
        private int switchOns;
        private float turnOnSpeechAt;

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
        /// Распознанная фраза игрока <paramref name="speaker"/> из голосового чата (с главного потока).
        /// Отвечает ближайшая к нему включённая колонка: на «Алиса, …» сразу, после одного
        /// «Алиса» — сигнал и несколько секунд ждёт вопрос.
        /// </summary>
        public static void OnSpeech(GameNetcodeStuff.PlayerControllerB speaker, string text)
        {
            text = (text ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
            if (text.Length == 0 || speaker == null || speaker.isPlayerDead)
                return;
            var station = NearestTo(speaker.transform.position, HearingRadius);
            if (station == null)
                return;

            var wake = text.IndexOf(WakeWord, StringComparison.Ordinal);

            // Ответ на её вопрос («Вы уверены?», «Сколько?»): «Алиса» не нужна
            var answering = station.asking != null && Time.time < station.askUntil;
            // Встречный вопрос («а у вас как?»): не похоже на ответ — с «Алиса» это новый вопрос, без — говорят между собой
            if (answering && station.asking == AskFollow
                && AliceIntents.MatchFollowUp(station.askingItem, wake >= 0 ? text.Substring(wake + WakeWord.Length) : text) == null)
            {
                if (wake < 0)
                    return;
                station.asking = null;
                answering = false;
            }
            if (answering)
            {
                var reply = wake >= 0 ? text.Substring(wake + WakeWord.Length).Trim() : text;
                if (reply.Length == 0)
                    return;
                ResetHearing?.Invoke(speaker);
                Debug.Log($"[Алиса] {speaker.playerUsername}: ответ на вопрос «{station.asking}»: «{reply}»");
                station.OnReply(reply.Replace("[unk]", " ").Trim());
                return;
            }

            // «Алиса, Алиса, Алиса!» — «Вы чего кричите?»
            if (wake >= 0 && !station.IsTalking && station.pendingAnswer == null && Shouted(station, text))
                return;

            // Уже проснулась (ждёт вопрос после «Алиса») — сразу думает; иначе сначала просыпается
            var listening = Time.time < station.listenUntil;
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
            string topic, why;
            if (command.Length == 0)
            {
                topic = null;
                why = "только «Алиса»";
            }
            else if (known.Length == 0)
            {
                topic = TopicMumble;
                why = "только [unk]";
            }
            else
            {
                topic = Topic(known, out why);
            }
            Debug.Log($"[Алиса] {speaker.playerUsername}: «{text}» → вопрос «{known}» → {topic ?? "слушаю"} ({why})");

            // Каждый шаг (позвали, спросили) начинает распознавание заново: следующая фраза не склеится с этой
            ResetHearing?.Invoke(speaker);

            // Пока думает или отвечает, других не слушает (кроме «стоп» и «замолчи»)
            if ((station.IsTalking && !AllowInterrupt || station.pendingAnswer != null) && topic != TopicStop && topic != TopicShush)
                return;

            if (topic == null)
            {
                // «Алиса» без вопроса: сигнал, иногда «Слушаю!» и потом слушает.
                // Уже проснулась на «Алиса» в куске фразы — сигнал прозвучал, просто слушает дальше
                if (listening)
                {
                    station.listenUntil = Mathf.Max(station.listenUntil, Time.time + ListenSilence);
                    return;
                }
                if (station.FindSet(TopicListenVoice) != null && Random.value < ListenVoiceChance)
                {
                    station.Say(TopicListenVoice);
                    station.listenUntil = Mathf.Max(Time.time, station.talkUntil) + ListenSilence;
                    station.listenStartedAt = Time.time;
                }
                else
                    station.Say(SoundListen);
                return;
            }
            if (topic == TopicUnknown)
                topic = station.CustomTopic(known) ?? topic;
            // Ничего не разобрала: «Вы что-то бормочете?» вперемешку с «Не поняла»
            if (topic == TopicMumble && (station.FindSet(TopicMumble) == null || Random.value < 0.5f))
                topic = TopicUnknown;
            if (topic == TopicShush && (station.IsJammed || station.FindSet(TopicShush) == null))
                topic = TopicStop;
            if (station.IsJammed && topic != TopicStop)
                topic = topic == TopicTime && station.FindSet(TopicTimeError) != null ? TopicTimeError : TopicError;

            // Каприз: на понятный вопрос «Нет связи с сервером», «Не хочу», «Бормочете?», «Не поняла»
            if (topic == TopicLouder || topic == TopicQuieter || topic == TopicVolume)
            {
                station.ChangeVolume(topic == TopicLouder ? 1 : topic == TopicQuieter ? -1 : 0);
                return;
            }

            // «Стоп» и «замолчи» сразу, на остальное сначала думает (глаза бегают), потом отвечает
            if (topic == TopicStop || topic == TopicShush)
                Answer(station, speaker, topic, known);
            else
                station.Think(() => Answer(station, speaker, topic, known), wake: !listening);
        }

        /// <summary>
        /// Кусок фразы, пока игрок ещё говорит: на «Алиса» колонка просыпается сразу (сигнал, слушает),
        /// не дожидаясь паузы. Думает она потом, перед ответом.
        /// </summary>
        public static void OnPartial(GameNetcodeStuff.PlayerControllerB speaker, string text)
        {
            text = (text ?? "").ToLowerInvariant().Replace('ё', 'е');
            if (speaker == null || speaker.isPlayerDead || text.IndexOf(WakeWord, StringComparison.Ordinal) < 0)
                return;
            var station = NearestTo(speaker.transform.position, HearingRadius);
            if (station == null || station.IsJammed)
                return;
            // Ждёт ответ на свой вопрос, уже слушает, говорит или думает — будить не надо
            if (station.asking != null && Time.time < station.askUntil || Time.time < station.listenUntil
                || station.IsTalking || station.pendingAnswer != null || Time.time < station.thinkUntil)
                return;
            station.Say(SoundListen);
        }

        private static void Answer(YandexStationItem station, GameNetcodeStuff.PlayerControllerB speaker, string topic, string known)
        {
            if (!station.IsOn)
                return;
            // После ответа на вопрос колонка какое-то время сама не болтает (не перебьёт «Сегодня без…»)
            station.eventFreeAt = Mathf.Max(station.eventFreeAt, Time.time + 20f);

            if (topic != TopicStop && topic != TopicShush && topic != TopicError && topic != TopicTimeError
                && Random.value < WhimChance)
            {
                var whims = new[] { TopicError, TopicRefuse, TopicMumble, TopicUnknown }.Where(t => station.FindSet(t) != null).ToArray();
                if (whims.Length > 0)
                {
                    station.Say(whims[Random.Range(0, whims.Length)]);
                    return;
                }
            }

            // Тот же вопрос второй раз за минуту: «Я уже отвечала». «Где Паша» после «где Андрей» — другой вопрос:
            // у вопросов про человека сравниваем весь текст, а не только тему
            var asked = topic == TopicWhereIs ? topic + "|" + known : topic;
            if (topic != TopicStop && topic != TopicShush && !IsAction(topic) && asked == station.lastAsked && Time.time - station.lastAskedAt < RepeatSeconds
                && station.FindSet(TopicRepeat) != null && Random.value < RepeatChance)
            {
                station.lastAskedAt = Time.time;
                station.Say(TopicRepeat);
                return;
            }
            station.lastAsked = asked;
            station.lastAskedAt = Time.time;

            if (station.AnswerMore(speaker, topic, known))
                return;

            if (topic == TopicGameAsk && station.SayRend(0.3f))
                return;
            if (topic == TopicGameAsk || topic == TopicBored)
            {
                station.Say(station.FindSet(topic) != null ? topic : TopicGameAsk);
                station.gameAnswerUntil = station.talkUntil + GameAnswerSeconds;
            }
            else if (topic == TopicWhoAreYou)
                station.Say(station.FindSet(TopicWhoAreYou) != null ? TopicWhoAreYou : "ItsMe");
            else if (topic == TopicRoute)
            {
                var answers = RouteHandler != null ? RouteHandler(known) : new[] { "Refuse" };
                station.Say(answers.FirstOrDefault(a => station.FindSet(a) != null) ?? TopicUnknown);
            }
            else if (topic == TopicWhereIs)
                station.Say(station.WhereIsTopic(speaker, known));
            else if (topic == TopicInsult)
            {
                // Обругали сразу после отказа — извиняется
                var sorry = Time.time - station.lastRefuseAt < 20f && station.FindSet(TopicSorry) != null;
                station.Say(sorry ? TopicSorry : TopicInsult);
            }
            else if (topic == TopicMyName || topic == TopicMyRole)
            {
                // Спрашивает локальный игрок: распознавание идёт у него. «Кто я» — роль, без неё имя
                var folder = NameFolder(speaker.playerUsername);
                if (folder != null && topic == TopicMyRole && station.FindSet(TopicRoles + folder) != null)
                    station.Say(TopicRoles + folder);
                else if (folder != null && station.FindSet(TopicCall + folder) != null && station.FindSet(TopicMyNameIntro) != null
                         && (station.FindSet(TopicNames + folder) == null || Random.value < 0.5f))
                    station.SaySequence(new List<string> { TopicMyNameIntro, TopicCall + folder }); // «Вас зовут» + «Паша!»
                else
                    station.Say(folder != null && station.FindSet(TopicNames + folder) != null ? TopicNames + folder : TopicNames + "Unknown");
            }
            else if (topic == TopicEnemies)
                station.Say(EnemyNear(station.transform.position, EventRadius) ? TopicEnemiesNear : TopicAllClear);
            else if (topic == TopicCount)
                station.SayCount(PlayersNear(station, 15f));
            else if (topic == TopicLoot)
                station.SayLoot();
            else if (topic == TopicWhoDied)
                station.SayWhoDied(speaker, known);
            else if (topic == TopicLightsOn || topic == TopicLightsOff)
                station.SayFirst(Lights != null ? Lights(topic == TopicLightsOn) : new[] { TopicRefuse });
            else if (topic == TopicBattery)
                station.Say(station.BatteryTopic() ?? TopicUnknown);
            else if (topic == TopicColor)
                station.Say(station.ColorTopic() ?? TopicUnknown);
            else if (topic == TopicFavoriteColor)
            {
                // Любимый цвет — любой из записанных: FavoriteColor и все Color/*
                var sets = station.phraseSets?.Where(x => x != null && x.clips.Length > 0
                    && (x.name == TopicFavoriteColor || x.name.StartsWith("Color/"))).ToList();
                if (sets != null && sets.Count > 0)
                    station.SayFrom(sets[Random.Range(0, sets.Count)]);
                else
                    station.Say(TopicUnknown);
            }
            else if (topic == TopicRefuse)
            {
                // «Не хочу» вперемешку с «Хочу!»
                var agree = station.FindSet(TopicAgree) != null && (station.FindSet(TopicRefuse) == null || Random.value < 0.5f);
                station.Say(agree ? TopicAgree : TopicRefuse);
                if (!agree)
                    station.lastRefuseAt = Time.time;
            }
            else if (topic == TopicTime && station.SayTime())
                return;
            else if (!topic.StartsWith("#") && station.FindSet(topic + AskSuffix) != null)
                station.SayTopic(topic);
            else if (topic == TopicLaunch)
            {
                // Рычаг: сначала «Вы уверены?», если корабль вообще может взлететь или сесть
                var refuse = LaunchCheck != null ? LaunchCheck() : new[] { TopicRefuse };
                if (refuse != null && refuse.Length > 0)
                    station.SayFirst(refuse);
                else
                    station.Ask(AskLaunch, null, LaunchConfirm);
            }
            else if (topic == TopicDoorsOpen || topic == TopicDoorsClose)
                station.SayFirst(Doors != null ? Doors(topic == TopicDoorsClose) : new[] { TopicRefuse });
            else if (topic == TopicBuy)
            {
                // «Купи про-фонарик» — «Сколько?»; «купи два фонарика» — сразу
                var item = AliceIntents.ItemIn(known);
                var count = AliceIntents.NumberIn(known);
                if (item == null)
                    station.SayFirst(BuyUnknown, TopicUnknown);
                else if (count != null)
                    station.SayBought(item, count.Value);
                else
                    station.Ask(AskBuy, item, BuyHowMany);
            }
            else
            {
                station.Say(topic);
                station.ExpectReply(topic);
            }
        }

        // ---- Корабль: рычаг, двери, магазин (делает плагин, у него игровые типы) ----

        /// <summary>null — рычаг можно дёрнуть, иначе темы отказа (первая с записями).</summary>
        public static Func<string[]> LaunchCheck;
        /// <summary>Дёрнуть рычаг (взлёт или посадка), вернуть темы ответа.</summary>
        public static Func<string[]> Launch;
        /// <summary>Закрыть (true) или открыть двери, вернуть темы ответа.</summary>
        public static Func<bool, string[]> Doors;
        /// <summary>Купить предмет (как в строке «$Flashlight: фонарик») столько штук, вернуть темы ответа.</summary>
        public static Func<string, int, string[]> Buy;
        /// <summary>Включить (true) или выключить свет на корабле, вернуть темы ответа.</summary>
        public static Func<bool, string[]> Lights;
        /// <summary>Шанс, что на непонятный ответ про рычаг она всё равно запустит.</summary>
        public static float LaunchAnywayChance = 0.1f;

        private const string TopicLaunch = "#launch", TopicDoorsOpen = "#doorsopen", TopicDoorsClose = "#doorsclose",
            TopicBuy = "#buy", TopicYes = "#yes", TopicNo = "#no";
        public const string LaunchConfirm = "Launch/Confirm", LaunchCancel = "Launch/Cancel", LaunchNoAnswer = "Launch/NoAnswer",
            LaunchAnyway = "Launch/Anyway", BuyHowMany = "Buy/HowMany", BuyCancel = "Buy/Cancel", BuyUnknown = "Buy/Unknown";
        private const string AskLaunch = "launch", AskBuy = "buy", AskFollow = "follow", AskSuffix = "/Ask";

        // Ответ из темы или её встречного вопроса <тема>/Ask («Всё отлично, а у вас как?»), по числу записей
        private void SayTopic(string topic)
        {
            var ask = FindSet(topic + AskSuffix);
            var plain = FindSet(topic);
            var said = ask != null && (plain == null || Random.Range(0, ask.clips.Length + plain.clips.Length) < ask.clips.Length)
                ? topic + AskSuffix : topic;
            Say(said);
            ExpectReply(said);
        }

        // После фразы со встречным вопросом ждём ответ без «Алиса» (строки «Тема > Ответ: …» в intents.txt)
        private void ExpectReply(string topic)
        {
            if (!AliceIntents.HasFollowUps(topic) || FindSet(topic) == null)
                return;
            asking = AskFollow;
            askingItem = topic;
            askStartedAt = Mathf.Max(Time.time, talkUntil);
            askUntil = askStartedAt + AnswerSeconds;
        }

        private static bool IsAction(string topic) =>
            topic == TopicLaunch || topic == TopicDoorsOpen || topic == TopicDoorsClose || topic == TopicBuy || topic == TopicRoute
            || topic == TopicCode || topic == TopicNavExit || topic == TopicNavShip || topic == TopicRepeatLast || topic == TopicMusicOn
            || topic == TopicMusicOff || topic == TopicInHand || topic == TopicScanMoon || topic == TopicLightsOn || topic == TopicLightsOff;

        // Чего она ждёт ответа (только на хосте, где слушают)
        private string asking, askingItem;
        private float askUntil, askStartedAt;

        private void SayFirst(params string[] topics) =>
            Say(topics.FirstOrDefault(t => FindSet(t) != null) ?? TopicUnknown);

        private void Ask(string kind, string item, string question)
        {
            asking = kind;
            askingItem = item;
            if (FindSet(question) != null)
                Say(question);
            else
            {
                Say(SoundListen); // нет записи вопроса — хотя бы сигнал «слушаю»
                listenUntil = 0f;
            }
            askStartedAt = Mathf.Max(Time.time, talkUntil);
            askUntil = askStartedAt + AnswerSeconds;
        }

        // Ответили на вопрос (или промолчали: reply == null)
        private void OnReply(string reply)
        {
            var kind = asking;
            var item = askingItem;
            asking = askingItem = null;
            if (!IsOn)
                return;
            var intent = string.IsNullOrEmpty(reply) ? null : AliceIntents.Match(reply, out _);
            if (OnReplyMore(kind, item, intent) || OnReplyLost(kind, intent))
                return;
            if (kind == AskFollow)
            {
                // «А у меня хорошо» — «Здорово!»; ответ на ответ тоже может ждать продолжения
                var next = string.IsNullOrEmpty(reply) ? null : AliceIntents.MatchFollowUp(item, reply);
                if (next == TopicJoke && item == TopicJoke && FindSet(TopicJokeNext) != null && FindSet(TopicJoke) != null)
                {
                    // «Ещё» — «А вот ещё…» и следующий анекдот
                    SaySequence(new List<string> { TopicJokeNext, TopicJoke });
                    ExpectReply(TopicJoke);
                }
                else if (next != null && FindSet(next) != null)
                {
                    Say(next);
                    ExpectReply(next);
                }
            }
            else if (kind == AskLaunch)
            {
                if (intent == TopicYes)
                    SayFirst(Launch != null ? Launch() : new[] { TopicRefuse });
                else if (intent == TopicNo)
                    SayFirst(LaunchCancel, "Ok");
                else if (Random.value < LaunchAnywayChance && Launch != null)
                {
                    // «Не расслышала… Ладно, запускаю!»
                    var done = Launch();
                    SayFirst(new[] { LaunchAnyway }.Concat(done).ToArray());
                }
                else
                    SayFirst(LaunchNoAnswer, TopicMumble, TopicUnknown);
            }
            else if (kind == AskBuy)
            {
                if (intent == TopicNo)
                    SayFirst(BuyCancel, "Ok");
                else
                    SayBought(item, AliceIntents.NumberIn(reply ?? "") ?? 1);
            }
        }

        // Не ответили на вопрос: рычаг — «не расслышала», покупка — одна штука
        private void UpdateAsking()
        {
            if (asking != null && Time.time > askUntil && !IsTalking)
                OnReply(null);
        }

        /// <summary>
        /// Игрок что-то говорит (распознавание слышит слова): пока он говорит, колонка ждёт,
        /// выключается после <see cref="ListenSilence"/> секунд тишины, но не позже <see cref="ListenMax"/>.
        /// </summary>
        public static void OnVoice(GameNetcodeStuff.PlayerControllerB speaker)
        {
            if (speaker == null || speaker.isPlayerDead)
                return;
            var station = NearestTo(speaker.transform.position, HearingRadius);
            if (station == null)
                return;
            if (station.listenUntil > 0f && Time.time < station.listenUntil)
                station.listenUntil = Mathf.Min(station.listenStartedAt + ListenMax, Mathf.Max(station.listenUntil, Time.time + ListenSilence));
            if (station.asking != null && Time.time < station.askUntil)
                station.askUntil = Mathf.Min(station.askStartedAt + ListenMax, Mathf.Max(station.askUntil, Time.time + ListenSilence));
        }

        // ---- «Думает» перед ответом ----

        /// <summary>Пауза перед ответом, секунды (случайная между Min и Max).</summary>
        public static float ThinkMin = 0.7f, ThinkMax = 1.5f;
        private const string TopicThink = "#think";
        private float thinkUntil, thinkStartAt;
        private bool thinkStarted;
        private Action pendingAnswer;

        // Решает тот, кто услышал; остальным уходит «#think» с длительностью в мс, чтобы у всех бегали глаза.
        // wake — сначала проснуться (сигнал, глаза), потом думать: даже на «Алиса, …» одним духом
        private void Think(Action answer, bool wake)
        {
            pendingAnswer = answer;
            thinkStarted = false;
            thinkStartAt = Time.time;
            if (wake)
            {
                Say(SoundListen);
                thinkStartAt += Mathf.Max(WakeSeconds, listenClip != null ? listenClip.length : 0f);
            }
            UpdateThinking();
        }

        private void UpdateThinking()
        {
            if (pendingAnswer == null)
                return;
            if (!thinkStarted)
            {
                if (Time.time < thinkStartAt)
                    return;
                thinkStarted = true;
                var ms = Mathf.RoundToInt(Random.Range(ThinkMin, Mathf.Max(ThinkMin, ThinkMax)) * 1000f);
                Speak(TopicThink, ms);
                SendSpeak(TopicThink, ms);
                return;
            }
            if (Time.time < thinkUntil)
                return;
            var answer = pendingAnswer;
            pendingAnswer = null;
            answer();
        }

        // Пока думает: по кругу бежит один сегмент в двух средних цифрах
        private long ThinkMask()
        {
            var seg = "abcdef"[(int)(Time.time * 12f) % 6].ToString();
            return Digit(1, Mask(seg)) | Digit(2, Mask(seg));
        }

        private string lastAsked;
        private float lastRefuseAt = -100f, quietUntil;
        private float gameAnswerUntil;
        private float lastAskedAt;

        /// <summary>Есть ли рядом с живым игроком включённая колонка (тогда плагин слушает его голос).</summary>
        public static bool NearPlayer(GameNetcodeStuff.PlayerControllerB player) =>
            player != null && player.isPlayerControlled && !player.isPlayerDead && NearestTo(player.transform.position, HearingRadius) != null;

        /// <summary>
        /// Стоит ли ответить на кусок фразы, не дожидаясь паузы: позвали «Алиса» и уже понятно о чём.
        /// </summary>
        public static bool IsCompleteCommand(string text)
        {
            text = (text ?? "").ToLowerInvariant().Replace('ё', 'е');
            var wake = text.IndexOf(WakeWord, StringComparison.Ordinal);
            if (wake < 0)
                return false;
            var end = text.IndexOf(' ', wake);
            var command = end < 0 ? "" : text.Substring(end + 1).Replace("[unk]", " ").Trim();
            // Одно слово («куда…», «где…») — ждём продолжения, кроме «стоп» и «замолчи»
            var intent = AliceIntents.Match(command, out var score);
            if (intent == null || score < 0.85f)
                return false;
            if (intent == TopicRoute)
                return false; // луну дослушиваем до конца
            return intent == TopicStop || intent == "#shush" || intent == TopicLouder || intent == TopicQuieter
                   || AliceIntents.Tokens(command).Length >= 2;
        }

        private static YandexStationItem NearestTo(Vector3 pos, float radius)
        {
            if (All.Count == 0)
                return null;
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
        public static string Topic(string command) => Topic(command, out _);

        /// <param name="why">Для лога: какой пример победил и с какой похожестью.</param>
        public static string Topic(string command, out string why)
        {
            // «Открой дверь бэ четыре», «отключи турель a2»: код как в терминале
            var codes = CodesIn(command);
            if (codes.Count > 0)
            {
                why = "код " + string.Join(",", codes);
                return TopicCode;
            }
            // Темы и примеры вопросов — в intents.txt, здесь только темы, которые считаются по игре
            var intent = AliceIntents.Match(command, out var score, out var example);
            why = example != null
                ? $"пример «{example}», похожесть {score:0.00}, порог {AliceIntents.Threshold:0.00}"
                : "нет примеров";
            // «Куда полетим, на Титан?» — назвали луну: ставим маршрут
            if ((intent == "Moon" || intent == "#navexit" || intent == "#navship") && AliceIntents.NamesMoon(command))
                intent = TopicRoute;
            switch (intent)
            {
                case null:
                    return TopicUnknown;
                case "#shush":
                    return TopicShush;
                case "#time":
                    return TopicTime;
                case "#yes":
                case "#no":
                    return "Ok"; // «да», «нет» без вопроса
                case "Weather":
                    return "Weather/" + (WeatherName() ?? "Other");
                case "Deadline":
                    var days = TimeOfDay.Instance != null ? TimeOfDay.Instance.daysUntilDeadline : 3;
                    return "Deadline" + Mathf.Clamp(days, 0, 3);
                case "Quota":
                    var tod = TimeOfDay.Instance;
                    return tod != null && tod.quotaFulfilled >= tod.profitQuota ? "QuotaDone" : "QuotaNotDone";
                default:
                    return intent;
            }
        }

        /// <summary>
        /// Погода луны, на которой (или к которой летит) корабль: название из WeatherRegistry,
        /// если он стоит (так видна и погода модов), иначе ванильное. Ясно → Clear, null — не знаем.
        /// </summary>
        public static string WeatherName()
        {
            // IsJammed спрашивает каждый кадр у каждой колонки: считаем раз за кадр
            if (weatherFrame == Time.frameCount)
                return weatherCached;
            weatherFrame = Time.frameCount;
            weatherCached = null;
            var level = StartOfRound.Instance != null ? StartOfRound.Instance.currentLevel : null;
            if (level == null)
                return null;
            var name = WeatherRegistryName(level);
            if (string.IsNullOrEmpty(name))
            {
                var weather = level.currentWeather;
                name = Enum.IsDefined(typeof(LevelWeatherType), weather) ? weather.ToString() : null;
            }
            return weatherCached = name == "None" ? "Clear" : name;
        }

        private static int weatherFrame = -1;
        private static string weatherCached;

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
            var index = NextIndex(set);
            Speak(set.name, index);
            SendSpeak(set.name, index);
        }

        // Записи темы идут по кругу в случайном порядке: пока не прозвучат все, ни одна не повторится,
        // и новый круг не начинается с той, что была последней
        private static readonly Dictionary<string, List<int>> Bags = new Dictionary<string, List<int>>();
        private static readonly Dictionary<string, int> LastFromBag = new Dictionary<string, int>();

        private static int NextIndex(PhraseSet set)
        {
            var n = set.clips.Length;
            if (n <= 1)
                return 0;
            if (!Bags.TryGetValue(set.name, out var bag) || bag.Count == 0 || bag.Any(i => i >= n))
            {
                bag = Enumerable.Range(0, n).OrderBy(_ => Random.value).ToList();
                if (LastFromBag.TryGetValue(set.name, out var last) && bag[0] == last)
                    (bag[0], bag[n - 1]) = (bag[n - 1], bag[0]);
                Bags[set.name] = bag;
            }
            var index = bag[0];
            bag.RemoveAt(0);
            LastFromBag[set.name] = index;
            return index;
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
                    parts.Add(set.name + "\t" + NextIndex(set));
            }
            if (parts.Count == 0)
                return;
            askedLocally = true;
            var topic = SeqPrefix + string.Join("\n", parts);
            Speak(topic, 0);
            SendSpeak(topic, 0);
        }

        /// <summary>
        /// Число словами из кусков: Numbers/0…19, десятки Numbers/20…90, сотни Numbers/100…900, Numbers/1000
        /// («тысяча двести сорок пять»). Больше — Numbers/More2000 («Больше двух тысяч!») или
        /// Numbers/More1000; нет куска — false (ничего не добавляет).
        /// </summary>
        private bool AddNumber(List<string> topics, int n)
        {
            if (n < 0)
                return false;
            var parts = new List<string>();
            if (NumberParts(n, parts))
            {
                topics.AddRange(parts);
                return true;
            }
            var more = n >= 2000 && FindSet(TopicNumbers + "More2000") != null ? TopicNumbers + "More2000"
                : n >= 1000 && FindSet(TopicNumbers + "More1000") != null ? TopicNumbers + "More1000" : null;
            if (more == null)
                return false;
            topics.Add(more);
            return true;
        }

        private bool NumberParts(int n, List<string> parts)
        {
            bool Add(int k)
            {
                if (FindSet(TopicNumbers + k) == null)
                    return false;
                parts.Add(TopicNumbers + k);
                return true;
            }
            if (FindSet(TopicNumbers + n) != null)
                return Add(n);
            if (n >= 2000)
                return false;
            if (n >= 1000)
                return Add(1000) && NumberParts(n - 1000, parts);
            if (n >= 100)
                return Add(n / 100 * 100) && (n % 100 == 0 || NumberParts(n % 100, parts));
            if (n >= 20)
                return Add(n / 10 * 10) && (n % 10 == 0 || Add(n % 10));
            return false;
        }

        /// <summary>Перечисление: «Паша и Андрей», «Паша, Андрей и ещё Сеня» (And — «и», And/Also — «и ещё», «а также»).</summary>
        private void AddList(List<string> topics, List<string> items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0 && i == items.Count - 1)
                    topics.Add(items.Count > 2 && FindSet(TopicAnd + "/Also") != null ? TopicAnd + "/Also" : TopicAnd);
                topics.Add(items[i]);
            }
        }

        /// <summary>«Который час»: время с экрана числами. false — чисел нет, ответит фраза из Time.</summary>
        /// <summary>«Давайте поиграем в Rend!» и сразу «Хотя нет…» — с такой вероятностью, если записи есть.</summary>
        private bool SayRend(float chance)
        {
            if (FindSet(TopicRend) == null || FindSet(TopicCancel) == null || Random.value > chance)
                return false;
            SaySequence(new List<string> { TopicRend, TopicCancel });
            return true;
        }

        /// <summary>
        /// «Где Андрей?», «где он?», «где все?»: по состоянию игроков. Стоит на месте полминуты — WhereIs/AFK,
        /// в комплексе — WhereIs/Inside, снаружи далеко от всех — WhereIs/Lost (One/Many),
        /// иначе WhereIs/AllAlive. Друга нет в игре — Events/Missing/&lt;Имя&gt;.
        /// </summary>
        private string WhereIsTopic(GameNetcodeStuff.PlayerControllerB speaker, string command)
        {
            var round = StartOfRound.Instance;
            const string folder = "WhereIs/";
            if (round == null)
                return folder + "AllAlive";

            // Названный по имени друг
            var tokens = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var named = FriendNamed(command);

            var others = round.allPlayerScripts
                .Where(p => p != null && p != speaker && (p.isPlayerControlled || p.isPlayerDead)).ToList();
            List<GameNetcodeStuff.PlayerControllerB> asked;
            if (named != null)
            {
                asked = others.Where(p => NameFolder(p.playerUsername) == named).ToList();
                if (asked.Count == 0)
                    return FindSet(EventsFolder + "Missing/" + named) != null ? EventsFolder + "Missing/" + named : folder + "Lost/One";
                // Погиб — «Его съела собака» или его личное PlayerDied
                if (asked.All(p => p.isPlayerDead))
                {
                    var options = new List<string>();
                    if (FindSet(EventsFolder + "PlayerDied/" + named) != null)
                        options.Add(EventsFolder + "PlayerDied/" + named);
                    if (DeathCauses.TryGetValue(asked[0].playerClientId, out var cause))
                    {
                        var c = EventsFolder + "DeathCause/" + cause;
                        if (FindSet(c + "/One") != null)
                            options.Add(c + "/One");
                        else if (FindSet(c) != null)
                            options.Add(c);
                    }
                    if (options.Count == 0 && FindSet(EventsFolder + "PlayerDied") != null)
                        options.Add(EventsFolder + "PlayerDied");
                    if (options.Count > 0)
                        return options[Random.Range(0, options.Count)];
                }
            }
            else if (tokens.Contains("все") || tokens.Contains("остальные"))
                asked = others;
            else
            {
                // «Где он?» — тот, кто дальше всех от спросившего
                var far = others.Where(p => !p.isPlayerDead)
                    .OrderByDescending(p => Vector3.Distance(p.transform.position, speaker.transform.position)).FirstOrDefault();
                asked = far != null ? new List<GameNetcodeStuff.PlayerControllerB> { far } : new List<GameNetcodeStuff.PlayerControllerB>();
            }

            var alive = asked.Where(p => !p.isPlayerDead).ToList();
            if (asked.Count == 1 && alive.Count == 1 && alive[0].timeSincePlayerMoving > 30f && FindSet(folder + "AFK") != null)
                return folder + "AFK";
            string Form(string state, int n) =>
                FindSet(folder + state + (n > 1 ? "/Many" : "/One")) != null ? folder + state + (n > 1 ? "/Many" : "/One") : folder + state;
            var inside = alive.Count(p => p.isInsideFactory);
            if (inside > 0)
                return Form("Inside", inside);
            // «Он снаружи!» — один, на луне, но не на корабле
            if (asked.Count == 1 && alive.Count == 1 && !alive[0].isInHangarShipRoom && FindSet(folder + "Outside/One") != null && Random.value < 0.5f)
                return folder + "Outside/One";
            var everyone = round.allPlayerScripts.Where(p => p != null && p.isPlayerControlled && !p.isPlayerDead).ToList();
            var lost = alive.Count(p => !p.isInHangarShipRoom
                                        && everyone.All(o => o == p || Vector3.Distance(o.transform.position, p.transform.position) > 40f));
            if (lost > 0)
                return Form("Lost", lost);
            if (asked.Count > alive.Count)
                return Form("Lost", asked.Count - alive.Count); // погибших не выдаёт: «потерялся»
            return folder + "AllAlive";
        }

        private bool SayTime()
        {
            var tod = TimeOfDay.Instance;
            if (tod == null || !tod.currentDayTimeStarted)
                return false;
            var minutes = (int)(tod.normalizedTimeOfDay * (60f * tod.numberOfHours)) + 360;
            if (SayTimeNumbers(minutes))
                return true;
            // Словами: Time/15 — «Три часа дня», Time/Almost/7 — «Почти семь».
            // Больше получаса — уже следующий час («почти семь»); нет записи на час — ближайший
            // записанный в пределах часа, иначе просто ближайший записанный
            var hour = minutes / 60 % 24;
            var next = (hour + 1) % 24;
            var prev = (hour + 23) % 24;
            var late = minutes % 60 > 30;
            var order = late
                ? new[] { TopicTime + "/Almost/" + next, TopicTime + "/" + next, TopicTime + "/" + hour, TopicTime + "/Almost/" + hour }
                : new[] { TopicTime + "/" + hour, TopicTime + "/Almost/" + hour, TopicTime + "/" + prev, TopicTime + "/Almost/" + next, TopicTime + "/" + next };
            foreach (var hourTopic in order)
            {
                if (FindSet(hourTopic) != null)
                {
                    Say(hourTopic);
                    return true;
                }
            }
            // Ближайший записанный час, сколько бы ни было до него
            var now = minutes / 60f;
            string best = null;
            var bestDist = float.MaxValue;
            for (var h = 0; h < 24; h++)
            {
                foreach (var (topic, at) in new[] { (TopicTime + "/" + h, (float)h), (TopicTime + "/Almost/" + h, h - 0.25f) })
                {
                    if (FindSet(topic) == null)
                        continue;
                    var d = Mathf.Abs(now - at) % 24f;
                    d = Mathf.Min(d, 24f - d);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = topic;
                    }
                }
            }
            Say(best ?? (FindSet(TopicTimeError) != null ? TopicTimeError : TopicTime));
            return true;
        }

        /// <summary>«Кто погиб»: имена через «и» («Паша и Андрей!», Call/&lt;Имя&gt;); все живы или имён нет — как «где все».</summary>
        private void SayWhoDied(GameNetcodeStuff.PlayerControllerB speaker, string command)
        {
            var round = StartOfRound.Instance;
            var dead = round == null ? new List<string>() : round.allPlayerScripts
                .Where(p => p != null && p.isPlayerDead)
                .Select(p => NameFolder(p.playerUsername)).Where(f => f != null && FindSet(TopicCall + f) != null)
                .Distinct().Select(f => TopicCall + f).ToList();
            if (dead.Count == 0)
            {
                Say(WhereIsTopic(speaker, command));
                return;
            }
            // «Погибли» (или «Кто-то умер, перепроверяю») и имена
            var topics = new List<string>();
            var intro = Random.value < 0.15f && FindSet("WhoDied/Recheck") != null ? "WhoDied/Recheck" : "WhoDied/Intro";
            if (FindSet(intro) != null && Random.value < 0.7f)
                topics.Add(intro);
            AddList(topics, dead);
            SaySequence(topics);
        }

        /// <summary>«Сколько на корабле»: «Смотрю…» (Wait), «Всего на корабле» (Loot/Intro) и сумма лута числом.</summary>
        private void SayLoot()
        {
            var total = 0;
            var count = 0;
            foreach (var item in FindObjectsOfType<GrabbableObject>())
            {
                if (item != null && item.itemProperties != null && item.itemProperties.isScrap && item.isInShipRoom)
                {
                    total += item.scrapValue;
                    count++;
                }
            }
            if (count > 0 && SayLootMore(count, total))
                return;
            var topics = new List<string>();
            if (FindSet(TopicWait) != null && Random.value < 0.5f)
                topics.Add(TopicWait);
            topics.Add(TopicLootIntro);
            if (AddNumber(topics, total))
                SaySequence(topics);
            else
                Say(TopicUnknown);
        }

        /// <summary>
        /// «На часах» (Time/Intro) + часы числом + Time/Hours/One|Few|Many («час», «часа», «часов») + минуты до 5
        /// + Time/Minutes/Many («минут»). Не хватает куска — false, тогда словами из Time/&lt;час&gt;.
        /// </summary>
        private bool SayTimeNumbers(int minutes)
        {
            if (!TimeInNumbers)
                return false;
            var h = minutes / 60 % 24;
            var m = (minutes % 60 + 2) / 5 * 5;
            if (m == 60)
            {
                m = 0;
                h = (h + 1) % 24;
            }
            var topics = new List<string>();
            if (FindSet(TopicTime + "/Intro") != null)
                topics.Add(TopicTime + "/Intro");
            var hours = TopicTime + "/Hours/" + (h % 10 == 1 && h != 11 ? "One" : h % 10 >= 2 && h % 10 <= 4 && (h < 12 || h > 14) ? "Few" : "Many");
            var mins = TopicTime + "/Minutes/Many";
            if (!NumberParts(h, topics) || FindSet(hours) == null)
                return false;
            topics.Add(hours);
            if (m > 0)
            {
                if (!NumberParts(m, topics) || FindSet(mins) == null)
                    return false;
                topics.Add(mins);
            }
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

        // Задала вопрос и ждёт ответ: смотрит и светится, как после «Алиса»
        private bool AwaitingAnswer => asking != null && askUntil < float.MaxValue && Time.time >= askStartedAt && Time.time < askUntil;

        private bool IsTalking => Time.time < talkUntil && audioSource != null && audioSource.isPlaying;

        private void Speak(string topic, int index)
        {
            if (audioSource == null || !IsOn)
                return;
            if (topic == TopicThink)
            {
                listenUntil = 0f;
                thinkUntil = Time.time + index / 1000f;
                return;
            }
            switch (topic)
            {
                case TopicStop:
                    queued.Clear();
                    audioSource.Stop();
                    talkUntil = 0f;
                    return;
                case SoundListen:
                    listenUntil = Time.time + ListenSilence;
                    listenStartedAt = Time.time;
                    PlaySound(listenClip);
                    return;
                case SoundTimeout:
                    listenUntil = 0f;
                    PlaySound(listenTimeoutClip);
                    return;
            }
            if (topic.StartsWith(VolumePrefix))
            {
                // Без слов: сигнал уже новой громкостью и проценты на экране
                if (int.TryParse(topic.Substring(VolumePrefix.Length), out var step))
                    volumeStep = Mathf.Clamp(step, MinVolumeStep, 10);
                ApplyVolume();
                listenUntil = 0f;
                volumeShownUntil = Time.time + VolumeShowSeconds;
                PlaySound(listenClip);
                return;
            }

            queued.Clear();
            if (!topic.StartsWith("#") || topic.StartsWith(SeqPrefix) && !topic.Contains("Repeat/Again"))
            {
                lastSaidTopic = topic;
                lastSaidIndex = index;
            }
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
            if (setName == TopicShush)
                quietUntil = Time.time + ShushSeconds; // помолчать: сама не болтает
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
        public static float EventChance = 0.2f;
        /// <summary>Шанс каприза вместо ответа (ошибка связи, «не хочу», «бормочете?», «не поняла»).</summary>
        public static float WhimChance = 0.15f;
        /// <summary>Пауза (с) после реплики колонки, прежде чем она снова заговорит сама.</summary>
        public static float EventCooldown = 90f;
        /// <summary>Одна и та же тема события не чаще, чем раз в столько секунд (на все колонки).</summary>
        public static float EventTopicCooldown = 600f;
        /// <summary>С какого расстояния (м) колонка замечает монстра.</summary>
        public static float EventRadius = 15f;
        /// <summary>Как далеко монстры слышат реплику на событие (м): громко — может приманить.</summary>
        public static float EventNoiseRange = 25f;
        /// <summary>Раз во сколько секунд колонка может поболтать сама, если рядом кто-то есть (0 — никогда).</summary>
        public static float IdleSeconds = 240f;

        public const string EventsFolder = "Events/";
        private const float EventCheckSeconds = 1.5f;

        // Общие на все колонки: две колонки не говорят про одно и то же разом
        private static readonly Dictionary<string, float> topicFreeAt = new Dictionary<string, float>();
        private static float anyEventFreeAt;
        private static readonly HashSet<string> LoggedEnemies = new HashSet<string>();

        private float noiseRange;
        private float nextEventCheck, eventFreeAt, nextIdle;
        private bool eventsReady, wasLanded, wasLeaving, wasHeld, wasNight, lowBatteryTold, wasAllDead;
        private int deadPlayers, playersNear, sipsSeen = -1, shipScrap = -1, credits = -1;
        private bool wasLate, wasMusic, wasWalkie, wasWinston;
        private int squeaksSeen = -1;
        private float nextScan;
        private readonly Dictionary<ulong, int> suits = new Dictionary<ulong, int>();
        private static Terminal terminal;
        /// <summary>Столько скрапа на корабле — «ой, сколько предметов».</summary>
        public static int ManyItems = 20;
        /// <summary>Скрап дешевле этого — хлам; столько хлама на корабле — «Ну и хлам тут у вас».</summary>
        public static int JunkValue = 20, ManyJunk = 8;

        private static Terminal Shop()
        {
            if (terminal == null)
                terminal = FindObjectOfType<Terminal>();
            return terminal;
        }
        private readonly HashSet<ulong> deadIds = new HashSet<ulong>();
        private readonly HashSet<ulong> seenEnemies = new HashSet<ulong>();
        private readonly List<string> reasons = new List<string>();
        private readonly List<string> pendingDeaths = new List<string>();
        /// <summary>Кто от чего погиб в этот день (для «чё с Пашей?»).</summary>
        private static readonly Dictionary<ulong, string> DeathCauses = new Dictionary<ulong, string>();
        private readonly HashSet<ulong> joined = new HashSet<ulong>(), lowHealth = new HashSet<ulong>(), bodiesSeen = new HashSet<ulong>();
        // Кто ушёл в комплекс (ждём на корабле), кто уже вернулся первым за посадку, на ком висит монстр
        private readonly HashSet<ulong> wentInside = new HashSet<ulong>(), onHeadSeen = new HashSet<ulong>();
        private bool firstBackTold;
        // Про кого уже сказали «Сегодня без …»: общее для всех колонок, раз в игре до посадки
        // и ещё раз после каждой посадки
        private static readonly HashSet<string> missingTold = new HashSet<string>();
        private static float missingClearedAt = -999f;
        private static StartOfRound greetedRound;
        private float lastJoinAt;
        private bool joinedSeeded, wasShipPhase = true, dropshipLanded, dropshipOpened, junkTold;
        private float deathsFlushAt;

        // Кто убил: ближайший к месту смерти монстр (enemyName без пробелов), иначе Unknown
        private static string DeathCause(GameNetcodeStuff.PlayerControllerB p)
        {
            var at = p.deadBody != null ? p.deadBody.transform.position : p.placeOfDeath;
            if (at == Vector3.zero)
                at = p.transform.position;
            EnemyAI best = null;
            var bestDist = 8f * 8f;
            foreach (var enemy in UnityEngine.Object.FindObjectsOfType<EnemyAI>())
            {
                if (enemy == null || enemy.enemyType == null)
                    continue;
                var d = (enemy.transform.position - at).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = enemy;
                }
            }
            if (best == null)
                return "Unknown";
            var name = best.enemyType.enemyName.Replace(" ", "");
            Debug.Log("[Alice] " + p.playerUsername + " died (" + p.causeOfDeath + "), nearest enemy " + name);
            return name;
        }

        private static bool EnemyNear(Vector3 pos, float radius)
        {
            foreach (var enemy in UnityEngine.Object.FindObjectsOfType<EnemyAI>())
                if (enemy != null && !enemy.isEnemyDead && (enemy.transform.position - pos).sqrMagnitude < radius * radius)
                    return true;
            return false;
        }

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
            if (reasons.Count == 0 || !IsOn || IsTalking || isPocketed || IsJammed || Time.time < quietUntil
                || Time.time < eventFreeAt || Time.time < anyEventFreeAt || listenUntil > 0f
                || Time.time < thinkUntil || pendingAnswer != null || asking != null)
                return;

            foreach (var topic in reasons)
            {
                if (topicFreeAt.TryGetValue(topic, out var free) && Time.time < free)
                    continue;
                var set = FindSet(topic);
                // Кто пришёл и кого нет — говорит всегда, остальное изредка
                var sure = topic.StartsWith(EventsFolder + "Missing/") || topic.StartsWith(EventsFolder + "Joined")
                    || topic == EventsFolder + "NewGame" || topic.StartsWith(EventsFolder + "FirstBack/")
                    || topic == EventsFolder + "DoorsClosedOutside" || topic == EventsFolder + "Fired" || topic == EventsFolder + "Teamkill"
                    || topic == EventsFolder + "Dropped" && Random.value < 0.6f; // уронили — говорит чаще остального
                if (set == null && topic != NavLostReason || !sure && Random.value > EventChance)
                    continue;
                topicFreeAt[topic] = Time.time + EventTopicCooldown;
                if (topic.StartsWith(EventsFolder + "Missing/"))
                    missingTold.Add(topic.Substring((EventsFolder + "Missing/").Length));
                eventFreeAt = Time.time + EventCooldown;
                anyEventFreeAt = Time.time + 3f;
                noiseRange = EventNoiseRange;
                // От скуки иногда «Давайте поиграем в Rend!» и тут же «Хотя нет…»
                if (topic == EventsFolder + "Idle" && SayRend(0.25f))
                    return;
                if (SayEventMore(topic))
                    return;
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
            CollectMoreReasons(round);

            // Дёрнули рычаг: корабль садится на луну — «Не забудьте зарядить фонарики!»
            if (!round.inShipPhase && wasShipPhase)
                Reason("Flashlight");
            wasShipPhase = round.inShipPhase;
            if (round.shipHasLanded && !wasLanded)
            {
                Reason("Landed");
                firstBackTold = false;
                wentInside.Clear();
                // После посадки ещё раз: кого нет (одна очистка на посадку, даже если колонок несколько)
                if (Time.time - missingClearedAt > 120f)
                {
                    missingTold.Clear();
                    missingClearedAt = Time.time;
                }
                if (Shop() is { } shop && (shop.numberOfItemsInDropship > 0 || shop.orderedItemsFromTerminal != null && shop.orderedItemsFromTerminal.Count > 0))
                    Reason("Delivery");
            }
            wasLanded = round.shipHasLanded;
            if (round.shipIsLeaving && !wasLeaving)
            {
                // Кто-то живой остался снаружи — «вы кого-то забыли на луне» (все мертвы — AllDead ниже)
                if (!round.allPlayersDead && round.allPlayerScripts.Any(p => p != null && p.isPlayerControlled && !p.isPlayerDead && !p.isInHangarShipRoom))
                    Reason("ForgotSomeone");
                Reason("Takeoff");
                Reason("Leaving");
            }
            wasLeaving = round.shipIsLeaving;
            if (round.allPlayersDead && !wasAllDead && !round.inShipPhase)
                Reason("AllDead");
            wasAllDead = round.allPlayersDead;

            if (hornHeard)
            {
                hornHeard = false;
                Reason("Horn");
            }

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
                    // Убили свои — «Зачем вы убили своего друга?» раньше обычного «Ой, нет!»
                    if (IsTeamkill(p))
                        Reason("Teamkill");
                    var folder = NameFolder(p.playerUsername);
                    if (folder != null)
                        Reason("PlayerDied/" + folder);
                    Reason("PlayerDied");
                    // Причину копим пару секунд: погибли разом несколько — «их», один — «его»
                    var cause = DeathCause(p);
                    DeathCauses[p.playerClientId] = cause;
                    MetReason(cause);
                    pendingDeaths.Add(cause);
                    deathsFlushAt = Time.time + 3f;
                }
            }
            deadPlayers = dead;
            if (pendingDeaths.Count > 0 && Time.time >= deathsFlushAt)
            {
                foreach (var cause in pendingDeaths.Distinct())
                {
                    var many = pendingDeaths.Count(c => c == cause) > 1;
                    var folder = "DeathCause/" + cause;
                    reasons.Add(EventsFolder + folder + (many ? "/Many" : "/One"));
                    reasons.Add(EventsFolder + folder);
                }
                if (pendingDeaths.Count > 1)
                    Reason("Panic");
                pendingDeaths.Clear();
            }

            // Кто подключился: «О, а вот и вы!», сначала личное Joined/<Имя>
            var pos0 = transform.position;
            foreach (var p in round.allPlayerScripts)
            {
                if (p == null)
                    continue;
                var id = p.playerClientId;
                var here = p.isPlayerControlled || p.isPlayerDead;
                if (here && joined.Add(id))
                {
                    // Пришёл: «А, вот он пришёл!», сначала его личное Joined/<Имя>
                    // (а «Сегодня без …» про него больше не звучит, даже если выйдет и вернётся)
                    var folder = NameFolder(p.playerUsername);
                    if (folder != null)
                        missingTold.Add(folder);
                    if (joinedSeeded)
                    {
                        if (folder != null)
                            Reason("Joined/" + folder);
                        Reason("Joined");
                    }
                    lastJoinAt = Time.time;
                }
                else if (!here)
                    joined.Remove(id); // отключился

                var close = Vector3.Distance(p.transform.position, pos0) < EventRadius;
                // Рядом мало здоровья — «Что-то вы неважно себя чувствуете»
                if (p.isPlayerControlled && !p.isPlayerDead && close && p.health <= 30)
                {
                    if (lowHealth.Add(id))
                        Reason("LowHealth");
                }
                else if (p.isPlayerDead || p.health > 50)
                    lowHealth.Remove(id);

                // Рядом тело — «Кругом кровь и кишки»
                var body = p.isPlayerDead ? p.deadBody : null;
                if (body != null && Vector3.Distance(body.transform.position, pos0) < 8f)
                {
                    if (bodiesSeen.Add(id))
                        Reason("Gore");
                }
                else if (!p.isPlayerDead)
                    bodiesSeen.Remove(id);

                // Вернулся из комплекса на корабль: «Вот и вы!» (Back), первый — «О, Андрей! А остальные где?»
                // (FirstBack/<Имя>), без лута — «Почему с пустыми руками?», ранен — «Будьте осторожнее!»
                if (!p.isPlayerControlled || p.isPlayerDead || !round.shipHasLanded || round.shipIsLeaving)
                    wentInside.Remove(id);
                else if (p.isInsideFactory)
                    wentInside.Add(id);
                else if (p.isInHangarShipRoom && wentInside.Remove(id))
                    OnBack(p, round);
            }
            joinedSeeded = true;

            // Зашли в игру (загрузили сохранение или новую): один раз всем «Привет, новички!»
            if (greetedRound != round && round.inShipPhase && PlayerNear(this, 8f))
            {
                greetedRound = round;
                Reason("NewGame");
            }

            // Все или часть зашли и 20 секунд никто не приходит — «Сегодня без Андрея?» про каждого, кого нет
            // (пока не скажет про каждого: повод держится, отмечается сказанным, когда прозвучал)
            // Только когда в игре есть кто-то кроме хоста: один на тесте — не «без всех»
            var players = round.allPlayerScripts.Where(p => p != null && (p.isPlayerControlled || p.isPlayerDead)).ToList();
            if (Time.time > lastJoinAt + 20f && players.Count >= 2 && (round.inShipPhase || round.shipHasLanded))
            {
                var present = players.Select(p => NameFolder(p.playerUsername)).Where(f => f != null).ToList();
                foreach (var folder in FriendNames.Values.Distinct())
                {
                    if (!present.Contains(folder) && !missingTold.Contains(folder))
                        Reason("Missing/" + folder);
                }
            }

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
                    // Монстр на голове у игрока рядом: «Какая красивая шляпа!» (Enemy/<имя>/OnHead)
                    var head = enemy is CentipedeAI centipede ? centipede.clingingToPlayer
                        : enemy is FlowerSnakeEnemy snake ? snake.clingingToPlayer : null;
                    if (head != null && !enemy.isEnemyDead && Vector3.Distance(head.transform.position, pos) < EventRadius)
                    {
                        if (onHeadSeen.Add(id))
                            Reason("Enemy/" + enemy.enemyType.enemyName + "/OnHead");
                    }
                    else
                        onHeadSeen.Remove(id);
                    if (!enemy.isEnemyDead && dist < EventRadius)
                    {
                        if (seenEnemies.Add(id))
                        {
                            Reason("Enemy/" + enemy.enemyType.enemyName);
                            Reason("Panic");
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
                {
                    // Егермейстер: «Егермейстер? Классика!», «Не увлекайтесь алкоголем, Арсений!»
                    if (DrinkAnimation.LastSipAlcohol)
                    {
                        var folder = NameFolder(DrinkAnimation.LastSipBy);
                        if (Random.value < 0.5f)
                            Reason("Drink/Jagermeister");
                        if (folder != null)
                            Reason("Drink/Alcohol/" + folder);
                        Reason("Drink/Alcohol");
                    }
                    Reason("Drink");
                }
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
                {
                    // «Давайте полетим на …» (Moon) — только ответ на «куда летим», сама не предлагает
                    Reason("Idle");
                }
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
                {
                    // Сдали лут в Компании — «Отлично, деньги на счету!»
                    if (round.currentLevel != null && round.currentLevel.levelID == 3)
                        Reason("Sold");
                    Reason("Money");
                }
                else if (credits >= 0 && terminal.groupCredits < credits)
                    Reason("Purchase");
                credits = terminal.groupCredits;
            }

            var scrap = 0;
            var junk = 0;
            var music = false;
            var walkie = false;
            var winston = false;
            var pos = transform.position;
            foreach (var item in FindObjectsOfType<GrabbableObject>())
            {
                if (item == null || item.itemProperties == null)
                    continue;
                if (item.isInShipRoom && item.itemProperties.isScrap && !item.isHeld)
                {
                    scrap++;
                    if (item.scrapValue < JunkValue)
                        junk++;
                }
                var close = Vector3.Distance(item.transform.position, pos) < EventRadius;
                if (close && item is BoomboxItem boombox && boombox.isPlayingMusic)
                    music = true;
                if (close && item is WalkieTalkie && item.isBeingUsed && item.isHeld)
                    walkie = true;
                if (close && item is WinstonItem)
                    winston = true;

            }
            if (shipScrap >= 0 && scrap > shipScrap && round.shipHasLanded)
            {
                // Много дешёвого — «Ну и хлам тут у вас», раз за день
                if (junk >= ManyJunk && !junkTold)
                {
                    Reason("Junk");
                    junkTold = true;
                }
                Reason(scrap >= ManyItems && shipScrap < ManyItems ? "ManyItems" : "Work");
            }
            if (round.inShipPhase)
                junkTold = false;

            // Доставка улетела нераскрытой — «Прилетела доставка и улетела!»
            var dropship = FindObjectOfType<ItemDropship>();
            if (dropship != null)
            {
                if (dropship.shipLanded)
                    dropshipOpened |= dropship.shipDoorsOpened;
                else if (dropshipLanded && !dropshipOpened)
                    Reason("DeliveryMissed");
                if (!dropship.shipLanded)
                    dropshipOpened = false;
                dropshipLanded = dropship.shipLanded;
            }
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
        }

        private void OnBack(GameNetcodeStuff.PlayerControllerB p, StartOfRound round)
        {
            var folder = NameFolder(p.playerUsername);
            var others = round.allPlayerScripts.Any(o => o != null && o != p && o.isPlayerControlled && !o.isPlayerDead && !o.isInHangarShipRoom);
            if (!firstBackTold && others && folder != null)
                Reason("FirstBack/" + folder);
            firstBackTold = true;
            if (p.health < 50)
                Reason("BackHurt");
            if (p.ItemSlots == null || !p.ItemSlots.Any(i => i != null && i.itemProperties != null && i.itemProperties.isScrap))
                Reason("BackEmpty");
            Reason("Back");
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

            if (Time.time >= turnOnSpeechAt)
                PlayQueued();
            UpdateThinking();
            UpdateOffer();
            UpdateAsking();
            UpdateNavigation();
            UpdateEvents();
            var talking = IsTalking;
            UpdateLevel(talking);
            UpdateRing(talking);
            long mask;
            if (!IsOn)
                mask = 0;
            else if (IsJammed)
                mask = GlitchMask();
            else if (Time.time < volumeShownUntil)
                mask = VolumeMask();
            else if (Time.time < thinkUntil)
                mask = ThinkMask();
            else if (talking && !clockWhileTalking)
                mask = FaceMask();
            else if (Time.time < listenUntil || AwaitingAnswer)
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
                : Time.time < listenUntil || AwaitingAnswer ? ringListenGlow * (0.8f + 0.2f * Mathf.Sin(Time.time * 6f))
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
                        if (CustomTopics.Add(set.name.ToLowerInvariant()))
                            AliceIntents.Load(set.name + ": " + set.name); // своя тема по-русски — сама себе пример
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
