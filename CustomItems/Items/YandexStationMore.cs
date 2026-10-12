using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace CustomItems.Items
{
    /// <summary>
    /// Яндекс Колонка, вторая часть: двери и турели по коду («открой дверь B4»), поиск выхода и корабля,
    /// что у вас в руке, сканирование луны, квота числами, повтор, мяуканье, новые события
    /// (мины, турели, пар, темнота, драки на корабле, брошенная колонка) и переиспользование записей
    /// из любых паков там, где они тоже подходят (<see cref="Reuse"/>).
    /// </summary>
    public partial class YandexStationItem
    {
        public const string TopicCode = "#code", TopicNavExit = "#navexit", TopicNavShip = "#navship", TopicInHand = "#inhand",
            TopicScanMoon = "#scanmoon", TopicQuota = "#quota", TopicRepeatLast = "#repeat", TopicMeow = "Meow", TopicKitty = "Kitty",
            TopicCallMe = "#callme", TopicMusicOn = "#musicon", TopicMusicOff = "#musicoff";

        // ---- Одни и те же записи в разных ситуациях ----

        /// <summary>
        /// Тема ← записи из других тем: «Тема/Целиком» или «Тема/Имя файла» (без .wav). Добавляются к теме
        /// при запуске у всех одинаково, поэтому номера записей в сети совпадают.
        /// </summary>
        private static readonly (string target, string source)[] Reuse =
        {
            ("Door/Fail/Door", "Error/Я попробовала, не получилось"), ("Door/Fail/Door", "Error/Я старалась, но не смогла"),
            ("Door/Fail/Door", "Door/Cant"),
            ("Door/Fail/Turret", "Error/Я попробовала, не получилось"), ("Door/Fail/Turret", "Error/Я старалась, но не смогла"),
            ("Door/Fail/Mine", "Error/Я попробовала, не получилось"), ("Door/Fail/Mine", "Error/Я старалась, но не смогла"),
            ("Route/Busy", "Error/Я попробовала, не получилось"), ("Launch/Busy", "Error/Я старалась, но не смогла"),
            ("Buy/Full", "Error/Я попробовала, не получилось"),
            ("Door/Done", "Ok/Готово"), ("Door/Done", "Ok/Сделано"), ("Door/Try", "Wait/Секунду"),
            ("Nav/Fail", "Error/Я попробовала, не получилось"), ("Nav/Start", "Wait/Секунду"), ("Nav/Start", "Wait/Смотрю"),
            ("Nav/TooFar", "Navigation/Маршрут перестроен"), ("Nav/Recalc", "Navigation/Маршрут перестроен"),
            ("Check/Start", "Wait/Смотрю"), ("Check/Start", "Wait/Секунду"), ("Check/Done", "Ok/Готово"),
            ("Scan/Cleared", "AllClear/Вокруг чисто"),
            ("Events/Behind", "EnemiesNear/Рядом есть враг"),
            ("Events/Abandoned", "Events/LeftBehind/Эй! Вы меня забыли"), ("Events/LeftBehind", "Events/Abandoned/Заберите меня"),
            ("Events/Fall", "Events/LowHealth/Вы ранены! Осторожнее"), ("Events/Fall", "Events/BackHurt/В следующий раз будьте осторожнее"),
            ("Events/Teamkill", "Events/PlayerDied/Мы потеряли товарища"), ("Events/Teamkill/Excuse", "Insult/Я всё запомню"),
            ("Events/DoorsClosedOutside", "Events/ForgotSomeone/Вы забыли кого-то"),
            ("Events/Eclipse", "Weather/Eclipsed"), ("Events/Sold", "Events/Money/Ура, деньги"),
            ("Events/Hold/Shotgun", "Events/WhatIsThis/Что это"), ("Events/Weird", "Events/WhatIsThis/Что это"),
            ("Events/Inside/Dark", "Weather/Blackout/Темно, включите фонарики"),
            ("Events/HalfBattery", "Battery/50"), ("Events/Dropped", "Events/PickedUp/Аккуратнее, я хрупкая"),
            ("Events/Shot", "Events/Panic/Спокойно! Без паники"), ("Events/Mine/Pressed", "Events/Panic/Мы погибнем мы все умрём"),
            ("Loot/High", "Events/ManyItems/Ого, сколько всего"), ("Loot/Low", "Events/Junk/Ну и хлам тут у вас"),
            ("WhoDied/Intro", "Events/PlayerDied/Кто-то умер"), ("Stopping", "Ok/Сделано"),
            ("Shout", "Launch/Takeoff/Поняла, поняла, не кричите, взлетаем"),
            ("Events/Company", "QuotaNotDone/Не забудьте про квоту"), ("Events/Teleported", "Events/Back/Вот и вы"),
            ("Events/Teleported", "Greeting/Вы вернулись"), ("Nav/Arrived/Ship", "Events/Back/Вот и вы"),
            ("Scan/VeryMany", "Events/ManyItems/Ого, сколько всего"), ("Events/Inside", "Events/Idle/Как то тихо стало"),
            ("Events/Dropped", "Insult/Как грубо"), ("Events/Dropped", "Events/Fall"), ("Events/Dropped", "Insult/Обидно, вообще то"),
            ("Events/Dropped", "Insult/Я всё запомню"), ("Events/Dropped", "Insult/Ну вот, а я к вам со всей душой"),
        };

        private void ApplyReuse()
        {
            if (phraseSets == null)
                return;
            var sets = phraseSets.Where(s => s != null).ToList();
            PhraseSet Get(string name) => sets.FirstOrDefault(s => string.Equals(s.name, name, StringComparison.OrdinalIgnoreCase));
            var added = new HashSet<string>();
            foreach (var (target, source) in Reuse)
            {
                var clips = new List<AudioClip>();
                var whole = Get(source);
                if (whole != null)
                    clips.AddRange(whole.clips.Where(c => c != null));
                else
                {
                    var slash = source.LastIndexOf('/');
                    var from = slash > 0 ? Get(source.Substring(0, slash)) : null;
                    var clip = from?.clips.FirstOrDefault(c => c != null && c.name == source.Substring(slash + 1));
                    if (clip != null)
                        clips.Add(clip);
                }
                if (clips.Count == 0)
                    continue;
                var set = Get(target);
                if (set == null)
                {
                    set = new PhraseSet { name = target, clips = new AudioClip[0] };
                    sets.Add(set);
                }
                else if (!added.Contains(target) && set.clips.Intersect(clips).Any())
                    continue; // уже добавлено (Start второй раз)
                set.clips = set.clips.Concat(clips.Where(c => !set.clips.Contains(c))).ToArray();
                added.Add(target);
            }
            phraseSets = sets.ToArray();
        }

        // ---- Ответы на новые вопросы ----

        /// <summary>Темы второй части. false — тема не отсюда.</summary>
        private bool AnswerMore(PlayerControllerB speaker, string topic, string known)
        {
            switch (topic)
            {
                case TopicCode:
                    SayCode(known);
                    return true;
                case TopicDoorsOpen when speaker.isInsideFactory:
                    SayFacilityDoor(speaker);
                    return true;
                case TopicNavExit:
                case TopicNavShip:
                    StartNavigation(speaker, topic == TopicNavShip || !speaker.isInsideFactory);
                    return true;
                case TopicStop when navTarget != null:
                    StopNavigation(true);
                    return true;
                case TopicInHand:
                    SayInHand(speaker, known);
                    return true;
                case TopicScanMoon:
                    SayScan();
                    return true;
                case "Quota":
                case "QuotaDone":
                case "QuotaNotDone":
                    return SayQuota();
                case TopicRepeatLast:
                    SayRepeat();
                    return true;
                case TopicMeow:
                    if (FindSet("Meow/Cant") != null && Random.value < 0.2f)
                        Say("Meow/Cant");
                    else
                        SayTopic(TopicMeow);
                    return true;
                case TopicCallMe:
                    SayFirst(Random.value < 0.5f ? new[] { "CallMe/Forgot", "CallMe/Refuse", "Ok" } : new[] { "CallMe/Refuse", "CallMe/Forgot", "Ok" });
                    return true;
                case TopicMusicOn:
                case TopicMusicOff:
                    SayMusic(topic == TopicMusicOn);
                    return true;
            }
            return false;
        }

        private void SayAny(params string[] topics)
        {
            var sets = topics.Select(FindSet).Where(s => s != null).ToList();
            if (sets.Count == 0)
            {
                Say(TopicUnknown);
                return;
            }
            // По числу записей: больше записей — чаще звучит
            var pick = Random.Range(0, sets.Sum(s => s.clips.Length));
            foreach (var s in sets)
            {
                if (pick < s.clips.Length)
                {
                    SayFrom(s);
                    return;
                }
                pick -= s.clips.Length;
            }
        }

        private List<string> Existing(params string[] topics) => topics.Where(t => FindSet(t) != null).ToList();

        // ---- Двери, турели и мины по коду, как с терминала ----

        private static readonly Dictionary<string, char> Letters = new Dictionary<string, char>
        {
            ["а"] = 'a', ["эй"] = 'a', ["б"] = 'b', ["бэ"] = 'b', ["би"] = 'b', ["бе"] = 'b', ["ц"] = 'c', ["цэ"] = 'c', ["це"] = 'c',
            ["си"] = 'c', ["д"] = 'd', ["дэ"] = 'd', ["де"] = 'd', ["ди"] = 'd', ["е"] = 'e', ["э"] = 'e', ["ф"] = 'f', ["эф"] = 'f',
            ["г"] = 'g', ["гэ"] = 'g', ["ге"] = 'g', ["джи"] = 'g', ["х"] = 'h', ["ха"] = 'h', ["аш"] = 'h', ["эйч"] = 'h',
            ["ай"] = 'i', ["й"] = 'j', ["джей"] = 'j', ["жи"] = 'j', ["к"] = 'k', ["ка"] = 'k', ["кей"] = 'k', ["кэй"] = 'k',
            ["л"] = 'l', ["эл"] = 'l', ["эль"] = 'l', ["м"] = 'm', ["эм"] = 'm', ["н"] = 'n', ["эн"] = 'n', ["о"] = 'o', ["оу"] = 'o',
            ["п"] = 'p', ["пэ"] = 'p', ["пе"] = 'p', ["пи"] = 'p', ["ку"] = 'q', ["кью"] = 'q', ["р"] = 'r', ["эр"] = 'r',
            ["с"] = 's', ["эс"] = 's', ["т"] = 't', ["тэ"] = 't', ["те"] = 't', ["ти"] = 't', ["у"] = 'u', ["ю"] = 'u',
            ["в"] = 'v', ["вэ"] = 'v', ["ве"] = 'v', ["ви"] = 'v', ["дабл"] = 'w', ["икс"] = 'x', ["кс"] = 'x', ["игрек"] = 'y',
            ["уай"] = 'y', ["з"] = 'z', ["зэт"] = 'z', ["зет"] = 'z', ["зед"] = 'z',
        };

        private static readonly Dictionary<string, char> Digits = new Dictionary<string, char>
        {
            ["ноль"] = '0', ["нуль"] = '0', ["один"] = '1', ["одна"] = '1', ["раз"] = '1', ["два"] = '2', ["две"] = '2', ["три"] = '3',
            ["четыре"] = '4', ["пять"] = '5', ["шесть"] = '6', ["семь"] = '7', ["восемь"] = '8', ["девять"] = '9',
        };

        // «и» и «ай» звучат и как буква, и как союз: буквой считаем только перед цифрой
        private static readonly HashSet<string> CodeVerbs = new HashSet<string>
        {
            "дверь", "двери", "дверку", "турель", "турели", "турельку", "мину", "мина", "мины", "код", "кодом", "номер",
        };

        /// <summary>Коды из текста («открой дверь бэ четыре», «b4», «би 4»): буква, потом цифра. Пусто — кода нет.</summary>
        public static List<string> CodesIn(string text)
        {
            var codes = new List<string>();
            var tokens = AliceIntents.Tokens(text);
            if (!tokens.Any(CodeVerbs.Contains))
                return codes;
            for (var i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i];
                // Слитно: «b4», «б4», «бэ4»
                if (t.Length >= 2 && char.IsDigit(t[t.Length - 1]))
                {
                    var head = t.Substring(0, t.Length - 1);
                    var l = head.Length == 1 && head[0] >= 'a' && head[0] <= 'z' ? head[0] : Letters.TryGetValue(head, out var c) ? c : '\0';
                    if (l != '\0')
                        codes.Add("" + l + t[t.Length - 1]);
                    continue;
                }
                if (i + 1 >= tokens.Length)
                    continue;
                var next = tokens[i + 1];
                var digit = next.Length == 1 && char.IsDigit(next[0]) ? next[0] : Digits.TryGetValue(next, out var d) ? d : '\0';
                if (digit == '\0')
                    continue;
                if (t.Length == 1 && t[0] >= 'a' && t[0] <= 'z')
                    codes.Add("" + t[0] + digit);
                else if (Letters.TryGetValue(t, out var letter))
                    codes.Add("" + letter + digit);
                else if (t == "и")
                {
                    codes.Add("e" + digit); // «и» — это E или I: проверим обе
                    codes.Add("i" + digit);
                }
            }
            return codes;
        }

        private static readonly string[] AlikeLetters = { "bdeptvgcz", "fs", "mn", "ai", "kq", "jg", "xs", "ou" };

        private static bool SoundsAlike(char a, char b) => a == b || AlikeLetters.Any(g => g.IndexOf(a) >= 0 && g.IndexOf(b) >= 0);

        private void SayCode(string known)
        {
            var codes = CodesIn(known);
            var objects = FindObjectsOfType<TerminalAccessibleObject>().Where(o => o != null && !string.IsNullOrEmpty(o.objectCode)).ToList();
            var target = codes.Select(c => objects.FirstOrDefault(o => string.Equals(o.objectCode, c, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(o => o != null);
            // «Бэ», «дэ», «вэ», «пэ», «тэ» на слух путаются: та же цифра и похожая буква — если такая одна
            if (target == null)
            {
                var similar = objects.Where(o => o.objectCode.Length == 2 && codes.Any(c => c[1] == char.ToLowerInvariant(o.objectCode[1])
                    && SoundsAlike(c[0], char.ToLowerInvariant(o.objectCode[0])))).Select(o => o.objectCode.ToLowerInvariant()).Distinct().ToList();
                if (similar.Count == 1)
                    target = objects.First(o => o.objectCode.ToLowerInvariant() == similar[0]);
            }
            if (target == null)
            {
                Debug.Log($"[Алиса] код: услышала {string.Join(",", codes)}, на карте {string.Join(",", objects.Select(o => o.objectCode).Distinct())}");
                SayFirst("Door/NotFound", "Route/Unknown", TopicUnknown);
                return;
            }
            var turret = target.GetComponent<Turret>() ?? target.GetComponentInParent<Turret>();
            var mine = target.GetComponent<Landmine>() ?? target.GetComponentInParent<Landmine>();
            var fail = turret != null ? "Door/Fail/Turret" : mine != null ? "Door/Fail/Mine" : "Door/Fail/Door";
            if (target.inCooldown || target.isBigDoor && !target.isPoweredOn)
            {
                SayFirst(fail, TopicRefuse);
                return;
            }
            // Как с терминала: игра сама открывает дверь или выключает турель и мину у всех
            target.CallFunctionFromTerminal();
            Debug.Log($"[Алиса] код {target.objectCode}: {(turret != null ? "турель" : mine != null ? "мина" : "дверь")}");
            var topics = Existing("Door/Try");
            if (FindSet("Door/Done") != null)
                topics.Add("Door/Done");
            if (topics.Count > 0)
                SaySequence(topics.Count > 1 && Random.value < 0.4f ? topics.Skip(1).ToList() : topics);
            else
                Say("Ok");
        }

        // «Открой дверь» в комплексе без кода — только диалог: обычные двери Алиса не открывает.
        // Делает вид, что пытается («Постараюсь открыть» → «Я попробовала, не получилось»),
        // или говорит, что нужен ключ, и спрашивает, есть ли он. По-настоящему открываются только двери по коду.
        private void SayFacilityDoor(PlayerControllerB speaker)
        {
            var door = FindObjectsOfType<DoorLock>().Where(d => d != null)
                .OrderBy(d => Vector3.Distance(d.transform.position, speaker.transform.position)).FirstOrDefault();
            if (door == null || Vector3.Distance(door.transform.position, speaker.transform.position) > 5f)
            {
                // Двери рядом нет: может, назвали код без кода — «какой номер?»
                SayFirst("Door/NotFound", "Door/Cant", TopicUnknown);
                return;
            }
            if (!door.isLocked && Random.value < 0.5f)
            {
                var tried = Existing("Door/Try");
                if (FindSet("Door/Fail/Door") != null)
                    tried.Add("Door/Fail/Door");
                if (tried.Count > 0)
                {
                    SaySequence(tried);
                    return;
                }
            }
            // «Дверь закрыта. Нужен ключ. У вас ключ есть?» — а ответ «нет» — «И у меня нет»
            var topics = new List<string> { Random.value < 0.5f ? "Door/Locked" : "Door/Cant", "Door/NeedKey" }.Where(t => FindSet(t) != null).ToList();
            var ask = FindSet("Door/AskKey") != null;
            if (ask)
                topics.Add("Door/AskKey");
            else if (FindSet("Door/NoKey") != null)
                topics.Add("Door/NoKey");
            if (topics.Count == 0)
            {
                Say(TopicRefuse);
                return;
            }
            SaySequence(topics);
            if (ask)
            {
                asking = AskKey;
                askStartedAt = Mathf.Max(Time.time, talkUntil);
                askUntil = askStartedAt + ListenSilence;
            }
        }

        private const string AskKey = "key", AskRepeat = "repeat", AskOrder = "order";

        // Ответ на «У вас ключ есть?», «Хотите, закажу?», «Вы хотите, чтобы я повторила?»; true — вопрос был отсюда
        private bool OnReplyMore(string kind, string item, string intent)
        {
            switch (kind)
            {
                case AskKey:
                    if (intent == TopicNo)
                        SayFirst("Door/NoKey/Me", "Door/NoKey", "Ok");
                    else if (intent == TopicYes)
                        Say("Ok");
                    return true;
                case AskRepeat:
                    if (intent == TopicYes)
                        SayRepeat(force: true);
                    else if (intent == TopicNo)
                        Say("Ok");
                    return true;
                case AskOrder:
                    if (intent == TopicYes)
                        SayBought(item, PlayersOnShip());
                    else if (intent == TopicNo)
                        SayFirst(BuyCancel, "Ok");
                    return true;
            }
            return false;
        }

        private static int PlayersOnShip()
        {
            var round = StartOfRound.Instance;
            return round == null ? 1 : Mathf.Max(1, round.allPlayerScripts.Count(p => p != null && p.isPlayerControlled && !p.isPlayerDead));
        }

        // «Заказала! В количестве двух» (Count/Intro + Count/Gen/N), иначе ответ магазина как есть
        private void SayBought(string item, int count)
        {
            var result = Buy != null ? Buy(item, count) : new[] { TopicRefuse };
            if (result.Length > 0 && result[0] == "Buy/Done" && FindSet("Buy/Done") != null && FindSet("Count/Intro") != null)
            {
                var gen = count <= 3 ? "Count/Gen/" + count : count <= 10 ? "Numbers/Gen/" + count : "Count/Gen/More4";
                if (count == 4 && FindSet(gen) == null)
                    gen = "Count/Gen/More4";
                if (FindSet(gen) != null && Random.value < 0.6f)
                {
                    SaySequence(new List<string> { "Buy/Done", "Count/Intro", gen });
                    return;
                }
            }
            SayFirst(result);
        }

        // ---- Что в руке ----

        private static readonly Dictionary<string, string> HandNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pro-flashlight"] = "Flashlight", ["Laser pointer"] = "Flashlight", ["Adrenaline Game Fuel"] = "Adrenaline",
        };

        private void SayInHand(PlayerControllerB speaker, string known)
        {
            var round = StartOfRound.Instance;
            var tokens = AliceIntents.Tokens(known);
            // «Что у него в руке» — у ближайшего другого, «у Паши» — у названного
            var player = speaker;
            var him = tokens.Contains("него") || tokens.Contains("нее") || tokens.Contains("неё");
            var named = FriendNamed(known);
            if (round != null && (him || named != null))
            {
                var other = round.allPlayerScripts.Where(p => p != null && p != speaker && p.isPlayerControlled && !p.isPlayerDead)
                    .Where(p => named == null || NameFolder(p.playerUsername) == named)
                    .OrderBy(p => Vector3.Distance(p.transform.position, speaker.transform.position)).FirstOrDefault();
                if (other != null)
                {
                    player = other;
                    him = true;
                }
            }
            var item = player.currentlyHeldObjectServer;
            var name = item != null && item.itemProperties != null ? item.itemProperties.itemName : null;
            var topics = new List<string>();
            var intro = him ? "Hand/Him" : "Hand/You";
            if (FindSet(intro) != null)
                topics.Add(intro);
            string what = null;
            if (name != null)
            {
                what = "Hand/" + (HandNames.TryGetValue(name, out var alias) ? alias : name);
                if (FindSet(what) == null)
                    what = "Hand/Unknown";
            }
            else
                what = "Hand/Unknown";
            if (FindSet(what) == null)
            {
                Say(TopicUnknown);
                return;
            }
            topics.Add(what);
            SaySequence(topics);
        }

        // ---- Сколько на луне ----

        private void SayScan()
        {
            var round = StartOfRound.Instance;
            if (round == null || round.inShipPhase || !round.shipHasLanded)
            {
                SayFirst("Route/Landed", TopicUnknown);
                return;
            }
            int count = 0, sum = 0;
            foreach (var item in FindObjectsOfType<GrabbableObject>())
            {
                if (item == null || item.itemProperties == null || !item.itemProperties.isScrap || item.isInShipRoom || item.isInElevator
                    || item.isHeld || item.isPocketed || item.deactivated || item.scrapValue <= 0)
                    continue;
                count++;
                sum += item.scrapValue;
            }
            var topics = new List<string>();
            if (FindSet("Check/Start") != null && Random.value < 0.5f)
                topics.Add("Check/Start");
            if (count == 0)
            {
                topics.AddRange(Existing("Scan/Cleared").Take(1));
                if (topics.Count == 0)
                    topics.Add(TopicAllClear);
                SaySequence(topics);
                return;
            }
            // Подробно: «Просканировала! На луне примерно» + N «предметов» + «на сумму» + сумма + «Они дорогие»
            var detailed = new List<string>(topics);
            if (FindSet("Scan/Intro") != null && AddNumber(detailed, count) && AddItemsWord(detailed, count))
            {
                detailed.Insert(topics.Count, "Scan/Intro");
                var withSum = new List<string>(detailed);
                if (FindSet("Sum") != null)
                {
                    withSum.Add("Sum");
                    if (AddSum(withSum, sum))
                        detailed = withSum;
                }
                var average = sum / count;
                var price = average < 15 ? "Scan/VeryCheap" : average < 30 ? "Scan/Cheap" : average > 80 ? "Scan/VeryExpensive" : average > 50 ? "Scan/Expensive" : null;
                if (price != null && FindSet(price) != null)
                    detailed.Add(price);
                SaySequence(detailed);
                return;
            }
            // Коротко: «Да, ещё много предметов!», «Осталось меньше 10 предметов!»
            var words = count < 10 && Random.value < 0.5f ? "Scan/Under10" : count <= 5 ? "Scan/Few" : count <= 20 ? "Scan/Many" : "Scan/VeryMany";
            topics.AddRange(Existing(words, "Scan/Left", "Scan/Many").Take(1));
            if (topics.Count == 0)
                topics.Add(TopicEnemiesNear);
            SaySequence(topics);
        }

        // «предмета» (2–4, 22–24…) или «предметов»; одного предмета нет — тоже «предметов»
        private bool AddItemsWord(List<string> topics, int n)
        {
            var few = n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14);
            var word = few && FindSet("Items/Few") != null ? "Items/Few" : FindSet("Items/Many") != null ? "Items/Many" : FindSet("Scan/Items") != null ? "Scan/Items" : null;
            if (word == null)
                return false;
            topics.Add(word);
            return true;
        }

        /// <summary>
        /// Сумма: до 2000 — числом, больше — «двух тысяч», «пяти тысяч» (Numbers/Gen/N + Numbers/Thousand/Gen)
        /// и остаток числом; так говорят «на сумму пяти тысяч».
        /// </summary>
        private bool AddSum(List<string> topics, int n)
        {
            var parts = new List<string>();
            if (n < 2000 || FindSet("Numbers/Thousand/Gen") == null)
                return AddNumber(topics, n);
            var k = n / 1000;
            if (k > 10 || FindSet("Numbers/Gen/" + k) == null)
                return AddNumber(topics, n);
            parts.Add("Numbers/Gen/" + k);
            parts.Add("Numbers/Thousand/Gen");
            if (n % 1000 >= 100 && !NumberParts(n % 1000 / 100 * 100, parts))
                return AddNumber(topics, n);
            topics.AddRange(parts);
            return true;
        }

        /// <summary>«Сколько на корабле»: «На корабле» + N «предметов» + «на сумму» + сумма, и «Ого, да вы богаты!» или «маловато».</summary>
        private bool SayLootMore(int count, int total)
        {
            if (FindSet("Loot/OnShip") == null || Random.value < 0.4f)
                return false;
            var topics = new List<string>();
            if (FindSet(TopicWait) != null && Random.value < 0.4f)
                topics.Add(TopicWait);
            topics.Add("Loot/OnShip");
            if (!AddNumber(topics, count) || !AddItemsWord(topics, count))
                return false;
            if (FindSet("Sum") != null)
            {
                topics.Add("Sum");
                if (!AddSum(topics, total))
                    return false;
            }
            var tod = TimeOfDay.Instance;
            if (tod != null)
            {
                var verdict = total >= tod.profitQuota - tod.quotaFulfilled ? "Loot/High" : total < (tod.profitQuota - tod.quotaFulfilled) / 3 ? "Loot/Low" : null;
                if (verdict != null && FindSet(verdict) != null)
                    topics.Add(verdict);
            }
            SaySequence(topics);
            return true;
        }

        // «Какая квота»: «Текущая квота» + сколько сдали, «Необходимая квота» + сколько надо
        private bool SayQuota()
        {
            var tod = TimeOfDay.Instance;
            if (tod == null || FindSet("Quota/Need") == null || Random.value < 0.3f)
                return false;
            var topics = new List<string>();
            if (FindSet("Quota/Current") != null)
            {
                topics.Add("Quota/Current");
                if (!AddSum(topics, tod.quotaFulfilled))
                    return false;
            }
            topics.Add("Quota/Need");
            if (!AddSum(topics, tod.profitQuota))
                return false;
            topics.Add(tod.quotaFulfilled >= tod.profitQuota ? "QuotaDone" : "QuotaNotDone");
            SaySequence(topics.Where(t => FindSet(t) != null).ToList());
            return true;
        }

        // ---- Повтори ----

        // Последняя сказанная фраза: тема и номер записи или вся склейка «#seq»
        private string lastSaidTopic;
        private int lastSaidIndex;

        private void SayRepeat(bool force = false)
        {
            if (lastSaidTopic == null)
            {
                Say(TopicUnknown);
                return;
            }
            if (!force && FindSet("Repeat/Ask") != null && Random.value < 0.25f)
            {
                Ask(AskRepeat, null, "Repeat/Ask");
                return;
            }
            var parts = new List<string>();
            var again = FindSet("Repeat/Again");
            if (again != null)
                parts.Add(again.name + "\t" + NextIndex(again));
            if (lastSaidTopic.StartsWith(SeqPrefix))
                parts.AddRange(lastSaidTopic.Substring(SeqPrefix.Length).Split('\n'));
            else
                parts.Add(lastSaidTopic + "\t" + lastSaidIndex);
            var topic = SeqPrefix + string.Join("\n", parts);
            askedLocally = true;
            Speak(topic, 0);
            SendSpeak(topic, 0);
        }

        // ---- Музыка: включает бумбокс рядом ----

        private void SayMusic(bool on)
        {
            var box = FindObjectsOfType<BoomboxItem>().Where(b => b != null && Vector3.Distance(b.transform.position, transform.position) < EventRadius)
                .OrderBy(b => Vector3.Distance(b.transform.position, transform.position)).FirstOrDefault();
            if (box == null || box.isPlayingMusic == on)
            {
                // Бумбокса нет — по-старому: «Включаю музыку, ой, у вас не обнаружена подписка»
                if (on)
                    Say("Music");
                else
                    SayFirst("Stopping", "Ok");
                return;
            }
            // Как ЛКМ по бумбоксу: игра включает музыку у всех
            box.ItemActivate(on, true);
            box.ActivateItemRpc(on, true);
            SayFirst(on ? "Music/On" : "Stopping", "Ok");
        }

        // ---- Поиск выхода и корабля ----

        /// <summary>Как часто колонка подсказывает дорогу (с).</summary>
        public static float NavSeconds = 4f;
        private PlayerControllerB navPlayer;
        private string navTarget; // "exit" или "ship"
        private float navNextAt, navStartedAt, navCornerBest = float.MaxValue, navGoUntil;
        private Vector3 navCorner;
        private bool navGoing, navFound;
        private string navLastSaid;

        private void StartNavigation(PlayerControllerB player, bool ship)
        {
            navPlayer = player;
            navTarget = ship ? "ship" : "exit";
            navStartedAt = Time.time;
            navFound = false;
            navGoing = false;
            navLastSaid = null;
            navCornerBest = float.MaxValue;
            var start = ship ? "Nav/Start/Ship" : "Nav/Start/Exit";
            SayFirst(Random.value < 0.6f ? new[] { start, "Nav/Start" } : new[] { "Nav/Start", start });
            navNextAt = Time.time + 1f;
        }

        private void StopNavigation(bool said)
        {
            navTarget = null;
            navPlayer = null;
            if (said)
                SayFirst("Stopping", "Nav/Done", "Ok");
        }

        private Vector3? NavGoal(PlayerControllerB p)
        {
            var round = StartOfRound.Instance;
            if (navTarget == "ship")
                return round != null && round.middleOfShipNode != null ? round.middleOfShipNode.position
                    : round != null && round.elevatorTransform != null ? round.elevatorTransform.position : (Vector3?)null;
            // Выход: ближайшая дверь наружу изнутри комплекса
            var exit = FindObjectsOfType<EntranceTeleport>().Where(e => e != null && !e.isEntranceToBuilding)
                .OrderBy(e => Vector3.Distance(e.transform.position, p.transform.position)).FirstOrDefault();
            return exit != null ? (exit.entrancePoint != null ? exit.transform.position : exit.transform.position) : (Vector3?)null;
        }

        private void UpdateNavigation()
        {
            if (navTarget == null || !IsServer && !askedLocally || Time.time < navNextAt)
                return;
            var p = navPlayer;
            if (p == null || p.isPlayerDead || !p.isPlayerControlled || !IsOn || Time.time - navStartedAt > 300f)
            {
                navTarget = null;
                return;
            }
            if (IsTalking || pendingAnswer != null)
                return;
            navNextAt = Time.time + NavSeconds;
            // Пришли: вышли из комплекса или зашли на корабль
            if (navTarget == "exit" && !p.isInsideFactory || navTarget == "ship" && p.isInHangarShipRoom)
            {
                var topics = Existing(navTarget == "exit" ? "Nav/Arrived/Exit" : "Nav/Arrived/Ship").Take(1).ToList();
                if (FindSet("Nav/Done") != null && (topics.Count == 0 || Random.value < 0.5f))
                    topics.Add("Nav/Done");
                navTarget = null;
                if (topics.Count > 0)
                    SaySequence(topics);
                return;
            }
            // Колонку унесли от игрока — подсказывать некому
            if (Vector3.Distance(transform.position, p.transform.position) > HearingRadius * 2f)
            {
                navTarget = null;
                return;
            }
            var goal = NavGoal(p);
            var path = new NavMeshPath();
            var from = p.transform.position;
            if (goal == null || !NavMesh.SamplePosition(from, out var fromHit, 5f, NavMesh.AllAreas)
                || !NavMesh.SamplePosition(goal.Value, out var toHit, 8f, NavMesh.AllAreas)
                || !NavMesh.CalculatePath(fromHit.position, toHit.position, NavMesh.AllAreas, path) || path.status == NavMeshPathStatus.PathInvalid
                || path.corners.Length < 2)
            {
                navTarget = null;
                SayFirst("Nav/Fail", TopicUnknown);
                return;
            }
            var topicsNow = new List<string>();
            if (!navFound)
            {
                navFound = true;
                if (FindSet("Nav/Found") != null)
                    topicsNow.Add("Nav/Found");
            }

            // Первый поворот дальше пары метров; цель близко — «Выход впереди/слева/сверху»
            var corners = path.corners;
            var total = 0f;
            for (var i = 1; i < corners.Length; i++)
                total += Vector3.Distance(corners[i - 1], corners[i]);
            var next = corners.Skip(1).FirstOrDefault(c => Vector3.Distance(c, fromHit.position) > 2.5f);
            if (next == Vector3.zero)
                next = corners[corners.Length - 1];
            var dir = Direction(p, next);
            if (total < 10f && navTarget == "exit")
            {
                var exitDir = Direction(p, corners[corners.Length - 1]);
                topicsNow.AddRange(Existing("Nav/Exit/" + exitDir).Take(1));
                Speak(topicsNow);
                return;
            }

            // Шли к повороту и прошли мимо — «Слишком много!», стоят на месте — «Ещё!»
            var toCorner = Vector3.Distance(from, navCorner);
            if (navGoing && Vector3.Distance(next, navCorner) < 3f)
            {
                if (toCorner < 3f)
                {
                    navGoing = false;
                    topicsNow.AddRange(Existing("Nav/Stop"));
                }
                else if (toCorner > navCornerBest + 4f)
                {
                    navGoing = false;
                    topicsNow.AddRange(Existing("Nav/TooFar").Take(1));
                }
                else if (p.timeSincePlayerMoving > 3f && FindSet("Nav/More") != null)
                {
                    navCornerBest = Mathf.Min(navCornerBest, toCorner);
                    topicsNow.Add("Nav/More");
                    Speak(topicsNow);
                    return;
                }
                else
                {
                    navCornerBest = Mathf.Min(navCornerBest, toCorner);
                    Speak(topicsNow); // идут — молчим
                    return;
                }
            }

            var dist = Vector3.Distance(from, next);
            var step = StepTopic(dir, dist);
            // Повтор той же подсказки — не чаще раза в три захода
            if (step == navLastSaid && topicsNow.Count == 0 && Random.value < 0.66f)
                return;
            if (step != null)
                topicsNow.Add(step);
            // За поворотом ещё один: «…а затем налево»
            var after = corners.SkipWhile(c => c != next).Skip(1).FirstOrDefault();
            if (step != null && after != Vector3.zero && FindSet("Nav/Then") != null)
            {
                var turn = Turn(next - from, after - next);
                if (turn != null && FindSet(turn) != null && dir == "Ahead")
                {
                    topicsNow.Add("Nav/Then");
                    topicsNow.Add(turn);
                }
            }
            navLastSaid = step;
            navCorner = next;
            navCornerBest = dist;
            navGoing = dir == "Ahead" && dist > 8f;
            Speak(topicsNow);
        }

        private void Speak(List<string> topics)
        {
            topics = topics.Where(t => FindSet(t) != null).ToList();
            if (topics.Count > 0)
                SaySequence(topics);
        }

        // Куда цель относительно взгляда игрока: Ahead, Behind, Left, Right, Up, Down
        private static string Direction(PlayerControllerB p, Vector3 target)
        {
            var delta = target - p.transform.position;
            if (Mathf.Abs(delta.y) > 2.5f && Mathf.Abs(delta.y) > new Vector2(delta.x, delta.z).magnitude * 0.5f)
                return delta.y > 0 ? "Up" : "Down";
            var forward = Vector3.ProjectOnPlane(p.transform.forward, Vector3.up).normalized;
            var flat = Vector3.ProjectOnPlane(delta, Vector3.up).normalized;
            var angle = Vector3.SignedAngle(forward, flat, Vector3.up);
            return Mathf.Abs(angle) < 45f ? "Ahead" : Mathf.Abs(angle) > 135f ? "Behind" : angle > 0 ? "Right" : "Left";
        }

        private static string Turn(Vector3 from, Vector3 to)
        {
            if (Mathf.Abs(to.y) > 2.5f)
                return to.y > 0 ? "Nav/Up" : "Nav/Down";
            var angle = Vector3.SignedAngle(Vector3.ProjectOnPlane(from, Vector3.up), Vector3.ProjectOnPlane(to, Vector3.up), Vector3.up);
            return Mathf.Abs(angle) < 35f ? null : angle > 0 ? "Nav/Right" : "Nav/Left";
        }

        // Подсказка на шаг: «Поверните налево», «Поднимитесь наверх», «Пройдите две комнаты вперёд»,
        // далеко прямо — «Идите вперёд, пока я не скажу стоп»
        private string StepTopic(string dir, float dist)
        {
            switch (dir)
            {
                case "Left":
                case "Right":
                    return FindSet("Nav/" + dir) != null ? "Nav/" + dir : null;
                case "Up":
                case "Down":
                    return FindSet("Nav/" + dir) != null ? "Nav/" + dir : null;
            }
            var way = dir == "Behind" ? "Nav/Back/" : "Nav/Forward/";
            if (dir == "Ahead" && dist > 25f && FindSet("Nav/Go") != null && Random.value < 0.5f)
                return "Nav/Go";
            var size = dist < 6f ? "Short" : dist < 14f ? "1" : dist < 24f ? "2" : dist < 34f ? "3" : "Many";
            return FindSet(way + size) != null ? way + size : FindSet(way + "Many") != null ? way + "Many" : null;
        }

        // ---- Новые события ----

        /// <summary>Сколько секунд колонка должна пролежать одна вне корабля, чтобы звать на помощь.</summary>
        public static float AbandonedSeconds = 60f;
        private float aloneSince = -1f, insideSince = -1f, lostOffered;
        private bool wasDoorsClosed, wasFiring, wasCompany, wasInside, fullChargeSeen = true, wasHalf = true, hiveTold;
        private float lastCharge = -1f;
        private int scannedSeen = -1;
        private readonly HashSet<ulong> fallSeen = new HashSet<ulong>(), teleportSeen = new HashSet<ulong>(), weirdSeen = new HashSet<ulong>();
        private readonly Dictionary<ulong, (int flips, float since, bool crouch)> weirdness = new Dictionary<ulong, (int, float, bool)>();
        private readonly Dictionary<ulong, int> lastHealth = new Dictionary<ulong, int>();
        private readonly HashSet<ulong> minesSeen = new HashSet<ulong>(), turretsSeen = new HashSet<ulong>(), steamSeen = new HashSet<ulong>(),
            exploded = new HashSet<ulong>(), behindSeen = new HashSet<ulong>();
        private readonly Dictionary<ulong, int> shells = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, ulong> holding = new Dictionary<ulong, ulong>();
        private float nextFacilityScan;
        private bool wasHeldForDrop, wasScanning;
        private Vector3 heldAt;

        private void CollectMoreReasons(StartOfRound round)
        {
            var pos = transform.position;
            var players = round.allPlayerScripts.Where(p => p != null && p.isPlayerControlled && !p.isPlayerDead).ToList();

            // Двери корабля закрыли, а снаружи живые — «Эй, там же ещё люди!»
            if (round.hangarDoorsClosed && !wasDoorsClosed && round.shipHasLanded && !round.shipIsLeaving
                && players.Any(p => !p.isInHangarShipRoom && !p.isInElevator))
                Reason("DoorsClosedOutside");
            wasDoorsClosed = round.hangarDoorsClosed;

            // Увольнение, Компания, затмение
            if (round.firingPlayersCutsceneRunning && !wasFiring)
                Reason("Fired");
            wasFiring = round.firingPlayersCutsceneRunning;
            var company = round.shipHasLanded && round.currentLevel != null && round.currentLevel.levelID == 3;
            if (company && !wasCompany)
                Reason("Company");
            wasCompany = company;
            if (round.shipHasLanded && !wasLanded && WeatherName() == "Eclipsed")
                Reason("Eclipse");

            // Улетаем, а тела остались на луне — «Как вы можете бросить его там?»
            if (round.shipIsLeaving && !wasLeaving)
            {
                var left = round.allPlayerScripts.Count(p => p != null && p.isPlayerDead && p.deadBody != null && !p.deadBody.isInShip && !p.deadBody.deactivated);
                if (left > 0)
                    Reason("LeftBody/" + (left > 1 ? "Many" : "One"));
                // И сама колонка осталась снаружи — «Заберите меня!»
                if (!isInShipRoom && !isInElevator && !isHeld)
                    Reason("Abandoned");
            }

            foreach (var p in players)
            {
                var id = p.playerClientId;
                var close = Vector3.Distance(p.transform.position, pos) < EventRadius;

                // Упал с высоты — «Ай, осторожнее!»
                if (p.takingFallDamage && close)
                {
                    if (fallSeen.Add(id))
                        Reason("Fall");
                }
                else if (!p.takingFallDamage)
                    fallSeen.Remove(id);

                // Телепорт вернул на корабль — «Ой! Вы откуда?»
                if (p.teleportedLastFrame && close)
                {
                    if (teleportSeen.Add(id))
                        Reason("Teleported");
                }
                else
                    teleportSeen.Remove(id);

                // Кто-то рядом то садится, то встаёт — «Вы что делаете?»
                if (close)
                {
                    weirdness.TryGetValue(id, out var w);
                    if (Time.time - w.since > 4f)
                        w = (0, Time.time, p.isCrouching);
                    if (p.isCrouching != w.crouch)
                        w = (w.flips + 1, w.since, p.isCrouching);
                    weirdness[id] = w;
                    if (w.flips >= 6)
                    {
                        if (weirdSeen.Add(id))
                            Reason("Weird");
                    }
                    else if (w.flips == 0)
                        weirdSeen.Remove(id);
                }

                // Ударили на корабле, а монстров рядом нет — «Не деритесь на корабле!»
                if (lastHealth.TryGetValue(id, out var hp) && p.health < hp && close && p.isInHangarShipRoom && !p.takingFallDamage
                    && !EnemyNear(p.transform.position, 10f)
                    && players.Any(o => o != p && Vector3.Distance(o.transform.position, p.transform.position) < 3f))
                    Reason("Fight");
                lastHealth[id] = p.health;

                // Взял в руки ружьё или бутылку
                var held = p.currentlyHeldObjectServer;
                var heldId = held != null ? held.NetworkObjectId : 0UL;
                if (close && held != null && (!holding.TryGetValue(id, out var was) || was != heldId))
                {
                    if (held is ShotgunItem)
                    {
                        Reason("Hold/Shotgun");
                        Reason("Hold/Gun");
                    }
                    else if (held is ToyGunItem)
                    {
                        Reason("Hold/Toy gun");
                        Reason("Hold/Gun");
                    }
                }
                holding[id] = heldId;
            }

            // Сканирует рядом (видно только свой скан: у хоста) — «Что вы ищете?»
            var me = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            var scanning = HUDManager.Instance != null && HUDManager.Instance.playerPingingScan > 0f && me != null
                           && Vector3.Distance(me.transform.position, pos) < EventRadius;
            if (scanning && !wasScanning)
                Reason("Scan");
            wasScanning = scanning;

            // Колонку уронили с высоты — «Ой, не бросайте меня так сильно!»
            if (isHeld)
            {
                wasHeldForDrop = true;
                heldAt = transform.position;
            }
            else if (wasHeldForDrop)
            {
                wasHeldForDrop = false;
                if (heldAt.y - transform.position.y > 1f || heldAt.y - targetFloorPosition.y > 2f && targetFloorPosition != Vector3.zero)
                    Reason("Dropped");
            }

            // Батарея: половина и подзарядили
            if (insertedBattery != null)
            {
                var charge = insertedBattery.charge;
                if (charge < 0.5f && wasHalf && IsOn)
                    Reason("HalfBattery");
                wasHalf = charge >= 0.5f;
                if (lastCharge >= 0f && charge - lastCharge > 0.15f)
                    Reason("Charged");
                lastCharge = charge;
            }

            // Брошенная колонка: лежит вне корабля, никого рядом — «Помогите! Я тут совсем одна!»
            var alone = round.shipHasLanded && !round.inShipPhase && !isHeld && !isInShipRoom && !isInElevator
                        && !players.Any(p => Vector3.Distance(p.transform.position, pos) < 25f);
            if (!alone)
                aloneSince = -1f;
            else if (aloneSince < 0f)
                aloneSince = Time.time;
            else if (Time.time - aloneSince > AbandonedSeconds)
            {
                Reason("Abandoned");
                aloneSince = Time.time;
            }

            // Внутри комплекса с игроком: темно, фонарик, странные звуки, «Вы заблудились?»
            var inside = isInFactory || isHeld && playerHeldBy != null && playerHeldBy.isInsideFactory;
            var holder = isHeld ? playerHeldBy : players.Where(p => p.isInsideFactory).OrderBy(p => Vector3.Distance(p.transform.position, pos)).FirstOrDefault();
            if (inside && !wasInside && holder != null)
            {
                var light = holder.ItemSlots != null && holder.ItemSlots.Any(i => i is FlashlightItem);
                Reason(light ? "Inside/Flashlight" : "Inside/NoFlashlight");
                Reason("Inside/Dark");
                insideSince = Time.time;
            }
            wasInside = inside;
            if (inside)
            {
                Reason("Inside");
                if (holder != null && navTarget == null && Time.time - insideSince > 240f && Time.time - lostOffered > 300f
                    && FindSet("Nav/Lost") != null && !players.Any(o => o != holder && Vector3.Distance(o.transform.position, holder.transform.position) < 20f))
                {
                    lostOffered = Time.time;
                    reasons.Add(NavLostReason);
                    lostPlayer = holder;
                }
            }

            // Монстр за спиной у того, кто держит колонку — «Обернитесь!»
            if (holder != null && RoundManager.Instance != null)
            {
                foreach (var enemy in RoundManager.Instance.SpawnedEnemies)
                {
                    if (enemy == null || enemy.isEnemyDead)
                        continue;
                    var to = enemy.transform.position - holder.transform.position;
                    var behind = to.magnitude < 8f && Vector3.Dot(holder.transform.forward, to.normalized) < -0.5f;
                    if (behind)
                    {
                        if (behindSeen.Add(enemy.NetworkObjectId))
                            Reason("Behind");
                    }
                    else
                        behindSeen.Remove(enemy.NetworkObjectId);
                }
            }

            // Улей на корабле — раз за день
            if (round.inShipPhase)
                hiveTold = false;

            if (Time.time < nextFacilityScan)
                return;
            nextFacilityScan = Time.time + 3f;
            CheckScanned();

            // Мины, турели, пар рядом; мина взорвалась
            foreach (var mine in FindObjectsOfType<Landmine>())
            {
                if (mine == null)
                    continue;
                var id = mine.NetworkObjectId;
                var d = Vector3.Distance(mine.transform.position, pos);
                if (mine.hasExploded)
                {
                    if (exploded.Add(id) && d < 30f)
                        Reason("Mine/Pressed");
                    continue;
                }
                if (d < 8f && mine.mineActivated)
                {
                    if (minesSeen.Add(id))
                        Reason("Mine");
                }
                else if (d > 15f)
                    minesSeen.Remove(id);
            }
            foreach (var turret in FindObjectsOfType<Turret>())
            {
                if (turret == null)
                    continue;
                var d = Vector3.Distance(turret.transform.position, pos);
                if (d < 12f && turret.turretActive)
                {
                    if (turretsSeen.Add(turret.NetworkObjectId))
                        Reason("Turret");
                }
                else if (d > 20f)
                    turretsSeen.Remove(turret.NetworkObjectId);
            }
            foreach (var valve in FindObjectsOfType<SteamValveHazard>())
            {
                if (valve == null)
                    continue;
                var id = (ulong)valve.GetInstanceID();
                if (valve.valveHasBurst && !valve.valveHasBeenRepaired && Vector3.Distance(valve.transform.position, pos) < 15f)
                {
                    if (steamSeen.Add(id))
                        Reason("Steam");
                }
                else
                    steamSeen.Remove(id);
            }
            // Выстрел из дробовика рядом — «Не попадите в глаз!»
            foreach (var gun in FindObjectsOfType<ShotgunItem>())
            {
                if (gun == null)
                    continue;
                var id = gun.NetworkObjectId;
                if (shells.TryGetValue(id, out var had) && gun.shellsLoaded < had && Vector3.Distance(gun.transform.position, pos) < EventRadius)
                    Reason("Shot");
                shells[id] = gun.shellsLoaded;
            }
            if (!hiveTold && round.shipHasLanded)
            {
                foreach (var item in FindObjectsOfType<GrabbableObject>())
                {
                    if (item != null && item.isInShipRoom && item.itemProperties != null && item.itemProperties.itemName == "Hive")
                    {
                        hiveTold = true;
                        Reason("HiveOnShip");
                        break;
                    }
                }
            }
        }

        private const string NavLostReason = EventsFolder + "#lost";
        private PlayerControllerB lostPlayer;

        // Сказать повод не просто записью, а с продолжением; true — сказано здесь
        private bool SayEventMore(string topic)
        {
            if (topic == NavLostReason)
            {
                // «Вы заблудились? Я могу помочь» — ответят «да», ведёт к выходу
                Ask(AskLost, null, "Nav/Lost");
                return true;
            }
            if (topic == EventsFolder + "Flashlight" && NoFlashlights() && Buy != null && FindSet("OutOf/Intro") != null && FindSet("OutOf/Flashlight") != null)
            {
                // «У вас кончились фонарики. Хотите, закажу?»
                var topics = new List<string> { "OutOf/Intro", "OutOf/Flashlight" };
                SaySequence(topics);
                if (FindSet("OutOf/Offer") != null)
                {
                    asking = AskOrder;
                    askingItem = "Flashlight";
                    pendingOffer = true;
                    askUntil = float.MaxValue; // ждём, пока доиграет «…фонарики»
                }
                return true;
            }
            if (topic == EventsFolder + "Teamkill")
            {
                SayFrom(FindSet(topic));
                ExpectReply(topic);
                return true;
            }
            return false;
        }

        private const string AskLost = "lost";
        private bool pendingOffer;

        private static bool NoFlashlights()
        {
            foreach (var item in FindObjectsOfType<FlashlightItem>())
            {
                if (item != null && (item.isInShipRoom || item.isHeld))
                    return false;
            }
            return true;
        }

        // «Хотите, закажу?» звучит после «…фонарики», когда склейка доиграла
        private void UpdateOffer()
        {
            if (!pendingOffer || IsTalking)
                return;
            pendingOffer = false;
            if (asking != AskOrder)
                return;
            Say("OutOf/Offer");
            askStartedAt = Mathf.Max(Time.time, talkUntil);
            askUntil = askStartedAt + ListenSilence;
        }

        // Ответ на «Вы заблудились?»
        private bool OnReplyLost(string kind, string intent)
        {
            if (kind != AskLost)
                return false;
            if (intent == TopicYes && lostPlayer != null)
                StartNavigation(lostPlayer, false);
            else if (intent == TopicNo)
                Say("Ok");
            return true;
        }

        /// <summary>
        /// «Как прошла встреча с брекеном?» (Events/Met/&lt;папка&gt;): монстра впервые отсканировали
        /// или он кого-то убил. Жуки и мухи — одна папка Bug.
        /// </summary>
        private static string MetFolder(string enemyName)
        {
            var key = (enemyName ?? "").Replace(" ", "").ToLowerInvariant();
            if (key.Contains("flowerman") || key.Contains("bracken"))
                return "Flowerman";
            if (key.Contains("spring"))
                return "Spring";
            if (key.Contains("hoard") || key.Contains("centipede") || key.Contains("bees") || key.Contains("bug") || key.Contains("butterfly")
                || key.Contains("spider") || key.Contains("fly"))
                return "Bug";
            return null;
        }

        private void MetReason(string enemyName)
        {
            var folder = MetFolder(enemyName);
            if (folder != null)
                Reason("Met/" + folder);
        }

        // Новый монстр в бестиарии терминала (отсканировали впервые): какой — по номеру сканирования
        private void CheckScanned()
        {
            var shop = Shop();
            if (shop == null || shop.scannedEnemyIDs == null)
                return;
            var count = shop.scannedEnemyIDs.Count;
            if (scannedSeen >= 0 && count > scannedSeen)
            {
                for (var i = scannedSeen; i < count; i++)
                {
                    var scanId = shop.scannedEnemyIDs[i];
                    var enemy = RoundManager.Instance?.SpawnedEnemies.FirstOrDefault(e => e != null && e.enemyType != null
                        && e.GetComponentInChildren<ScanNodeProperties>() is { } node && node.creatureScanID == scanId);
                    if (enemy != null)
                        MetReason(enemy.enemyType.enemyName);
                }
            }
            scannedSeen = count;
        }

        // Убили своего: смерть от удара или выстрела, рядом другой игрок, монстров нет
        private bool IsTeamkill(PlayerControllerB p)
        {
            var round = StartOfRound.Instance;
            if (p.causeOfDeath != CauseOfDeath.Bludgeoning && p.causeOfDeath != CauseOfDeath.Gunshots && p.causeOfDeath != CauseOfDeath.Stabbing)
                return false;
            var at = p.deadBody != null ? p.deadBody.transform.position : p.placeOfDeath;
            return !EnemyNear(at, 8f) && round.allPlayerScripts.Any(o => o != null && o != p && o.isPlayerControlled && !o.isPlayerDead
                                                                           && Vector3.Distance(o.transform.position, at) < 6f);
        }

        // ---- «Алиса, Алиса, Алиса!» — «Вы чего кричите?» ----

        private static float shoutAt;
        private static int shoutCount;

        /// <summary>Позвали несколько раз подряд (или трижды в одной фразе): true — ответила «Вы чего кричите?».</summary>
        private static bool Shouted(YandexStationItem station, string text)
        {
            if (station.FindSet("Shout") == null)
                return false;
            var times = 0;
            for (var i = text.IndexOf(WakeWord, StringComparison.Ordinal); i >= 0; i = text.IndexOf(WakeWord, i + 1, StringComparison.Ordinal))
                times++;
            shoutCount = Time.time - shoutAt < 6f ? shoutCount + times : times;
            shoutAt = Time.time;
            if (shoutCount < 3)
                return false;
            shoutCount = 0;
            station.Say("Shout");
            return true;
        }
    }
}
