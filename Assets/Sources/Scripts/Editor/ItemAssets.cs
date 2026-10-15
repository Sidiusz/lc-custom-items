// Создаёт для каждого предмета мода:
//   Assets/Sources/Data/Items/<Предмет>.asset   ассет Item игры (название, стоимость, вес, хват, оффсеты...)
//   Assets/Sources/Prefabs/<Предмет>.prefab     префаб: модель, NetworkObject, коллайдер, Rigidbody,
//                                               AudioSource, скан-нода и скрипт поведения со звуками
//   Assets/Sources/Data/ItemRegistry.asset      список предметов мода: редкость, магазин, цена
// и помечает их бандлом sidiuszcustomitems.
//
// Запускается сам после импорта DLL (Lethal Company → Setup) и создаёт только то, чего ещё нет:
// всё, что вы поменяли в инспекторе, не трогается. Значения ниже нужны только для первого
// создания, дальше источник правды — сами ассеты.
//
// Типы игры и мода берутся по имени через рефлексию, поэтому скрипт компилируется
// и без DLL из Assets/Plugins (тогда он просто ждёт их появления).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class ItemAssets
{
    private const string Art = "Assets/Sources/Art";
    private const string ItemsDir = "Assets/Sources/Data/Items";
    private const string PrefabsDir = "Assets/Sources/Prefabs";
    private const string RegistryPath = "Assets/Sources/Data/ItemRegistry.asset";
    private const string Bundle = "sidiuszcustomitems";

    private class Spec
    {
        public string Name;          // имя файлов ассета и префаба, папки в Art/*/<Name>
        public string ItemName;      // название в игре (и секция конфига BepInEx)
        public string Behaviour;     // класс поведения из SidiuszCustomItems.Items.dll
        public string Template = "Rubber Ducky"; // ванильный предмет-шаблон
        public bool Scrap = true;
        public int MinValue, MaxValue;
        public int Rarity;
        public bool InShop;
        public int Price = 25;
        public float Weight = 1.05f;
        public bool TwoHanded;
        // Оффсеты ниже — от пивота исходной модели (обычно её центра). Префаб собирается
        // с пивотом в нижней точке, и при создании Item они пересчитываются (MovePivotOffsets)
        public float VerticalOffset = 0.1f;
        public Vector3 RestingRotation;
        public Vector3 RotationOffset;
        public bool SyncInteract;                                    // Q/E-взаимодействие у всех игроков
        public string[] ToolTips = new string[0];
        public bool SaveItemVariable;
        public string Icon;                                          // путь к спрайту, необязательно
        public string Texture;                                       // текстура на все меши (HDRP/Lit), необязательно
        public string DropSound;                                     // звук падения, иначе берётся у шаблона
        public (string field, string path)[] Clips = new (string, string)[0];
        public Action<GameObject> Customize;                         // доп. настройка префаба
        public float BatterySeconds;                                 // > 0: работает от батареи столько секунд, ЛКМ вкл/выкл
        public bool Phrases;                                         // фразы по темам из подпапок YandexPhrases (phraseSets)
        public string[] HiddenChildren = new string[0];              // объекты модели, выключенные в префабе
        public Action<Component> SetupBehaviour;                     // доп. поля скрипта предмета
    }

    private static readonly Spec[] Specs =
    {
        new Spec
        {
            Name = "Jagermeister", ItemName = "Jagermeister", Behaviour = "CustomItems.Items.JagermeisterItem",
            Template = "Perfume bottle",
            MinValue = 40, MaxValue = 100, Rarity = 30, Weight = 1.03f,
            VerticalOffset = 0.125f, // центр модели на середине бутылки высотой 25 см
            ToolTips = new[] { "Drink : [LMB]" },
            SaveItemVariable = true, // сохранять оставшиеся глотки
            Clips = new[] { ("drinkClip", Art + "/Audio/Jagermeister/JagermeisterDrink.wav") },
            DropSound = Art + "/Audio/Jagermeister/JagermeisterDrop.wav", // стеклянную бутылку ставят на пол
            Customize = SetupJagermeister,
        },
        new Spec
        {
            Name = "Adrenaline", ItemName = "Adrenaline Game Fuel", Behaviour = "CustomItems.Items.AdrenalineItem",
            Template = "Red soda",
            MinValue = 80, MaxValue = 160, Rarity = 30, Weight = 1.01f,
            VerticalOffset = 0.084f, // центр модели на середине банки высотой 16.8 см
            ToolTips = new[] { "Drink : [LMB]" },
            SaveItemVariable = true, // сохранять оставшиеся глотки
            Icon = Art + "/Textures/Adrenaline/AdrenalineIcon.png",
            Texture = Art + "/Textures/Adrenaline/AdrenalineCan.png",
            HiddenChildren = new[] { "Crushed" }, // мятую пустую банку показывает AdrenalineItem
        },
        new Spec
        {
            Name = "Monitor27", ItemName = "Computer monitor", Behaviour = "CustomItems.Items.Monitor27Item",
            Template = "Cash register", // двуручная анимация переноски
            MinValue = 100, MaxValue = 220, Rarity = 20, Weight = 1.28f, TwoHanded = true,
            VerticalOffset = 0.24f, // половина высоты: модель стоит на подставке
            Texture = Art + "/Textures/Monitor27/Monitor27.png",
            DropSound = Art + "/Audio/Monitor27/Monitor27Drop.wav", // пустой пластиковый корпус
        },
        new Spec
        {
            Name = "ToyGun", ItemName = "Toy gun", Behaviour = "CustomItems.Items.ToyGunItem",
            Template = "Zap gun",
            MinValue = 40, MaxValue = 90, Rarity = 25, Weight = 1.03f,
            ToolTips = new[] { "Shoot : [LMB]", "Reload : [R]" },
            RestingRotation = new Vector3(0f, 0f, 90f), // на полу лежит на боку
            VerticalOffset = 0.03f,                      // половина толщины
            RotationOffset = new Vector3(0f, 90f, 0f),   // стволом вперёд
            SaveItemVariable = true,                     // пульки в магазине
            SyncInteract = true,                         // перезарядка у всех игроков
            Clips = new[]
            {
                ("shotClip", Art + "/Audio/ToyGun/ToyGunShot.wav"),
                ("reloadClip", Art + "/Audio/ToyGun/ToyGunReload.wav"),
                ("dryFireClip", Art + "/Audio/ToyGun/ToyGunDryFire.wav"),
            },
            Customize = SetupToyGun,
        },
        new Spec
        {
            Name = "ToyGunAmmo", ItemName = "Toy gun pellets", Behaviour = "CustomItems.Items.ToyGunAmmoItem",
            Scrap = false, Rarity = 0, InShop = true, Price = 15, Weight = 1.01f,
            VerticalOffset = 0.046f,  // половина высоты коробки с клапаном
            SaveItemVariable = true,  // остаток пулек в пачке
            Texture = Art + "/Textures/ToyGunAmmo/ToyGunAmmo.png",
        },
        new Spec
        {
            Name = "Winston", ItemName = "Winston plush", Behaviour = "CustomItems.Items.WinstonItem",
            MinValue = 70, MaxValue = 160, Rarity = 30, Weight = 1.03f,
            VerticalOffset = 0.13f, // центр модели на середине игрушки высотой 26 см
            Texture = Art + "/Textures/Winston/Winston.png",
            DropSound = Art + "/Audio/Winston/WinstonDrop.wav", // мягкая игрушка, а не писк утки
            ToolTips = new[] { "Talk : [LMB]" },
            Clips = new[] { ("voiceClip", Art + "/Audio/Winston/WinstonVoice.wav") },
        },
        new Spec
        {
            Name = "YandexStation", ItemName = "Yandex Station", Behaviour = "CustomItems.Items.YandexStationItem",
            Scrap = false, Rarity = 0, InShop = true, Price = 100, Weight = 1.04f,
            VerticalOffset = 0.045f,    // половина высоты колонки 9 см
            ToolTips = new[] { "Turn on/off : [LMB]", "Talk : [E]" },
            BatterySeconds = 1800f,     // полный заряд на 30 минут работы, заряжается на зарядке корабля
            SaveItemVariable = true,    // цвет и «включена»
            SyncInteract = true,        // E: поболтать (фразу выбирает держащий)
            DropSound = YandexSounds + "/Drop.wav", // мягкий тканевый глухой стук
            Phrases = true,
            Clips = new[]
            {
                ("listenClip", YandexSounds + "/Listen.wav"),
                ("listenTimeoutClip", YandexSounds + "/ListenTimeout.wav"),
                ("turnOnClip", YandexSounds + "/TurnOn.wav"),
                ("turnOffClip", YandexSounds + "/TurnOff.wav"),
            },
            Customize = SetupYandexStation,
        },
        // Вейпы (Tools/Vape): скрап с небольшой редкостью, одноразовый OXYA попадается вдвое чаще
        Vape("Pixl", "PYXL vape", 10, 0.083f, true),
        Vape("Box", "VAPEFIT vape", 10, 0.062f, true),
        Vape("Pod", "OXYA pod", 20, 0.1f, false),
    };

    // Колонка недолго была мебелью (Prefabs/YandexStation<Цвет>): если эти префабы успели создаться, убираем
    private static readonly string[] Obsolete =
        new[] { "Grey", "Blue", "Pink", "Black" }.Select(c => $"{PrefabsDir}/YandexStation{c}.prefab").ToArray();

    static ItemAssets()
    {
        EditorApplication.delayCall += () =>
        {
            if (TypesReady(log: false))
                CreateMissing(rebuildPrefabs: false);
        };
    }

    [MenuItem("Lethal Company/Create Missing Item Assets")]
    private static void CreateMissingMenu()
    {
        if (TypesReady(log: true))
            CreateMissing(rebuildPrefabs: false);
    }

    /// <summary>Перед сборкой бандла: досоздать недостающее. false, если DLL ещё не подключены.</summary>
    public static bool EnsureForBuild()
    {
        if (!TypesReady(log: true))
            return false;
        CreateMissing(rebuildPrefabs: false);
        return true;
    }

    [MenuItem("Lethal Company/Rebuild Item Prefabs")]
    private static void RebuildMenu()
    {
        if (!TypesReady(log: true))
            return;
        // В batchmode (-executeMethod ItemAssets.RebuildMenu) диалог не показать, пересобираем сразу
        if (Application.isBatchMode || EditorUtility.DisplayDialog("Пересобрать префабы",
                "Префабы всех предметов будут созданы заново из моделей (ассеты Item и ItemRegistry не меняются). " +
                "Ручные правки префабов пропадут.", "Пересобрать", "Отмена"))
            CreateMissing(rebuildPrefabs: true);
    }

    // ---------------- основной проход

    private static void CreateMissing(bool rebuildPrefabs)
    {
        var created = new List<string>();
        var registry = LoadOrCreate(RegistryPath, T("CustomItems.ItemRegistry"), created);
        RemoveEmptyEntries(registry);

        foreach (var spec in Specs)
        {
            var model = FindModel(spec.Name);
            if (model == null)
            {
                Debug.LogWarning($"{spec.ItemName}: нет модели в {Art}/Models/{spec.Name}/");
                continue;
            }

            var itemPath = $"{ItemsDir}/{spec.Name}.asset";
            var item = AssetDatabase.LoadAssetAtPath<ScriptableObject>(itemPath);
            if (item == null || !T("Item").IsInstanceOfType(item))
            {
                item = LoadOrCreate(itemPath, T("Item"), created);
                FillItem(item, spec);
            }

            var itemIsNew = created.Contains(itemPath);
            var prefabPath = $"{PrefabsDir}/{spec.Name}.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            // Префаб старого вида с пивотом не внизу: после пересборки поправим оффсеты в Item
            var pivotIsOld = prefab != null && Mathf.Abs(BottomY(prefab)) > 0.002f;
            // Пересобираем и префаб старого вида (голая модель без скрипта предмета),
            // и сохранённый с потерянными скриптами
            if (prefab == null || rebuildPrefabs || pivotIsOld || prefab.GetComponent(T("GrabbableObject")) == null
                || HasMissingScripts(prefab) || !HasTexture(prefab, spec) || HasVisibleHidden(prefab, spec))
            {
                prefab = BuildPrefab(spec, model, item, prefabPath, out var lift);
                created.Add(prefabPath);
                // Оффсеты из Spec и старых ассетов считались от пивота модели (обычно её центра)
                if (itemIsNew || pivotIsOld)
                {
                    MovePivotOffsets(item, lift * prefab.transform.localScale.y);
                    created.Add($"{spec.Name}: пивот внизу, модель поднята на {lift:0.###} м, оффсеты в Item пересчитаны");
                }
            }

            if (Get(item, "spawnPrefab") as Object == null)
            {
                Set(item, "spawnPrefab", prefab);
                EditorUtility.SetDirty(item);
            }

            if (spec.Phrases)
                SyncPhrases(prefab, spec);
            AddToRegistry(registry, item, spec);
            MarkBundle(itemPath);
            MarkBundle(prefabPath);
        }

        foreach (var path in Obsolete)
        {
            if (File.Exists(path) && AssetDatabase.DeleteAsset(path))
                created.Add("удалён устаревший " + path);
        }
        MarkBundle(RegistryPath);
        AssetDatabase.SaveAssets();
        var icons = ItemIcons.RenderMissing();
        if (icons > 0)
            created.Add($"иконки: {icons}");
        if (created.Count > 0)
            Debug.Log("Предметы: созданы\n" + string.Join("\n", created));
    }

    private static void FillItem(ScriptableObject item, Spec spec)
    {
        Set(item, "itemName", spec.ItemName);
        Set(item, "isScrap", spec.Scrap);
        Set(item, "minValue", spec.MinValue);
        Set(item, "maxValue", spec.MaxValue);
        if (spec.InShop)
        {
            Set(item, "creditsWorth", spec.Price);
            Set(item, "highestSalePercentage", 80); // как у ванильных товаров: скидка не больше 80%
        }
        Set(item, "weight", spec.Weight);
        Set(item, "twoHanded", spec.TwoHanded);
        Set(item, "isConductiveMetal", false);
        Set(item, "itemSpawnsOnGround", true);
        Set(item, "canBeGrabbedBeforeGameStart", true);
        Set(item, "allowDroppingAheadOfPlayer", true);
        Set(item, "syncUseFunction", true);
        Set(item, "requiresBattery", spec.BatterySeconds > 0f);
        Set(item, "batteryUsage", spec.BatterySeconds);
        Set(item, "automaticallySetUsingPower", spec.BatterySeconds > 0f); // ЛКМ вкл/выкл, с пустой батареей не включить
        Set(item, "syncInteractLRFunction", spec.SyncInteract);
        Set(item, "canBeInspected", false);
        Set(item, "saveItemVariable", spec.SaveItemVariable);
        Set(item, "toolTips", spec.ToolTips);
        Set(item, "positionOffset", Vector3.zero);
        Set(item, "rotationOffset", spec.RotationOffset);
        Set(item, "restingRotation", spec.RestingRotation);
        Set(item, "verticalOffset", spec.VerticalOffset);
        var groups = Field(item, "spawnPositionTypes");
        if (groups != null)
            groups.SetValue(item, Activator.CreateInstance(groups.FieldType));
        if (spec.Icon != null)
            Set(item, "itemIcon", AssetDatabase.LoadAssetAtPath<Sprite>(spec.Icon));
        if (spec.DropSound != null)
            Set(item, "dropSFX", AssetDatabase.LoadAssetAtPath<AudioClip>(spec.DropSound));
        EditorUtility.SetDirty(item);
    }

    private static GameObject BuildPrefab(Spec spec, GameObject model, ScriptableObject item, string path, out float lift)
    {
        // Масштаб корня настраивается (окно настройки в игре → Scale префаба), пересборка его не сбрасывает
        var previous = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var scale = previous != null ? previous.transform.localScale : Vector3.one;
        var root = new GameObject(spec.Name);
        try
        {
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            visual.name = "Model";
            visual.transform.localPosition = Vector3.zero;
            // Коллайдеры из модели не нужны: свой BoxCollider ставим на корень
            if (visual.GetComponentsInChildren<Collider>(true).Length > 0)
            {
                PrefabUtility.UnpackPrefabInstance(visual, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                foreach (var c in visual.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(c);
            }
            ApplyTexture(root, spec);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (spec.HiddenChildren.Contains(t.name))
                    t.gameObject.SetActive(false);
            spec.Customize?.Invoke(root);

            // Пивот (точка, которую игра ставит в руку и на пол) — в нижней точке модели:
            // поднимаем всё содержимое корня так, чтобы низ видимых мешей был на y = 0
            lift = -ModelBounds(root).min.y;
            foreach (Transform child in root.transform)
                child.localPosition += Vector3.up * lift;

            var bounds = ModelBounds(root);
            var collider = root.AddComponent<BoxCollider>();
            collider.center = bounds.center;
            collider.size = bounds.size;

            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = 2f;
            audio.maxDistance = 30f;

            AddScanNode(root, spec, bounds.center);

            root.AddComponent(T("Unity.Netcode.NetworkObject"));
            var behaviour = root.AddComponent(T(spec.Behaviour));
            if (behaviour == null)
                throw new InvalidOperationException($"{spec.Name}: не удалось добавить {spec.Behaviour} (нет MonoScript для класса?)");
            Set(behaviour, "itemProperties", item);
            Set(behaviour, "grabbable", true);
            Set(behaviour, "grabbableToEnemies", true);
            Set(behaviour, "mainObjectRenderer", root.GetComponentInChildren<MeshRenderer>());
            Set(behaviour, "templateItemName", spec.Template);
            foreach (var (field, clipPath) in spec.Clips)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
                if (clip == null)
                    Debug.LogWarning($"{spec.ItemName}: нет звука {clipPath}");
                Set(behaviour, field, clip);
            }
            if (spec.Phrases)
                Set(behaviour, "phraseSets", PhraseSets(behaviour));
            spec.SetupBehaviour?.Invoke(behaviour);

            root.transform.localScale = scale;
            EnsureFolder(PrefabsDir);
            return PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // Скан-нода: предмет виден на сканере (ПКМ), и игра может выставить ему цену
    private static void AddScanNode(GameObject root, Spec spec, Vector3 center)
    {
        var node = new GameObject("ScanNode");
        node.transform.SetParent(root.transform, false);
        node.transform.localPosition = center;
        var collider = node.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = Vector3.one * 0.5f;

        var scan = node.AddComponent(T("ScanNodeProperties"));
        Set(scan, "headerText", spec.ItemName);
        Set(scan, "subText", spec.Scrap ? "Value: " : "");
        Set(scan, "nodeType", spec.Scrap ? 2 : 0); // 2 = скрап (зелёная рамка), 0 = обычный
        Set(scan, "minRange", 1);
        Set(scan, "maxRange", 13);
        Set(scan, "requiresLineOfSight", true);
        Set(scan, "creatureScanID", -1);
    }

    private static void AddToRegistry(ScriptableObject registry, ScriptableObject item, Spec spec)
    {
        var list = (IList)Get(registry, "items");
        foreach (var e in list)
            if (Get(e, "item") as Object == item)
                return;
        var entry = Activator.CreateInstance(list.GetType().GetGenericArguments()[0]);
        Set(entry, "item", item);
        Set(entry, "rarity", spec.Rarity);
        Set(entry, "inShop", spec.InShop);
        Set(entry, "price", spec.Price);
        list.Add(entry);
        EditorUtility.SetDirty(registry);
    }

    // ---------------- фразы колонки по темам: подпапки Phrases → PhraseSet[]

    private const string YandexPhrases = Art + "/Audio/YandexStation/Phrases";
    private const string YandexSounds = Art + "/Audio/YandexStation/Sounds";

    private static AudioClip[] ClipsIn(string folder) =>
        Directory.GetFiles(folder)
            .Select(p => p.Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(AssetDatabase.LoadAssetAtPath<AudioClip>).Where(c => c != null).ToArray();

    // Тема = путь подпапки от Phrases (Weather/Rainy); звуки прямо в Phrases — тема Random
    private static Array PhraseSets(Component behaviour)
    {
        var field = Field(behaviour, "phraseSets");
        var setType = field.FieldType.GetElementType();
        var sets = new List<object>();
        if (AssetDatabase.IsValidFolder(YandexPhrases))
        {
            var folders = new[] { YandexPhrases }.Concat(Directory.GetDirectories(YandexPhrases, "*", SearchOption.AllDirectories)
                .Select(d => d.Replace('\\', '/')).OrderBy(d => d, StringComparer.Ordinal));
            foreach (var folder in folders)
            {
                var clips = ClipsIn(folder);
                if (clips.Length == 0)
                    continue;
                var name = folder == YandexPhrases ? "Random" : folder.Substring(YandexPhrases.Length + 1);
                var existing = sets.FirstOrDefault(x => (string)Get(x, "name") == name);
                if (existing != null)
                {
                    Set(existing, "clips", ((AudioClip[])Get(existing, "clips")).Concat(clips).ToArray());
                    continue;
                }
                var set = Activator.CreateInstance(setType);
                Set(set, "name", name);
                Set(set, "clips", clips);
                sets.Add(set);
            }
        }
        var array = Array.CreateInstance(setType, sets.Count);
        for (var i = 0; i < sets.Count; i++)
            array.SetValue(sets[i], i);
        return array;
    }

    // Фразы добавили или удалили: обновляем список на готовом префабе (остальное в нём не трогаем)
    private static void SyncPhrases(GameObject prefab, Spec spec)
    {
        var behaviour = prefab.GetComponent(T(spec.Behaviour));
        var fresh = PhraseSets(behaviour);
        var current = Get(behaviour, "phraseSets") as Array;
        string Key(Array a) => a == null ? "" : string.Join("|", a.Cast<object>().Select(x =>
            Get(x, "name") + ":" + string.Join(",", ((AudioClip[])Get(x, "clips") ?? new AudioClip[0]).Select(c => c != null ? c.name : "-"))));
        if (Key(current) == Key(fresh))
            return;
        Set(behaviour, "phraseSets", fresh);
        EditorUtility.SetDirty(behaviour);
        PrefabUtility.SavePrefabAsset(prefab);
        Debug.Log($"{spec.ItemName}: фразы обновлены, тем: {fresh.Length}");
    }

    // ---------------- текстура из Spec.Texture: материал HDRP/Lit в Art/Materials/<Name>/

    private static string TextureMaterialPath(Spec spec) => $"{Art}/Materials/{spec.Name}/{spec.Name}.mat";

    private static void ApplyTexture(GameObject root, Spec spec)
    {
        if (spec.Texture == null)
            return;
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(spec.Texture);
        if (texture == null)
        {
            Debug.LogWarning($"{spec.ItemName}: нет текстуры {spec.Texture}");
            return;
        }
        var path = TextureMaterialPath(spec);
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = NewLitMaterial(path, Color.white, 0.3f);
            material.SetTexture("_BaseColorMap", texture);
            material.SetTexture("_MainTex", texture);
            EditorUtility.SetDirty(material);
        }
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            r.sharedMaterials = Enumerable.Repeat(material, r.sharedMaterials.Length).ToArray();
    }

    // Префаб, собранный до появления текстуры, пересобираем
    private static bool HasTexture(GameObject prefab, Spec spec)
    {
        if (spec.Texture == null || AssetDatabase.LoadAssetAtPath<Texture2D>(spec.Texture) == null)
            return true;
        var material = AssetDatabase.LoadAssetAtPath<Material>(TextureMaterialPath(spec));
        return material != null && prefab.GetComponentsInChildren<Renderer>(true)
            .All(r => r.sharedMaterials.All(m => m == material));
    }

    // Префаб, собранный до того, как у предмета появились скрытые объекты (мятая банка), пересобираем
    private static bool HasVisibleHidden(GameObject prefab, Spec spec) =>
        prefab.GetComponentsInChildren<Transform>(true)
            .Any(t => spec.HiddenChildren.Contains(t.name) && t.gameObject.activeSelf);

    // ---------------- игрушечный пистолет: материал с текстурой и точка дула

    private const string ToyGunTexture = Art + "/Textures/ToyGun/ToyGunTexture.png";
    private const string ToyGunMaterial = Art + "/Materials/ToyGun/ToyGunMaterial.mat";
    // Дуло в координатах модели (скрипт Tools/ToyGun/model.py печатает её при запуске)
    private static readonly Vector3 ToyGunMuzzle = new Vector3(0f, 0.1344f, 0.2f);

    private static void SetupToyGun(GameObject root)
    {
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(ToyGunTexture);
        var material = AssetDatabase.LoadAssetAtPath<Material>(ToyGunMaterial);
        if (material == null)
        {
            EnsureFolder(Path.GetDirectoryName(ToyGunMaterial));
            var shader = Shader.Find("HDRP/Lit");
            material = new Material(shader != null ? shader : Shader.Find("Standard"));
            material.SetTexture("_BaseColorMap", texture);
            material.SetTexture("_MainTex", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", 0.55f);
            AssetDatabase.CreateAsset(material, ToyGunMaterial);
        }
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            r.sharedMaterials = Enumerable.Repeat(material, r.sharedMaterials.Length).ToArray();

        var muzzle = new GameObject("Muzzle");
        muzzle.transform.SetParent(root.transform, false);
        muzzle.transform.localPosition = ToyGunMuzzle;
    }

    // ---------------- Яндекс Станция: материалы по объектам модели, экран «12:35»

    private const string YandexMaterials = Art + "/Materials/YandexStation/";

    // Модель (Tools/YandexStation/model.py): Body, Trim, Marks, Ring и светящиеся
    // Seg<0-3><a-g>, ColonTop/ColonBottom. Какие из них горят, решает
    // YandexStationItem; в префабе включены «12:35», так колонка выглядит на иконке.
    private static void SetupYandexStation(GameObject root)
    {
        Material Mat(string name, Color color, float smoothness, Color emission)
        {
            var path = YandexMaterials + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path) ?? NewLitMaterial(path, color, smoothness);
            m.SetColor("_BaseColor", color);
            m.SetColor("_Color", color);
            m.SetColor("_EmissiveColor", emission);
            EditorUtility.SetDirty(m);
            return m;
        }

        var display = new Color(1f, 0.84f, 0.62f);
        var ring = new Color(0.58f, 0.36f, 1f);
        var mats = new Dictionary<string, Material>
        {
            // Цвет корпуса и ножки меняет YandexStationItem (E), здесь серый по умолчанию
            { "Body", Mat("YandexStationBody", new Color(0.30f, 0.31f, 0.34f), 0.15f, Color.black) },
            { "Trim", Mat("YandexStationTrim", new Color(0.10f, 0.10f, 0.11f), 0.3f, Color.black) },
            { "Marks", Mat("YandexStationMarks", new Color(0.55f, 0.55f, 0.58f), 0.2f, Color.black) },
            { "Ring", Mat("YandexStationRing", ring, 0.5f, ring * 3f) },
            { "Display", Mat("YandexStationDisplay", display, 0.5f, display * 12f) },
        };

        // Чтобы переставить и выключить объекты модели, экземпляр FBX надо распаковать
        var model = root.transform.Find("Model");
        if (PrefabUtility.IsPartOfPrefabInstance(model.gameObject))
            PrefabUtility.UnpackPrefabInstance(model.gameObject, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);

        var shown = new HashSet<string> { "ColonTop", "ColonBottom" };
        foreach (var (d, segs) in new[] { (0, "bc"), (1, "abdeg"), (2, "abcdg"), (3, "acdfg") })
            foreach (var s in segs)
                shown.Add($"Seg{d}{s}");

        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            var glowing = r.name.StartsWith("Seg") || r.name.StartsWith("Colon");
            r.sharedMaterial = glowing ? mats["Display"] : mats.TryGetValue(r.name, out var mat) ? mat : mats["Body"];
            if (glowing)
                r.gameObject.SetActive(shown.Contains(r.name));
        }
        // Корпус первым: его берёт mainObjectRenderer
        model.Find("Body")?.SetAsFirstSibling();
    }

    // ---------------- Вейпы: материалы по объектам модели, стиль экрана

    private static Spec Vape(string style, string itemName, int rarity, float halfHeight, bool rechargeable) => new Spec
    {
        Name = "Vape" + style, ItemName = itemName, Behaviour = "CustomItems.Items.VapeItem",
        Template = "TZP-Inhalant", // поза «у рта» и звуки подбора
        MinValue = 30, MaxValue = 80, Rarity = rarity, Weight = 1.01f,
        VerticalOffset = halfHeight, // центр модели на середине высоты
        ToolTips = new[] { "Vape : [LMB]" },
        SaveItemVariable = true,     // счётчик затяжек; заряд, держание ЛКМ и батарею код включает сам
        Customize = root => SetupVape(root, style),
        SetupBehaviour = b =>
        {
            Set(b, "screenStyle", Enum.Parse(T("CustomItems.Items.VapeScreen+Style"), style));
            Set(b, "rechargeable", rechargeable);
        },
    };

    // Модель (Tools/Vape/model.py): один объект — один материал. Body/Panel с градиентом из
    // Textures/Vape<Модель>/Vape<Модель>Body.png, Screen светится (картинку рисует VapeScreen),
    // Glass и Liquid прозрачные. Mouth — пустышка.
    private static void SetupVape(GameObject root, string style)
    {
        var dir = $"{Art}/Materials/Vape{style}/";
        var tex = $"{Art}/Textures/Vape{style}/Vape{style}";
        Material Mat(string name, Color color, float smoothness, string texture = null, bool glow = false, bool clear = false)
        {
            var path = dir + $"Vape{style}{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path) ?? NewLitMaterial(path, color, smoothness);
            var map = texture != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(texture) : null;
            if (map != null)
                m.SetTexture("_BaseColorMap", map);
            if (glow && map != null)
            {
                m.SetTexture("_EmissiveColorMap", map);
                m.EnableKeyword("_EMISSIVE_COLOR_MAP");
                m.SetColor("_EmissiveColor", Color.white * 6f);
            }
            if (clear)
                MakeTransparent(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        Color C(int r, int g, int b, float a = 1f) => new Color(r / 255f, g / 255f, b / 255f, a);
        var body = style == "Pod" ? Mat("Body", C(16, 16, 18), 0.6f) : Mat("Body", Color.white, 0.7f, tex + "Body.png");
        var logo = Mat("Logo", C(240, 240, 245), 0.4f);
        var mats = new Dictionary<string, Material>
        {
            { "Body", body },
            { "Panel", body },
            { "Cap", Mat("Cap", C(14, 14, 16), 0.6f) },
            { "Mouthpiece", style == "Pixl" ? Mat("Mouthpiece", C(255, 90, 205), 0.6f) : Mat("Cap", C(14, 14, 16), 0.6f) },
            { "Hole", Mat("Trim", C(20, 12, 20), 0.2f) },
            { "Bezel", Mat("Bezel", C(8, 8, 10), 0.9f) },
            { "Screen", Mat("Screen", Color.white, 0.8f, tex + "Screen.png", glow: true) },
            { "Logo", logo },
            { "Logo8000", logo },
            { "Glass", Mat("Glass", C(200, 215, 225, 0.25f), 0.8f, clear: true) },
            { "Liquid", Mat("Liquid", C(255, 160, 40, 0.6f), 0.8f, clear: true) },
            { "Chimney", Mat("Chimney", C(140, 140, 150), 0.7f) },
            { "Button", Mat("Gold", C(220, 175, 80), 0.75f) },
            { "Stripe", Mat("Gold", C(220, 175, 80), 0.75f) },
            { "Stripe2", Mat("Gold", C(220, 175, 80), 0.75f) },
        };
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            if (mats.TryGetValue(r.name, out var mat))
                r.sharedMaterial = mat;
        // Корпус первым: его берёт mainObjectRenderer
        var model = root.transform.Find("Model");
        if (PrefabUtility.IsPartOfPrefabInstance(model.gameObject))
            PrefabUtility.UnpackPrefabInstance(model.gameObject, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
        model.Find("Body")?.SetAsFirstSibling();
    }

    // ---------------- Егермейстер: прозрачное стекло, сквозь которое видна жидкость

    private const string JagerTexture = Art + "/Textures/Jagermeister/Jagermeister.png";
    private const string JagerGlass = Art + "/Materials/Jagermeister/JagermeisterGlass.mat";
    private const string JagerLiquid = Art + "/Materials/Jagermeister/JagermeisterLiquid.mat";

    // Модель (Tools/Jagermeister/model.py): Bottle и Liquid1..Liquid3, слои жидкости
    // прячет JagermeisterItem по числу оставшихся глотков.
    private static void SetupJagermeister(GameObject root)
    {
        var glass = AssetDatabase.LoadAssetAtPath<Material>(JagerGlass);
        if (glass == null)
        {
            glass = NewLitMaterial(JagerGlass, Color.white, 0.5f);
            glass.SetTexture("_BaseColorMap", AssetDatabase.LoadAssetAtPath<Texture2D>(JagerTexture));
        }
        var liquid = AssetDatabase.LoadAssetAtPath<Material>(JagerLiquid)
                     ?? NewLitMaterial(JagerLiquid, new Color(0.22f, 0.08f, 0.03f), 0.4f);

        // Настройки применяются и к уже созданным материалам (при Rebuild Item Prefabs).
        // Гладкое стекло с Preserve Specular Lighting давало в игре яркий оранжевый блик
        // поверх всей бутылки: отражение не гасится альфой. Делаем стекло матовее и
        // гасим блик вместе с прозрачностью.
        glass.SetFloat("_Smoothness", 0.5f);
        glass.SetFloat("_EnableBlendModePreserveSpecularLighting", 0f);
        MakeTransparent(glass);
        liquid.SetFloat("_Smoothness", 0.4f);
        EditorUtility.SetDirty(liquid);

        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            r.sharedMaterial = r.name.StartsWith("Liquid") ? liquid : glass;
    }

    private static Material NewLitMaterial(string path, Color color, float smoothness)
    {
        EnsureFolder(Path.GetDirectoryName(path));
        var shader = Shader.Find("HDRP/Lit");
        var material = new Material(shader != null ? shader : Shader.Find("Standard"));
        material.SetColor("_BaseColor", color);
        material.SetColor("_Color", color);
        material.SetFloat("_Smoothness", smoothness);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    // Прозрачность HDRP/Lit с альфой из текстуры. HDMaterial берём рефлексией, чтобы
    // не добавлять сборку HDRP в asmdef; он же выставляет ключевые слова и рендер-стейт.
    private static void MakeTransparent(Material material)
    {
        material.SetFloat("_SurfaceType", 1f);
        material.SetFloat("_BlendMode", 0f); // Alpha
        material.SetFloat("_EnableFogOnTransparent", 1f);
        var hd = Type.GetType("UnityEngine.Rendering.HighDefinition.HDMaterial, Unity.RenderPipelines.HighDefinition.Runtime");
        hd?.GetMethod("SetSurfaceType", new[] { typeof(Material), typeof(bool) })?.Invoke(null, new object[] { material, true });
        hd?.GetMethod("ValidateMaterial", new[] { typeof(Material) })?.Invoke(null, new object[] { material });
        if (hd == null)
            Debug.LogWarning("Егермейстер: не нашёл HDMaterial, проверьте в инспекторе, что у стекла Surface Type = Transparent");
        EditorUtility.SetDirty(material);
    }

    // ---------------- помощники

    private static bool TypesReady(bool log)
    {
        var missing = new[] { "Item", "GrabbableObject", "ScanNodeProperties", "Unity.Netcode.NetworkObject", "CustomItems.ItemRegistry" }
            .Where(n => FindType(n) == null).ToArray();
        if (missing.Length > 0)
        {
            if (log)
                Debug.LogError("Нет типов " + string.Join(", ", missing) + ". DLL игры: Lethal Company → Setup → Import Game DLLs; " +
                               "SidiuszCustomItems.Items.dll: соберите мод (dotnet build) и вернитесь в Unity.");
            return false;
        }
        return ScriptsReady(log);
    }

    // Тип в домене ещё не значит, что у Unity есть для него MonoScript. Без MonoScript
    // AddComponent/CreateAsset сохраняют m_Script: {fileID: 0}. Так бывает в batchmode
    // (-executeMethod), пока скрипты Assembly-CSharp не импортированы: тогда ничего не
    // создаём и не пересобираем, чтобы не испортить рабочие ассеты.
    private static bool ScriptsReady(bool log)
    {
        var needed = new[] { "Item", "ScanNodeProperties", "Unity.Netcode.NetworkObject", "CustomItems.ItemRegistry" }
            .Concat(Specs.Select(s => s.Behaviour))
            .Select(FindType).Where(t => t != null).ToArray();

        string[] Missing()
        {
            // GetAllRuntimeMonoScripts видит только загруженные MonoScript'ы, а сразу после запуска (-executeMethod) они ещё не загружены
            foreach (var guid in AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" }))
                AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid));
            var known = new HashSet<Type>(MonoImporter.GetAllRuntimeMonoScripts()
                .Where(m => m != null).Select(m => m.GetClass()).Where(c => c != null));
            return needed.Where(t => !known.Contains(t)).Select(t => t.Name).ToArray();
        }

        var missing = Missing();
        if (missing.Length > 0 && Application.isBatchMode)
        {
            // MonoScript без класса (GetClass() == null) оживает после переимпорта своего .cs
            foreach (var guid in AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets/Sources/Scripts/GameStubs" }))
                AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(guid), ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            missing = Missing();
        }
        if (missing.Length > 0 && log)
            Debug.LogError("У Unity ещё нет скриптов для " + string.Join(", ", missing) +
                           ": ассеты предметов не трогаю. Дождитесь окончания компиляции и импорта и повторите.");
        return missing.Length == 0;
    }

    private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

    private static Type FindType(string fullName)
    {
        if (TypeCache.TryGetValue(fullName, out var cached))
            return cached;
        Type found = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name != "Assembly-CSharp" && name != "SidiuszCustomItems.Items" && name != "Unity.Netcode.Runtime")
                continue;
            found = asm.GetType(fullName);
            if (found != null)
                break;
        }
        if (found != null)
            TypeCache[fullName] = found;
        return found;
    }

    private static Type T(string fullName) =>
        FindType(fullName) ?? throw new InvalidOperationException("Не найден тип " + fullName);

    private static GameObject FindModel(string name)
    {
        var folder = $"{Art}/Models/{name}";
        if (!AssetDatabase.IsValidFolder(folder))
            return null;
        return AssetDatabase.FindAssets("t:Model", new[] { folder })
            .Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g)))
            .FirstOrDefault(m => m != null);
    }

    private static ScriptableObject LoadOrCreate(string path, Type type, List<string> created)
    {
        var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
        if (asset != null && type.IsInstanceOfType(asset))
            return asset;
        DeleteBroken(path);
        EnsureFolder(Path.GetDirectoryName(path));
        asset = ScriptableObject.CreateInstance(type);
        AssetDatabase.CreateAsset(asset, path);
        created.Add(path);
        return asset;
    }

    private static bool HasMissingScripts(GameObject prefab) =>
        prefab.GetComponentsInChildren<Transform>(true)
            .Any(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) > 0);

    // Строки, чей Item был сломан и пересоздан, указывают в никуда
    private static void RemoveEmptyEntries(ScriptableObject registry)
    {
        var list = (IList)Get(registry, "items");
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (Get(list[i], "item") as Object != null)
                continue;
            list.RemoveAt(i);
            EditorUtility.SetDirty(registry);
        }
    }

    // Ассет, сохранённый, когда Unity не смог загрузить DLL, остаётся без скрипта
    // (m_Script: {fileID: 0}). Такой не починить, только создать заново.
    private static void DeleteBroken(string path)
    {
        if (!File.Exists(path))
            return;
        Debug.LogWarning("Пересоздаю сломанный ассет без скрипта: " + path);
        AssetDatabase.DeleteAsset(path);
    }

    // Модель поднялась на lift относительно пивота. Чтобы предмет остался там же в руке
    // и на полу, сдвигаем пивот обратно: в руке вдоль «верха» предмета (с учётом Rotation Offset),
    // на полу на столько, на сколько подъём виден по вертикали при Resting Rotation
    private static void MovePivotOffsets(ScriptableObject item, float lift)
    {
        if (Mathf.Abs(lift) < 0.0005f)
            return;
        var rotation = (Vector3)Get(item, "rotationOffset");
        var position = (Vector3)Get(item, "positionOffset");
        Set(item, "positionOffset", position - Quaternion.Euler(rotation) * (Vector3.up * lift));

        var resting = (Vector3)Get(item, "restingRotation");
        var vertical = (float)Get(item, "verticalOffset");
        var rise = (Quaternion.Euler(resting.x, 0f, resting.z) * (Vector3.up * lift)).y;
        Set(item, "verticalOffset", vertical - rise);
        EditorUtility.SetDirty(item);
    }

    // Нижняя точка мешей префаба в координатах его корня (0, если пивот уже внизу).
    // Считаем по мешам: у ассета префаба нет мировых границ рендереров.
    private static float BottomY(GameObject prefab)
    {
        var root = prefab.transform;
        var min = float.PositiveInfinity;
        void Add(Transform t, Mesh mesh)
        {
            if (mesh == null)
                return;
            var m = root.worldToLocalMatrix * t.localToWorldMatrix;
            var b = mesh.bounds;
            for (var i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                min = Mathf.Min(min, m.MultiplyPoint3x4(corner).y);
            }
        }
        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(false))
            Add(mf.transform, mf.sharedMesh);
        foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            Add(smr.transform, smr.sharedMesh);
        return float.IsPositiveInfinity(min) ? 0f : min;
    }

    private static Bounds ModelBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(false);
        if (renderers.Length == 0)
            return new Bounds(Vector3.zero, Vector3.one * 0.3f);
        var b = renderers[0].bounds;
        foreach (var r in renderers)
            b.Encapsulate(r.bounds);
        return b; // корень стоит в начале координат без поворота, мировые границы = локальные
    }

    private static void MarkBundle(string path)
    {
        var importer = AssetImporter.GetAtPath(path);
        if (importer != null && importer.assetBundleName != Bundle)
            importer.assetBundleName = Bundle;
    }

    private static FieldInfo Field(object target, string name)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f != null)
                return f;
        }
        Debug.LogWarning($"У {target.GetType().Name} нет поля {name}");
        return null;
    }

    private static object Get(object target, string name) => Field(target, name)?.GetValue(target);

    private static void Set(object target, string name, object value) => Field(target, name)?.SetValue(target, value);

    // AssetDatabase.CreateAsset требует, чтобы папка уже была известна Unity
    private static void EnsureFolder(string folder)
    {
        folder = folder.Replace('\\', '/');
        if (AssetDatabase.IsValidFolder(folder))
            return;
        var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }
}
