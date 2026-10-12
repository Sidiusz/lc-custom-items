using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using CustomItems.Items;
using LethalLib.Modules;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

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

            // Настройки из игрового окна (BepInEx/config/SidiuszCustomItems.tuning.txt) поверх ассетов
            ItemTuningStore.Load(Paths.ConfigPath);

            foreach (var entry in registry.items)
                Register(entry);
            foreach (var entry in registry.furniture)
                RegisterFurniture(entry);

            SetupItemTuner();

            // Настройки поведения, которые не живут в ассетах: их удобно менять без пересборки
            const string adrenaline = "Adrenaline Game Fuel";
            AdrenalineItem.SleepyPlayers = Config.Bind(adrenaline, "SleepyPlayers", "Sidiusz",
                "Ники через запятую (регистр и значки вроде * не важны): этих игроков энергетик может вырубить").Value.Split(',');
            AdrenalineItem.SleepChance = Config.Bind(adrenaline, "SleepChance", 20f,
                "Шанс в процентах, что игрок из SleepyPlayers уснёт от глотка").Value;
            AdrenalineItem.SleepDuration = Config.Bind(adrenaline, "SleepSeconds", 10f,
                "Сколько секунд спит игрок из SleepyPlayers").Value;
            AdrenalineItem.BoostDuration = Config.Bind(adrenaline, "BoostSeconds", 30f,
                "Сколько секунд после глотка стамина восстанавливается быстрее").Value;
            AdrenalineItem.HeartAttackChancePerSip = Config.Bind(adrenaline, "HeartAttackChancePerSip", 1f,
                "Шанс сердечного приступа в процентах за каждый глоток за день: 1-й глоток 1%, 2-й 2%, 3-й 3%...").Value;
            SetupToyGun();
            SetupYandexStation(dir);

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
            ItemTuningStore.Register(item);

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

        // ---- Мебель: покупается в Decor терминала, ставится через режим строительства (B) ----

        private void RegisterFurniture(ItemRegistry.FurnitureEntry entry)
        {
            var prefab = entry.prefab;
            if (prefab == null || prefab.GetComponent<NetworkObject>() == null || string.IsNullOrEmpty(entry.name))
            {
                Log.LogError($"Пропускаю мебель '{entry.name}' в ItemRegistry: нет префаба с NetworkObject или названия");
                return;
            }

            var inShop = Config.Bind(entry.name, "InShop", true, "Продавать в разделе Decor терминала").Value;
            var price = Config.Bind(entry.name, "Price", entry.price, "Цена в магазине").Value;
            if (!inShop)
                return;

            PrepareFurniture(prefab);
            LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(prefab);
            var unlockable = new UnlockableItem
            {
                unlockableName = entry.name,
                prefabObject = prefab,
                unlockableType = 1, // предмет корабля, а не костюм
                IsPlaceable = true,
                spawnPrefab = true,
                alwaysInStock = true, // в Decor не по ротации, а всегда
                canBeStored = true,
                maxNumber = 1,
            };
            LethalLib.Modules.Unlockables.RegisterUnlockable(unlockable, StoreType.Decor, null, null, null, price);
        }

        // Компоненты мебели игры навешиваем здесь, а не в Unity: так в проекте не нужны их
        // заглушки с полями. В префабе из Unity есть дочерние PlacementCollider (за него
        // хватает режим строительства) и InteractTrigger (E), оба с BoxCollider.
        private static void PrepareFurniture(GameObject prefab)
        {
            var networkObject = prefab.GetComponent<NetworkObject>();
            GlobalObjectIdHash?.SetValue(networkObject, StableHash(PluginGuid + "/" + prefab.name));

            var parent = prefab.GetComponent<AutoParentToShip>() ?? prefab.AddComponent<AutoParentToShip>();

            var placement = prefab.transform.Find("PlacementCollider");
            var body = prefab.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == "Body")
                       ?? prefab.GetComponentInChildren<MeshFilter>(true);
            if (placement != null)
            {
                placement.gameObject.layer = Layer("PlaceableShipObjects", 26);
                var placeable = placement.GetComponent<PlaceableShipObject>() ?? placement.gameObject.AddComponent<PlaceableShipObject>();
                placeable.parentObject = parent;
                placeable.mainMesh = body;
                placeable.mainTransform = prefab.transform;
                placeable.placeObjectCollider = placement.GetComponent<Collider>();
                placeable.yOffset = HeightAboveFloor(prefab);
                placeable.AllowPlacementOnCounters = true;
            }
            else
            {
                Log.LogError($"{prefab.name}: нет дочернего PlacementCollider, поставить через B не получится");
            }

            var interact = prefab.transform.Find("InteractTrigger");
            if (interact != null)
            {
                interact.gameObject.layer = Layer("InteractableObject", 9);
                interact.gameObject.tag = "InteractTrigger";
                var trigger = interact.GetComponent<InteractTrigger>() ?? interact.gameObject.AddComponent<InteractTrigger>();
                trigger.hoverTip = YandexStationItem.InteractTip;
                trigger.interactable = true;
                trigger.oneHandedItemAllowed = true;
                trigger.twoHandedItemAllowed = true;
                trigger.interactCooldown = true;
                trigger.cooldownTime = 1f;
                trigger.holdingInteractEvent = new InteractEventFloat();
                trigger.onInteract = new InteractEvent();
                trigger.onInteractEarly = new InteractEvent();
                trigger.onInteractEarlyOtherClients = new InteractEvent();
                trigger.onStopInteract = new InteractEvent();
                trigger.onCancelAnimation = new InteractEvent();
            }
        }

        // Режим строительства ставит центр префаба на высоту yOffset над полом
        private static float HeightAboveFloor(GameObject prefab)
        {
            var min = float.MaxValue;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null)
                    continue;
                var b = mf.sharedMesh.bounds;
                var p = prefab.transform.InverseTransformPoint(mf.transform.TransformPoint(b.min));
                min = Mathf.Min(min, p.y);
            }
            return min == float.MaxValue ? 0.05f : -min;
        }

        private static int Layer(string name, int fallback)
        {
            var layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
        }

        private void SetupYandexStation(string dir)
        {
            const string section = "Yandex Station";
            YandexStationItem.WakeWord = Config.Bind(section, "WakeWord", "алиса",
                "Слово, на которое колонка откликается (как его пишет распознавание, строчными)").Value.Trim().ToLowerInvariant();
            YandexStationItem.HearingRadius = Config.Bind(section, "HearingRadius", 8f,
                "С какого расстояния (м) к колонке можно обращаться голосом").Value;
            var voice = Config.Bind(section, "VoiceCommands", true,
                "Распознавать голос («Алиса, какая погода?»). Распознаётся только у вас на компьютере, офлайн, " +
                "и только пока рядом стоит колонка. Выключите, если не нужно или тормозит").Value;
            if (!voice)
                return;

            var grammar = Config.Bind(section, "UseGrammar", true,
                "Распознавать только известные фразы (имя, погода, квота, дедлайн, время, шутка...) и их сочетания. " +
                "Так маленькая русская модель почти не ошибается. Выключите, чтобы распознавалось всё подряд").Value;
            var extra = Config.Bind(section, "ExtraPhrases", "",
                "Свои фразы для распознавания через запятую (для своих тем в Phrases), строчными").Value;
            if (grammar)
            {
                AliceListener.Grammar = AliceListener.DefaultGrammar
                    .Concat(new[] { YandexStationItem.WakeWord })
                    .Concat(extra.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
                    .Distinct().ToArray();
            }

            AliceListener.VoskDir = Path.Combine(dir, "vosk");
            if (!Directory.Exists(Path.Combine(AliceListener.VoskDir, "model-ru")))
            {
                Log.LogWarning($"Алиса: нет модели распознавания в {AliceListener.VoskDir}/model-ru, голосовые команды выключены");
                return;
            }
            var go = new GameObject("SidiuszCustomItems.AliceListener") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<AliceListener>();
        }

        private void SetupToyGun()
        {
            ToyGunItem.DamagePerPellet = Config.Bind("Toy gun", "Damage", 0.1f,
                "Урон за пульку. Дробная часть копится: при 0.1 каждые 10 попаданий в одну цель наносят 1 урон").Value;
            ToyGunItem.MagazineSize = Mathf.Max(1, Config.Bind("Toy gun", "MagazineSize", 12,
                "Сколько пулек помещается в магазин").Value);
            ToyGunAmmoItem.BoxSize = Mathf.Max(1, Config.Bind("Toy gun pellets", "PelletsPerBox", 24,
                "Сколько пулек в одной пачке из магазина терминала").Value);
            var keyName = Config.Bind("Toy gun", "ReloadKey", "R",
                "Клавиша перезарядки (имя из Unity Input System: R, T, G...). E тоже перезаряжает").Value;
            if (System.Enum.TryParse<Key>(keyName, true, out var key))
                ToyGunReloadKey.Key = key;
            else
                Log.LogWarning($"Не знаю клавишу '{keyName}', перезарядка на {ToyGunReloadKey.Key}");
            ToyGunItem.ReloadKeyName = ToyGunReloadKey.Key.ToString();

            var go = new GameObject("SidiuszCustomItems.ToyGunReloadKey") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<ToyGunReloadKey>();
        }

        private void SetupItemTuner()
        {
            var enabled = Config.Bind("Item tuner", "Enabled", false,
                "Окно настройки предметов прямо в игре: смещение и поворот в руке, наклон и высота на полу, размер. " +
                "Сохранённое пишется в SidiuszCustomItems.tuning.txt рядом с этим конфигом").Value;
            var keyName = Config.Bind("Item tuner", "Key", "F8",
                "Клавиша, открывающая окно (имя из Unity Input System: F8, F9, Backquote, Insert...)").Value;
            if (!enabled)
                return;

            if (System.Enum.TryParse<Key>(keyName, true, out var key))
                ItemTuner.ToggleKey = key;
            else
                Log.LogWarning($"Не знаю клавишу '{keyName}', окно настройки открывается на {ItemTuner.ToggleKey}");

            // Отдельный объект: объект BepInEx в Lethal Company уничтожается, и его Update не вызывается
            var go = new GameObject("SidiuszCustomItems.ItemTuner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<ItemTuner>();
            new Harmony(PluginGuid + ".tuner").CreateClassProcessor(typeof(ItemTuner.BlockPlayerInput)).Patch();
            Log.LogInfo($"Окно настройки предметов: {ItemTuner.ToggleKey}");
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

            // Строки с числами по точному тексту не переводятся, их переводим сами
            ToyGunItem.AmmoTipFormat = "Пульки : {0}/{1}";
            ToyGunItem.NoAmmoHeader = "Игрушечный пистолет";
            ToyGunItem.NoAmmoBody = "В инвентаре нет пулек. Их можно купить в магазине терминала.";
            ToyGunAmmoItem.TipFormat = "Пулек в пачке : {0}";
            YandexStationItem.InteractTip = "Поговорить с Алисой : [E]";

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
