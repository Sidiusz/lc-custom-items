using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CustomItems.Items
{
    /// <summary>
    /// Понимание вопросов Алисы без списков в коде. Темы и примеры вопросов лежат в тексте
    /// (intents.txt в моде и свой файл в конфиге): «Тема: фраза, фраза, …». Вопрос сравнивается
    /// с каждым примером по основам слов (русский стеммер Snowball) с нечётким совпадением,
    /// редкие слова весят больше частых (IDF). Побеждает тема лучшего примера, если он похож
    /// достаточно. {name} в примере — любой друг из таблицы имён в любой форме.
    /// </summary>
    public static class AliceIntents
    {
        /// <summary>Ниже этой похожести вопрос не понят.</summary>
        public static float Threshold = 0.7f;

        public const string NameSlot = "{name}";
        /// <summary>Любая луна из строк «@Луна: …»: «поставь {moon}» = «поставь Ашуру».</summary>
        public const string MoonSlot = "{moon}";
        /// <summary>Предмет магазина из строк «$Flashlight: фонарик» (после «купи», «закажи»).</summary>
        public const string ItemSlot = "{item}";
        /// <summary>Число словами или цифрами («два», «пару», «3») после «купи», «закажи».</summary>
        public const string NumberSlot = "{number}";

        // Предмет: основы слов его имени подряд («про фонарик» → про, фонарик) → как в магазине
        private static readonly List<KeyValuePair<string[], string>> ItemNames = new List<KeyValuePair<string[], string>>();
        private static readonly HashSet<string> ItemStems = new HashSet<string>();

        private static readonly Dictionary<string, int> NumberWords = new Dictionary<string, int>
        {
            ["один"] = 1, ["одну"] = 1, ["одна"] = 1, ["одного"] = 1, ["два"] = 2, ["две"] = 2,
            ["пару"] = 2, ["пара"] = 2, ["три"] = 3, ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7,
            ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10, ["одиннадцать"] = 11, ["двенадцать"] = 12,
            // «в количестве трёх», «трёх штук», «парочку», «десяток»
            ["одной"] = 1, ["двух"] = 2, ["двое"] = 2, ["пары"] = 2, ["парочку"] = 2, ["парочка"] = 2, ["трех"] = 3,
            ["трое"] = 3, ["тройку"] = 3, ["четырех"] = 4, ["четверо"] = 4, ["пяти"] = 5, ["пяток"] = 5,
            ["шести"] = 6, ["полдюжины"] = 6, ["семи"] = 7, ["восьми"] = 8, ["девяти"] = 9, ["десяти"] = 10,
            ["десяток"] = 10, ["одиннадцати"] = 11, ["двенадцати"] = 12, ["дюжину"] = 12, ["дюжина"] = 12,
        };

        // «про-фонарики» и «3-х» — два слова
        private static IEnumerable<string> Pieces(string text) =>
            Tokens(text).SelectMany(t => t.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries));

        // «3», «3шт», «3х» → 3
        private static int? Digits(string token)
        {
            var n = 0;
            var i = 0;
            for (; i < token.Length && char.IsDigit(token[i]) && n < 1000; i++)
                n = n * 10 + (token[i] - '0');
            return i > 0 && n > 0 && (i == token.Length || token.Substring(i) is "шт" or "х" or "штук" or "штуки") ? n : (int?)null;
        }

        private static HashSet<string> buyVerbs;
        private static HashSet<string> BuyVerbs => buyVerbs ?? (buyVerbs = new HashSet<string>(
            new[] { "купи", "купим", "купить", "закажи", "закажем", "заказать", "доставь", "прикупи" }.Select(Stem)));

        /// <summary>Сколько штук назвали («два», «пару», «3»), или null.</summary>
        public static int? NumberIn(string text)
        {
            // «штуку» — одна, только если другого числа нет: «три штуки» — три
            int? piece = null;
            foreach (var t in Pieces(text))
            {
                if (t == "штуку" || t == "штучку")
                    piece = 1;
                else if (NumberWords.TryGetValue(t, out var n))
                    return n;
                else if (Digits(t) is { } d)
                    return d;
            }
            return piece;
        }

        /// <summary>
        /// Предмет магазина, названный в тексте, как в строке «$Pro-flashlight: про фонарик»
        /// (самое длинное совпавшее имя: «про фонарик» — не просто «фонарик»), или null.
        /// </summary>
        public static string ItemIn(string text)
        {
            var stems = Pieces(text).Select(Stem).ToArray();
            string best = null;
            var bestLength = 0;
            foreach (var pair in ItemNames)
            {
                var name = pair.Key;
                if (name.Length <= bestLength)
                    continue;
                for (var i = 0; i + name.Length <= stems.Length; i++)
                {
                    var all = true;
                    for (var j = 0; j < name.Length && all; j++)
                        all = Similarity(name[j], stems[i + j]) >= 0.75f;
                    if (all)
                    {
                        best = pair.Value;
                        bestLength = name.Length;
                        break;
                    }
                }
            }
            return best;
        }

        private class Example
        {
            public string Topic, Text;
            public string[] Stems;
        }

        private static readonly List<Example> Examples = new List<Example>();
        // Ответы на её встречный вопрос: тема фразы, после которой ждём ответ → примеры ответов
        private static readonly Dictionary<string, List<Example>> FollowUps = new Dictionary<string, List<Example>>();

        /// <summary>Ждёт ли колонка ответа после фразы из этой темы («а у вас как?»).</summary>
        public static bool HasFollowUps(string topic) => topic != null && FollowUps.ContainsKey(topic);

        /// <summary>Тема ответа на реплику после фразы из <paramref name="after"/>, или null.</summary>
        public static string MatchFollowUp(string after, string reply)
        {
            if (after == null || !FollowUps.TryGetValue(after, out var examples))
                return null;
            var stems = Tokens(reply).Where(t => !t.StartsWith("алис")).Select(Stem).ToArray();
            if (stems.Length == 0)
                return null;
            Example best = null;
            var score = 0f;
            foreach (var e in examples)
            {
                var sc = Score(e.Stems, stems);
                if (sc > score + 1e-4f)
                {
                    score = sc;
                    best = e;
                }
            }
            return best != null && score >= Threshold ? best.Topic : null;
        }
        private static readonly Dictionary<string, float> Idf = new Dictionary<string, float>();
        private static readonly HashSet<string> Words = new HashSet<string>();
        // Форма или основа имени → папка друга
        private static readonly Dictionary<string, string> NameForms = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> NameStems = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> MoonStems = new Dictionary<string, string>();

        /// <summary>Луна (как в терминале: Titan), если слово — её русское имя из строк «@Луна: …».</summary>
        public static string MoonIn(string token) => MoonStems.TryGetValue(Stem(token), out var moon) ? moon : null;

        /// <summary>Есть ли в тексте русское имя луны.</summary>
        public static bool NamesMoon(string text) => MoonInText(text) != null;

        // Основы слов, после которых называют луну
        private static HashSet<string> routeVerbs;
        private static HashSet<string> RouteVerbs => routeVerbs ?? (routeVerbs = new HashSet<string>(
            new[] { "поставь", "полетели", "полетим", "лети", "летим", "летите", "маршрут", "курс", "проложи" }.Select(Stem)));

        private static bool IsRoute(string[] tokens) => tokens.Any(t => RouteVerbs.Contains(Stem(t)));

        // Как луны звучат, без пробелов: «ашуранс» → Assurance
        private static readonly Dictionary<string, string> MoonSounds = new Dictionary<string, string>();

        /// <summary>
        /// Луна, названная в тексте: целым словом («Титана»), а после «поставь», «полетели» ещё и
        /// похоже по звучанию кусками, как их слышит распознавание («пашу рен с» ≈ «ашуранс»).
        /// null — луны нет.
        /// </summary>
        public static string MoonInText(string text)
        {
            var all = Tokens(text).Where(t => !t.StartsWith("алис")).ToArray();
            var fuzzy = IsRoute(all);
            var tokens = all.Where(t => !RouteVerbs.Contains(Stem(t)) && t != "на" && t != "до").ToArray();
            string best = null;
            var bestGain = 0;
            foreach (var t in tokens)
            {
                var moon = MoonIn(t);
                if (moon != null && t.Length > bestGain)
                {
                    bestGain = t.Length;
                    best = moon;
                }
            }
            if (!fuzzy)
                return best;
            // Подряд идущие 1–3 слова, склеенные, против всех имён лун: ошибок не больше четверти,
            // побеждает кусок, где совпало больше букв
            for (var i = 0; i < tokens.Length; i++)
            {
                var glued = "";
                for (var j = i; j < tokens.Length && j < i + 3; j++)
                {
                    glued += tokens[j];
                    if (glued.Length < 3)
                        continue;
                    foreach (var pair in MoonSounds)
                    {
                        var d = Levenshtein(glued, pair.Key);
                        var gain = pair.Key.Length - d;
                        if (d * 4 <= Math.Max(glued.Length, pair.Key.Length) && gain > bestGain)
                        {
                            bestGain = gain;
                            best = pair.Value;
                        }
                    }
                }
            }
            return best;
        }

        /// <summary>Все слова примеров и формы имён: из них собирается грамматика распознавания.</summary>
        public static IEnumerable<string> Vocabulary => Words.Concat(NameForms.Keys).Concat(NumberWords.Keys);

        public static int Count => Examples.Count;

        /// <summary>Примеры целиком (без {name}) — тоже в грамматику, чтобы связные фразы узнавались лучше.</summary>
        /// Примеры с {moon} — с каждым звучанием каждой луны («поставь асу ранс»): так распознавание
        /// охотнее собирает название из кусков словаря, а не подставляет похожее слово.
        public static IEnumerable<string> Phrases =>
            Examples.Concat(FollowUps.Values.SelectMany(l => l)).Where(e => !e.Text.Contains("{")).Select(e => e.Text)
                .Concat(Examples.Where(e => e.Text.Contains(MoonSlot) && !e.Text.Replace(MoonSlot, "").Contains("{"))
                    .SelectMany(e => MoonPhrases.Select(m => e.Text.Replace(MoonSlot, m))))
                .Concat(MoonPhrases)
                .Distinct();

        // Звучания лун как написаны (куски через пробел, с «ё»)
        private static readonly List<string> MoonPhrases = new List<string>();

        public static void Clear()
        {
            Examples.Clear();
            FollowUps.Clear();
            Words.Clear();
            Idf.Clear();
        }

        /// <summary>
        /// Добавляет темы из текста: строки «Тема: пример, пример». # — комментарий.
        /// Тема — папка фраз (Joke, Events/Suit) или особая (#time, #whereis…).
        /// </summary>
        public static void Load(string text)
        {
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") && !line.Contains(":") || line.StartsWith("# "))
                    continue;
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var topic = line.Substring(0, colon).Trim();
                if (topic.StartsWith("$"))
                {
                    // Предмет магазина: «$Pro-flashlight: про фонарик, профонарик»
                    foreach (var alias in line.Substring(colon + 1).Split(','))
                    {
                        var words = Tokens(alias, keepYo: true);
                        if (words.Length == 0)
                            continue;
                        foreach (var w in words)
                        {
                            Words.Add(w);
                            ItemStems.Add(Stem(Normalize(w)));
                        }
                        ItemNames.Add(new KeyValuePair<string[], string>(words.Select(w => Stem(Normalize(w))).ToArray(), topic.Substring(1).Trim()));
                    }
                    continue;
                }
                if (topic.StartsWith("@"))
                {
                    // Луна: «@Titan: титан, титана» — как её называют вслух. Кусками через пробел
                    // («асу ранс») — как слышит распознавание: такие сверяются только склеенными
                    var moon = topic.Substring(1).Trim();
                    foreach (var alias in line.Substring(colon + 1).Split(','))
                    {
                        var parts = Tokens(alias, keepYo: true);
                        if (parts.Length == 0)
                            continue;
                        foreach (var w in parts)
                            Words.Add(w);
                        MoonPhrases.Add(string.Join(" ", parts));
                        if (parts.Length == 1)
                            MoonStems[Stem(parts[0])] = moon;
                        MoonSounds[Normalize(string.Concat(parts))] = moon;
                    }
                    continue;
                }
                // Продолжение разговора: «HowAreYou/Ask > HowAreYou/Good: хорошо» — после фразы из
                // HowAreYou/Ask («а у вас как?») ответ «хорошо» без «Алиса» получает HowAreYou/Good
                var target = Examples;
                var arrow = topic.IndexOf('>');
                if (arrow > 0)
                {
                    var after = topic.Substring(0, arrow).Trim();
                    topic = topic.Substring(arrow + 1).Trim();
                    if (!FollowUps.TryGetValue(after, out target))
                        FollowUps[after] = target = new List<Example>();
                }
                foreach (var phrase in line.Substring(colon + 1).Split(','))
                {
                    var tokens = Tokens(phrase);
                    if (tokens.Length == 0)
                        continue;
                    // В грамматику — как написано, с «ё»: в словаре распознавания «ещё», а не «еще»
                    foreach (var t in Tokens(phrase, keepYo: true))
                    {
                        if (!IsSlot(t))
                            Words.Add(t);
                    }
                    target.Add(new Example
                    {
                        Topic = topic,
                        Text = string.Join(" ", tokens),
                        Stems = tokens.Select(t => IsSlot(t) ? t : Stem(t)).ToArray(),
                    });
                }
            }
            RecountIdf();
        }

        // Вес слова, которого нет ни в одном примере: как у самого редкого
        private static float MaxIdf = 1f;

        private static void RecountIdf()
        {
            Idf.Clear();
            var df = new Dictionary<string, int>();
            foreach (var e in Examples.Concat(FollowUps.Values.SelectMany(l => l)))
            {
                foreach (var s in e.Stems.Distinct())
                    df[s] = df.TryGetValue(s, out var n) ? n + 1 : 1;
            }
            foreach (var pair in df)
                Idf[pair.Key] = (float)Math.Log(1.0 + (double)Examples.Count / pair.Value);
            Idf[NameSlot] = 3f;
            Idf[MoonSlot] = 3f;
            Idf[ItemSlot] = 3f;
            Idf[NumberSlot] = 2f;
            MaxIdf = Idf.Values.DefaultIfEmpty(1f).Max();
        }

        /// <summary>
        /// Друзья, как их зовут вслух: папка → имена («Pavel» → паша, павел, пашок). Падежи
        /// (Пашей, Пашу, Пашка) добавляются сами.
        /// </summary>
        public static void SetNames(Dictionary<string, string[]> names)
        {
            NameForms.Clear();
            NameStems.Clear();
            foreach (var pair in names)
            {
                foreach (var name in pair.Value)
                {
                    foreach (var form in Forms(Normalize(name)))
                    {
                        NameForms[form] = pair.Key;
                        var stem = Stem(form);
                        if (stem.Length >= 3)
                            NameStems[stem] = pair.Key;
                    }
                }
            }
        }

        /// <summary>Папка друга, названного в тексте, или null.</summary>
        public static string NameIn(string text)
        {
            foreach (var t in Tokens(text))
            {
                var folder = NameOf(t);
                if (folder != null)
                    return folder;
            }
            return null;
        }

        private static string NameOf(string token)
        {
            if (NameForms.TryGetValue(token, out var folder))
                return folder;
            return NameStems.TryGetValue(Stem(token), out folder) ? folder : null;
        }

        /// <summary>Лучшая тема для вопроса и её похожесть (0–1). null — ничего похожего.</summary>
        public static string Match(string command, out float score) => Match(command, out score, out _);

        /// <param name="example">Самый похожий пример (для лога), даже если он ниже порога.</param>
        public static string Match(string command, out float score, out string example)
        {
            score = 0f;
            example = null;
            // «Алиса» в середине вопроса — обращение, а не смысл
            var words = Tokens(command).Where(t => !t.StartsWith("алис")).ToArray();
            // «Поставь …», «полетели на …»: дальше луна, а не друг («пашу рен с» — это Assurance, не Паша)
            var route = IsRoute(words);
            var tokens = words.Select(t => !route && NameOf(t) != null ? NameSlot : MoonIn(t) != null ? MoonSlot : t).ToArray();
            if (route && MoonInText(command) != null)
                tokens = tokens.Where(t => RouteVerbs.Contains(Stem(t)) || t == "на").Append(MoonSlot).ToArray();
            // «Купи два про-фонарика»: предмет и число — слоты, имя предмета из нескольких слов — один слот
            if (words.Any(t => BuyVerbs.Contains(Stem(t))))
            {
                // «Купи про-фонарики в количестве трёх штук»: глагол и предмет есть — это покупка,
                // сколько и чего, разберёт ItemIn/NumberIn, лишние слова похожесть не роняют
                if (ItemIn(command) != null)
                {
                    score = 1f;
                    example = "купи {number} {item}";
                    return "#buy";
                }
                var slots = new List<string>();
                foreach (var t in tokens)
                {
                    var slot = NumberWords.ContainsKey(t) || Digits(t) != null ? NumberSlot
                        : ItemStems.Contains(Stem(t)) ? ItemSlot : t;
                    if (slot == ItemSlot && slots.Count > 0 && slots[slots.Count - 1] == ItemSlot)
                        continue;
                    slots.Add(slot);
                }
                tokens = slots.ToArray();
            }
            if (tokens.Length == 0)
                return null;
            var stems = tokens.Select(t => IsSlot(t) ? t : Stem(t)).ToArray();

            Example best = null;
            foreach (var e in Examples)
            {
                var s = Score(e.Stems, stems);
                if (s > score + 1e-4f)
                {
                    score = s;
                    best = e;
                }
            }
            example = best?.Text;
            // «Как помер Паша?» похоже на «Паша помер?», но спрашивают, как именно
            if (best != null && score >= Threshold)
                return best.Topic == "#isalive" && AsksHow(words) ? "#howdied" : best.Topic;
            // Примеры не подошли, но назван друг и спрошено «где/куда» или «жив/умер»: «куда Арсений пошёл»,
            // «Паша вообще где шляется», «Андрей там живой?» — вопрос про него, как бы ни сказали
            var about = AboutFriend(words, route);
            if (about != null)
            {
                score = Threshold;
                example = "{name} + " + about;
                return about;
            }
            return null;
        }

        private static readonly string[] WhereWords = { "где", "куда", "откуда", "делся", "делась", "подевался", "пропал", "запропастился", "шляется", "носит", "потерялся", "потерялась", "заблудился", "заблудилась" };
        private static readonly string[] HowWords = { "как", "отчего", "почему", "причина", "кто", "чего", "чем" };

        // Спрашивают, как погиб: «как», «отчего», «кто убил», «что его убило» — но не «жив?» и не «что, умер?»
        // ---- Слова-паразиты ----

        // «Слушай, а скажи пожалуйста, где Паша?» — то же, что «где Паша»: эти слова смысла не несут
        private static readonly HashSet<string> Fillers = new HashSet<string>
        {
            "слушай", "слышь", "пожалуйста", "плиз", "короче", "блин", "бля", "эй", "ну", "ну-ка", "нука", "подскажи",
            "вообще", "типа", "кстати", "алиса",
        };
        // Только первым словом: «скажи, сколько время» (но «скажи привет Паше», «скажи мяу» — команды)
        private static readonly HashSet<string> LeadFillers = new HashSet<string> { "скажи", "ладно", "так", "а", "о" };
        // Только последним: «где Сеня ходит, а?», «как дела щас»
        private static readonly HashSet<string> TailFillers = new HashSet<string> { "а", "щас", "ну", "да", "ага", "плиз" };

        /// <summary>Вопрос без слов-паразитов («слушай», «пожалуйста», «короче», «а скажи…»); пусто не бывает.</summary>
        public static string StripFillers(string command)
        {
            var words = Tokens(command, keepYo: true).ToList();
            var all = words.Count;
            words.RemoveAll(w => Fillers.Contains(w.Replace('ё', 'е')));
            while (words.Count > 1 && LeadFillers.Contains(words[0]))
                words.RemoveAt(0);
            while (words.Count > 1 && TailFillers.Contains(words[words.Count - 1]))
                words.RemoveAt(words.Count - 1);
            return words.Count == 0 || words.Count == all ? command : string.Join(" ", words);
        }

        /// <summary>
        /// Что сравнивать с примерами: вопрос без слов-паразитов, если по словам он похож не хуже
        /// («скажи мяу» остаётся «скажи мяу», «слушай где паша» становится «где паша»).
        /// </summary>
        public static string Clean(string command)
        {
            var stripped = StripFillers(command);
            if (stripped == command)
                return command;
            Match(command, out var before);
            Match(stripped, out var after);
            // Уже уверенно понятный вопрос («скажи мяу») не трогаем
            return after > before + 1e-4f || before < Threshold && after + 1e-4f >= before ? stripped : command;
        }

        /// <summary>
        /// «Как умер / жив ли» по смыслу легко путаются: «как», «отчего», «кто убил» — как погиб, иначе жив ли
        /// (с именем) или кто погиб (без имени).
        /// </summary>
        public static string FixDeathTopic(string command, string topic)
        {
            if (topic != "#howdied" && topic != "#isalive")
                return topic;
            var words = Tokens(command);
            if (topic == "#isalive")
                return AsksHow(words) ? "#howdied" : topic;
            if (AsksHow(words) || words.Contains("он") || words.Contains("она") || words.Contains("его") || words.Contains("ее"))
                return topic;
            return words.Any(t => NameOf(t) != null) ? "#isalive" : "#whodied";
        }

        private static bool AsksHow(string[] words) =>
            words.Any(t => DeadStems.Any(t.StartsWith)) && !words.Any(t => t.StartsWith("жив"))
            && (words.Any(t => HowWords.Contains(t)) || words.Contains("что") && words.Any(t => t.StartsWith("убил")));
        private static readonly string[] DeadStems = { "умер", "погиб", "мертв", "сдох", "помер", "убил", "убит", "жив" };

        private static string AboutFriend(string[] words, bool route)
        {
            if (route || !words.Any(t => NameOf(t) != null))
                return null;
            if (words.Any(t => DeadStems.Any(t.StartsWith)))
                return AsksHow(words) ? "#howdied" : "#isalive";
            if (words.Any(t => WhereWords.Contains(t)))
                return "#whereis";
            return null;
        }

        private static bool IsSlot(string token) => token == NameSlot || token == MoonSlot || token == ItemSlot || token == NumberSlot;

        // Насколько вопрос покрывает пример (с весами слов) и насколько сам вопрос из него состоит.
        // Лишнее редкое слово в вопросе («поставь» рядом с именем) сильно снижает похожесть:
        // лучше «не поняла», чем ответ не на тот вопрос.
        private static float Score(string[] example, string[] command)
        {
            float total = 0f, got = 0f;
            var used = new bool[command.Length];
            foreach (var e in example)
            {
                var w = Idf.TryGetValue(e, out var v) ? v : 1f;
                total += w;
                float bestSim = 0f;
                var bestAt = -1;
                for (var i = 0; i < command.Length; i++)
                {
                    var sim = Similarity(e, command[i]);
                    if (sim > bestSim)
                    {
                        bestSim = sim;
                        bestAt = i;
                    }
                }
                got += w * bestSim;
                if (bestAt >= 0 && bestSim >= 0.7f)
                    used[bestAt] = true;
            }
            if (total <= 0f)
                return 0f;
            var recall = got / total;
            float all = 0f, matched = 0f;
            for (var i = 0; i < command.Length; i++)
            {
                var w = Idf.TryGetValue(command[i], out var v) ? v : MaxIdf;
                all += w;
                if (used[i])
                    matched += w;
            }
            var precision = all > 0f ? matched / all : 0f;
            return recall * (0.4f + 0.6f * precision);
        }

        private static float Similarity(string a, string b)
        {
            if (a == b)
                return 1f;
            if (IsSlot(a) || IsSlot(b))
                return 0f;
            var min = Math.Min(a.Length, b.Length);
            if (min >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)))
                return 0.85f;
            if (min >= 4 && Levenshtein(a, b) <= 1)
                return 0.75f;
            if (min >= 6 && Levenshtein(a, b) <= 2)
                return 0.7f;
            return 0f;
        }

        public static string Normalize(string text) => (text ?? "").ToLowerInvariant().Replace('ё', 'е');

        public static string[] Tokens(string text, bool keepYo = false)
        {
            var sb = new StringBuilder();
            foreach (var c in keepYo ? (text ?? "").ToLowerInvariant() : Normalize(text))
                sb.Append(char.IsLetterOrDigit(c) || c == '{' || c == '}' || c == '-' ? c : ' ');
            return sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t != "unk").ToArray();
        }

        // ---- Падежи имён ----

        /// <summary>Имя и его падежи: паша → паши, паше, пашу, пашей, пашой; пашок → пашка, пашку…</summary>
        public static IEnumerable<string> Forms(string name)
        {
            var forms = new List<string> { name };
            void Add(string stem, params string[] ends) => forms.AddRange(ends.Select(e => stem + e));
            if (name.Length < 3)
                return forms;
            var last = name[name.Length - 1];
            var body = name.Substring(0, name.Length - 1);
            if (name.EndsWith("ий"))
                Add(name.Substring(0, name.Length - 2), "ия", "ию", "ием", "ии");
            else if (last == 'й')
                Add(body, "я", "ю", "ем", "е");
            else if (last == 'а')
                Add(body, "ы", "и", "е", "у", "ой", "ей");
            else if (last == 'я')
                Add(body, "и", "е", "ю", "ей", "ей");
            else if (last == 'ь')
                Add(body, "я", "ю", "ем", "е");
            else if (name.EndsWith("ок") || name.EndsWith("ек") || name.EndsWith("ел"))
                // Беглая гласная: пашок → пашка, павел → павла
                Add(name.Substring(0, name.Length - 2) + name[name.Length - 1], "а", "у", "ом", "е");
            else if (!IsVowel(last))
                Add(name, "а", "у", "ом", "е", "ы");
            return forms.Distinct();
        }

        // ---- Стеммер Snowball для русского ----

        private static bool IsVowel(char c) => "аеиоуыэюя".IndexOf(c) >= 0;

        private static readonly string[] PerfectiveGerund1 = { "вшись", "вши", "в" };
        private static readonly string[] PerfectiveGerund2 = { "ившись", "ывшись", "ивши", "ывши", "ив", "ыв" };
        private static readonly string[] Adjective =
        {
            "ими", "ыми", "его", "ого", "ему", "ому", "ее", "ие", "ые", "ое", "ей", "ий", "ый", "ой", "ем", "им", "ым",
            "ом", "их", "ых", "ую", "юю", "ая", "яя", "ою", "ею",
        };
        private static readonly string[] Participle1 = { "ем", "нн", "вш", "ющ", "щ" };
        private static readonly string[] Participle2 = { "ивш", "ывш", "ующ" };
        private static readonly string[] Reflexive = { "ся", "сь" };
        private static readonly string[] Verb1 = { "ете", "йте", "ешь", "нно", "ла", "на", "ли", "ем", "ло", "но", "ет", "ют", "ны", "ть", "й", "л", "н" };
        private static readonly string[] Verb2 =
        {
            "ейте", "уйте", "ила", "ыла", "ена", "ите", "или", "ыли", "ило", "ыло", "ено", "ует", "уют", "ены", "ить",
            "ыть", "ишь", "ей", "уй", "ил", "ыл", "им", "ым", "ен", "ят", "ит", "ыт", "ую", "ю",
        };
        private static readonly string[] Noun =
        {
            "иями", "ями", "ами", "ией", "иям", "ием", "иях", "ев", "ов", "ие", "ье", "еи", "ии", "ей", "ой", "ий", "ям",
            "ем", "ам", "ом", "ах", "ях", "ию", "ью", "ия", "ья", "а", "е", "и", "й", "о", "у", "ы", "ь", "ю", "я",
        };
        private static readonly string[] Superlative = { "ейше", "ейш" };
        private static readonly string[] Derivational = { "ость", "ост" };

        private static readonly Dictionary<string, string> StemCache = new Dictionary<string, string>();

        public static string Stem(string word)
        {
            word = Normalize(word);
            lock (StemCache)
            {
                if (StemCache.TryGetValue(word, out var cached))
                    return cached;
            }
            var stem = StemUncached(word);
            lock (StemCache)
                StemCache[word] = stem;
            return stem;
        }

        private static string StemUncached(string word)
        {
            if (word.Length < 3 || !word.All(c => c >= 'а' && c <= 'я'))
                return word;
            // RV — после первой гласной; R2 — по правилам Snowball
            var rv = 0;
            while (rv < word.Length && !IsVowel(word[rv]))
                rv++;
            rv++;
            if (rv > word.Length)
                return word;
            var r1 = Region(word, 0);
            var r2 = Region(word, r1);

            var w = word;
            if (!Cut(ref w, rv, PerfectiveGerund2, false) && !Cut(ref w, rv, PerfectiveGerund1, true))
            {
                Cut(ref w, rv, Reflexive, false);
                if (!CutAdjectival(ref w, rv) && !Cut(ref w, rv, Verb2, false) && !Cut(ref w, rv, Verb1, true))
                    Cut(ref w, rv, Noun, false);
            }
            Cut(ref w, rv, new[] { "и" }, false);
            Cut(ref w, Math.Max(rv, r2), Derivational, false);
            if (w.EndsWith("нн") && w.Length - 1 >= rv)
                w = w.Substring(0, w.Length - 1);
            else if (Cut(ref w, rv, Superlative, false))
            {
                if (w.EndsWith("нн") && w.Length - 1 >= rv)
                    w = w.Substring(0, w.Length - 1);
            }
            else if (w.EndsWith("ь") && w.Length - 1 >= rv)
                w = w.Substring(0, w.Length - 1);
            return w.Length > 0 ? w : word;
        }

        private static int Region(string word, int from)
        {
            for (var i = from + 1; i < word.Length; i++)
            {
                if (!IsVowel(word[i]) && IsVowel(word[i - 1]))
                    return i + 1;
            }
            return word.Length;
        }

        private static bool CutAdjectival(ref string w, int rv)
        {
            if (!Cut(ref w, rv, Adjective, false))
                return false;
            if (!Cut(ref w, rv, Participle2, false))
                Cut(ref w, rv, Participle1, true);
            return true;
        }

        // Срезает самое длинное окончание в пределах RV; afterAYa — только после «а»/«я» (её не трогаем)
        private static bool Cut(ref string w, int rv, string[] endings, bool afterAYa)
        {
            string best = null;
            foreach (var e in endings)
            {
                if (!w.EndsWith(e, StringComparison.Ordinal) || w.Length - e.Length < rv)
                    continue;
                if (afterAYa)
                {
                    var i = w.Length - e.Length - 1;
                    if (i < rv || w[i] != 'а' && w[i] != 'я')
                        continue;
                }
                if (best == null || e.Length > best.Length)
                    best = e;
            }
            if (best == null)
                return false;
            w = w.Substring(0, w.Length - best.Length);
            return true;
        }

        public static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
                prev[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1], prev[j]) + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }
    }
}
