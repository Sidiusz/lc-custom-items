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
        public float VerticalOffset = 0.1f;
        public Vector3 RestingRotation;
        public Vector3 RotationOffset;
        public bool SyncInteract;                                    // Q/E-взаимодействие у всех игроков
        public string[] ToolTips = new string[0];
        public bool SaveItemVariable;
        public string Icon;                                          // путь к спрайту, необязательно
        public string Texture;                                       // текстура на все меши (HDRP/Lit), необязательно
        public (string field, string path)[] Clips = new (string, string)[0];
        public Action<GameObject> Customize;                         // доп. настройка префаба
        public string[] HiddenChildren = new string[0];              // объекты модели, выключенные в префабе
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
            Icon = Art + "/Textures/Adrenaline/AdrenalineCanIcon.png",
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

            var prefabPath = $"{PrefabsDir}/{spec.Name}.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            // Пересобираем и префаб старого вида (голая модель без скрипта предмета),
            // и сохранённый с потерянными скриптами
            if (prefab == null || rebuildPrefabs || prefab.GetComponent(T("GrabbableObject")) == null || HasMissingScripts(prefab)
                || !HasTexture(prefab, spec) || HasVisibleHidden(prefab, spec))
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
            ApplyTexture(root, spec);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (spec.HiddenChildren.Contains(t.name))
                    t.gameObject.SetActive(false);
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
            foreach (var guid in AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets/Sources/GameStubs" }))
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
