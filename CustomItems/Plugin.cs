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

            Register<Monitor27Item>(bundle, new CustomItemDef
            {
                Name = "Computer monitor",
                ModelAsset = "Monitor27",
                TemplateItem = "Cash register", // двуручная анимация переноски
                MinValue = 100,
                MaxValue = 220,
                DefaultRarity = 20,
                Weight = 1.28f,
                TwoHanded = true,
                VerticalOffset = 0.24f, // половина высоты: модель стоит на подставке
            });

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
