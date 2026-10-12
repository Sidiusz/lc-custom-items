using System.Linq;
using CustomItems.Items;
using HarmonyLib;
using UnityEngine;

namespace CustomItems
{
    /// <summary>
    /// Колонку в терминале можно купить как «buy alisa» (цвет каждой выпадает случайно).
    /// </summary>
    [HarmonyPatch]
    public static class AliceShop
    {
        /// <summary>Дополнительные слова для покупки колонки, кроме «yandex».</summary>
        public static string[] Aliases = { "alisa", "alice", "алиса", "алису" };

        // LethalLib добавляет предметы в Terminal.Awake, Start идёт после
        [HarmonyPatch(typeof(Terminal), "Start")]
        [HarmonyPostfix]
        private static void AddAliases(Terminal __instance)
        {
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

        // Рожок и горн (NoisemakerProp): колонка рядом может сказать Events/Horn. ItemActivate идёт у всех, решает хост
        [HarmonyPatch(typeof(NoisemakerProp), nameof(NoisemakerProp.ItemActivate))]
        [HarmonyPostfix]
        private static void Horn(NoisemakerProp __instance)
        {
            YandexStationItem.OnHorn(__instance.transform.position);
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
