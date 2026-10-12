using System.Collections.Generic;
using System.Linq;
using CustomItems.Items;
using HarmonyLib;
using UnityEngine;

namespace CustomItems
{
    /// <summary>
    /// Колонку в терминале можно купить как «buy alisa» и сразу выбрать цвет:
    /// «buy alisa pink», «buy alisa 2 blue». Цвет купленных колонок запоминает хост,
    /// и колонки из шаттла появляются уже нужного цвета (см. YandexStationItem.OrderColors).
    /// </summary>
    [HarmonyPatch]
    public static class AliceShop
    {
        /// <summary>Дополнительные слова для покупки колонки, кроме «yandex».</summary>
        public static string[] Aliases = { "alisa", "alice", "алиса", "алису" };

        // Слова цветов по порядку YandexStationItem.bodyColors: серая, голубая, розовая, чёрная
        private static readonly string[][] ColorWords =
        {
            new[] { "grey", "gray", "silver", "сер" },
            new[] { "blue", "cyan", "голуб", "син" },
            new[] { "pink", "rose", "розов" },
            new[] { "black", "dark", "черн", "чёрн" },
        };

        private static int pendingColor = -1;
        private static int orderedBefore, creditsBefore;

        // LethalLib добавляет предметы в Terminal.Awake, Start идёт после
        [HarmonyPatch(typeof(Terminal), "Start")]
        [HarmonyPostfix]
        private static void AddAliases(Terminal __instance)
        {
            YandexStationItem.RegisterMessages();
            var nodes = __instance.terminalNodes;
            var buy = nodes.allKeywords.FirstOrDefault(k => k.word == "buy");
            var info = nodes.allKeywords.FirstOrDefault(k => k.word == "info");
            var station = buy?.compatibleNouns.FirstOrDefault(n => IsStation(__instance, n.result));
            if (station == null)
            {
                Plugin.Log.LogWarning("Алиса: не нашла колонку в магазине терминала, «buy alisa» не работает");
                return;
            }

            var infoNode = info?.compatibleNouns.FirstOrDefault(n => n.noun == station.noun)?.result;
            var keywords = nodes.allKeywords.ToList();
            var buyNouns = buy.compatibleNouns.ToList();
            var infoNouns = info != null ? info.compatibleNouns.ToList() : null;
            foreach (var alias in Aliases)
            {
                if (keywords.Any(k => k.word == alias))
                    continue;
                var keyword = ScriptableObject.CreateInstance<TerminalKeyword>();
                keyword.name = "Alisa-" + alias;
                keyword.word = alias;
                keyword.defaultVerb = buy;
                keywords.Add(keyword);
                buyNouns.Add(new CompatibleNoun(keyword, station.result));
                if (infoNouns != null && infoNode != null)
                    infoNouns.Add(new CompatibleNoun(keyword, infoNode));
            }
            nodes.allKeywords = keywords.ToArray();
            buy.compatibleNouns = buyNouns.ToArray();
            if (infoNouns != null)
                info.compatibleNouns = infoNouns.ToArray();
        }

        // Запоминаем цвет из введённой строки: «buy alisa pink»
        [HarmonyPatch(typeof(Terminal), nameof(Terminal.ParsePlayerSentence))]
        [HarmonyPrefix]
        private static void ReadColor(Terminal __instance)
        {
            var text = __instance.screenText.text;
            var added = Mathf.Clamp(__instance.textAdded, 0, text.Length);
            var words = text.Substring(text.Length - added).ToLowerInvariant()
                .Split(new[] { ' ', ',', '.' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words[0] == "confirm" || words[0] == "c" || "confirm".StartsWith(words[0]))
                return; // подтверждение покупки: цвет остаётся от прошлой строки
            pendingColor = -1;
            foreach (var word in words)
            {
                for (var c = 0; c < ColorWords.Length; c++)
                {
                    if (ColorWords[c].Any(word.StartsWith))
                        pendingColor = c;
                }
            }
        }

        // Покупка: CONFIRM вызывает LoadNewNodeIfAffordable, и он добавляет предметы в заказ
        [HarmonyPatch(typeof(Terminal), nameof(Terminal.LoadNewNodeIfAffordable))]
        [HarmonyPrefix]
        private static void BeforeBuy(Terminal __instance)
        {
            orderedBefore = __instance.orderedItemsFromTerminal.Count;
            creditsBefore = __instance.groupCredits;
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.LoadNewNodeIfAffordable))]
        [HarmonyPostfix]
        private static void AfterBuy(Terminal __instance, TerminalNode node)
        {
            if (node == null || node.isConfirmationNode || !IsStation(__instance, node))
                return;
            var bought = __instance.orderedItemsFromTerminal.Count - orderedBefore;
            if (bought <= 0 && node.itemCost > 0)
                bought = (creditsBefore - __instance.groupCredits) / node.itemCost;
            if (bought <= 0)
                return; // не хватило кредитов или места в шаттле
            YandexStationItem.OrderColors(pendingColor, bought);
            pendingColor = -1;
        }

        private static bool IsStation(Terminal terminal, TerminalNode node)
        {
            if (node == null || node.buyItemIndex < 0 || node.buyItemIndex >= terminal.buyableItemsList.Length)
                return false;
            var item = terminal.buyableItemsList[node.buyItemIndex];
            return item != null && item.spawnPrefab != null && item.spawnPrefab.GetComponent<YandexStationItem>() != null;
        }
    }
}
