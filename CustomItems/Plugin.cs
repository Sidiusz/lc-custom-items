using System;
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
            SetupJagermeister();
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

        private void SetupYandexStation(string dir)
        {
            const string section = "Yandex Station";
            YandexStationItem.WakeWord = Config.Bind(section, "WakeWord", "алиса",
                "Слово, на которое колонка откликается (как его пишет распознавание, строчными)").Value.Trim().ToLowerInvariant();
            YandexStationItem.HearingRadius = Config.Bind(section, "HearingRadius", 8f,
                "С какого расстояния (м) к колонке можно обращаться голосом").Value;
            YandexStationItem.ListenSilence = Config.Bind(section, "ListenSilence", 3f,
                "Сколько секунд тишины после «Алиса» (или вопроса колонки) она ждёт, потом выключается сигналом. Пока говорят — ждёт").Value;
            YandexStationItem.ListenMax = Config.Bind(section, "ListenMax", 12f,
                "Дольше этого (секунды) колонка не слушает, даже если говорят без остановки").Value;
            YandexStationItem.ThinkMin = Config.Bind(section, "ThinkMin", 0.7f,
                "Пауза «думает» перед ответом, от (секунды)").Value;
            YandexStationItem.ThinkMax = Config.Bind(section, "ThinkMax", 1.5f,
                "Пауза «думает» перед ответом, до (секунды)").Value;
            YandexStationItem.AllowInterrupt = Config.Bind(section, "AllowInterrupt", false,
                "Можно задать новый вопрос, пока колонка ещё отвечает. По умолчанию она дослушивает себя и только потом слушает других").Value;
            YandexStationItem.JammingWeathers = Config.Bind(section, "JammingWeathers", "Solar Flare",
                "Погоды через запятую (как их называет игра или WeatherRegistry), при которых колонка на луне сбоит и вместо ответа выдаёт ошибку").Value
                .Split(',').Select(w => w.Trim()).Where(w => w.Length > 0).ToArray();
            new Harmony(PluginGuid + ".alisa").PatchAll(typeof(AliceShop));

            YandexStationItem.SetNames(Config.Bind(section, "Names",
                "Sidiusz=Daniil; Sinon=Andrey; Senya,Arseniy,Арсений,Shakabuy=Arseniy; Pavlezki,Pavel=Pavel",
                "Друзья по нику Steam: «ник,ник=Папка» через точку с запятой. На «Алиса, как меня зовут?» колонка говорит фразу " +
                "из Phrases/Names/<Папка> (незнакомому — из Names/Unknown), а при смерти игрока может сказать Events/PlayerDied/<Папка>. " +
                "Ники сравниваются без регистра и значков, ник может быть длиннее (Sidiusz* подходит к Sidiusz)").Value);
            var spoken = Config.Bind(section, "SpokenNames",
                "Daniil=даниил,данил,даня,сидиус; Andrey=андрей,андрюха,андрюша,синон; Arseniy=арсений,сеня,арсен,шакабуй; " +
                "Pavel=павел,паша,пашка,пашок,павлецки",
                "Как друзей зовут вслух: «Папка=имя,имя» через точку с запятой, строчными. Падежи (Пашей, Пашу, Пашка) " +
                "колонка добавляет сама. Это {name} в intents.txt: «Алиса, где Андрей?», «чё с Пашей?», «Сеня умер?»").Value;
            YandexStationItem.SetSpokenNames(spoken);
            YandexStationItem.ShushSeconds = Config.Bind(section, "ShushSeconds", 60f,
                "Столько секунд после «замолчи» колонка не говорит сама (события)").Value;

            const string events = "Yandex Station Events";
            YandexStationItem.EventsEnabled = Config.Bind(events, "Enabled", true,
                "Колонка сама изредка реагирует на то, что вокруг: монстр рядом, посадка, смерть игрока, ночь, мало заряда. " +
                "Фразы в папках Phrases/Events/<Тема>; тема без фраз молчит").Value;
            YandexStationItem.EventChance = Rarer(Config.Bind(events, "Chance", 0.2f,
                "Шанс (0..1), что колонка что-то скажет на повод"), 0.35f);
            YandexStationItem.EventCooldown = Rarer(Config.Bind(events, "Cooldown", 90f,
                "Сколько секунд колонка молчит после реплики на событие"), 45f);
            YandexStationItem.EventTopicCooldown = Rarer(Config.Bind(events, "TopicCooldown", 600f,
                "Одна и та же тема (например, та же собака) не чаще, чем раз в столько секунд"), 180f);
            YandexStationItem.EventRadius = Config.Bind(events, "EnemyRadius", 15f,
                "С какого расстояния (м) колонка замечает монстра").Value;
            YandexStationItem.EventNoiseRange = Config.Bind(events, "NoiseRange", 25f,
                "Как далеко монстры слышат реплику на событие (м). Слепая собака прибежит на голос").Value;
            YandexStationItem.IdleSeconds = Rarer(Config.Bind(events, "IdleSeconds", 0f,
                "Примерно раз во столько секунд колонка может поболтать сама без повода (тема Idle), если рядом игрок. 0 — никогда"), 120f, 240f);
            YandexStationItem.RepeatChance = Config.Bind(section, "RepeatChance", 0.5f,
                "Шанс (0..1) «Я уже отвечала» на тот же вопрос второй раз за минуту; иначе отвечает снова").Value;
            YandexStationItem.TimeInNumbers = Config.Bind(section, "TimeInNumbers", true,
                "«Который час» числами из кусков: «На часах пятнадцать часов тридцать минут» (Time/Intro, Numbers, " +
                "Time/Hours, Time/Minutes). Не хватает куска или false — словами из Time/<час> («Почти семь»)").Value;
            YandexStationItem.ListenVoiceChance = Config.Bind(section, "ListenVoiceChance", 0.3f,
                "Шанс (0..1) на «Алиса» без вопроса ответить «Слушаю!» (папка Listen), а не сигналом").Value;
            YandexStationItem.WhimChance = Config.Bind(section, "WhimChance", 0.15f,
                "Шанс (0..1), что колонка капризничает вместо ответа: «Нет связи с сервером» (Error), «Не хочу» (Refuse), " +
                "«Вы что-то бормочете?» (Mumble) или «Не поняла» (Unknown). На солнечной вспышке ошибка всегда").Value;

            var voice = Config.Bind(section, "VoiceCommands", true,
                "Отвечать на голос из голосового чата («Алиса, какая погода?»). Распознаётся офлайн и только когда " +
                "игрока слышно в чате рядом с включённой колонкой. Выключите, если не нужно или тормозит").Value;
            if (!voice)
                return;
            AliceListener.HostListens = Config.Bind(section, "HostListens", true,
                "Распознаёт хост, сразу всех игроков: свой микрофон и голоса остальных из голосового чата. Колонка " +
                "понимает любого, ответ один. false — каждый распознаёт свой микрофон у себя (если у хоста слабый ПК)").Value;

            // Своя модель знает и обычную речь, и слова Алисы (луны, предметы), поэтому по умолчанию без грамматики:
            // старый UseGrammar = true (для маленькой модели) хуже — новый ключ, чтобы он не остался включённым
            var grammar = Config.Bind(section, "OnlyKnownPhrases", false,
                "Распознавать только известные фразы (имя, погода, квота, дедлайн, время, шутка...) и их сочетания. " +
                "Модель и так знает слова Алисы, а с этой настройкой путает «Алиса» с похожими словами").Value;
            var extra = Config.Bind(section, "ExtraPhrases", "",
                "Свои фразы для распознавания через запятую (для своих тем в Phrases), строчными").Value;
            LoadIntents();
            AliceRoute.Install();
            AliceShip.Install();
            if (grammar)
            {
                AliceListener.Grammar = AliceIntents.Phrases.Concat(AliceIntents.Vocabulary)
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

        // Вопросы Алисы: встроенный intents.txt и свой файл в папке конфига (дополняет встроенный)
        private void LoadIntents()
        {
            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream("SidiuszCustomItems.intents.txt"))
            {
                if (stream != null)
                    using (var reader = new StreamReader(stream))
                        AliceIntents.Load(reader.ReadToEnd());
            }
            var own = Path.Combine(Paths.ConfigPath, "SidiuszCustomItems.intents.txt");
            try
            {
                if (!File.Exists(own))
                {
                    File.WriteAllText(own,
                        "# Свои вопросы Алисы, дополняют встроенные. Строка: «Тема: пример, пример, пример».\n" +
                        "# Тема — папка фраз в Phrases (Joke, Events/Suit…) или особая: #whereis, #time, #battery, #count, #enemies.\n" +
                        "# {name} — любой друг из SpokenNames в любом падеже. Примеры:\n" +
                        "# Joke: насмеши меня, давай прикол\n" +
                        "# #whereis: как там {name}, {name} где\n");
                }
                AliceIntents.Load(File.ReadAllText(own));
            }
            catch (Exception e)
            {
                Log.LogWarning("Алиса: не прочитала " + own + ": " + e.Message);
            }
            Log.LogInfo($"Алиса: примеров вопросов {AliceIntents.Count}");
        }

        // События стали реже: старые значения по умолчанию в уже созданном конфиге меняем на новые
        private static float Rarer(BepInEx.Configuration.ConfigEntry<float> entry, params float[] oldDefaults)
        {
            if (oldDefaults.Any(d => Mathf.Approximately(entry.Value, d)))
                entry.Value = (float)entry.DefaultValue;
            return entry.Value;
        }

        private void SetupJagermeister()
        {
            const string section = "Jagermeister";
            VomitEffects.WeakPlayers = Config.Bind(section, "WeakPlayers", "Senya,Shakabuy",
                "Ники через запятую (регистр и значки не важны, хватает начала ника): этих тошнит с первого глотка и они рыгают").Value.Split(',');
            VomitEffects.VomitAfterSips = Mathf.Max(1, Config.Bind(section, "VomitAfterSips", 2,
                "Со скольких одновременно действующих глотков игрока начинает тошнить").Value);
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
            go.AddComponent<AliceFurniture>();
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
            ToyGunItem.ReloadTipFormat = "Перезарядить : [{0}]";
            ToyGunItem.NoAmmoHeader = "Игрушечный пистолет";
            ToyGunItem.NoAmmoBody = "В инвентаре нет пулек. Их можно купить в магазине терминала.";
            ToyGunAmmoItem.TipFormat = "Пулек в пачке : {0}";
            YandexStationItem.OnOffTip = "Вкл/выкл : [LMB]";
            YandexStationItem.TalkTip = "Поболтать : [E]";

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
