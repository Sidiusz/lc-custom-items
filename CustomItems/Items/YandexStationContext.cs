using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;
using Random = UnityEngine.Random;

namespace CustomItems.Items
{
    /// <summary>
    /// Память разговора и контекст. Колонка помнит последний вопрос (тему, о ком он, кто спросил),
    /// недавние события и итоги прошлого дня, поэтому понимает короткие переспросы без полного вопроса
    /// («а Паша?», «а остальные?», «а ты?», «ещё», «почему?», «точно?», «а сейчас?», «а вчера?»),
    /// а сразу после ответа слышит их даже без «Алиса». Ответы зависят от того, кто спрашивает
    /// (папки &lt;Тема&gt;/&lt;Имя&gt;), от времени суток (&lt;Тема&gt;/Morning|Evening|Night|Orbit), от настроения дня
    /// (&lt;Тема&gt;/Sad, &lt;Тема&gt;/Happy) и от того, сколько раз уже спрашивали (&lt;Тема&gt;/Again).
    /// Здесь же новые вопросы по игре (сколько ещё собрать, сколько до отлёта, кто в комплексе…),
    /// обида на грубость и диалоги, которые колонка начинает сама и ждёт ответ.
    /// Фразы из кусков: Glue/Name/&lt;Имя&gt; («Паша…») + Glue/Place/Inside («…в комплексе.») и т. п.;
    /// нет кусков — звучат целые фразы.
    /// </summary>
    public partial class YandexStationItem
    {
        /// <summary>Сколько секунд после ответа колонка слышит переспрос («а Паша?», «почему?») без «Алиса».</summary>
        public static float FollowSeconds = 6f;
        /// <summary>Сколько секунд помнит, о чём был разговор (для «а он?», «ещё», «точно?»).</summary>
        public static float ContextSeconds = 120f;
        /// <summary>Столько секунд колонка не разговаривает после третьей грубости подряд (пока не извинятся).</summary>
        public static float OffendedSeconds = 90f;
        /// <summary>Шанс начать ответ с обращения по имени (Lead/&lt;Имя&gt;: «Паша, слушай…»).</summary>
        public static float LeadInChance = 0.12f;
        /// <summary>Шанс ответить вариантом под человека, время суток или настроение, если такой записан.</summary>
        public static float FlavorChance = 0.6f;
        /// <summary>Столько стоит лут в руках вернувшегося — «Ого, сколько всего принесли!».</summary>
        public static int RichValue = 150;

        private const string TopicMore = "#more", TopicWhy = "#why", TopicReally = "#really", TopicAndYou = "#andyou",
            TopicAndHim = "#andhim", TopicAndName = "#andname", TopicAndOthers = "#andothers", TopicNow = "#now",
            TopicBefore = "#before", TopicSaidAlready = "#saidalready", TopicApology = "#apology", TopicQuotaLeft = "#quotaleft",
            TopicTimeLeft = "#timeleft", TopicHealth = "#health", TopicWhoInside = "#whoinside", TopicWhoOnShip = "#whoonship",
            TopicNews = "#news", TopicDangers = "#dangers", TopicAboutFriend = "#aboutfriend", TopicEarnedBy = "#earnedby",
            TopicCoin = "#coin", TopicDice = "#dice", TopicChoose = "#choose", TopicBall = "#ball", TopicComing = "#coming",
            TopicSelfWhere = "#selfwhere";
        private const string AskRoute = "route", NotAll = "notall";
        private const string Glue = "Glue/";

        // Переспросы: их колонка слышит и без «Алиса» сразу после ответа
        private static readonly HashSet<string> FollowIntents = new HashSet<string>
        {
            TopicMore, TopicWhy, TopicReally, TopicAndYou, TopicAndHim, TopicAndName, TopicAndOthers, TopicNow, TopicBefore,
            TopicSaidAlready, "Thanks",
        };

        // Вопросы о том, что можно проверить по игре: на «точно?» колонка перепроверяет
        private static readonly HashSet<string> Checkable = new HashSet<string>
        {
            TopicWhereIs, TopicWhoDied, TopicEnemies, TopicCount, TopicWhoInside, TopicWhoOnShip, TopicDangers, TopicLoot,
            TopicScanMoon, TopicQuotaLeft, TopicCredits, TopicHealth,
        };

        // ---- Что помнит ----

        private string ctxTopic, ctxKnown, ctxSaid, ctxSubject;
        private ulong ctxSpeaker = ulong.MaxValue;
        private float ctxAt = -1000f, followUntil;
        private bool keepContext;
        private string leadIn;
        // Кто что спрашивал: тема, кто, когда
        private readonly List<(string topic, ulong who, float at)> askLog = new List<(string, ulong, float)>();
        // Что было: событие («Events/Enemy/…», «died») и о ком
        private readonly List<(string what, string who, float at)> memory = new List<(string, string, float)>();
        private float offendedUntil;
        private readonly List<float> insults = new List<float>();

        // День: кто погиб, итоги прошлого дня
        private readonly List<string> dayDead = new List<string>();
        private int dayDeadCount;
        private readonly HashSet<ulong> ctxDeadIds = new HashSet<ulong>();
        private bool yesterdayKnown;
        private List<string> yesterdayDead = new List<string>();
        private int yesterdayDeadCount, yesterdayBrought;
        private bool ctxWasShipPhase = true, ctxWasLanded;
        private float lastDeathAt = -1000f;

        // Сама начинает разговор: что уже спросила за день
        private bool deadlineAsked, leaveAsked, comfortAsked, orbitAsked, quotaEnoughTold;
        private int quotaSeen = -1;
        private float allOnShipSince = -1f, lonelySince = -1f, orbitSince = -1f, nextQuotaCheck;
        private readonly HashSet<ulong> afkAsked = new HashSet<ulong>();

        private bool Fresh => ctxTopic != null && Time.time - ctxAt < ContextSeconds;

        private static ulong IdOf(PlayerControllerB p) => p != null ? p.playerClientId : ulong.MaxValue;

        // Сколько раз тему спрашивали за последние seconds (who == null — кто угодно)
        private int AskedBy(string topic, PlayerControllerB who, float seconds)
        {
            var id = IdOf(who);
            return askLog.Count(a => a.topic == topic && Time.time - a.at < seconds && (who == null || a.who == id));
        }

        // Запомнить ответ: о чём, о ком, кто спросил. Переспрос («почему?») тему не меняет
        private void Remember(PlayerControllerB speaker, string topic, string known)
        {
            if (topic == null || topic == TopicStop || topic == TopicShush || topic == TopicLouder || topic == TopicQuieter
                || topic == TopicVolume || topic == TopicUnknown || topic == TopicMumble || topic == TopicError)
                return;
            askLog.Add((topic, IdOf(speaker), Time.time));
            if (askLog.Count > 60)
                askLog.RemoveAt(0);
            if (!keepContext)
            {
                ctxTopic = topic;
                ctxKnown = known ?? "";
                ctxSubject = Subject(ctxKnown);
                ctxSpeaker = IdOf(speaker);
                ctxSaid = lastSaidTopic;
            }
            ctxAt = Time.time;
            followUntil = Mathf.Max(Time.time, talkUntil) + FollowSeconds;
        }

        // О ком вопрос: папка друга, «он» или «все»
        private static string Subject(string known)
        {
            var named = FriendNamed(known);
            if (named != null)
                return named;
            var tokens = AliceIntents.Tokens(known);
            if (tokens.Any(t => t == "все" || t == "остальные" || t == "всех"))
                return "все";
            return tokens.Any(t => t == "он" || t == "она" || t == "него" || t == "нее" || t == "ним" || t == "ней") ? "он" : null;
        }

        private void RememberEvent(string what, string who = null)
        {
            memory.Add((what, who, Time.time));
            if (memory.Count > 30)
                memory.RemoveAt(0);
        }

        /// <summary>Переспрос без «Алиса» сразу после ответа: «а Паша?», «почему?», «спасибо».</summary>
        private bool HearsFollowUp(string text) =>
            Time.time < followUntil && asking == null && pendingAnswer == null && IsContextFollowUp(text);

        /// <summary>Похоже ли на короткий переспрос (без учёта времени).</summary>
        private static bool IsContextFollowUp(string text)
        {
            var words = AliceIntents.Tokens(text).Where(t => !t.StartsWith("алис")).ToArray();
            if (words.Length == 0 || words.Length > 5)
                return false;
            if (words.Length == 1 && (words[0] == "точно" || words[0] == "правда"))
                return true;
            var intent = AliceIntents.Match(string.Join(" ", words), out var score);
            return intent != null && score >= 0.85f && FollowIntents.Contains(intent);
        }

        // ---- Ответ с учётом контекста ----

        // Ответ на вопрос: сначала память и новые темы, потом обычные ответы (AnswerCore)
        private static void Answer(YandexStationItem station, PlayerControllerB speaker, string topic, string known)
        {
            if (!station.IsOn)
                return;
            station.keepContext = false;
            station.leadIn = null;
            if (station.AnswerContext(speaker, ref topic, ref known))
            {
                station.eventFreeAt = Mathf.Max(station.eventFreeAt, Time.time + 20f);
                station.leadIn = null;
                station.Remember(speaker, topic, known);
                return;
            }
            if (station.leadIn == null) // «Сейчас проверю…» от «точно?» уже стоит
                station.PrepareLeadIn(speaker, topic);
            AnswerCore(station, speaker, topic, known);
            station.leadIn = null;
            station.Remember(speaker, topic, known);
        }

        /// <summary>true — ответила здесь. Может заменить тему и текст (переспрос) и вернуть false: тогда ответит AnswerCore.</summary>
        private bool AnswerContext(PlayerControllerB speaker, ref string topic, ref string known)
        {
            var words = AliceIntents.Tokens(known);
            // «Точно?» — переспрос, а не «да»; «извини» — извинение, а не «извинись»
            if (topic == "Ok" && words.Length == 1 && (words[0] == "точно" || words[0] == "правда") && Fresh)
                topic = TopicReally;
            if (topic == TopicSorry && words.Any(w => w.StartsWith("извини") || w.StartsWith("прост"))
                && !words.Any(w => w == "скажи" || w == "попроси" || w == "извинись" || w.StartsWith("извиняй")))
                topic = TopicApology;
            // «Сколько будет двадцать пять минус три» — числа любые, по примерам не узнать
            if (topic == TopicUnknown && NumbersIn(words).Count >= 2
                && words.Any(w => new[] { "плюс", "минус", "умнож", "дели", "раздели", "подели", "прибав", "отним" }.Any(w.StartsWith)))
                topic = "Math";

            // Обиделась: молчит или фыркает, пока не извинятся
            if (Time.time < offendedUntil)
            {
                if (topic == TopicStop || topic == TopicShush || topic == TopicLouder || topic == TopicQuieter || topic == TopicVolume)
                    return false;
                keepContext = true;
                if (topic == TopicApology)
                {
                    offendedUntil = 0f;
                    insults.Clear();
                    SayFirst("Offended/Forgive", "Apology/Accept", TopicSorry);
                }
                else if (Random.value < 0.7f)
                    SayFirst("Offended", "Offended/Start", TopicRefuse);
                return true;
            }

            switch (topic)
            {
                case TopicMore:
                case TopicWhy:
                case TopicReally:
                case TopicAndYou:
                case TopicAndHim:
                case TopicAndName:
                case TopicAndOthers:
                case TopicNow:
                case TopicBefore:
                case TopicSaidAlready:
                    return FollowUp(speaker, ref topic, ref known);
                case TopicApology:
                    keepContext = true;
                    SayFirst(Time.time - lastInsultAt < 300f ? "Apology/Accept" : "Apology/ForWhat", TopicSorry, "Ok");
                    return true;
                case TopicInsult:
                    insults.RemoveAll(t => Time.time - t > 300f);
                    insults.Add(Time.time);
                    if (insults.Count >= 3 && FindSet("Offended/Start") != null)
                    {
                        lastInsultAt = Time.time;
                        offendedUntil = Time.time + OffendedSeconds;
                        Say("Offended/Start");
                        return true;
                    }
                    return false;
                case "Thanks":
                    keepContext = true;
                    SayThanks(speaker);
                    return true;
                case "HowAreYou":
                    return SayHowAreYou(speaker);
                case "Scared":
                    if (speaker != null && EnemyNear(speaker.transform.position, 20f) && FindSet("Scared/Danger") != null)
                    {
                        Say("Scared/Danger");
                        return true;
                    }
                    return false;
                case "Advice":
                    return SayAdvice(speaker);
                case "Best":
                    if (earned.Any(e => e.Value > 0) && FindSet("Earned/BestIs") != null && Random.value < 0.4f)
                    {
                        SayBestEarner();
                        return true;
                    }
                    return false;
                case "Math":
                    return SayMath(known);
                case TopicWhereIs:
                    return SayWhereIsGlued(speaker, known);
                case TopicQuotaLeft:
                    SayQuotaLeft(null);
                    return true;
                case TopicTimeLeft:
                    SayTimeLeft();
                    return true;
                case TopicHealth:
                    SayHealth(speaker);
                    return true;
                case TopicWhoInside:
                case TopicWhoOnShip:
                    SayWhoIn(speaker, topic == TopicWhoInside);
                    return true;
                case TopicNews:
                    SayNews();
                    return true;
                case TopicDangers:
                    SayDangers();
                    return true;
                case TopicAboutFriend:
                    SayAboutFriend(known);
                    return true;
                case TopicEarnedBy:
                    SayEarnedBy(speaker, known);
                    return true;
                case TopicSelfWhere:
                    SaySelfWhere();
                    return true;
                case TopicCoin:
                    // Иногда сначала «Подбрасываю!»
                    var coin = Random.value < 0.03f ? "Coin/Edge" : Random.value < 0.5f ? "Coin/Heads" : "Coin/Tails";
                    if (FindSet(coin) != null && FindSet("Coin/Intro") != null && Random.value < 0.4f)
                        SaySequence(new List<string> { "Coin/Intro", coin });
                    else
                        SayFirst(coin, TopicUnknown);
                    return true;
                case TopicDice:
                    var dice = Existing("Dice/Intro");
                    dice.Add(TopicNumbers + Random.Range(1, 7));
                    Speak(dice);
                    return true;
                case TopicChoose:
                    SayChoose(speaker);
                    return true;
                case TopicBall:
                    SayBall();
                    return true;
                case TopicComing:
                    SayFirst("Others/Coming", "Ok");
                    return true;
            }
            return false;
        }

        // ---- Переспросы ----

        private bool FollowUp(PlayerControllerB speaker, ref string topic, ref string known)
        {
            var fresh = Fresh;
            var said = ctxSaid ?? "";
            var asked = topic;
            string next = null, nextKnown = known;
            switch (asked)
            {
                case TopicAndName:
                    // «А Паша?» — тот же вопрос про Пашу; без контекста — где он
                    var name = FriendNamed(known);
                    next = !fresh ? TopicWhereIs
                        : ctxTopic == TopicInHand || ctxTopic == TopicAboutFriend || ctxTopic == TopicCallName || ctxTopic == TopicHello ? ctxTopic
                        : ctxTopic == TopicEarned || ctxTopic == TopicEarnedBy ? TopicEarnedBy
                        : TopicWhereIs;
                    if (name == null)
                        next = TopicWhereIs;
                    break;
                case TopicAndHim:
                    next = fresh && (ctxTopic == TopicInHand || ctxTopic == TopicEarned || ctxTopic == TopicEarnedBy) ? ctxTopic : TopicWhereIs;
                    if (next == TopicEarned)
                        next = TopicEarnedBy;
                    nextKnown = next == TopicInHand ? "что у него в руке" : "где он";
                    break;
                case TopicAndOthers:
                    next = fresh && (ctxTopic == TopicEarned || ctxTopic == TopicEarnedBy) ? TopicLoot : TopicWhereIs;
                    nextKnown = "где остальные";
                    break;
                case TopicAndYou:
                    keepContext = true;
                    SayAndYou();
                    return true;
                case TopicNow:
                    // «А сейчас?» — заново, без «я уже отвечала»; без контекста — который час
                    next = fresh && !FollowIntents.Contains(ctxTopic) ? ctxTopic : TopicTime;
                    nextKnown = fresh ? ctxKnown : known;
                    // Погода, дедлайн, квота считаются по игре в Topic(): спрашиваем заново
                    if (fresh && (ctxTopic.StartsWith("Weather/") || ctxTopic.StartsWith("Deadline") || ctxTopic.StartsWith("Quota")))
                        next = Topic(ctxKnown);
                    break;
                case TopicMore:
                    keepContext = true;
                    if (fresh && ctxTopic == TopicJoke)
                    {
                        next = TopicJoke;
                        break;
                    }
                    // «Ещё» на рассказ (факт, совет, мяу) — следующая запись той же темы
                    if (fresh && !ctxTopic.StartsWith("#") && !ctxTopic.Contains("/") && !RepeatTopics.Contains(ctxTopic)
                        && FindSet(ctxTopic) is { } set && set.clips.Length > 1)
                    {
                        lastAsked = null;
                        var more = ctxTopic == "Advice" && FindSet("Advice/More") != null ? new List<string> { "Advice/More" }
                            : Existing("More/Intro").Take(Random.value < 0.5f ? 1 : 0).ToList();
                        Speak(more.Append(ctxTopic).ToList());
                        return true;
                    }
                    SayFirst("More/Nothing", "Ok");
                    return true;
                case TopicWhy:
                    keepContext = true;
                    SayFirst(fresh ? WhyFolder(said, ctxTopic) : "Why", "Why", TopicUnknown);
                    return true;
                case TopicReally:
                    keepContext = true;
                    if (!fresh)
                    {
                        SayFirst("Really", "Really/Sure", "Ok");
                        return true;
                    }
                    var factual = Checkable.Contains(ctxTopic) || ctxTopic == TopicTime || ctxTopic == TopicTimeLeft
                                  || ctxTopic.StartsWith("Weather") || ctxTopic.StartsWith("Deadline") || ctxTopic.StartsWith("Quota");
                    if (!factual)
                    {
                        SayFirst("Really/Joking", "Really", "Ok");
                        return true;
                    }
                    // Перепроверяет по-настоящему: «Сейчас проверю…» и ответ заново
                    if (Checkable.Contains(ctxTopic) && Random.value < 0.6f && FindSet("Check/Start") != null)
                    {
                        keepContext = false;
                        leadIn = "Check/Start";
                        next = ctxTopic;
                        nextKnown = ctxKnown;
                        break;
                    }
                    SayFirst("Really/Sure", "Really", "Ok");
                    return true;
                case TopicBefore:
                    keepContext = true;
                    SayYesterday();
                    return true;
                case TopicSaidAlready:
                    keepContext = true;
                    if (fresh && ctxTopic == TopicJoke)
                    {
                        next = TopicJoke;
                        break;
                    }
                    SayFirst("SaidAlready", TopicSorry, "Ok");
                    return true;
            }
            if (next == null)
                return false;
            Debug.Log($"[Алиса] переспрос {asked} после {ctxTopic ?? "ничего"} → {next} «{nextKnown}»");
            lastAsked = null; // переспрос — не повтор: без «Я уже отвечала»
            topic = next;
            known = nextKnown;
            return AnswerContext(speaker, ref topic, ref known);
        }

        // «Почему?» — по тому, что она сказала последним
        private string WhyFolder(string said, string topic)
        {
            string key = null;
            if (said.Contains("Refuse") || topic == TopicRefuse)
                key = "Refuse";
            else if (said.Contains("Launch/"))
                key = "Launch";
            else if (said.Contains("Route/"))
                key = "Route";
            else if (said.Contains("Buy/"))
                key = "Buy";
            else if (said.Contains("WhereIs/NotHere"))
                key = "NotHere";
            else if (said.Contains("WhoDied") || said.Contains("PlayerDied") || said.Contains("DeathCause"))
                key = "Dead";
            else if (said.Contains("Weather/") || topic.StartsWith("Weather"))
                key = "Weather";
            else if (said.Contains("Insult") || said.Contains("Offended") || said.Contains("Hurt/Yes"))
                key = "Insult";
            else if (said.Contains("Love"))
                key = "Love";
            else if (said.Contains("Unknown") || said.Contains("Mumble"))
                key = "Unknown";
            else if (said.Contains("Error"))
                key = "Error";
            else if (said.Contains("Door/"))
                key = "Door";
            return key != null && FindSet("Why/" + key) != null ? "Why/" + key : "Why";
        }

        // «А ты?» — про себя, в ответ на то, о чём спрашивали
        private void SayAndYou()
        {
            var t = Fresh ? ctxTopic : null;
            if (t == "HowAreYou" || t == TopicHealth)
            {
                if (t == TopicHealth && BatteryTopic() is { } battery && Random.value < 0.5f)
                    Say(battery);
                else
                    SayFirst(MoodTopic() ?? "HowAreYou", "HowAreYou", "AndYou");
                return;
            }
            if (t == TopicWhereAmI || t == TopicWhereIs)
            {
                SaySelfWhere();
                return;
            }
            var map = t == TopicMyName || t == TopicMyRole ? TopicWhoAreYou
                : t == "Love" ? "Love"
                : t == "Scared" ? "Self/Scared"
                : t == TopicEarned || t == TopicEarnedBy ? "Self/Earned"
                : t == "Tired" ? "Self/Tired"
                : t == "Food" || t == "Hungry" ? "Food"
                : t == "Dream" || t == "Fear" || t == "Hobby" || t == "Secret" ? t
                : null;
            SayFirst(map ?? "AndYou", "AndYou", "Ok");
        }

        // «А ты где?» — где стоит сама колонка
        private void SaySelfWhere()
        {
            var where = isHeld ? "Self/Where/Hands" : isInShipRoom || isInElevator ? "Self/Where/Ship" : isInFactory ? "Self/Where/Inside" : "Self/Where/Outside";
            SayFirst(where, "Here", "Ok");
        }

        // «А вчера?» — итоги прошлого дня: кто погиб, сколько принесли
        private void SayYesterday()
        {
            var t = Fresh ? ctxTopic : null;
            if (!yesterdayKnown)
            {
                SayFirst("Before/Forgot", TopicUnknown);
                return;
            }
            if (t == TopicWhoDied || t == TopicWhereIs || t == TopicCount)
            {
                if (yesterdayDeadCount == 0)
                {
                    SayFirst("Before/NoDead", "Before/Forgot");
                    return;
                }
                var names = yesterdayDead.Where(f => FindSet(TopicCall + f) != null).Distinct().Select(f => TopicCall + f).ToList();
                var topics = Existing("Before/Dead");
                if (topics.Count > 0 && names.Count > 0)
                {
                    AddList(topics, names);
                    SaySequence(topics);
                }
                else
                    SayFirst("Before/SomeDead", "Before/Forgot");
                return;
            }
            if (t == TopicEarned || t == TopicLoot || t == TopicBestEarner || t == TopicEarnedBy || t == TopicQuotaLeft)
            {
                var topics = Existing("Before/Loot");
                if (topics.Count > 0 && yesterdayBrought > 0 && AddSum(topics, yesterdayBrought))
                {
                    AddUnit(topics, "Credits");
                    SaySequence(topics);
                }
                else
                    SayFirst(yesterdayBrought > 0 ? "Before/Forgot" : "Before/NoLoot", "Before/Forgot");
                return;
            }
            SayFirst("Before/Forgot", TopicUnknown);
        }

        // ---- Ответы с настроением ----

        /// <summary>
        /// Вариант темы под ситуацию, если записан: &lt;Тема&gt;/Again (спросили снова), &lt;Тема&gt;/&lt;Имя&gt; (кто спросил),
        /// &lt;Тема&gt;/Morning|Day|Evening|Night|Orbit, &lt;Тема&gt;/Sad (сегодня кто-то погиб), &lt;Тема&gt;/Happy (квота есть).
        /// </summary>
        private string Flavor(string topic, PlayerControllerB speaker)
        {
            if (string.IsNullOrEmpty(topic) || topic.StartsWith("#"))
                return topic;
            var again = AskedBy(topic, speaker, 600f) >= (topic == "Greeting" ? 1 : 2);
            if (again && FindSet(topic + "/Again") != null && Random.value < 0.7f)
                return topic + "/Again";
            var options = new List<string>();
            var name = speaker != null ? NameFolder(speaker.playerUsername) : null;
            if (name != null)
                options.Add(topic + "/" + name);
            options.Add(topic + "/" + DayPart());
            if (dayDeadCount > 0)
                options.Add(topic + "/Sad");
            if (QuotaMet())
                options.Add(topic + "/Happy");
            options = options.Where(o => FindSet(o) != null).ToList();
            if (options.Count == 0 || Random.value > FlavorChance)
                return topic;
            return options[Random.Range(0, options.Count)];
        }

        /// <summary>
        /// «Привет» — приветствие, а не замечание: «Мы на орбите, можно отдохнуть» или «Скоро стемнеет» звучат только
        /// после приветствия («Привет! Мы на орбите…»). Записи для событий («Вы вернулись», «Ну наконец-то,
        /// я уже думала, что одна полечу») на «привет» не отвечают.
        /// </summary>
        private bool SayGreeting(PlayerControllerB speaker)
        {
            var greeting = FindSet("Greeting");
            var index = greeting != null ? PickIndex(greeting, IsPlainGreeting) : -1;
            if (index < 0)
                return false;
            var flavor = Flavor("Greeting", speaker);
            if (flavor != "Greeting" && (flavor.EndsWith("/Again") || !SituationFlavors.Any(f => flavor.EndsWith("/" + f))))
            {
                Say(flavor); // «Мы уже здоровались!», «Привет, Паша!» — сами приветствие
                return true;
            }
            var parts = new List<string> { greeting.name + "\t" + index };
            if (flavor != "Greeting" && FindSet(flavor) is { } extra)
                parts.Add(extra.name + "\t" + NextIndex(extra));
            askedLocally = true;
            var topic = SeqPrefix + string.Join("\n", parts);
            Speak(topic, 0);
            SendSpeak(topic, 0);
            return true;
        }

        private static readonly string[] SituationFlavors = { "Morning", "Day", "Evening", "Night", "Orbit", "Sad", "Happy" };

        private static bool IsPlainGreeting(string clip)
        {
            var lower = clip.ToLowerInvariant();
            return !lower.Contains("вернулись") && !lower.Contains("наконец");
        }

        private static string DayPart()
        {
            var tod = TimeOfDay.Instance;
            var round = StartOfRound.Instance;
            if (tod == null || round == null || round.inShipPhase || !tod.currentDayTimeStarted)
                return "Orbit";
            var hour = ((int)(tod.normalizedTimeOfDay * (60f * tod.numberOfHours)) + 360) / 60;
            return hour < 11 ? "Morning" : hour < 17 ? "Day" : hour < 21 ? "Evening" : "Night";
        }

        private static bool QuotaMet()
        {
            var tod = TimeOfDay.Instance;
            return tod != null && tod.profitQuota > 0 && tod.quotaFulfilled >= tod.profitQuota;
        }

        // Как она себя чувствует сейчас: обижена, батарейка, монстр рядом, кто-то погиб, квота
        private string MoodTopic()
        {
            var mood = Time.time - lastInsultAt < 180f ? "HowAreYou/Hurt"
                : insertedBattery != null && insertedBattery.charge < 0.25f ? "HowAreYou/LowBattery"
                : EnemyNear(transform.position, EventRadius) ? "HowAreYou/Scared"
                : Time.time - lastDeathAt < 600f ? "HowAreYou/Sad"
                : QuotaMet() ? "HowAreYou/Happy" : null;
            return mood != null && FindSet(mood) != null ? mood : null;
        }

        // «Как дела?» — по настроению; спрашивают третий раз — «Да всё так же»
        private bool SayHowAreYou(PlayerControllerB speaker)
        {
            if (AskedBy("HowAreYou", speaker, 600f) >= 2 && FindSet("HowAreYou/Again") != null)
            {
                Say("HowAreYou/Again");
                return true;
            }
            var mood = MoodTopic();
            if (mood == null || Random.value > 0.75f)
                return false; // как раньше: HowAreYou или «а у вас как?»
            Say(mood);
            ExpectReply(mood);
            return true;
        }

        // «Спасибо»: сразу после помощи — «Пожалуйста»; ни с того ни с сего — «А за что?»; в третий раз — «Да хватит уже»
        private void SayThanks(PlayerControllerB speaker)
        {
            var recent = ctxTopic != null && Time.time - ctxAt < 40f;
            if (AskedBy("Thanks", null, 300f) >= 2 && FindSet("Thanks/Again") != null && Random.value < 0.7f)
                Say("Thanks/Again");
            else if (!recent && FindSet("Thanks/ForWhat") != null && Random.value < 0.6f)
                Say("Thanks/ForWhat");
            else
                Say(Flavor("Thanks", speaker));
        }

        // «Что делать?» — по ситуации: поздно, квота горит, ранен, монстр, на орбите
        private bool SayAdvice(PlayerControllerB speaker)
        {
            var tod = TimeOfDay.Instance;
            var round = StartOfRound.Instance;
            var options = new List<string>();
            if (speaker != null && EnemyNear(speaker.transform.position, 20f))
                options.Add("Advice/Danger");
            if (speaker != null && speaker.health < 50)
                options.Add("Advice/Hurt");
            if (round != null && round.inShipPhase)
                options.Add("Advice/Orbit");
            else if (tod != null && tod.normalizedTimeOfDay > 0.7f)
                options.Add("Advice/Late");
            if (tod != null && tod.daysUntilDeadline <= 1 && !QuotaMet())
                options.Add("Advice/Quota");
            var pick = options.FirstOrDefault(o => FindSet(o) != null);
            if (pick == null || Random.value > 0.7f)
            {
                // Иногда «Совет:» перед обычным советом
                if (FindSet("Advice") == null || FindSet("Advice/Intro") == null || Random.value > 0.35f)
                    return false;
                SaySequence(new List<string> { "Advice/Intro", "Advice" });
                return true;
            }
            Say(pick);
            return true;
        }

        // ---- Обращение в начале ответа ----

        private static readonly HashSet<string> NoLeadIn = new HashSet<string>
        {
            TopicStop, TopicShush, TopicLouder, TopicQuieter, TopicVolume, TopicRepeatLast, TopicJoke, TopicMeow, TopicKitty,
            "Thanks", "Greeting", "Goodbye", TopicCode,
        };

        // «Паша, слушай…» (Lead/<Имя>), «Опять? Ну ладно…» (Lead/Often), «Ладно уж, отвечу…» (Lead/Grudge)
        private void PrepareLeadIn(PlayerControllerB speaker, string topic)
        {
            leadIn = null;
            if (topic == null || NoLeadIn.Contains(topic))
                return;
            var name = speaker != null ? NameFolder(speaker.playerUsername) : null;
            if (Time.time - lastInsultAt < 300f && insults.Count >= 2 && Random.value < 0.3f)
                leadIn = "Lead/Grudge";
            else if (AskedBy(topic, null, 180f) >= 2 && Random.value < 0.5f)
                leadIn = "Lead/Often";
            else if (name != null && Random.value < LeadInChance)
                leadIn = "Lead/" + name;
            if (leadIn != null && FindSet(leadIn) == null)
                leadIn = null;
        }

        // Обращение говорится один раз, перед первой фразой ответа
        private string TakeLeadIn()
        {
            var lead = leadIn;
            leadIn = null;
            return lead != null && FindSet(lead) != null ? lead : null;
        }

        // ---- Куски фраз ----

        // Имя в начале фразы: Glue/Name/<Имя> («Паша…»), иначе Call/<Имя> («Паша!»)
        private string NamePiece(string folder)
        {
            if (folder == null)
                return null;
            return FindSet(Glue + "Name/" + folder) != null ? Glue + "Name/" + folder
                : FindSet(TopicCall + folder) != null ? TopicCall + folder : null;
        }

        // «…кредитов.» после числа, если записано
        private void AddUnit(List<string> topics, string unit)
        {
            if (FindSet(Glue + "Unit/" + unit) != null)
                topics.Add(Glue + "Unit/" + unit);
        }

        // «Где Паша?» кусками: «Паша…» + «…в комплексе.»; нет кусков — целая фраза WhereIs/…
        private bool SayWhereIsGlued(PlayerControllerB speaker, string known)
        {
            var named = FriendNamed(known);
            var whole = WhereIsTopic(speaker, known);
            if (named == null || NamePiece(named) == null || Random.value < 0.35f && FindSet(whole) != null)
            {
                Say(whole);
                return true;
            }
            var place = whole.Contains("NotHere") ? "NotHere" : whole.Contains("PlayerDied") || whole.Contains("DeathCause") ? "Dead"
                : whole.Contains("/AFK") ? "AFK" : whole.Contains("/Inside") ? "Inside" : whole.Contains("/Outside") ? "Outside"
                : whole.Contains("/Lost") ? "Lost" : whole.Contains("/Ship") ? "Ship" : null;
            if (place == null || FindSet(Glue + "Place/" + place) == null)
            {
                Say(whole);
                return true;
            }
            SaySequence(new List<string> { NamePiece(named), Glue + "Place/" + place });
            return true;
        }

        // ---- Вопросы по игре ----

        private static int ShipLootValue()
        {
            var sum = 0;
            foreach (var item in FindObjectsOfType<GrabbableObject>())
            {
                if (item != null && item.itemProperties != null && item.itemProperties.isScrap && item.isInShipRoom)
                    sum += item.scrapValue;
            }
            return sum;
        }

        // «Сколько ещё собрать?»: квота минус сданное минус лут на корабле. intro — своё начало (ответ на «какую квоту?»)
        private void SayQuotaLeft(List<string> intro)
        {
            var tod = TimeOfDay.Instance;
            if (tod == null)
            {
                Say(TopicUnknown);
                return;
            }
            var left = tod.profitQuota - tod.quotaFulfilled - ShipLootValue();
            var topics = intro ?? new List<string>();
            if (left <= 0)
            {
                topics.AddRange(Existing("QuotaLeft/Done", "QuotaDone").Take(1));
                Speak(topics);
                return;
            }
            topics.AddRange(Existing("QuotaLeft/Intro"));
            if (!topics.Contains("QuotaLeft/Intro") || !AddSum(topics, left))
            {
                SayFirst("QuotaNotDone", TopicUnknown);
                return;
            }
            AddUnit(topics, "Credits");
            // Иногда ещё и сколько дней осталось
            var days = "Deadline" + Mathf.Clamp(tod.daysUntilDeadline, 0, 3);
            if (FindSet(days) != null && Random.value < 0.4f)
                topics.Add(days);
            SaySequence(topics);
        }

        // «Сколько до отлёта?»: часы до полуночи (в полночь корабль улетает сам)
        private void SayTimeLeft()
        {
            var tod = TimeOfDay.Instance;
            var round = StartOfRound.Instance;
            if (tod == null || round == null || round.inShipPhase || !tod.currentDayTimeStarted)
            {
                SayFirst("TimeLeft/Orbit", "Route/Landed", TopicUnknown);
                return;
            }
            var hours = (1f - tod.normalizedTimeOfDay) * tod.numberOfHours;
            if (hours < 1f)
            {
                SayFirst("TimeLeft/Soon", "Events/LateDay", TopicUnknown);
                return;
            }
            var h = Mathf.FloorToInt(hours);
            var topics = Existing("TimeLeft/Intro");
            var word = TopicTime + "/Hours/" + (h % 10 == 1 && h != 11 ? "One" : h % 10 >= 2 && h % 10 <= 4 && (h < 12 || h > 14) ? "Few" : "Many");
            if (topics.Count == 0 || !NumberParts(h, topics) || FindSet(word) == null)
            {
                SayFirst(hours < 3f ? "TimeLeft/Soon" : "TimeLeft/Plenty", "Events/LateDay", TopicUnknown);
                return;
            }
            topics.Add(word);
            SaySequence(topics);
        }

        // «Как моё здоровье?»
        private void SayHealth(PlayerControllerB speaker)
        {
            var hp = speaker != null ? speaker.health : 100;
            var topic = speaker != null && speaker.criticallyInjured || hp < 30 ? "Health/Critical" : hp < 90 ? "Health/Hurt" : "Health/Full";
            SayFirst(topic, "Health/Hurt", TopicUnknown);
        }

        // «Кто в комплексе?» / «кто на корабле?»: имена через «и» + «…в комплексе.» (или «В комплексе:» + имена)
        private void SayWhoIn(PlayerControllerB speaker, bool inside)
        {
            var round = StartOfRound.Instance;
            var folder = inside ? "WhoInside/" : "WhoOnShip/";
            if (round == null)
            {
                Say(TopicUnknown);
                return;
            }
            var there = round.allPlayerScripts.Where(p => p != null && p != speaker && p.isPlayerControlled && !p.isPlayerDead
                                                          && (inside ? p.isInsideFactory : p.isInHangarShipRoom)).ToList();
            if (there.Count == 0)
            {
                var onlyYou = speaker != null && (inside ? speaker.isInsideFactory : speaker.isInHangarShipRoom);
                SayFirst(onlyYou ? folder + "OnlyYou" : folder + "Nobody", folder + "Nobody", TopicUnknown);
                return;
            }
            var names = there.Select(p => NameFolder(p.playerUsername)).Where(f => f != null).Distinct().ToList();
            var pieces = names.Select(NamePiece).Where(n => n != null).ToList();
            if (pieces.Count == 0 || pieces.Count < there.Count)
            {
                SayFirst(folder + "Someone", inside ? "WhereIs/Inside/Many" : "WhereIs/Ship/Many", TopicUnknown);
                return;
            }
            var place = Glue + "Place/" + (inside ? "Inside" : "Ship");
            var topics = new List<string>();
            if (FindSet(place) != null && (Random.value < 0.6f || FindSet(folder + "Intro") == null))
            {
                AddList(topics, pieces);
                topics.Add(place);
            }
            else
            {
                topics.AddRange(Existing(folder + "Intro"));
                AddList(topics, names.Select(n => TopicCall + n).Where(n => FindSet(n) != null).ToList());
            }
            Speak(topics);
        }

        // «Что нового?» — что было за последние минуты
        private void SayNews()
        {
            var recent = memory.Where(m => Time.time - m.at < 300f).ToList();
            var died = recent.Where(m => m.what == "died").Select(m => m.who).Where(f => f != null && FindSet(TopicCall + f) != null)
                .Distinct().Select(f => TopicCall + f).ToList();
            if (died.Count > 0 && FindSet("News/Died") != null)
            {
                var topics = new List<string> { "News/Died" };
                AddList(topics, died);
                SaySequence(topics);
                return;
            }
            // Нет «Плохие новости. Погиб…» — «Мы кого-то потеряли. Паша… …погиб.»
            var deadNames = recent.Where(m => m.what == "died").Select(m => NamePiece(m.who)).Where(n => n != null).Distinct().ToList();
            if (deadNames.Count > 0 && FindSet(Glue + "Place/Dead") != null)
            {
                var topics = Existing("News/Died/Someone");
                AddList(topics, deadNames);
                topics.Add(Glue + "Place/Dead");
                SaySequence(topics);
                return;
            }
            var last = recent.LastOrDefault(m => m.what != "died").what ?? "";
            var news = last.Contains("Enemy/") || last.Contains("Panic") ? "News/Monster"
                : last.Contains("Money") || last.Contains("Sold") ? "News/Money"
                : last.Contains("Work") || last.Contains("ManyItems") || last.Contains("Back/Rich") ? "News/Loot"
                : last.Contains("Joined") ? "News/Joined"
                : last.Contains("Delivery") ? "News/Delivery"
                : recent.Any(m => m.what == "died") ? "News/Died/Someone" : "News/Nothing";
            SayFirst(news, "News/Nothing", "Ok");
        }

        // «Что тут опасного?» — сколько монстров на луне и кто самый опасный
        private void SayDangers()
        {
            var round = StartOfRound.Instance;
            if (round == null || round.inShipPhase || !round.shipHasLanded)
            {
                SayFirst("Dangers/Orbit", "AllClear", TopicUnknown);
                return;
            }
            var enemies = RoundManager.Instance != null ? RoundManager.Instance.SpawnedEnemies.Where(e => e != null && !e.isEnemyDead && e.enemyType != null).ToList()
                : new List<EnemyAI>();
            var count = enemies.Count;
            var topics = new List<string> { count == 0 ? "Dangers/None" : count <= 3 ? "Dangers/Few" : "Dangers/Many" };
            var notable = enemies.Select(e => "Dangers/" + e.enemyType.enemyName.Replace(" ", "")).FirstOrDefault(t => FindSet(t) != null);
            if (notable != null && (Random.value < 0.7f || FindSet(topics[0]) == null))
                topics.Add(notable);
            topics = topics.Where(t => FindSet(t) != null).ToList();
            if (topics.Count == 0)
                topics.Add(count == 0 ? TopicAllClear : TopicEnemiesNear);
            Speak(topics);
        }

        // «Как тебе Паша?» — его личное мнение или по тому, как он сегодня: лучший, погиб, ничего не принёс
        private void SayAboutFriend(string known)
        {
            var name = FriendNamed(known);
            if (name == null)
            {
                SayFirst("Opinion/Unknown", "Names/Unknown", TopicUnknown);
                return;
            }
            var round = StartOfRound.Instance;
            var player = round?.allPlayerScripts.FirstOrDefault(p => p != null && (p.isPlayerControlled || p.isPlayerDead) && NameFolder(p.playerUsername) == name);
            var today = new List<string>();
            if (player != null)
            {
                earned.TryGetValue(player.playerClientId, out var sum);
                if (player.isPlayerDead)
                    today.Add("Opinion/Tail/Dead");
                else if (sum > 0 && earned.Count > 1 && earned.OrderByDescending(e => e.Value).First().Key == player.playerClientId)
                    today.Add("Opinion/Tail/Best");
                else if (sum == 0 && round.shipHasLanded && TimeOfDay.Instance != null && TimeOfDay.Instance.normalizedTimeOfDay > 0.4f)
                    today.Add("Opinion/Tail/Lazy");
            }
            // «Паша…» + «…сегодня лучше всех!»
            var tail = today.FirstOrDefault(t => FindSet(t) != null);
            if (tail != null && NamePiece(name) != null && (Random.value < 0.6f || FindSet("Opinion/" + name) == null))
            {
                SaySequence(new List<string> { NamePiece(name), tail });
                return;
            }
            // Своё мнение о нём: целой фразой (Opinion/<Имя>) или кусками «Сеня…» + «…душа компании» (Opinion/Tail/<Имя>)
            var own = "Opinion/Tail/" + name;
            if (FindSet(own) != null && NamePiece(name) != null && (Random.value < 0.6f || FindSet("Opinion/" + name) == null))
            {
                SaySequence(new List<string> { NamePiece(name), own });
                return;
            }
            SayFirst("Opinion/" + name, "Opinion/Unknown", TopicUnknown);
        }

        // «А Паша?» после «сколько я принёс» — сколько принёс он
        private void SayEarnedBy(PlayerControllerB speaker, string known)
        {
            var round = StartOfRound.Instance;
            var named = FriendNamed(known);
            var player = round?.allPlayerScripts.Where(p => p != null && p != speaker && (p.isPlayerControlled || p.isPlayerDead))
                .Where(p => named == null || NameFolder(p.playerUsername) == named)
                .OrderBy(p => speaker != null ? Vector3.Distance(p.transform.position, speaker.transform.position) : 0f).FirstOrDefault();
            if (player == null)
            {
                SayFirst(named != null ? "WhereIs/NotHere" : TopicUnknown, TopicUnknown);
                return;
            }
            earned.TryGetValue(player.playerClientId, out var sum);
            var name = NameFolder(player.playerUsername);
            if (sum <= 0)
            {
                if (NamePiece(name) != null && FindSet("Earned/Tail/None") != null)
                    SaySequence(new List<string> { NamePiece(name), "Earned/Tail/None" });
                else
                    SayFirst("Earned/HimNone", "Earned/None", TopicUnknown);
                return;
            }
            var topics = new List<string>();
            if (NamePiece(name) != null && FindSet("Earned/Tail/Intro") != null)
                topics.AddRange(new[] { NamePiece(name), "Earned/Tail/Intro" }); // «Паша…» «…принёс на…»
            else
                topics.AddRange(Existing("Earned/Him"));
            if (topics.Count == 0 || !AddSum(topics, sum))
            {
                SayFirst("Loot/High", "Ok");
                return;
            }
            AddUnit(topics, "Credits");
            SaySequence(topics);
        }

        // «Выбери, кто пойдёт первым»: случайный живой рядом по имени + «…идёт первым.»
        private void SayChoose(PlayerControllerB speaker)
        {
            var round = StartOfRound.Instance;
            var pos = speaker != null ? speaker.transform.position : transform.position;
            var names = round == null ? new List<string>() : round.allPlayerScripts
                .Where(p => p != null && p.isPlayerControlled && !p.isPlayerDead && Vector3.Distance(p.transform.position, pos) < 20f)
                .Select(p => NameFolder(p.playerUsername)).Where(f => NamePiece(f) != null).Distinct().ToList();
            if (names.Count == 0)
            {
                SayFirst("Choose/Nobody", TopicRefuse);
                return;
            }
            var pick = names[Random.Range(0, names.Count)];
            // «Паша…» + «…идёт первым.» или «Пусть идёт…» + «Паша!»
            var topics = FindSet("Choose/Tail") != null && (Random.value < 0.6f || FindSet("Choose/Intro") == null)
                ? new List<string> { NamePiece(pick), "Choose/Tail" }
                : Existing("Choose/Intro").Append(FindSet(TopicCall + pick) != null ? TopicCall + pick : NamePiece(pick)).ToList();
            SaySequence(topics);
        }

        // «Да или нет?», «мы выживем?»: шар судьбы. В тяжёлый день чаще «нет»
        private void SayBall()
        {
            var no = 0.33f + Mathf.Min(dayDeadCount, 3) * 0.1f;
            var r = Random.value;
            SayFirst(r < no ? "Ball/No" : r < no + 0.33f ? "Ball/Yes" : "Ball/Maybe", "Ball/Maybe", TopicUnknown);
        }

        // ---- Арифметика: «сколько будет семь плюс пять» ----

        private static readonly Dictionary<string, int> Numerals = new Dictionary<string, int>
        {
            ["ноль"] = 0, ["нуль"] = 0, ["один"] = 1, ["одна"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3, ["четыре"] = 4, ["пять"] = 5,
            ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10, ["одиннадцать"] = 11, ["двенадцать"] = 12,
            ["тринадцать"] = 13, ["четырнадцать"] = 14, ["пятнадцать"] = 15, ["шестнадцать"] = 16, ["семнадцать"] = 17,
            ["восемнадцать"] = 18, ["девятнадцать"] = 19, ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40, ["пятьдесят"] = 50,
            ["шестьдесят"] = 60, ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90, ["сто"] = 100, ["двести"] = 200,
            ["триста"] = 300, ["четыреста"] = 400, ["пятьсот"] = 500, ["тысяча"] = 1000,
        };

        // Числа подряд складываются: «двадцать пять» = 25, «сто три» = 103
        private static List<(int value, int at)> NumbersIn(string[] tokens)
        {
            var list = new List<(int, int)>();
            int? current = null;
            var start = 0;
            for (var i = 0; i < tokens.Length; i++)
            {
                int v;
                var isNum = Numerals.TryGetValue(tokens[i], out v) || int.TryParse(tokens[i], out v);
                if (isNum && current != null && v < current.Value && (current.Value % 10 == 0 && v < 10 || current.Value % 100 == 0 && v < 100))
                {
                    current += v;
                    continue;
                }
                if (current != null)
                    list.Add((current.Value, start));
                current = isNum ? v : (int?)null;
                start = i;
            }
            if (current != null)
                list.Add((current.Value, start));
            return list;
        }

        // Посчитала — «Будет» + число; не разобрала числа — false (смешная запись из Math)
        private bool SayMath(string known)
        {
            var tokens = AliceIntents.Tokens(known);
            var nums = NumbersIn(tokens);
            if (nums.Count < 2)
                return false;
            var between = tokens.Skip(nums[0].at + 1).Take(Math.Max(0, nums[1].at - nums[0].at - 1)).ToArray();
            bool Has(params string[] stems) => between.Any(t => stems.Any(t.StartsWith));
            int a = nums[0].value, b = nums[1].value;
            long result;
            if (Has("плюс", "прибав", "сложи"))
                result = a + b;
            else if (Has("минус", "отним", "вычест"))
                result = a - b;
            else if (Has("умнож", "помнож", "раз"))
                result = (long)a * b;
            else if (Has("дели", "раздели", "подели"))
            {
                if (b == 0)
                {
                    SayFirst("Math/Zero", "Math/Hard", "Math");
                    return true;
                }
                if (a % b != 0)
                {
                    SayFirst("Math/Hard", "Math");
                    return true;
                }
                result = a / b;
            }
            else
                return false;
            if (result < 0)
            {
                SayFirst("Math/Negative", "Math/Hard", "Math");
                return true;
            }
            var topics = new List<string>();
            // Иногда повторяет пример: «Семь… …плюс… пять… Будет… двенадцать»
            var op = Has("плюс", "прибав", "сложи") ? "Math/Op/Plus" : Has("минус", "отним", "вычест") ? "Math/Op/Minus" : null;
            if (op != null && FindSet(op) != null && a < 1000 && b < 1000 && Random.value < 0.35f)
            {
                var echo = new List<string>();
                if (NumberParts(a, echo))
                {
                    echo.Add(op);
                    if (NumberParts(b, echo))
                        topics.AddRange(echo);
                }
            }
            var intro = Existing("Math/Intro");
            if (intro.Count > 0)
                topics.Add(intro[0]);
            if (result > 1999 || !AddNumber(topics, (int)result))
            {
                SayFirst("Math/Hard", "Math");
                return true;
            }
            if (a == 2 && b == 2 && result == 4 && FindSet("Math/Quota") != null && Random.value < 0.5f)
                topics.Add("Math/Quota"); // «…Минус квота — ноль.»
            else if (Random.value < 0.25f)
                topics.AddRange(Existing("Math/Easy"));
            SaySequence(topics);
            return true;
        }

        // ---- Ответы на её вопросы ----

        /// <summary>
        /// Ответы на вопросы из этого файла: «Куда летим?» (луна), «Сегодня без Андрея?», «Не все на борту, всё равно взлетаем?»,
        /// «Все на борту. Улетаем?» (взлёт), «Вы помните про квоту?» (числа квоты). true — разобрано здесь.
        /// </summary>
        private bool OnReplyContext(string kind, string item, string reply, string intent)
        {
            var empty = string.IsNullOrEmpty(reply);
            if (kind == AskRoute)
            {
                if (empty)
                    return true;
                var moonText = "полетели на " + reply;
                if (AliceIntents.MoonInText(moonText) != null && RouteHandler != null)
                    SayFirst(RouteHandler(moonText).Concat(new[] { TopicUnknown }).ToArray());
                else if (intent == TopicDunno || intent == "Moon")
                    Say("Moon"); // «Давайте полетим на Титан»
                else if (intent == TopicNo || intent == TopicNotYou)
                    SayFirst("Orbit/Later", "Ok");
                else
                    SayFirst("Route/Unknown", TopicUnknown);
                return true;
            }
            if (kind == AskMissing && !empty)
            {
                var words = AliceIntents.Tokens(reply);
                if (intent == TopicComing || intent == TopicNo || words.Any(w => w.StartsWith("скоро") || w.StartsWith("приде") || w.StartsWith("зайд") || w.StartsWith("подключ")))
                    SayFirst("Missing/Wait", "Ok");
                else if (intent == TopicDunno)
                    SayFirst("Missing/Dunno", "Ok");
                else if (intent == TopicYes || words.Contains("без"))
                    SayFirst("Missing/Sad", "Ok");
                else
                    SayFirst("Ok");
                return true;
            }
            if (kind == AskOthers && intent == TopicComing)
            {
                SayFirst("Others/Coming", "Ok");
                return true;
            }
            if (kind == AskLaunch && item == NotAll && intent != TopicYes && intent != TopicNo)
            {
                // «Не все на борту, всё равно взлетаем?» — молчат: не взлетает
                SayFirst(LaunchCancel, "Ok");
                return true;
            }
            if (kind == AskFollow && !empty)
            {
                switch (AliceIntents.MatchFollowUp(item, reply))
                {
                    case EventsFolder + "Leave/Yes":
                        LaunchNow(EventsFolder + "Leave/Yes");
                        return true;
                    case EventsFolder + "Deadline/No":
                        // «Нет» на «Вы помните про квоту?» — напоминает числами
                        var tod = TimeOfDay.Instance;
                        var topics = Existing(EventsFolder + "Deadline/No");
                        if (tod != null && FindSet("Quota/Need") != null)
                        {
                            topics.Add("Quota/Need");
                            if (!AddSum(topics, tod.profitQuota))
                                topics.Remove("Quota/Need");
                        }
                        SayQuotaLeft(topics);
                        return true;
                }
            }
            return false;
        }

        // Дёрнуть рычаг по её же предложению: «Взлетаем!» + ответ рычага
        private void LaunchNow(string intro)
        {
            var refuse = LaunchCheck != null ? LaunchCheck() : new[] { TopicRefuse };
            if (refuse != null && refuse.Length > 0 || Launch == null)
            {
                SayFirst(refuse ?? new[] { TopicRefuse });
                return;
            }
            var done = Launch().FirstOrDefault(t => FindSet(t) != null);
            var topics = Existing(intro);
            if (done != null)
                topics.Add(done);
            Speak(topics);
        }

        /// <summary>
        /// «Взлетай», а кто-то живой ещё снаружи: «Не все на борту! Паша и Андрей… Всё равно взлетаем?» и ждёт «да».
        /// false — все на борту (обычное «Вы уверены?»).
        /// </summary>
        private bool AskLaunchNotAll()
        {
            var round = StartOfRound.Instance;
            if (round == null || !round.shipHasLanded || round.shipIsLeaving || FindSet("Launch/NotAll") == null)
                return false;
            var outside = round.allPlayerScripts.Where(p => p != null && p.isPlayerControlled && !p.isPlayerDead && !p.isInHangarShipRoom).ToList();
            if (outside.Count == 0)
                return false;
            var topics = new List<string> { "Launch/NotAll" };
            var names = outside.Select(p => NameFolder(p.playerUsername)).Where(f => f != null && FindSet(TopicCall + f) != null)
                .Distinct().Select(f => TopicCall + f).ToList();
            if (names.Count > 0 && names.Count == outside.Count)
            {
                AddList(topics, names);
                topics.AddRange(Existing(Glue + "Place/OutsideMany").Take(names.Count > 1 ? 1 : 0));
                topics.AddRange(Existing(Glue + "Place/Outside").Take(names.Count == 1 ? 1 : 0));
            }
            topics.AddRange(Existing("Launch/StillGo"));
            SaySequence(topics);
            AskAfterSpeech(AskLaunch);
            askingItem = NotAll;
            return true;
        }

        // ---- Сама начинает разговор ----

        // Эти поводы она говорит всегда (не по шансу EventChance)
        private static bool SureContext(string topic) =>
            topic == EventsFolder + "Orbit/Ask" || topic == EventsFolder + "Leave/Ask" || topic == EventsFolder + "Deadline/Ask"
            || topic == EventsFolder + "Comfort/Ask" || topic == EventsFolder + "QuotaEnough" || topic.StartsWith(EventsFolder + "DiedAgain")
            || topic.StartsWith(EventsFolder + "Back/Rich") || topic == EventsFolder + "Lonely/Ask" && Random.value < 0.5f
            || topic == EventsFolder + "AFK/Ask" && Random.value < 0.5f;

        // Повод без своей записи, но из кусков: «Паша…» + «…был хорошим сотрудником.» (поминки), «Паша…» + «Опять!»
        private List<string> GluedEvent(string topic)
        {
            string Name(string prefix) => topic.StartsWith(EventsFolder + prefix) ? NamePiece(topic.Substring((EventsFolder + prefix).Length)) : null;
            if (Name("Memorial/") is { } dead && FindSet("Opinion/Tail/Dead") != null)
                return new List<string> { dead, "Opinion/Tail/Dead" };
            if (Name("DiedAgain/") is { } again && FindSet(EventsFolder + "DiedAgain") != null)
                return new List<string> { again, EventsFolder + "DiedAgain" };
            return null;
        }

        // Повод с продолжением: «Куда летим?» ждёт название луны. true — сказано здесь
        private bool SayEventContext(string topic)
        {
            if (topic != EventsFolder + "Orbit/Ask")
                return false;
            SayFrom(FindSet(topic));
            AskAfterSpeech(AskRoute);
            return true;
        }

        // Вернулся с полными руками: «Ого, сколько всего!» (Events/Back/Rich/<Имя> или Events/Back/Rich)
        private void BackRich(PlayerControllerB p)
        {
            var value = p.ItemSlots == null ? 0 : p.ItemSlots.Where(i => i != null && i.itemProperties != null && i.itemProperties.isScrap).Sum(i => i.scrapValue);
            if (value < RichValue)
                return;
            var name = NameFolder(p.playerUsername);
            if (name != null)
                Reason("Back/Rich/" + name);
            Reason("Back/Rich");
        }

        /// <summary>Поводы этого файла (только хост): смерти и итоги дня, вопросы, которые колонка задаёт сама.</summary>
        private void CollectContextReasons(StartOfRound round)
        {
            var tod = TimeOfDay.Instance;
            var pos = transform.position;
            var alive = round.allPlayerScripts.Where(p => p != null && p.isPlayerControlled && !p.isPlayerDead).ToList();

            // Смерти за день; погиб второй день подряд — «Андрей, опять?!» раньше обычного «Ой, нет»
            foreach (var p in round.allPlayerScripts)
            {
                if (p == null)
                    continue;
                if (!p.isPlayerDead)
                {
                    ctxDeadIds.Remove(p.playerClientId);
                    continue;
                }
                if (!ctxDeadIds.Add(p.playerClientId) || round.inShipPhase)
                    continue;
                var folder = NameFolder(p.playerUsername);
                dayDeadCount++;
                if (folder != null)
                    dayDead.Add(folder);
                lastDeathAt = Time.time;
                RememberEvent("died", folder);
                if (folder != null && yesterdayKnown && yesterdayDead.Contains(folder))
                {
                    reasons.Insert(0, EventsFolder + "DiedAgain");
                    reasons.Insert(0, EventsFolder + "DiedAgain/" + folder);
                }
            }

            // Конец дня (корабль ушёл на орбиту): запоминаем итоги для «а вчера?»
            if (round.inShipPhase && !ctxWasShipPhase)
            {
                yesterdayKnown = true;
                yesterdayDead = new List<string>(dayDead);
                yesterdayDeadCount = dayDeadCount;
                yesterdayBrought = earned.Values.Sum();
                dayDead.Clear();
                dayDeadCount = 0;
                deadlineAsked = leaveAsked = comfortAsked = orbitAsked = false;
                lastDeathAt = -1000f;
                afkAsked.Clear();
            }
            ctxWasShipPhase = round.inShipPhase;

            // Сели: последний день перед сдачей, а квоты нет — «Вы же помните про квоту?»
            var landed = round.shipHasLanded && !round.shipIsLeaving && !round.inShipPhase;
            var company = round.currentLevel != null && round.currentLevel.levelID == 3;
            if (landed && !ctxWasLanded && tod != null && !company && !deadlineAsked && tod.daysUntilDeadline <= 1
                && tod.quotaFulfilled + ShipLootValue() < tod.profitQuota)
            {
                deadlineAsked = true;
                Reason("Deadline/Ask");
            }
            ctxWasLanded = landed;

            // На орбите, рядом кто-то есть: «Куда летим?» — ответят луной, поставит маршрут
            if (round.inShipPhase && !round.shipIsLeaving)
            {
                if (orbitSince < 0f)
                    orbitSince = Time.time;
                if (!orbitAsked && Time.time - orbitSince > 25f && PlayerNear(this, 8f) && RouteHandler != null)
                {
                    orbitAsked = true;
                    Reason("Orbit/Ask");
                }
            }
            else
                orbitSince = -1f;

            // Все живые на корабле, день к вечеру, что-то принесли: «Все на борту. Улетаем?» — «да» дёргает рычаг
            var allOnShip = landed && !company && alive.Count > 0 && alive.All(p => p.isInHangarShipRoom);
            if (!allOnShip)
                allOnShipSince = -1f;
            else if (allOnShipSince < 0f)
                allOnShipSince = Time.time;
            else if (!leaveAsked && Time.time - allOnShipSince > 20f && tod != null && tod.normalizedTimeOfDay > 0.55f
                     && (earned.Values.Sum() > 0 || tod.normalizedTimeOfDay > 0.8f) && (isInShipRoom || isInElevator) && PlayerNear(this, 10f))
            {
                leaveAsked = true;
                Reason("Leave/Ask");
            }

            // Через пару минут после смерти, на корабле: «Вы как? Держитесь?»
            if (!comfortAsked && lastDeathAt > 0f && Time.time - lastDeathAt > 120f && Time.time - lastDeathAt < 400f && PlayerNear(this, 8f))
            {
                comfortAsked = true;
                Reason("Comfort/Ask");
            }

            // Один у колонки, остальные в комплексе: «Скучаете? Хотите анекдот?»
            var near = alive.Where(p => Vector3.Distance(p.transform.position, pos) < 8f).ToList();
            var lonely = landed && alive.Count >= 2 && near.Count == 1 && alive.Where(p => p != near[0]).All(p => p.isInsideFactory);
            if (!lonely)
                lonelySince = -1f;
            else if (lonelySince < 0f)
                lonelySince = Time.time;
            else if (Time.time - lonelySince > 90f)
            {
                lonelySince = Time.time;
                Reason("Lonely/Ask");
            }

            // Стоит рядом и не двигается полторы минуты: «Эй, вы там не уснули?»
            foreach (var p in alive)
            {
                if (Vector3.Distance(p.transform.position, pos) < 5f && p.timeSincePlayerMoving > 90f)
                {
                    if (afkAsked.Add(p.playerClientId))
                        Reason("AFK/Ask");
                }
                else if (p.timeSincePlayerMoving < 5f)
                    afkAsked.Remove(p.playerClientId);
            }

            // На корабле уже хватает на квоту: раз за квоту
            if (tod != null && Time.time > nextQuotaCheck)
            {
                nextQuotaCheck = Time.time + 5f;
                if (tod.profitQuota != quotaSeen)
                {
                    quotaSeen = tod.profitQuota;
                    quotaEnoughTold = tod.quotaFulfilled >= tod.profitQuota;
                }
                if (!quotaEnoughTold && !company && tod.profitQuota > 0 && tod.quotaFulfilled + ShipLootValue() >= tod.profitQuota)
                {
                    quotaEnoughTold = true;
                    Reason("QuotaEnough");
                }
            }
        }
    }
}
