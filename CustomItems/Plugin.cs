using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using CustomItems.Items;
using LethalLib.Modules;
using Unity.Netcode;
using UnityEngine;

namespace CustomItems
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("evaisa.lethallib")]
    [BepInDependency("lethalrussian.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = ModInfo.Guid;
        public const string PluginName = "SidiuszCustomItems";
        public const string PluginVersion = "0.1.0";

        // Имя файла AssetBundle, который собирается в Unity
        private const string BundleName = "sidiuszcustomitems";

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

            var registry = bundle.LoadAllAssets<ItemRegistry>().FirstOrDefault();
            if (registry == null)
            {
                Log.LogError("В бандле нет ItemRegistry: соберите бандл заново (README, раздел 2)");
                return;
            }

            foreach (var entry in registry.items)
                Register(entry);

            // Настройки поведения, которые не живут в ассетах: их удобно менять без пересборки
            AdrenalineItem.SleepyPlayers = Config.Bind("Adrenaline Game Fuel", "SleepyPlayers", "Sidiusz",
                "Ники через запятую: этих игроков энергетик не бодрит, а вырубает").Value.Split(',');
            AdrenalineItem.BoostDuration = Config.Bind("Adrenaline Game Fuel", "BoostSeconds", 30f,
                "Сколько секунд стамина восстанавливается быстрее").Value;
            AdrenalineItem.SleepDuration = Config.Bind("Adrenaline Game Fuel", "SleepSeconds", 10f,
                "Сколько секунд спит игрок из SleepyPlayers").Value;
            ToyGunItem.DamagePerPellet = Config.Bind("Toy gun", "Damage", 0.1f,
                "Урон за пульку. Дробная часть копится: при 0.1 каждые 10 попаданий в одну цель наносят 1 урон").Value;

            LoadRussianTranslation(Path.Combine(dir, "translations"));

            Log.LogInfo($"{PluginName} {PluginVersion} загружен");
        }

        private void Register(ItemRegistry.Entry entry)
        {
            var item = entry.item;
            var prefab = item != null ? item.spawnPrefab : null;
            if (prefab == null || prefab.GetComponent<GrabbableObject>() == null)
            {
                Log.LogError($"Пропускаю '{(item != null ? item.name : "пустую строку")}' в ItemRegistry: " +
                             "у Item нет Spawn Prefab или на префабе нет скрипта предмета");
                return;
            }

            // Секция конфига по английскому названию: так она не меняется при смене языка
            var section = item.itemName;
            PreparePrefab(prefab);
            LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(prefab);

            // Значения из Unity служат умолчаниями, конфиг BepInEx их переопределяет
            var rarity = Config.Bind(section, "Rarity", entry.rarity,
                "Шанс появления как скрап (вес относительно других предметов, 0 = не спавнится)").Value;
            var inShop = Config.Bind(section, "InShop", entry.inShop, "Продавать в магазине терминала").Value;
            var price = Config.Bind(section, "Price", entry.price, "Цена в магазине").Value;

            var asScrap = item.isScrap && rarity > 0;
            if (asScrap)
                LethalLib.Modules.Items.RegisterScrap(item, rarity, Levels.LevelTypes.All);
            if (inShop)
                LethalLib.Modules.Items.RegisterShopItem(item, price);
            if (!asScrap && !inShop)
                LethalLib.Modules.Items.RegisterItem(item);
        }

        // Названия и подсказки в ассетах всегда английские: русификатор LethalRussian
        // подменяет их по точному английскому тексту и сам склоняет названия
        // («Выбросить плюшевого Уинстона»). Перевод лежит в translations/Sidiusz-CustomItems.txt.
        // LethalRussian вызываем через рефлексию, чтобы мод работал и без него.
        private static void LoadRussianTranslation(string translations)
        {
            var api = System.AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("LethalRussian.Api", false))
                .FirstOrDefault(t => t != null);
            // LoadTranslations(string directory, Func<string, bool> isEnabled): грузит все *.txt
            // из папки, isEnabled = null значит «включено всё» (переключатель заводит сам LethalRussian)
            var load = api?.GetMethod("LoadTranslations", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), typeof(System.Func<string, bool>) }, null);
            if (load == null)
                return; // русификатора нет: остаётся английский

            try
            {
                var count = load.Invoke(null, new object[] { translations, null });
                Log.LogInfo($"Перевод для LethalRussian загружен ({count})");
            }
            catch (System.Exception e)
            {
                Log.LogWarning("Не удалось передать перевод в LethalRussian: " + e);
            }
        }

        // Тег и слои игры есть не в каждом Unity-проекте, поэтому ставим их здесь по именам.
        // Хеш NetworkObject в редакторе без пакета Netcode не считается, задаём свой
        // постоянный: у всех игроков он одинаковый, пока не переименован префаб.
        private static void PreparePrefab(GameObject prefab)
        {
            prefab.tag = "PhysicsProp";
            var props = LayerMask.NameToLayer("Props");
            var scanNode = LayerMask.NameToLayer("ScanNode");
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
            {
                var isScanNode = t.GetComponent<ScanNodeProperties>() != null;
                t.gameObject.layer = isScanNode ? scanNode : props;
            }

            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject != null)
                GlobalObjectIdHash?.SetValue(networkObject, StableHash(PluginGuid + "/" + prefab.name));
        }

        private static readonly FieldInfo GlobalObjectIdHash =
            typeof(NetworkObject).GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static uint StableHash(string text)
        {
            // FNV-1a: не зависит от рантайма, в отличие от string.GetHashCode
            var hash = 2166136261u;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return hash;
        }
    }
}
