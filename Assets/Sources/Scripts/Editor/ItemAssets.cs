// Создаёт для каждого предмета мода:
//   Assets/Sources/Data/Items/<Предмет>.asset   ассет Item игры (название, стоимость, вес, хват, оффсеты...)
//   Assets/Sources/Prefabs/<Предмет>.prefab     префаб: модель, NetworkObject, коллайдер, Rigidbody,
//                                               AudioSource, скан-нода и скрипт поведения со звуками
//   Assets/Sources/Data/ItemRegistry.asset      список предметов мода: редкость, магазин, цена
// и помечает их бандлом customitems.
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
    private const string Bundle = "customitems";

    private class Spec
    {
        public string Name;          // имя файлов ассета и префаба, папки в Art/*/<Name>
        public string ItemName;      // название в игре (и секция конфига BepInEx)
        public string NameRu;        // название по-русски (в ItemRegistry)
        public string[] ToolTipsRu = new string[0];
        public string Behaviour;     // класс поведения из CustomItems.dll
        public string Template = "Rubber Ducky"; // ванильный предмет-шаблон
        public bool Scrap = true;
        public int MinValue, MaxValue;
        public int Rarity;
        public bool InShop;
        public int Price = 25;
        public float Weight = 1.05f;
        public bool TwoHanded;
        public float VerticalOffset = 0.1f;
        public Vector3 RestingRotation;
        public string[] ToolTips = new string[0];
        public bool SaveItemVariable;
        public string Icon;                                          // путь к спрайту, необязательно
        public (string field, string path)[] Clips = new (string, string)[0];
        public Action<GameObject> Customize;                         // доп. настройка префаба
    }

    private static readonly Spec[] Specs =
    {
        new Spec
        {
            Name = "Jagermeister", ItemName = "Jagermeister", Behaviour = "CustomItems.Items.JagermeisterItem",
            NameRu = "Егермейстер", ToolTipsRu = new[] { "Выпить : [LMB]" },
            Template = "Perfume bottle",
            MinValue = 40, MaxValue = 100, Rarity = 30, Weight = 1.03f,
            VerticalOffset = 0.125f, // центр модели на середине бутылки высотой 25 см
            ToolTips = new[] { "Drink : [LMB]" },
            SaveItemVariable = true, // сохранять оставшиеся глотки
        },
        new Spec
        {
            Name = "Adrenaline", ItemName = "Adrenaline Game Fuel", Behaviour = "CustomItems.Items.AdrenalineItem",
            NameRu = "Энергетик Adrenaline", ToolTipsRu = new[] { "Выпить : [LMB]" },
            Template = "Red soda",
            MinValue = 80, MaxValue = 160, Rarity = 30, Weight = 1.01f,
            VerticalOffset = 0.084f, // центр модели на середине банки высотой 16.8 см
            ToolTips = new[] { "Drink : [LMB]" },
            SaveItemVariable = true, // пустая банка остаётся пустой после сохранения
            Icon = Art + "/Textures/Adrenaline/AdrenalineCanIcon.png",
        },
        new Spec
        {
            Name = "Monitor27", ItemName = "Computer monitor", Behaviour = "CustomItems.Items.Monitor27Item",
            NameRu = "Монитор 27\"",
            Template = "Cash register", // двуручная анимация переноски
            MinValue = 100, MaxValue = 220, Rarity = 20, Weight = 1.28f, TwoHanded = true,
            VerticalOffset = 0.24f, // половина высоты: модель стоит на подставке
        },
        new Spec
        {
            Name = "ToyGun", ItemName = "Toy gun", Behaviour = "CustomItems.Items.ToyGunItem",
            NameRu = "Игрушечный пистолет", ToolTipsRu = new[] { "Выстрел : [LMB]" },
            Template = "Zap gun",
            Scrap = false, Rarity = 0, InShop = true, Price = 30, Weight = 1.03f,
            ToolTips = new[] { "Shoot : [LMB]" },
            RestingRotation = new Vector3(0f, 0f, 90f), // на полу лежит на боку
            Clips = new[] { ("shotClip", Art + "/Audio/ToyGun/ToyGunShot.wav") },
            Customize = SetupToyGun,
        },
        new Spec
        {
            Name = "Winston", ItemName = "Winston plush", Behaviour = "CustomItems.Items.WinstonItem",
            NameRu = "Плюшевый Уинстон", ToolTipsRu = new[] { "Поговорить : [LMB]" },
            MinValue = 70, MaxValue = 160, Rarity = 30, Weight = 1.03f,
            VerticalOffset = 0.13f, // центр модели на середине игрушки высотой 26 см
            ToolTips = new[] { "Talk : [LMB]" },
            Clips = new[] { ("voiceClip", Art + "/Audio/Winston/WinstonVoice.wav") },
        },
    };

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
        if (EditorUtility.DisplayDialog("Пересобрать префабы",
                "Префабы всех предметов будут созданы заново из моделей (ассеты Item и ItemRegistry не меняются). " +
                "Ручные правки префабов пропадут.", "Пересобрать", "Отмена"))
            CreateMissing(rebuildPrefabs: true);
    }

    // ---------------- основной проход

    private static void CreateMissing(bool rebuildPrefabs)
    {
        var created = new List<string>();
        var registry = LoadOrCreate(RegistryPath, T("CustomItems.ItemRegistry"), created);

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
            if (item == null)
            {
                item = LoadOrCreate(itemPath, T("Item"), created);
                FillItem(item, spec);
            }

            var prefabPath = $"{PrefabsDir}/{spec.Name}.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            // Префаб старого вида (голая модель без скрипта предмета) тоже пересобираем
            if (prefab == null || rebuildPrefabs || prefab.GetComponent(T("GrabbableObject")) == null)
            {
                prefab = BuildPrefab(spec, model, item, prefabPath);
                created.Add(prefabPath);
            }

            if (Get(item, "spawnPrefab") as Object == null)
            {
                Set(item, "spawnPrefab", prefab);
                EditorUtility.SetDirty(item);
            }

            AddToRegistry(registry, item, spec);
            MarkBundle(itemPath);
            MarkBundle(prefabPath);
        }

        MarkBundle(RegistryPath);
        AssetDatabase.SaveAssets();
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
            Set(item, "creditsWorth", spec.Price);
        Set(item, "weight", spec.Weight);
        Set(item, "twoHanded", spec.TwoHanded);
        Set(item, "isConductiveMetal", false);
        Set(item, "itemSpawnsOnGround", true);
        Set(item, "canBeGrabbedBeforeGameStart", true);
        Set(item, "allowDroppingAheadOfPlayer", true);
        Set(item, "syncUseFunction", true);
        Set(item, "canBeInspected", false);
        Set(item, "saveItemVariable", spec.SaveItemVariable);
        Set(item, "toolTips", spec.ToolTips);
        Set(item, "positionOffset", Vector3.zero);
        Set(item, "rotationOffset", Vector3.zero);
        Set(item, "restingRotation", spec.RestingRotation);
        Set(item, "verticalOffset", spec.VerticalOffset);
        var groups = Field(item, "spawnPositionTypes");
        if (groups != null)
            groups.SetValue(item, Activator.CreateInstance(groups.FieldType));
        if (spec.Icon != null)
            Set(item, "itemIcon", AssetDatabase.LoadAssetAtPath<Sprite>(spec.Icon));
        EditorUtility.SetDirty(item);
    }

    private static GameObject BuildPrefab(Spec spec, GameObject model, ScriptableObject item, string path)
    {
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
            spec.Customize?.Invoke(root);

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
        {
            if (Get(e, "item") as Object != item)
                continue;
            // Строки, созданные до появления перевода, дополняем, не трогая заполненное
            if (string.IsNullOrEmpty(Get(e, "nameRu") as string) && spec.NameRu != null)
            {
                Set(e, "nameRu", spec.NameRu);
                if (!(Get(e, "toolTipsRu") is string[] tips) || tips.Length == 0)
                    Set(e, "toolTipsRu", spec.ToolTipsRu);
                EditorUtility.SetDirty(registry);
            }
            return;
        }
        var entry = Activator.CreateInstance(list.GetType().GetGenericArguments()[0]);
        Set(entry, "item", item);
        Set(entry, "rarity", spec.Rarity);
        Set(entry, "inShop", spec.InShop);
        Set(entry, "price", spec.Price);
        Set(entry, "nameRu", spec.NameRu);
        Set(entry, "toolTipsRu", spec.ToolTipsRu);
        list.Add(entry);
        EditorUtility.SetDirty(registry);
    }

    // ---------------- игрушечный пистолет: материал с текстурой и точка дула

    private const string ToyGunTexture = Art + "/Textures/ToyGun/ToyGunTexture.png";
    private const string ToyGunMaterial = Art + "/Materials/ToyGun/ToyGunMaterial.mat";
    // Дуло в координатах модели (скрипт Tools/ToyGun/model.py печатает её при запуске)
    private static readonly Vector3 ToyGunMuzzle = new Vector3(0f, 0.084f, 0.125f);

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

    // ---------------- помощники

    private static bool TypesReady(bool log)
    {
        var missing = new[] { "Item", "GrabbableObject", "Unity.Netcode.NetworkObject", "CustomItems.ItemRegistry" }
            .Where(n => FindType(n) == null).ToArray();
        if (missing.Length > 0 && log)
            Debug.LogError("Нет типов " + string.Join(", ", missing) + ". DLL игры: Lethal Company → Setup → Import Game DLLs; " +
                           "CustomItems.dll: соберите мод (dotnet build) и вернитесь в Unity.");
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
            if (name != "Assembly-CSharp" && name != "CustomItems" && name != "Unity.Netcode.Runtime")
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
        if (asset != null)
            return asset;
        EnsureFolder(Path.GetDirectoryName(path));
        asset = ScriptableObject.CreateInstance(type);
        AssetDatabase.CreateAsset(asset, path);
        created.Add(path);
        return asset;
    }

    private static Bounds ModelBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
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
