using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using CustomItems.Items;
using LethalLib.Modules;
using UnityEngine;

namespace CustomItems
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("evaisa.lethallib")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "sidiusz.customitems";
        public const string PluginName = "CustomItems";
        public const string PluginVersion = "0.1.0";

        // Имя файла AssetBundle, который собирается в Unity
        private const string BundleName = "customitems";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;

            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var bundle = AssetBundle.LoadFromFile(Path.Combine(dir, BundleName));
            if (bundle == null)
            {
                Log.LogError($"Не найден AssetBundle '{BundleName}' рядом с DLL ({dir})");
                return;
            }

            // ---- Предметы. Новый предмет = описание + класс поведения + одна строка Register ----

            var duck = Register<RubberDuckItem>(bundle, new CustomItemDef
            {
                Name = "Rubber duck",
                ModelAsset = "RubberDuck",
                MinValue = 60,
                MaxValue = 150,
                DefaultRarity = 30,
                Weight = 1.05f,
                ToolTips = new[] { "Squeak : [LMB]" },
            });
            if (duck != null)
                duck.squeakClip = bundle.LoadAsset<AudioClip>("Squeak");

            var adrenaline = Register<AdrenalineItem>(bundle, new CustomItemDef
            {
                Name = "Adrenaline Game Fuel",
                ModelAsset = "AdrenalineCan",
                IconAsset = "AdrenalineCanIcon",
                TemplateItem = "Red soda",
                MinValue = 80,
                MaxValue = 160,
                DefaultRarity = 30,
                Weight = 1.01f,
                VerticalOffset = 0.084f, // центр модели на середине банки высотой 16.8 см
                ToolTips = new[] { "Drink : [LMB]" },
            });
            if (adrenaline != null)
            {
                adrenaline.itemProperties.saveItemVariable = true; // пустая банка остаётся пустой после сохранения
                AdrenalineItem.SleepyPlayers = Config.Bind("Adrenaline Game Fuel", "SleepyPlayers", "Sidiusz",
                    "Ники через запятую: этих игроков энергетик не бодрит, а вырубает").Value.Split(',');
                AdrenalineItem.BoostDuration = Config.Bind("Adrenaline Game Fuel", "BoostSeconds", 30f,
                    "Сколько секунд стамина восстанавливается быстрее").Value;
                AdrenalineItem.SleepDuration = Config.Bind("Adrenaline Game Fuel", "SleepSeconds", 10f,
                    "Сколько секунд спит игрок из SleepyPlayers").Value;
            }

            Log.LogInfo($"{PluginName} {PluginVersion} загружен");
        }

        private T Register<T>(AssetBundle bundle, CustomItemDef def) where T : CustomGrabbable
        {
            var behaviour = ItemFactory.Create<T>(bundle, def);
            if (behaviour == null)
                return null;

            var item = behaviour.itemProperties;
            var section = def.Name;

            var rarity = Config.Bind(section, "Rarity", def.DefaultRarity,
                "Шанс появления как скрап (вес относительно других предметов, 0 = не спавнится)").Value;
            var inShop = Config.Bind(section, "InShop", def.DefaultInShop, "Продавать в магазине терминала").Value;
            var price = Config.Bind(section, "Price", def.DefaultPrice, "Цена в магазине").Value;

            if (def.IsScrap && rarity > 0)
                LethalLib.Modules.Items.RegisterScrap(item, rarity, Levels.LevelTypes.All);
            if (inShop)
                LethalLib.Modules.Items.RegisterShopItem(item, price);
            if (!(def.IsScrap && rarity > 0) && !inShop)
                LethalLib.Modules.Items.RegisterItem(item);

            return behaviour;
        }
    }
}
