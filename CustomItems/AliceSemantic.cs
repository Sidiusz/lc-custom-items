using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CustomItems
{
    /// <summary>
    /// Понимание вопросов Алисы по смыслу, а не по словам. Маленькая русская модель
    /// (sergeyzh/rubert-mini-frida, 32 млн параметров, GGUF f16, 68 МБ) превращает фразу в вектор смысла;
    /// похожие по смыслу фразы дают близкие векторы, даже если общих слов нет
    /// («сколько у нас бабла» ≈ «сколько денег»).
    ///
    /// Векторы считаются заранее для всех примеров intents.txt и текстов записей (имя файла записи —
    /// её текст), в фоне при загрузке, и кешируются на диск. Вопрос сравнивается с ними косинусом:
    /// сначала примеры (с запасом над второй темой), потом — строже — тексты записей обычных тем.
    ///
    /// Сверху — «болтовня»: фразы не к колонке; вопрос ближе к ним, чем к темам, — не вопрос.
    ///
    /// Модель крутит llama.cpp (нативные llama.dll, ggml*.dll из LLamaSharp.Backend.Cpu) через свой
    /// тонкий P/Invoke: только DllImport и простые структуры, без NativeLibrary и прочего из .NET 5+,
    /// так что работает в Mono игры. Нет библиотек или модели — Алиса понимает как раньше, по словам.
    /// </summary>
    public static partial class AliceSemantic
    {
        // Пороги подобраны на ~230 вопросах и ~70 фразах не к колонке (Tools/AliceSemantic)
        /// <summary>Пример intents.txt засчитывается от этой похожести (косинус, 0–1)…</summary>
        public static float IntentThreshold = 0.86f;
        /// <summary>…если лучшая тема хоть настолько похожее второй (иначе смысл неоднозначен).</summary>
        public static float IntentMargin = 0.03f;
        /// <summary>Чуть ниже порога тоже годится, если другие темы далеко: от этой похожести…</summary>
        public static float LooseThreshold = 0.84f;
        /// <summary>…и с таким отрывом от второй темы.</summary>
        public static float LooseMargin = 0.1f;
        /// <summary>Текст записи засчитывается от этой похожести: ответ на вопрос похож хуже, поэтому строже.</summary>
        public static float PhraseThreshold = 0.88f;
        /// <summary>Совпадение по словам от этой похожести не перепроверяется смыслом.</summary>
        public static float FuzzySure = 0.85f;
        /// <summary>Слабое совпадение по словам смысл перебивает, только если сам уверен хотя бы так.</summary>
        public static float OverrideThreshold = 0.9f;

        /// <summary>
        /// Потоков на один вопрос (и на фоновый подсчёт при загрузке). Вопрос короткий, одного хватает
        /// (~5 мс); больше потоков, чем свободных ядер, — потоки llama.cpp толкаются с игрой и бывает
        /// в сотни раз медленнее.
        /// </summary>
        public static int Threads = 1;

        public static Action<string> Log = _ => { };
        public static Action<string> LogWarning = _ => { };

        /// <summary>Модель загружена и векторы примеров посчитаны: можно спрашивать.</summary>
        public static bool Ready => ready;

        /// <summary>
        /// Темы, которые смысл не выбирает: им нужен разобранный кусок вопроса (луна, предмет, код, имя),
        /// а «да» и «нет» без вопроса — слишком короткие, их хватает по словам.
        /// </summary>
        public static readonly HashSet<string> NoSemanticIntents = new HashSet<string>
        {
            "#route", "#buy", "#code", "#callme", "#yes", "#no", "#confirmoff", "#confirmon",
        };

        /// <summary>
        /// Папки фраз, тексты которых не ответы на вопросы: события, куски для склейки (числа,
        /// навигация), ответы особых тем и служебные звуки. Сверяем только обычные темы.
        /// Путь папки или её начало («Events» — и все подпапки).
        /// </summary>
        public static readonly string[] PhraseDeny =
        {
            "Events", "Numbers", "Nav", "Navigation", "Battery", "Buy", "Call", "CallMe", "Check", "Color", "Count",
            "Deadline0", "Deadline1", "Deadline2", "Deadline3", "Door", "Doors", "Error", "Hand", "Items", "Launch",
            "Lights", "Listen", "Loot", "Mumble", "Unknown", "Names", "MyName", "Roles", "Quota", "QuotaDone",
            "QuotaNotDone", "Repeat", "Route", "Time", "TimeError", "TimeIntro", "Weather", "Scan", "Sum", "Wait",
            "TurnOn", "Off", "Stopping", "Shout", "And", "Agree", "AllClear", "Almost", "Ok", "OutOf", "Moon",
            "Random", "Refuse", "Shush", "WhoDied", "WhereIs", "EnemiesNear", "Idle", "Think", "Game/Cancel",
            "Game/Rend", "Game/Unknown",
        };

        // Тема «болтовня»: не вопрос колонке
        private const string Chatter = "-";

        /// <summary>
        /// Что говорят друг другу, а не колонке (её позвали, но дальше разговор не к ней или шум).
        /// Отсекают ложные совпадения: вопрос должен быть ближе к теме, чем ко всему этому.
        /// </summary>
        public static readonly string[] ChatterExamples =
        {
            "иди сюда", "пошли туда", "я налево, ты направо", "подожди меня", "стой там", "бегом",
            "смотри сюда", "это что за штука", "дай мне это", "возьми вот это", "положи на место",
            "у меня полный инвентарь", "я нашёл вазу", "тут какая-то фигня лежит", "неси сюда", "я на лестнице",
            "у меня микрофон не работает", "пацаны", "ребята, сюда", "ну всё, хана", "блин", "капец", "офигеть",
            "ну ты даёшь", "я сейчас приду", "мне надо отойти", "я покушать", "кот орёт", "завтра на работу",
            "что на ужин", "я хочу спать", "посмотрим кино", "сегодня был тяжёлый день", "после игры созвонимся",
            "у меня садится телефон", "сохрани игру", "зачем ты это сделал", "ты куда побежал", "я тебя не вижу",
            "это моя добыча", "я первый нашёл", "давай разделимся", "встречаемся у входа", "ладно, идём",
            "ахаха", "ну и ну", "вот это да", "бывает", "короче", "ну такое",
        };

        /// <summary>
        /// Приставки задачи для модели: rubert-mini-frida учили с «paraphrase: » для похожих по смыслу фраз
        /// (для e5 было бы «query: »/«passage: »). Вопрос, примеры и тексты записей — все как перефразировки.
        /// </summary>
        public static string QueryPrefix = "paraphrase: ", PassagePrefix = "paraphrase: ";
        // Версия формата кеша и того, как готовим текст: поменялась — кеш считается заново
        private const string CacheVersion = "alice-semantic-2";

        private class Entry
        {
            public string Topic, Text;
            public float[] Vector;
            public bool Slot;
        }

        private static readonly object Gate = new object();
        private static volatile bool ready;
        private static List<Entry> intents = new List<Entry>();
        private static List<Entry> phrases = new List<Entry>();
        private static Embedder embedder;
        private static readonly Dictionary<string, KeyValuePair<string, float>> Answers = new Dictionary<string, KeyValuePair<string, float>>();
        private static readonly Dictionary<string, string> AnswersWhy = new Dictionary<string, string>();

        /// <summary>
        /// Запускает в фоне: загрузка библиотек и модели, векторы примеров (из кеша или заново).
        /// </summary>
        /// <param name="nativeDir">Папка с llama.dll, ggml*.dll (Windows) или libllama.so (тесты на Linux).</param>
        /// <param name="modelPath">Файл модели .gguf.</param>
        /// <param name="cachePath">Файл кеша векторов (null — без кеша).</param>
        /// <param name="intentsText">Тексты intents.txt (встроенный и свой): «Тема: пример, пример».</param>
        /// <param name="phraseTexts">Записи: (папка-тема, текст записи).</param>
        public static void Start(string nativeDir, string modelPath, string cachePath, IEnumerable<string> intentsText,
            IEnumerable<KeyValuePair<string, string>> phraseTexts)
        {
            var intentList = ParseIntents(intentsText);
            var phraseList = ParsePhrases(phraseTexts);
            var thread = new Thread(() => Load(nativeDir, modelPath, cachePath, intentList, phraseList))
            {
                IsBackground = true,
                Name = "AliceSemantic",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
        }

        /// <summary>Загрузка в этом потоке (для тестов). Возвращает, получилось ли.</summary>
        public static bool LoadNow(string nativeDir, string modelPath, string cachePath, IEnumerable<string> intentsText,
            IEnumerable<KeyValuePair<string, string>> phraseTexts)
        {
            var intentList = ParseIntents(intentsText);
            var phraseList = ParsePhrases(phraseTexts);
            Load(nativeDir, modelPath, cachePath, intentList, phraseList);
            return ready;
        }

        private static void Load(string nativeDir, string modelPath, string cachePath, List<Entry> intentList, List<Entry> phraseList)
        {
            var started = DateTime.UtcNow;
            try
            {
                if (!File.Exists(modelPath))
                {
                    LogWarning($"Алиса: нет модели смысла {modelPath}, понимаю только по словам");
                    return;
                }
                var e = Embedder.Open(nativeDir, modelPath, Math.Max(1, Threads));
                var modelKey = CacheVersion + "|" + QueryPrefix + "|" + PassagePrefix + "|" + Path.GetFileName(modelPath) + "|" + new FileInfo(modelPath).Length;
                var cache = ReadCache(cachePath, modelKey);
                var fresh = 0;
                foreach (var entry in intentList.Concat(phraseList))
                {
                    if (!cache.TryGetValue(entry.Text, out var v))
                    {
                        lock (Gate)
                            v = e.Embed(entry.Text);
                        if (v == null)
                            continue;
                        cache[entry.Text] = v;
                        fresh++;
                    }
                    entry.Vector = v;
                }
                if (fresh > 0)
                {
                    // В кеш только нужное сейчас: старые примеры не копятся
                    var used = new HashSet<string>(intentList.Concat(phraseList).Select(x => x.Text));
                    WriteCache(cachePath, modelKey, cache.Where(p => used.Contains(p.Key)));
                }
                lock (Gate)
                {
                    embedder = e;
                    e.Threads = Threads;
                    // Прогрев с потоками для вопросов: первый настоящий вопрос не ждёт их запуска
                    e.Embed(QueryPrefix + "привет");
                    intents = intentList.Where(x => x.Vector != null).ToList();
                    phrases = phraseList.Where(x => x.Vector != null).ToList();
                    ready = true;
                }
                Log($"Алиса: понимаю по смыслу, примеров {intents.Count}, записей {phrases.Count}, " +
                    $"посчитано заново {fresh}, за {(DateTime.UtcNow - started).TotalSeconds:0.0} с");
            }
            catch (Exception ex)
            {
                LogWarning("Алиса: не загрузилась модель смысла, понимаю только по словам. " + ex.Message);
            }
        }

        /// <summary>
        /// Тема вопроса по смыслу: лучший пример intents.txt, иначе лучший текст записи (строже).
        /// null — не уверена или модель ещё не готова (тогда не ждёт).
        /// </summary>
        public static string Match(string command, out float score, out string why)
        {
            var topic = MatchRaw(command, out score, out why);
            return topic == Chatter ? null : topic;
        }

        // Как Match, но болтовня — отдельной темой Chatter
        private static string MatchRaw(string command, out float score, out string why)
        {
            score = 0f;
            why = null;
            if (!ready)
            {
                why = "смысл: модель ещё грузится";
                return null;
            }
            var q = Question(command);
            if (q.Length == 0)
                return null;
            // Тот же вопрос второй раз — из памяти
            lock (Answers)
            {
                if (Answers.TryGetValue(q, out var known))
                {
                    score = known.Value;
                    why = AnswersWhy[q];
                    return known.Key;
                }
            }
            // Подсчёт занят (вдруг ещё досчитывается кеш) — не ждём, отвечаем по словам
            if (!Monitor.TryEnter(Gate))
            {
                why = "смысл: модель занята";
                return null;
            }
            float[] v;
            try
            {
                v = embedder.Embed(QueryPrefix + q);
            }
            finally
            {
                Monitor.Exit(Gate);
            }
            var topic = Decide(v, out score, out why);
            lock (Answers)
            {
                if (Answers.Count > 500)
                {
                    Answers.Clear();
                    AnswersWhy.Clear();
                }
                Answers[q] = new KeyValuePair<string, float>(topic, score);
                AnswersWhy[q] = why;
            }
            return topic;
        }

        /// <summary>Для подбора порогов: лучшая и вторая тема примеров, лучшая запись (без порогов).</summary>
        public static string Explain(string command, out float best, out string secondTopic, out float second,
            out string phraseTopic, out float phrase)
        {
            best = second = phrase = 0f;
            secondTopic = phraseTopic = null;
            if (!ready)
                return null;
            float[] v;
            lock (Gate)
                v = embedder.Embed(QueryPrefix + Question(command));
            var b = Best(intents, v);
            var p = Best(phrases, v).FirstOrDefault();
            if (b.Count > 1)
            {
                secondTopic = b[1].Item1.Topic;
                second = b[1].Item2;
            }
            if (p != null)
            {
                phraseTopic = p.Item1.Topic;
                phrase = p.Item2;
            }
            if (b.Count == 0)
                return null;
            best = b[0].Item2;
            return b[0].Item1.Topic;
        }

        private static string Decide(float[] v, out float score, out string why)
        {
            score = 0f;
            if (v == null)
            {
                why = "смысл: не посчитался";
                return null;
            }
            // Лучший пример каждой темы
            var best = Best(intents, v);
            var first = best.Count > 0 ? best[0] : null;
            var second = best.Count > 1 ? best[1] : null;
            if (first != null)
            {
                score = first.Item2;
                var gap = first.Item2 - (second?.Item2 ?? 0f);
                why = $"смысл: пример «{Strip(first.Item1.Text)}» {first.Item2:0.000}" +
                      (second != null ? $", вторая тема {second.Item1.Topic} {second.Item2:0.000}" : "");
                // Больше похоже на разговор между собой, чем на вопрос: не отвечаем вовсе
                if (first.Item1.Topic == Chatter)
                {
                    why = $"смысл: похоже на разговор не со мной «{Strip(first.Item1.Text)}» {first.Item2:0.000}";
                    return Chatter;
                }
                if (first.Item2 >= IntentThreshold && gap >= IntentMargin || first.Item2 >= LooseThreshold && gap >= LooseMargin)
                    return first.Item1.Topic;
            }
            else
                why = "смысл: нет примеров";
            var p = Best(phrases, v).FirstOrDefault();
            if (p != null)
            {
                why += $"; запись {p.Item1.Topic} «{Strip(p.Item1.Text)}» {p.Item2:0.000}";
                if (p.Item2 >= PhraseThreshold)
                {
                    score = p.Item2;
                    return p.Item1.Topic;
                }
            }
            return null;
        }

        // Темы по убыванию похожести их лучшего примера
        private static List<Tuple<Entry, float>> Best(List<Entry> entries, float[] v)
        {
            var byTopic = new Dictionary<string, Tuple<Entry, float>>();
            foreach (var e in entries)
            {
                var s = Dot(e.Vector, v);
                if (!byTopic.TryGetValue(e.Topic, out var b) || s > b.Item2)
                    byTopic[e.Topic] = Tuple.Create(e, s);
            }
            return byTopic.Values.OrderByDescending(t => t.Item2).ToList();
        }

        /// <summary>
        /// Что ответить, зная совпадение по словам: уверенное по словам остаётся, слабое смысл может
        /// перебить (если уверен сам), не найденное по словам — ищется по смыслу.
        /// </summary>
        /// <returns>Тема (как в intents.txt) или null.</returns>
        public static string Combine(string command, string fuzzyTopic, float fuzzyScore, ref string why)
        {
            if (fuzzyTopic != null && fuzzyScore >= FuzzySure)
                return fuzzyTopic;
            var topic = MatchRaw(command, out var score, out var semWhy);
            // Уверенно болтовня, а по словам похоже слабо («я пошёл налево» ≈ «я пошёл») — не отвечаем
            if (topic == Chatter)
            {
                why += "; " + semWhy;
                return score >= IntentThreshold ? null : fuzzyTopic;
            }
            if (topic == null)
            {
                if (semWhy != null)
                    why += "; " + semWhy;
                return fuzzyTopic;
            }
            if (fuzzyTopic == null)
            {
                why += "; " + semWhy + " → " + topic;
                return topic;
            }
            if (topic != fuzzyTopic && score >= OverrideThreshold)
            {
                why += "; " + semWhy + " → " + topic + " вместо " + fuzzyTopic;
                return topic;
            }
            why += "; " + semWhy;
            return fuzzyTopic;
        }

        // ---- Тексты ----

        /// <summary>Примеры из intents.txt: как в AliceIntents, но без лун, магазина и ответов после вопроса.</summary>
        private static List<Entry> ParseIntents(IEnumerable<string> texts)
        {
            var list = new List<Entry>();
            foreach (var text in texts ?? Enumerable.Empty<string>())
            {
                foreach (var raw in (text ?? "").Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("# ") || line.StartsWith("#") && !line.Contains(":"))
                        continue;
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    var topic = line.Substring(0, colon).Trim();
                    if (topic.StartsWith("$") || topic.StartsWith("@") || topic.Contains(">") || NoSemanticIntents.Contains(topic))
                        continue;
                    foreach (var phrase in line.Substring(colon + 1).Split(','))
                    {
                        // Слоты — живым словом: модель понимает «где андрей», а не «где {name}»
                        var p = phrase.Replace("{name}", SlotName).Replace("{moon}", SlotMoon).Replace("{item}", "фонарик")
                            .Replace("{number}", "два");
                        var clean = CleanQuestion(p);
                        if (clean.Length > 0 && !p.Contains("{"))
                            list.Add(new Entry { Topic = topic, Text = QueryPrefix + clean, Slot = p != phrase });
                    }
                }
            }
            // Болтовня между собой: если вопрос ближе к ней, чем к любой теме, — это не вопрос
            foreach (var c in ChatterExamples)
                list.Add(new Entry { Topic = Chatter, Text = QueryPrefix + CleanQuestion(c) });
            // Тема только с примерами со слотом («передай привет {name}») без слота не имеет смысла: её не берём
            var slotOnly = new HashSet<string>(list.GroupBy(e => e.Topic).Where(g => g.All(e => e.Slot)).Select(g => g.Key));
            return list.Where(e => !slotOnly.Contains(e.Topic))
                .GroupBy(e => e.Topic + "\n" + e.Text).Select(g => g.First()).ToList();
        }

        private static List<Entry> ParsePhrases(IEnumerable<KeyValuePair<string, string>> phraseTexts) =>
            (phraseTexts ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .Where(p => PhraseAllowed(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
                .Select(p => new Entry { Topic = p.Key, Text = PassagePrefix + CleanPhrase(p.Value) })
                .GroupBy(e => e.Topic + "\n" + e.Text).Select(g => g.First()).ToList();

        /// <summary>Записи этой папки сверяем с вопросом (обычная тема, не событие и не кусок).</summary>
        public static bool PhraseAllowed(string topic)
        {
            if (string.IsNullOrEmpty(topic) || topic.StartsWith("#"))
                return false;
            foreach (var d in PhraseDeny)
            {
                if (topic.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                    topic.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            // Подпапки (Joke/Laugh, HowAreYou/Good) — реакции на ответ, а не ответы на вопрос
            return !topic.Contains("/") || topic == "Game/Ask" || topic == "Game/Bored";
        }

        // {name} и {moon} в примерах — этими словами; в вопросе любое имя друга и луна заменяются на них же:
        // «где пашка» сравнивается как «где андрей», с примером «где {name}»
        private const string SlotName = "андрей", SlotMoon = "титан";

        /// <summary>Имя друга в любом падеже (ставит плагин: AliceIntents.NameIn).</summary>
        public static Func<string, bool> IsName = _ => false;
        /// <summary>Название луны (ставит плагин: AliceIntents.MoonIn).</summary>
        public static Func<string, bool> IsMoon = _ => false;

        /// <summary>Вопрос для сравнения: как <see cref="CleanQuestion"/>, имена и луны — словами из примеров.</summary>
        public static string Question(string command) =>
            string.Join(" ", CleanQuestion(command).Split(' ').Select(w => IsName(w) ? SlotName : IsMoon(w) ? SlotMoon : w));

        /// <summary>Вопрос как его сравнивать: строчными, без знаков и без обращения «Алиса».</summary>
        public static string CleanQuestion(string text)
        {
            var sb = new StringBuilder();
            foreach (var c in (text ?? "").ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '{' || c == '}' ? c : ' ');
            var words = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !w.StartsWith("алис") && w != "unk" && w != "-");
            return string.Join(" ", words);
        }

        // Имя записи — её текст; бывает обрезан или с номером в конце («… (2)»)
        private static string CleanPhrase(string text)
        {
            var t = text.Trim();
            var paren = t.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0 && t.EndsWith(")"))
                t = t.Substring(0, paren);
            return t.ToLowerInvariant();
        }

        private static string Strip(string text) =>
            QueryPrefix.Length > 0 && text.StartsWith(QueryPrefix) ? text.Substring(QueryPrefix.Length)
            : PassagePrefix.Length > 0 && text.StartsWith(PassagePrefix) ? text.Substring(PassagePrefix.Length) : text;

        private static float Dot(float[] a, float[] b)
        {
            var n = Math.Min(a.Length, b.Length);
            float s = 0f;
            for (var i = 0; i < n; i++)
                s += a[i] * b[i];
            return s;
        }

        // ---- Кеш векторов: «текст → вектор», модель в заголовке ----

        private static Dictionary<string, float[]> ReadCache(string path, string modelKey)
        {
            var cache = new Dictionary<string, float[]>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return cache;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
                {
                    if (r.ReadString() != modelKey)
                        return cache;
                    var count = r.ReadInt32();
                    var dim = r.ReadInt32();
                    for (var i = 0; i < count; i++)
                    {
                        var text = r.ReadString();
                        var v = new float[dim];
                        for (var j = 0; j < dim; j++)
                            v[j] = r.ReadSingle();
                        cache[text] = v;
                    }
                }
            }
            catch (Exception e)
            {
                LogWarning("Алиса: кеш смысла испорчен, считаю заново: " + e.Message);
                cache.Clear();
            }
            return cache;
        }

        private static void WriteCache(string path, string modelKey, IEnumerable<KeyValuePair<string, float[]>> entries)
        {
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                var list = entries.ToList();
                var tmp = path + ".tmp";
                using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
                {
                    w.Write(modelKey);
                    w.Write(list.Count);
                    w.Write(list.Count > 0 ? list[0].Value.Length : 0);
                    foreach (var p in list)
                    {
                        w.Write(p.Key);
                        foreach (var x in p.Value)
                            w.Write(x);
                    }
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                LogWarning("Алиса: не записала кеш смысла: " + e.Message);
            }
        }

        // ---- llama.cpp ----

        /// <summary>
        /// Вектор фразы через llama.cpp: токены → энкодер → усреднение (как задано в модели) → длина 1.
        /// Не потокобезопасен: снаружи под замком.
        /// </summary>
        private sealed class Embedder
        {
            private IntPtr model, ctx, vocab;
            private int dim, maxTokens;
            private Native.Batch batch;
            private bool encoderOnly;
            private WordPiece wordPiece;
            public int Threads;
            private int threadsSet = -1;

            public static Embedder Open(string nativeDir, string modelPath, int threads)
            {
                Native.Preload(nativeDir);
                Native.llama_log_set(Native.Quiet, IntPtr.Zero);
                Native.llama_backend_init();
                var mp = Native.llama_model_default_params();
                mp.n_gpu_layers = 0;
                var e = new Embedder { Threads = threads };
                e.model = Native.llama_model_load_from_file(modelPath, mp);
                if (e.model == IntPtr.Zero)
                    throw new Exception("llama.cpp не открыл " + modelPath);
                var cp = Native.llama_context_default_params();
                cp.n_ctx = 512;
                cp.n_batch = 512;
                cp.n_ubatch = 512;
                cp.n_seq_max = 1;
                cp.n_threads = threads;
                cp.n_threads_batch = threads;
                cp.embeddings = 1;
                cp.pooling_type = -1; // как в модели (у e5 — среднее)
                e.ctx = Native.llama_init_from_model(e.model, cp);
                if (e.ctx == IntPtr.Zero)
                    throw new Exception("llama.cpp не создал контекст");
                e.vocab = Native.llama_model_get_vocab(e.model);
                e.dim = Native.llama_model_n_embd(e.model);
                e.maxTokens = 512;
                e.batch = Native.llama_batch_init(e.maxTokens, 0, 1);
                e.encoderOnly = Native.llama_model_has_encoder(e.model) && !Native.llama_model_has_decoder(e.model);
                // WordPiece в llama.cpp убирает у букв надстрочные знаки (й → и, ё → е), а русские модели
                // учились с ними: режем на токены сами, словарь берём из модели
                if (Native.llama_vocab_type(e.vocab) == 3)
                    e.wordPiece = new WordPiece(e.vocab);
                return e;
            }

            public float[] Embed(string text)
            {
                if (threadsSet != Threads)
                {
                    Native.llama_set_n_threads(ctx, Threads, Threads);
                    threadsSet = Threads;
                }
                var tokens = new int[maxTokens];
                int n;
                if (wordPiece != null)
                    n = wordPiece.Tokenize(text, tokens);
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    n = Native.llama_tokenize(vocab, bytes, bytes.Length, tokens, tokens.Length, true, false);
                    if (n < 0)
                        n = tokens.Length; // длиннее окна — берём начало
                }
                if (n == 0)
                    return null;
                batch.n_tokens = n;
                for (var i = 0; i < n; i++)
                {
                    Marshal.WriteInt32(batch.token, i * 4, tokens[i]);
                    Marshal.WriteInt32(batch.pos, i * 4, i);
                    Marshal.WriteInt32(batch.n_seq_id, i * 4, 1);
                    Marshal.WriteInt32(Marshal.ReadIntPtr(batch.seq_id, i * IntPtr.Size), 0, 0);
                    Marshal.WriteByte(batch.logits, i, 1);
                }
                var mem = Native.llama_get_memory(ctx);
                if (mem != IntPtr.Zero)
                    Native.llama_memory_clear(mem, true);
                var rc = encoderOnly ? Native.llama_encode(ctx, batch) : Native.llama_decode(ctx, batch);
                if (rc != 0)
                    return null;
                var ptr = Native.llama_get_embeddings_seq(ctx, 0);
                if (ptr == IntPtr.Zero)
                    return null;
                var v = new float[dim];
                Marshal.Copy(ptr, v, 0, dim);
                double norm = 0;
                foreach (var x in v)
                    norm += x * x;
                var k = norm > 0 ? (float)(1.0 / Math.Sqrt(norm)) : 0f;
                for (var i = 0; i < dim; i++)
                    v[i] *= k;
                return v;
            }
        }

        /// <summary>
        /// Токены BERT (WordPiece) как в transformers без перевода в строчные: слова и знаки препинания
        /// отдельно, слово — самыми длинными кусками словаря слева направо («##» — продолжение слова).
        /// </summary>
        private sealed class WordPiece
        {
            private readonly Dictionary<string, int> starts = new Dictionary<string, int>();
            private readonly Dictionary<string, int> parts = new Dictionary<string, int>();
            private readonly int cls, sep, unk;
            private int maxLength;

            public WordPiece(IntPtr vocab)
            {
                var count = Native.llama_vocab_n_tokens(vocab);
                unk = -1;
                for (var i = 0; i < count; i++)
                {
                    // llama.cpp хранит начало слова как «▁слово», продолжение — без «##»
                    var text = Native.TokenText(vocab, i);
                    if (text == null)
                        continue;
                    if (text == "[UNK]")
                        unk = i;
                    if (text.Length > 1 && text[0] == '\u2581')
                        starts[text.Substring(1)] = i;
                    else if (!(text.StartsWith("[") && text.EndsWith("]")))
                        parts[text] = i;
                    maxLength = Math.Max(maxLength, text.Length);
                }
                cls = Native.llama_vocab_cls(vocab);
                if (cls < 0)
                    cls = Native.llama_vocab_bos(vocab);
                sep = Native.llama_vocab_sep(vocab);
                if (sep < 0)
                    sep = Native.llama_vocab_eos(vocab);
            }

            public int Tokenize(string text, int[] tokens)
            {
                var n = 0;
                tokens[n++] = cls;
                foreach (var word in Words(text))
                {
                    if (n >= tokens.Length - 1)
                        break;
                    var pieces = new List<int>();
                    var start = 0;
                    while (start < word.Length)
                    {
                        var found = -1;
                        var end = Math.Min(word.Length, start + maxLength);
                        for (; end > start; end--)
                        {
                            var piece = word.Substring(start, end - start);
                            if ((start == 0 ? starts : parts).TryGetValue(piece, out found))
                                break;
                            found = -1;
                        }
                        if (found < 0)
                        {
                            pieces.Clear();
                            pieces.Add(unk);
                            break;
                        }
                        pieces.Add(found);
                        start = end;
                    }
                    foreach (var p in pieces)
                    {
                        if (n < tokens.Length - 1 && p >= 0)
                            tokens[n++] = p;
                    }
                }
                tokens[n++] = sep;
                return n;
            }

            private static IEnumerable<string> Words(string text)
            {
                var sb = new StringBuilder();
                foreach (var c in text)
                {
                    if (char.IsWhiteSpace(c) || char.IsControl(c))
                    {
                        if (sb.Length > 0)
                            yield return sb.ToString();
                        sb.Length = 0;
                    }
                    else if (char.IsPunctuation(c) || char.IsSymbol(c) && c < 128)
                    {
                        if (sb.Length > 0)
                            yield return sb.ToString();
                        sb.Length = 0;
                        yield return c.ToString();
                    }
                    else
                        sb.Append(c);
                }
                if (sb.Length > 0)
                    yield return sb.ToString();
            }
        }

        /// <summary>
        /// Свой P/Invoke к llama.cpp той версии, что в LLamaSharp.Backend.Cpu 0.27.0 (раскладка структур
        /// взята оттуда же). Только DllImport, IntPtr и простые структуры — работает в Mono Unity.
        /// </summary>
        private static class Native
        {
            private const string Lib = "llama";

            [StructLayout(LayoutKind.Sequential)]
            public struct ModelParams
            {
                public IntPtr devices, tensor_buft_overrides;
                public int n_gpu_layers, split_mode, main_gpu;
                public IntPtr tensor_split, progress_callback, progress_callback_user_data, kv_overrides;
                public byte vocab_only, use_mmap, use_direct_io, use_mlock, check_tensors, use_extra_bufts, no_host, no_alloc;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct ContextParams
            {
                public uint n_ctx, n_batch, n_ubatch, n_seq_max;
                public int n_threads, n_threads_batch;
                public int rope_scaling_type, pooling_type, attention_type, flash_attn_type;
                public float rope_freq_base, rope_freq_scale, yarn_ext_factor, yarn_attn_factor, yarn_beta_fast, yarn_beta_slow;
                public uint yarn_orig_ctx;
                public float defrag_threshold;
                public IntPtr cb_eval, cb_eval_user_data;
                public int type_k, type_v;
                public IntPtr abort_callback, abort_callback_user_data;
                public byte embeddings, offload_kqv, no_perf, op_offload, swa_full, kv_unified;
                public IntPtr samplers;
                public UIntPtr n_samplers;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Batch
            {
                public int n_tokens;
                public IntPtr token, embd, pos, n_seq_id, seq_id, logits;
            }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            public delegate void LogCallback(int level, IntPtr text, IntPtr userData);

            // Держим ссылку, иначе сборщик мусора уберёт делегат, а llama.cpp будет его звать
            public static readonly LogCallback Quiet = (level, text, data) => { };

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void llama_log_set(LogCallback callback, IntPtr userData);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void llama_backend_init();
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern ModelParams llama_model_default_params();
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern ContextParams llama_context_default_params();
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr llama_model_load_from_file(byte[] path, ModelParams p);
            public static IntPtr llama_model_load_from_file(string path, ModelParams p) =>
                // Путь в UTF-8 с нулём на конце: в профиле могут быть русские буквы
                llama_model_load_from_file(Encoding.UTF8.GetBytes(path + "\0"), p);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr llama_init_from_model(IntPtr model, ContextParams p);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr llama_model_get_vocab(IntPtr model);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int llama_model_n_embd(IntPtr model);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.U1)]
            public static extern bool llama_model_has_encoder(IntPtr model);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.U1)]
            public static extern bool llama_model_has_decoder(IntPtr model);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int llama_tokenize(IntPtr vocab, byte[] text, int textLen, [Out] int[] tokens, int maxTokens,
                [MarshalAs(UnmanagedType.U1)] bool addSpecial, [MarshalAs(UnmanagedType.U1)] bool parseSpecial);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern Batch llama_batch_init(int nTokens, int embd, int nSeqMax);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr llama_get_memory(IntPtr ctx);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void llama_memory_clear(IntPtr mem, [MarshalAs(UnmanagedType.U1)] bool data);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int llama_encode(IntPtr ctx, Batch batch);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int llama_decode(IntPtr ctx, Batch batch);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr llama_get_embeddings_seq(IntPtr ctx, int seqId);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int llama_vocab_type(IntPtr vocab);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int llama_vocab_n_tokens(IntPtr vocab);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr llama_vocab_get_text(IntPtr vocab, int token);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int llama_vocab_bos(IntPtr vocab);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int llama_vocab_eos(IntPtr vocab);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int llama_vocab_sep(IntPtr vocab);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int llama_vocab_cls(IntPtr vocab);

            /// <summary>Текст токена из словаря модели (UTF-8).</summary>
            public static string TokenText(IntPtr vocab, int token)
            {
                var p = llama_vocab_get_text(vocab, token);
                if (p == IntPtr.Zero)
                    return null;
                var len = 0;
                while (Marshal.ReadByte(p, len) != 0)
                    len++;
                var bytes = new byte[len];
                Marshal.Copy(p, bytes, 0, len);
                return Encoding.UTF8.GetString(bytes);
            }
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern void llama_set_n_threads(IntPtr ctx, int threads, int threadsBatch);

            // Mono ищет DLL рядом с игрой, а не рядом с модом: грузим их заранее по полному пути
            // (зависимости раньше), дальше DllImport("llama") находит уже загруженную
            public static void Preload(string dir)
            {
                // Потоки OpenMP в ggml не крутятся вхолостую между вопросами, а спят: процессор нужен игре
                if (Environment.GetEnvironmentVariable("OMP_WAIT_POLICY") == null)
                    Environment.SetEnvironmentVariable("OMP_WAIT_POLICY", "PASSIVE");
                if (Environment.OSVersion.Platform != PlatformID.Win32NT || string.IsNullOrEmpty(dir))
                    return;
                // Сборка под набор команд процессора: AVX2 (почти все с 2013 года), иначе AVX. Без AVX не работаем:
                // такая llama.dll упала бы с недопустимой инструкцией вместе с игрой
                var cpu = IsProcessorFeaturePresent(AvxTwo) ? "avx2" : IsProcessorFeaturePresent(Avx) ? "avx" : null;
                if (cpu == null)
                    throw new PlatformNotSupportedException("процессор без AVX");
                foreach (var name in new[] { "ggml-base.dll", "ggml-cpu.dll", "ggml.dll", "llama.dll" })
                {
                    var path = Path.Combine(dir, cpu, name);
                    if (LoadLibraryW(path) == IntPtr.Zero)
                        throw new FileNotFoundException($"Не загрузилась {path} (ошибка {Marshal.GetLastWin32Error()}; " +
                            "нужен Microsoft Visual C++ Redistributable 2015–2022 x64)");
                }
            }

            [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibraryW(string path);

            // PF_AVX_INSTRUCTIONS_AVAILABLE, PF_AVX2_INSTRUCTIONS_AVAILABLE. Старая Windows про AVX2 может не знать
            // и ответить «нет» — тогда просто берём сборку с AVX
            private const int Avx = 39, AvxTwo = 40;

            [DllImport("kernel32")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool IsProcessorFeaturePresent(int feature);
        }
    }

#if !ALICE_SEMANTIC_HARNESS
    // Подключение к игре: модель рядом с DLL мода, тексты — intents.txt и имена записей Станции
    public static partial class AliceSemantic
    {
        /// <summary>Папка рядом с DLL мода: модель и llama.dll, ggml*.dll в подпапках avx2/ и avx/.</summary>
        public const string Folder = "alice-semantic";
        public const string ModelFile = "rubert-mini-frida-f16.gguf";

        public static void Install(string pluginDir, BepInEx.Configuration.ConfigFile config)
        {
            const string section = "Yandex Station";
            var enabled = config.Bind(section, "UnderstandMeaning", true,
                "Понимать вопросы по смыслу, а не только по словам («сколько у нас бабла» = «сколько денег»). " +
                "Маленькая нейросеть на процессоре, ~5 мс на вопрос, ~250 МБ памяти. false — только по словам, как раньше").Value;
            IntentThreshold = config.Bind(section, "MeaningThreshold", IntentThreshold,
                "Насколько вопрос должен быть похож по смыслу на пример из intents.txt (0..1). Больше — реже ошибается, " +
                "но чаще «не поняла»").Value;
            Threads = Math.Max(1, config.Bind(section, "MeaningThreads", Threads,
                "Потоков процессора на понимание вопроса по смыслу").Value);
            if (!enabled)
                return;
            Log = s => Plugin.Log.LogInfo(s);
            LogWarning = s => Plugin.Log.LogWarning(s);

            var dir = Path.Combine(pluginDir, Folder);
            var texts = new List<string>();
            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream("SidiuszCustomItems.intents.txt"))
            {
                if (stream != null)
                    using (var reader = new StreamReader(stream))
                        texts.Add(reader.ReadToEnd());
            }
            var own = Path.Combine(BepInEx.Paths.ConfigPath, "SidiuszCustomItems.intents.txt");
            try
            {
                if (File.Exists(own))
                    texts.Add(File.ReadAllText(own));
            }
            catch (Exception e)
            {
                LogWarning("Алиса: не прочитала " + own + ": " + e.Message);
            }
            // Записи Станции (префаб уже загружен из бандла): папка — тема, имя файла — текст
            var phrases = new List<KeyValuePair<string, string>>();
            foreach (var station in UnityEngine.Resources.FindObjectsOfTypeAll<Items.YandexStationItem>().Take(1))
            {
                if (station.phraseSets == null)
                    continue;
                foreach (var set in station.phraseSets)
                {
                    if (set?.clips == null)
                        continue;
                    foreach (var clip in set.clips)
                    {
                        if (clip != null)
                            phrases.Add(new KeyValuePair<string, string>(set.name, clip.name));
                    }
                }
            }
            var cache = Path.Combine(BepInEx.Paths.CachePath, "SidiuszCustomItems.alice-semantic.bin");
            IsName = w => Items.AliceIntents.NameIn(w) != null;
            IsMoon = w => Items.AliceIntents.MoonIn(w) != null;
            Items.YandexStationItem.SemanticMatch = Combine;
            Start(dir, Path.Combine(dir, ModelFile), cache, texts, phrases);
        }
    }
#endif
}
