using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace CustomItems
{
    /// <summary>
    /// Шанс скрапа задаётся долей от всего скрапа луны, а не весом. Вес LethalLib добавляет
    /// к весам ванильных предметов, а их сумма на луне — сотни и тысячи, да ещё и другие моды
    /// докидывают свой скрап: вес 30 давал нашим предметам доли процента. Перед спавном скрапа
    /// (его делает хост) вес каждого нашего предмета пересчитывается так, чтобы он выпадал
    /// с шансом ScrapChance процентов на каждый предмет скрапа, на любой луне и с любыми модами.
    /// </summary>
    [HarmonyPatch]
    internal static class ScrapSpawn
    {
        // Item → шанс в долях (0.03 = 3%)
        internal static readonly Dictionary<Item, float> Chances = new Dictionary<Item, float>();

        // Наши предметы вместе не займут больше этой доли скрапа
        private const float MaxTotal = 0.9f;

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnScrapInLevel))]
        [HarmonyPrefix]
        private static void SpawnScrapInLevel(RoundManager __instance)
        {
            var scrap = __instance.currentLevel != null ? __instance.currentLevel.spawnableScrap : null;
            if (scrap == null || Chances.Count == 0)
                return;

            var ours = scrap.Where(s => s?.spawnableItem != null && Chances.ContainsKey(s.spawnableItem)).ToList();
            if (ours.Count == 0)
                return;
            var others = scrap.Where(s => s?.spawnableItem != null && !Chances.ContainsKey(s.spawnableItem)).Sum(s => s.rarity);
            if (others <= 0)
                return;

            var items = ours.Select(s => s.spawnableItem).Distinct().ToList();
            var total = items.Sum(i => Chances[i]);
            var scale = total > MaxTotal ? MaxTotal / total : 1f;
            total *= scale;

            foreach (var item in items)
            {
                // Доля p от всего скрапа: вес = others * p / (1 - сумма наших долей)
                var weight = others * Chances[item] * scale / (1f - total);
                var entries = ours.Where(s => s.spawnableItem == item).ToList();
                foreach (var entry in entries)
                    entry.rarity = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.RoundToInt(weight / entries.Count));
            }

            Plugin.Log.LogInfo($"Скрап на {__instance.currentLevel.PlanetName}: вес остальных {others}, наши " +
                               string.Join(", ", items.Select(i => $"{i.itemName} {Chances[i] * scale * 100f:0.#}%")));
        }
    }
}
