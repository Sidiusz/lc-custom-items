using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;

namespace CustomItems.Items
{
    public partial class YandexStationItem
    {
        /// <summary>
        /// Ждёт ли ближайшая к игроку колонка его слов без «Алиса»: проснулась и слушает, задала вопрос
        /// («Вы уверены?») или ждёт, во что играть. Проснулась на «Алиса» другого игрока — этого не ждёт.
        /// Такую фразу плагин распознаёт точнее (Whisper), как и с «Алиса».
        /// </summary>
        public static bool IsHearing(PlayerControllerB speaker)
        {
            if (speaker == null)
                return false;
            var station = HearingStation(speaker);
            return station != null && (Time.time < station.listenUntil && station.ListensTo(speaker)
                                       || station.asking != null && Time.time < station.askUntil
                                       || Time.time < station.gameAnswerUntil
                                       || Time.time < station.followUntil); // «а Паша?» сразу после ответа
        }

        // Отмена посреди речи: «…замолчи…», «…не тебе…», «отбой» — дальше колонка не слушает
        private static readonly HashSet<string> CancelWords = new HashSet<string>
        {
            "замолчи", "замолкни", "заткнись", "помолчи", "отмена", "отмени", "отбой", "стоп", "тихо", "хватит", "проехали", "забей"
        };

        /// <summary>Длиннее этого (слов) вопрос к колонке не бывает: дальше это разговор с другими.</summary>
        public const int MaxCommandWords = 14;
        /// <summary>Непонятная речь из стольких слов — не вопрос ей, а разговор: закрывается молча.</summary>
        public const int LongTalkWords = 7;

        /// <summary>
        /// Есть ли в фразе отмена: стоп-слово или «не тебе». Сама команда («хватит болтать», «это я не тебе»,
        /// «стоп маршрут») — не отмена посреди речи, её разбирают как обычно.
        /// </summary>
        public static bool HasCancel(string[] words)
        {
            var intent = AliceIntents.Match(string.Join(" ", words), out var score);
            if ((intent == TopicStop || intent == TopicShush || intent == TopicNotYou) && score >= 0.7f)
                return false;
            for (var i = 0; i < words.Length; i++)
            {
                if (CancelWords.Contains(words[i]))
                    return true;
                if (words[i] == "не" && i + 1 < words.Length && (words[i + 1] == "тебе" || words[i + 1] == "тебя"))
                    return true;
            }
            return false;
        }

        /// <summary>Слова после последнего «Алиса» (или всё, если её не звали в этой фразе).</summary>
        private static string[] AfterWake(string text)
        {
            var words = AliceIntents.Tokens(text);
            var wake = Array.LastIndexOf(words, WakeWord);
            return wake < 0 ? words : words.Skip(wake + 1).ToArray();
        }

        /// <summary>
        /// Кусок фразы, пока игрок говорит: колонка слушает его, а он посреди речи сказал «замолчи», «стоп», «не тебе».
        /// Тогда она сразу перестаёт слушать (сигнал конца) и эту фразу не разбирает. Одно «стоп» или «замолчи» —
        /// обычная команда, её разбирает OnHeard. true — фразу дальше не передавать.
        /// </summary>
        public static bool CancelsListening(PlayerControllerB speaker, string text)
        {
            if (speaker == null || speaker.isPlayerDead)
                return false;
            var words = AfterWake(text);
            if (words.Length < 2 || !HasCancel(words))
                return false;
            var station = HearingStation(speaker);
            if (station == null || !(Time.time < station.listenUntil && station.ListensTo(speaker))
                || station.IsTalking || station.pendingAnswer != null)
                return false;
            Debug.Log($"[Алиса] {speaker.playerUsername}: «{text}» — отмена посреди речи, больше не слушает");
            station.StopListening();
            return true;
        }

        /// <summary>Перестать слушать: сигнал конца прослушивания, без «Не поняла».</summary>
        private void StopListening()
        {
            var listening = Time.time < listenUntil;
            listenUntil = 0f;
            listenAgainFor = null;
            wokeBy = null;
            if (listening && IsOn && !IsJammed)
                Say(SoundTimeout);
        }

        /// <summary>Включена ли хоть одна колонка (тогда пора загружать распознавание).</summary>
        public static bool AnyOn
        {
            get
            {
                foreach (var station in All)
                {
                    if (station != null && station.IsOn)
                        return true;
                }
                return false;
            }
        }
    }
}
