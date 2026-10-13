using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;
using Random = UnityEngine.Random;

namespace CustomItems.Items
{
    /// <summary>
    /// Разговор: встреча на корабле по имени, «Это вы мне?», «где я», какие вопросы можно повторять.
    /// Список всех диалогов с ветками — /mnt/project-files/yandex/alice-dialogues.md (в репозитории README).
    /// </summary>
    public partial class YandexStationItem
    {
        private const string TopicWhereAmI = "#whereami", TopicNotYou = "#notyou", TopicDunno = "#dunno", TopicHere = "#here",
            TopicHurt = "#hurt", TopicEarned = "#earned", TopicBestEarner = "#bestearner", TopicMoonAbout = "#moonabout",
            TopicCredits = "#credits", TopicHello = "#hello", TopicCallName = "#callname";
        private const string AskWake = "wake", AskOthers = "others", AskMissing = "missing";

        /// <summary>
        /// «Я уже отвечала» — только на вопросы, ответ на которые не меняется. Анекдот, «мяу», «спасибо»,
        /// время, заряд, «кто умер» и прочее можно спрашивать сколько угодно.
        /// </summary>
        private static readonly HashSet<string> RepeatTopics = new HashSet<string>
        {
            "WhoAreYou", "Skills", "FavoriteColor", "Sigurd", "Weather", "Moon", "Deadline", "Quota", "#myname", "#myrole", "#color",
        };

        /// <summary>Как часто колонка после «Алиса» и тишины спрашивает «Это вы мне?» вместо сигнала.</summary>
        public static float WakeAskChance = 0.5f;
        private float lastWakeAskAt = -100f;

        // ---- «Алиса» … тишина … «Это вы мне?» ----

        // Позвали и молчат: иногда «Это вы мне?» и ждёт «да» / «нет, не тебе»; true — спросила
        private bool AskIfMe()
        {
            if (FindSet("Wake/Ask") == null || Random.value > WakeAskChance || Time.time < lastWakeAskAt + 30f)
                return false;
            lastWakeAskAt = Time.time;
            Ask(AskWake, null, "Wake/Ask");
            return true;
        }

        /// <summary>
        /// Ответ на «Это вы мне?»: «нет», «не тебе» — «Поняла»; «да» — «Слушаю»; вопрос — true, его надо разобрать как обычный.
        /// </summary>
        private bool OnWakeReply(string reply)
        {
            asking = askingItem = null;
            var intent = string.IsNullOrEmpty(reply) ? null : AliceIntents.Match(reply, out _);
            if (intent == TopicNo || intent == TopicNotYou)
            {
                SayFirst("Wake/NotMe", "Ok");
                return false;
            }
            if (intent == TopicYes || intent == TopicHere)
            {
                SayFirst(TopicListenVoice, SoundListen);
                listenUntil = Mathf.Max(Time.time, talkUntil) + ListenSilence;
                listenStartedAt = Time.time;
                return false;
            }
            return true;
        }

        private const string TopicMistake = "#mistake";

        // Извиняется сама («Простите», «Извините», «Не хотела»), а не прощает
        private static bool IsOwnApology(string clip)
        {
            var lower = clip.ToLowerInvariant();
            return lower.Contains("прост") || lower.Contains("извин") || lower.Contains("не хотела");
        }

        /// <summary>Темы разговора. false — тема не отсюда.</summary>
        private bool AnswerTalk(PlayerControllerB speaker, string topic, string known)
        {
            switch (topic)
            {
                case TopicNotYou:
                    SayFirst("Wake/NotMe", "Ok");
                    return true;
                case TopicMistake:
                    // «Ты заказала не то» — «Простите, я ошиблась», а не «Ладно, прощаю» из той же папки
                    var sorry = FindSet("Sorry");
                    var index = sorry != null ? PickIndex(sorry, IsOwnApology) : -1;
                    if (index < 0)
                    {
                        SayFirst("Sorry", TopicUnknown);
                        return true;
                    }
                    askedLocally = true;
                    Speak(sorry.name, index);
                    SendSpeak(sorry.name, index);
                    return true;
                case TopicHere:
                    // «Ты тут?» — «Слушаю» / «Если что, я здесь, слушаю и молчу»
                    SayAny("Here", TopicListenVoice);
                    listenUntil = Mathf.Max(Time.time, talkUntil) + ListenSilence;
                    listenStartedAt = Time.time;
                    return true;
                case TopicDunno:
                    SayFirst("Ok");
                    return true;
                case TopicWhereAmI:
                    SayWhereAmI(speaker);
                    return true;
                case "Insult":
                    lastInsultAt = Time.time; // «Ты обиделась?» — «Немножко»
                    return false;
                case TopicHurt:
                    SayFirst(Time.time - lastInsultAt < 180f ? "Hurt/Yes" : "Hurt/No", "Sorry", "Ok");
                    return true;
                case TopicEarned:
                    SayEarned(speaker);
                    return true;
                case TopicBestEarner:
                    SayBestEarner();
                    return true;
                case TopicMoonAbout:
                    SayMoonAbout();
                    return true;
                case TopicCredits:
                    SayCredits();
                    return true;
                case TopicHello:
                case TopicCallName:
                    SayToFriend(known, topic == TopicHello);
                    return true;
            }
            return false;
        }

        private float lastInsultAt = -1000f;

        // ---- Кто сколько принёс ----

        // Последний, кто держал предмет; кредиты за лут, занесённый на корабль за этот день
        private readonly Dictionary<GrabbableObject, PlayerControllerB> lastHolder = new Dictionary<GrabbableObject, PlayerControllerB>();
        private readonly HashSet<GrabbableObject> countedLoot = new HashSet<GrabbableObject>();
        private readonly Dictionary<ulong, int> earned = new Dictionary<ulong, int>();
        private bool lootSeeded, wasLandedForLoot;

        private void CollectLoot(StartOfRound round)
        {
            var landed = round.shipHasLanded && !round.inShipPhase;
            if (landed && !wasLandedForLoot)
            {
                earned.Clear(); // новый день — считаем заново
                lootSeeded = false;
            }
            wasLandedForLoot = landed;
            foreach (var item in FindObjectsOfType<GrabbableObject>())
            {
                if (item == null || item.itemProperties == null || !item.itemProperties.isScrap || item.scrapValue <= 0)
                    continue;
                if (item.playerHeldBy != null)
                    lastHolder[item] = item.playerHeldBy;
                if (!item.isInShipRoom || !countedLoot.Add(item))
                    continue;
                // Что уже лежало на корабле до посадки — не считаем
                if (!lootSeeded || !landed || !lastHolder.TryGetValue(item, out var who) || who == null)
                    continue;
                earned.TryGetValue(who.playerClientId, out var sum);
                earned[who.playerClientId] = sum + item.scrapValue;
            }
            lootSeeded = true;
            lastHolder.Keys.Where(k => k == null).ToList().ForEach(k => lastHolder.Remove(k));
            countedLoot.RemoveWhere(k => k == null);
        }

        // «Сколько я принёс?» — «Вы принесли на» + сумма; ничего — «Вы пока ничего не принесли»; больше всех — «Вы молодец!»
        private void SayEarned(PlayerControllerB speaker)
        {
            earned.TryGetValue(speaker.playerClientId, out var sum);
            // Нет «Вы принесли на» — по имени кусками: «Паша…» + «…принёс на…» / «…пока ничего не принёс.»
            var me = NamePiece(NameFolder(speaker.playerUsername));
            var glued = FindSet("Earned/Intro") == null && me != null;
            if (sum <= 0)
            {
                if (FindSet("Earned/None") == null && me != null && FindSet("Earned/Tail/None") != null)
                    SaySequence(new List<string> { me, "Earned/Tail/None" });
                else
                    SayFirst("Earned/None", "Loot/Low", TopicUnknown);
                return;
            }
            var topics = glued ? Existing(me, "Earned/Tail/Intro") : Existing("Earned/Intro");
            if (glued && topics.Count < 2)
                topics.Clear();
            if (topics.Count == 0 || !AddNumber(topics, sum))
            {
                SayFirst("Loot/High", "Ok");
                return;
            }
            AddUnit(topics, "Credits"); // «…кредитов.», если записан кусок
            if (earned.Count > 1 && earned.OrderByDescending(e => e.Value).First().Key == speaker.playerClientId && FindSet("Earned/Best") != null)
                topics.Add("Earned/Best");
            SaySequence(topics);
        }

        // «Кто больше всех принёс?» — «Больше всех принёс» + имя
        private void SayBestEarner()
        {
            var round = StartOfRound.Instance;
            var best = earned.Where(e => e.Value > 0).OrderByDescending(e => e.Value).Select(e => e.Key).FirstOrDefault();
            var player = round?.allPlayerScripts.FirstOrDefault(p => p != null && p.playerClientId == best && earned.ContainsKey(best));
            var name = player != null ? NameFolder(player.playerUsername) : null;
            if (name == null || FindSet(TopicCall + name) == null)
            {
                SayFirst("Earned/Nobody", "Loot/Low", TopicUnknown);
                return;
            }
            // Нет «Больше всех принёс» — «Паша…» + «…сегодня лучше всех!»
            if (FindSet("Earned/BestIs") == null && NamePiece(name) != null && FindSet("Opinion/Tail/Best") != null)
                SaySequence(new List<string> { NamePiece(name), "Opinion/Tail/Best" });
            else
                SaySequence(Existing("Earned/BestIs", TopicCall + name));
        }

        // «Сколько у нас денег?» — «На счету» + число кредитов
        private void SayCredits()
        {
            var terminal = FindObjectOfType<Terminal>();
            var topics = Existing("Credits/Intro");
            // Нет «На счету» — просто число и «…кредитов.»
            var bare = topics.Count == 0 && FindSet(Glue + "Unit/Credits") != null;
            if (terminal == null || topics.Count == 0 && !bare || !AddNumber(topics, terminal.groupCredits))
            {
                SayFirst(terminal != null && terminal.groupCredits < 50 ? "Buy/NoMoney" : "Credits", TopicUnknown);
                return;
            }
            AddUnit(topics, "Credits");
            SaySequence(topics);
        }

        // «Расскажи про луну» — Moon/About/<Луна> (название как в терминале без номера: Experimentation)
        private void SayMoonAbout()
        {
            var level = StartOfRound.Instance?.currentLevel;
            var name = level != null ? level.PlanetName ?? "" : "";
            var space = name.IndexOf(' ');
            if (space >= 0 && name.Take(space).All(char.IsDigit))
                name = name.Substring(space + 1);
            name = name.Replace(" ", "");
            SayFirst("Moon/About/" + name, "Moon/About", TopicUnknown);
        }

        // «Скажи привет Паше» — «Паша! Привет»; «позови Пашу» — «Паша!» погромче
        private void SayToFriend(string command, bool hello)
        {
            var name = FriendNamed(command);
            if (name == null || FindSet(TopicCall + name) == null)
            {
                SayFirst("Names/Unknown", TopicUnknown);
                return;
            }
            var topics = new List<string> { TopicCall + name };
            if (hello)
                topics.AddRange(Existing("Hello", "Greeting").Take(1));
            else
            {
                noiseRange = EventNoiseRange * 2f; // зовёт громко — слышно дальше
                topics.AddRange(Existing("CallName").Take(1));
            }
            SaySequence(topics);
        }

        // ---- Поминки ----

        private readonly Dictionary<ulong, float> diedAt = new Dictionary<ulong, float>();
        private readonly HashSet<ulong> mourned = new HashSet<ulong>();

        // Через минуту после смерти иногда «Помянем Андрея» (Events/Memorial/<Имя> или Events/Memorial)
        private void CollectMemorial(StartOfRound round)
        {
            foreach (var p in round.allPlayerScripts)
            {
                if (p == null)
                    continue;
                var id = p.playerClientId;
                if (!p.isPlayerDead)
                {
                    diedAt.Remove(id);
                    mourned.Remove(id);
                    continue;
                }
                if (!diedAt.ContainsKey(id))
                    diedAt[id] = Time.time;
                if (mourned.Contains(id) || Time.time - diedAt[id] < 60f)
                    continue;
                mourned.Add(id);
                var name = NameFolder(p.playerUsername);
                if (name != null)
                    Reason("Memorial/" + name);
                Reason("Memorial");
            }
        }

        // «Где я?»: на корабле, в комплексе, снаружи. Вне корабля без своей записи — «Вы заблудились?» и ведёт
        private void SayWhereAmI(PlayerControllerB speaker)
        {
            if (speaker.isInHangarShipRoom || speaker.isInElevator)
            {
                SayFirst("WhereIs/Me/Ship", "Ok");
                return;
            }
            var mine = speaker.isInsideFactory ? "WhereIs/Me/Inside" : "WhereIs/Me/Outside";
            if (FindSet(mine) != null && Random.value < 0.6f)
            {
                Say(mine);
                return;
            }
            lostPlayer = speaker;
            Ask(AskLost, null, FindSet("Nav/Lost") != null ? "Nav/Lost" : mine);
        }

        // Торопят: «быстрее», «срочно», ругаются
        private static readonly string[] HurryStems =
        {
            "быстр", "срочн", "живо", "немедлен", "сейчас", "скорее", "сука", "суки", "епт", "ёпт", "черт", "чёрт", "блин", "нахуй", "нахер",
        };
        // Команду на взлёт повторили в одной фразе: «взлетай взлетай», «погнали погнали»
        private static readonly string[] LaunchStems = { "взлет", "взлёт", "погнал", "полетел", "поехал", "запуск", "запуст", "улета", "лет", "сади", "приземл", "дёрн", "дерн", "валим", "гони" };

        private static bool Hurried(string command)
        {
            var words = (command ?? "").ToLowerInvariant().Split(new[] { ' ', ',', '!', '.' }, System.StringSplitOptions.RemoveEmptyEntries);
            // «Взлетай взлетай!», а ещё мат или «быстрее» рядом со взлётом: «взлетаем, блядь» — без «Вы уверены?»
            var launch = words.Count(w => LaunchStems.Any(w.StartsWith));
            return launch >= 2 || launch >= 1 && words.Any(w => HurryStems.Any(w.StartsWith) || w.StartsWith("бля") || w.Contains("хуй")
                                                                  || w.Contains("пизд") || w.Contains("ёб") || w.StartsWith("еба") || w.StartsWith("заеб"));
        }

        // ---- Встреча на корабле ----

        /// <summary>Сколько секунд надо пробыть вне корабля, чтобы на возвращение колонка сказала «Андрей! Вот и вы».</summary>
        public static float AwaySeconds = 20f;
        /// <summary>Вошедших с разницей не больше стольких секунд встречает вместе («Андрей и Паша!»).</summary>
        public static float ArriveGroupSeconds = 3f;

        private readonly Dictionary<ulong, float> awaySince = new Dictionary<ulong, float>();
        private readonly List<PlayerControllerB> arrivals = new List<PlayerControllerB>();
        private float firstArrivalAt;

        // Кто зашёл на корабль после прогулки (только на луне, колонка сама на корабле)
        private void CollectArrivals(StartOfRound round)
        {
            CollectLoot(round);
            CollectMemorial(round);
            var landed = round.shipHasLanded && !round.shipIsLeaving && !round.inShipPhase;
            foreach (var p in round.allPlayerScripts)
            {
                if (p == null)
                    continue;
                var id = p.playerClientId;
                if (!landed || !p.isPlayerControlled || p.isPlayerDead)
                {
                    awaySince.Remove(id);
                    continue;
                }
                if (!p.isInHangarShipRoom)
                {
                    if (!awaySince.ContainsKey(id))
                        awaySince[id] = Time.time;
                    continue;
                }
                if (!awaySince.TryGetValue(id, out var since))
                    continue;
                awaySince.Remove(id);
                if (Time.time - since < AwaySeconds || !isInShipRoom && !isInElevator || arrivals.Contains(p))
                    continue;
                if (arrivals.Count == 0)
                    firstArrivalAt = Time.time;
                arrivals.Add(p);
            }
            if (!landed)
                arrivals.Clear();
        }

        // Встречает, когда группа вошедших собралась и колонка свободна (решает хост)
        private void UpdateArrivals()
        {
            if (arrivals.Count == 0 || !IsServer || Time.time < firstArrivalAt + ArriveGroupSeconds)
                return;
            if (!IsOn || IsJammed || isPocketed || IsTalking || asking != null || pendingAnswer != null || listenUntil > 0f
                || Time.time < thinkUntil || navTarget != null)
            {
                if (Time.time > firstArrivalAt + 30f)
                    arrivals.Clear(); // давно зашли — поздно здороваться
                return;
            }
            var round = StartOfRound.Instance;
            var came = arrivals.Where(p => p != null && p.isPlayerControlled && !p.isPlayerDead).ToList();
            arrivals.Clear();
            if (round == null || came.Count == 0)
                return;

            var names = came.Select(p => NameFolder(p.playerUsername)).Where(f => f != null && FindSet(TopicCall + f) != null)
                .Distinct().ToList();
            var outside = round.allPlayerScripts.Count(o => o != null && !came.Contains(o) && o.isPlayerControlled && !o.isPlayerDead && !o.isInHangarShipRoom);
            var topics = new List<string>();
            var ask = false;
            if (outside > 0)
            {
                // Кто-то ещё гуляет: «О, Андрей! А остальные где?» или «Андрей и Паша! А остальные где?»
                if (names.Count == 1 && FindSet(EventsFolder + "FirstBack/" + names[0]) != null && FindSet("Back/Others") == null)
                {
                    topics.Add(EventsFolder + "FirstBack/" + names[0]);
                    ask = true;
                }
                else
                {
                    AddList(topics, names.Select(n => TopicCall + n).ToList());
                    if (FindSet("Back/Others") != null)
                    {
                        topics.Add("Back/Others");
                        ask = true;
                    }
                    else
                        topics.Add(EventsFolder + "Back");
                }
            }
            else
            {
                // Вошёл последний: «Ого, все в сборе, даже Андрей!» или «Андрей! Вот и вы»
                var last = names.Count == 1 ? EventsFolder + "Joined/" + names[0] : null;
                if (last != null && round.allPlayerScripts.Count(o => o != null && o.isPlayerControlled) > 2 && FindSet(last) != null && Random.value < 0.4f)
                    topics.Add(last);
                else
                {
                    AddList(topics, names.Select(n => TopicCall + n).ToList());
                    topics.Add(FindSet("Back/AllHere") != null && round.allPlayerScripts.Count(o => o != null && o.isPlayerControlled) > 1 && Random.value < 0.5f
                        ? "Back/AllHere" : EventsFolder + "Back");
                }
            }
            topics = topics.Where(t => FindSet(t) != null).ToList();
            if (topics.Count == 0)
                return;
            eventFreeAt = Mathf.Max(eventFreeAt, Time.time + EventCooldown);
            noiseRange = EventNoiseRange;
            SaySequence(topics);
            if (ask)
                AskAfterSpeech(AskOthers);
        }

        // Ждёт ответ на то, что сейчас договаривает (склейку или событие)
        private void AskAfterSpeech(string kind)
        {
            asking = kind;
            askingItem = null;
            askStartedAt = Mathf.Max(Time.time, talkUntil);
            askUntil = askStartedAt + AnswerSeconds;
        }

        /// <summary>
        /// «А остальные где?»: «умерли» — проверяет, кто погиб; «не знаю» — сама говорит, где они;
        /// «в комплексе», «скоро будут» — «Понятно». «Сегодня без Андрея?» — на любой ответ «Понятно».
        /// </summary>
        private bool OnReplyTalk(string kind, string reply, string intent)
        {
            switch (kind)
            {
                case AskOthers:
                    if (string.IsNullOrEmpty(reply))
                        return true;
                    if (intent == TopicWhoDied)
                        SayWhoDied(null, "все");
                    else if (intent == TopicDunno || intent == TopicWhereIs)
                        Say(WhereIsTopic(null, "где остальные"));
                    else
                        SayFirst("Ok");
                    return true;
                case AskMissing:
                    if (!string.IsNullOrEmpty(reply))
                        SayFirst("Ok");
                    return true;
                case AskWake:
                    return true; // разобран в OnSpeech; молчат — ничего
            }
            return false;
        }
    }
}
