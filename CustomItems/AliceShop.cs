using System.Linq;
using CustomItems.Items;
using HarmonyLib;
using UnityEngine;

namespace CustomItems
{
    /// <summary>
    /// Лишние слова для покупки в терминале, кроме названий от LethalLib («toy-gun-pellets»):
    /// колонку можно купить как «buy alisa» (цвет каждой выпадает случайно), пульки — «buy пачка пулек».
    /// </summary>
    [HarmonyPatch]
    public static class AliceShop
    {
        /// <summary>Дополнительные слова для покупки колонки, кроме «yandex».</summary>
        public static string[] Aliases = { "alisa", "alice", "алиса", "алису" };

        /// <summary>Слова для пачки пулек. Терминал узнаёт и начало слова: «пулек» найдёт «пульки».</summary>
        public static string[] PelletAliases = { "pellets", "pellet", "пульки", "пулек", "пульку", "пачка", "пачку", "патроны" };

        // LethalLib добавляет предметы в Terminal.Awake, Start идёт после
        [HarmonyPatch(typeof(Terminal), "Start")]
        [HarmonyPostfix]
        private static void AddAliases(Terminal __instance)
        {
            var nodes = __instance.terminalNodes;
            var buy = nodes.allKeywords.FirstOrDefault(k => k.word == "buy");
            var info = nodes.allKeywords.FirstOrDefault(k => k.word == "info");
            if (buy == null)
                return;
            var keywords = nodes.allKeywords.ToList();
            var buyNouns = buy.compatibleNouns.ToList();
            var infoNouns = info != null ? info.compatibleNouns.ToList() : null;

            void Add<T>(string what, string[] aliases) where T : Component
            {
                var noun = buy.compatibleNouns.FirstOrDefault(n => Sells<T>(__instance, n.result));
                if (noun == null)
                {
                    Plugin.Log.LogWarning($"Терминал: не нашла {what} в магазине, русские названия не работают");
                    return;
                }
                var infoNode = info?.compatibleNouns.FirstOrDefault(n => n.noun == noun.noun)?.result;
                foreach (var alias in aliases)
                {
                    if (keywords.Any(k => k.word == alias))
                        continue;
                    var keyword = ScriptableObject.CreateInstance<TerminalKeyword>();
                    keyword.name = typeof(T).Name + "-" + alias;
                    keyword.word = alias;
                    keyword.defaultVerb = buy;
                    keywords.Add(keyword);
                    buyNouns.Add(new CompatibleNoun(keyword, noun.result));
                    if (infoNouns != null && infoNode != null)
                        infoNouns.Add(new CompatibleNoun(keyword, infoNode));
                }
            }

            Add<YandexStationItem>("колонку", Aliases);
            Add<ToyGunAmmoItem>("пачку пулек", PelletAliases);

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

        private static bool Sells<T>(Terminal terminal, TerminalNode node) where T : Component
        {
            if (node == null || node.buyItemIndex < 0 || node.buyItemIndex >= terminal.buyableItemsList.Length)
                return false;
            var item = terminal.buyableItemsList[node.buyItemIndex];
            return item != null && item.spawnPrefab != null && item.spawnPrefab.GetComponent<T>() != null;
        }
    }
}
