using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CustomItems.Items;
using UnityEngine;

namespace CustomItems
{
    /// <summary>
    /// «Алиса, полетели на Титан»: колонка ставит маршрут, как терминал командой route.
    /// Луны и цены берутся из самого терминала (слово route и его подтверждения), поэтому
    /// подходят и модовые луны. Название в речи: русские имена из intents.txt (строки
    /// «@Titan: титан»), иначе транслит, сверенный с названиями лун с одной-двумя ошибками.
    /// </summary>
    public static class AliceRoute
    {
        public const string Done = "Route/Done", Landed = "Route/Landed", Busy = "Route/Busy",
            Already = "Route/Already", NoMoney = "Route/NoMoney", NoMoon = "Route/Unknown";

        private struct Moon
        {
            public string Word; // как в терминале: titan, rend, 85-rend
            public int LevelId, Cost;
        }

        public static void Install() => YandexStationItem.RouteHandler = Route;

        /// <summary>Ставит маршрут по тексту вопроса, возвращает тему ответа (первая с записями).</summary>
        private static string[] Route(string command)
        {
            var round = StartOfRound.Instance;
            var terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            if (round == null || terminal == null)
                return new[] { NoMoon, "Refuse" };

            var moons = Moons(terminal);
            var moon = Find(command, moons);
            if (moon == null)
                return new[] { NoMoon, "Unknown" };
            var m = moon.Value;
            if (round.isChallengeFile)
                return new[] { "Refuse" };
            if (!round.inShipPhase)
                return new[] { Landed, "Refuse" };
            if (round.travellingToNewLevel)
                return new[] { Busy, "Refuse" };
            if (round.currentLevel != null && round.currentLevel.levelID == m.LevelId)
                return new[] { Already, "Ok" };
            if (terminal.groupCredits < m.Cost)
                return new[] { NoMoney, "Refuse" };

            terminal.groupCredits -= m.Cost;
            round.ChangeLevelServerRpc(m.LevelId, terminal.groupCredits);
            Plugin.Log.LogInfo($"Алиса: маршрут на {m.Word} за {m.Cost}");
            return new[] { Done, "Navigation", "Ok" };
        }

        private static List<Moon> Moons(Terminal terminal)
        {
            var list = new List<Moon>();
            var route = terminal.terminalNodes?.allKeywords?.FirstOrDefault(k => k.word == "route");
            if (route?.compatibleNouns == null)
                return list;
            foreach (var noun in route.compatibleNouns)
            {
                var options = noun?.result?.terminalOptions;
                var confirm = options?.Select(o => o?.result).FirstOrDefault(n => n != null && n.buyRerouteToMoon >= 0);
                if (confirm == null || noun.noun == null)
                    continue;
                list.Add(new Moon { Word = noun.noun.word.ToLowerInvariant(), LevelId = confirm.buyRerouteToMoon, Cost = confirm.itemCost });
            }
            return list;
        }

        private static Moon? Find(string command, List<Moon> moons)
        {
            if (moons.Count == 0)
                return null;
            var tokens = AliceIntents.Tokens(command);

            // Русское имя из intents.txt («@Titan: титан, титана»), по основе слова
            foreach (var t in tokens)
            {
                var name = AliceIntents.MoonIn(t);
                if (name == null)
                    continue;
                var key = Simplify(name);
                foreach (var m in moons)
                {
                    if (Simplify(m.Word) == key || Simplify(m.Word).Contains(key))
                        return m;
                }
            }

            // Иначе транслит: «эмбрион» → embrion, «адаманс» → adamans ≈ adamance
            Moon? best = null;
            var bestDist = int.MaxValue;
            foreach (var t in tokens)
            {
                if (t.Length < 3)
                    continue;
                var latin = Translit(AliceIntents.Stem(t));
                foreach (var m in moons)
                {
                    var word = Simplify(m.Word);
                    var head = word.Substring(0, Math.Min(word.Length, latin.Length + 1));
                    var d = Math.Min(AliceIntents.Levenshtein(latin, word), AliceIntents.Levenshtein(latin, head));
                    if (d < bestDist && d <= (latin.Length >= 6 ? 2 : 1))
                    {
                        bestDist = d;
                        best = m;
                    }
                }
            }
            return best;
        }

        // Без цифр и знаков: «85-rend» → rend
        private static string Simplify(string word) =>
            new string((word ?? "").ToLowerInvariant().Where(c => c >= 'a' && c <= 'z').ToArray());

        private static readonly Dictionary<char, string> Latin = new Dictionary<char, string>
        {
            ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ж'] = "zh", ['з'] = "z",
            ['и'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p",
            ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "c", ['ч'] = "ch",
            ['ш'] = "sh", ['щ'] = "sh", ['ы'] = "y", ['э'] = "e", ['ю'] = "u", ['я'] = "ya",
        };

        private static string Translit(string word)
        {
            var sb = new StringBuilder();
            foreach (var c in word)
            {
                if (Latin.TryGetValue(c, out var s))
                    sb.Append(s);
            }
            return sb.ToString();
        }
    }
}
