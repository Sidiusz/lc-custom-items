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
        private const string TopicWhereAmI = "#whereami", TopicNotYou = "#notyou", TopicDunno = "#dunno", TopicHere = "#here";
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

        /// <summary>Темы разговора. false — тема не отсюда.</summary>
        private bool AnswerTalk(PlayerControllerB speaker, string topic)
        {
            switch (topic)
            {
                case TopicNotYou:
                    SayFirst("Wake/NotMe", "Ok");
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
            }
            return false;
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

        // Команду на взлёт повторили в одной фразе: «взлетай взлетай», «погнали погнали»
        private static readonly string[] LaunchStems = { "взлет", "взлёт", "погнал", "полетел", "поехал", "запуск", "запуст", "улета" };

        private static bool Hurried(string command)
        {
            var words = (command ?? "").ToLowerInvariant().Split(new[] { ' ', ',', '!', '.' }, System.StringSplitOptions.RemoveEmptyEntries);
            return words.Count(w => LaunchStems.Any(w.StartsWith)) >= 2;
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
