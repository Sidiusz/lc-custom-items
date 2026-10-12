// Кладёт в Assets/Plugins библиотеки, без которых Unity не знает типы предметов:
//   Game/        Assembly-CSharp.dll и Unity.Netcode.Runtime.dll из папки игры (Item,
//                GrabbableObject, ScanNodeProperties, NetworkObject);
//   CustomItems/ CustomItems.dll из CustomItems/bin после dotnet build (скрипты поведения
//                предметов и ItemRegistry).
// Папка Assets/Plugins в .gitignore: файлы игры в репозиторий не кладём.
//
// DLL игры копируются один раз (меню Lethal Company → Setup → Import Game DLLs),
// CustomItems.dll обновляется сам, когда вы возвращаетесь в Unity после dotnet build.
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GameLibraries
{
    private const string GameDir = "Assets/Plugins/Game";
    private const string ModDir = "Assets/Plugins/CustomItems";
    private const string FolderPref = "CustomItems.GameFolder";
    private const string AskedKey = "CustomItems.AskedForGameFolder";

    // Имя DLL и постоянный GUID её .meta
    private static readonly (string file, string guid)[] GameDlls =
    {
        ("Assembly-CSharp.dll", "618390f720014580a02bdc3d3f5d463f"),
        ("Unity.Netcode.Runtime.dll", "35edae7ec8954af3ad3615f4bfe7b32f"),
    };
    private const string ModDll = "CustomItems.dll";
    private const string ModGuid = "21b41f406a9745749aad8abee7828c35";
    private static readonly string[] ModBuilds =
    {
        "CustomItems/bin/Release/netstandard2.1/CustomItems.dll",
        "CustomItems/bin/Debug/netstandard2.1/CustomItems.dll",
    };

    private static readonly string[] DefaultGameFolders =
    {
        @"C:\Program Files (x86)\Steam\steamapps\common\Lethal Company",
        @"D:\SteamLibrary\steamapps\common\Lethal Company",
        @"E:\SteamLibrary\steamapps\common\Lethal Company",
    };

    public static bool GameDllsPresent
    {
        get
        {
            foreach (var (file, _) in GameDlls)
                if (!File.Exists(Path.Combine(GameDir, file)))
                    return false;
            return true;
        }
    }

    public static bool ModDllPresent => File.Exists(Path.Combine(ModDir, ModDll));

    static GameLibraries()
    {
        EditorApplication.delayCall += () =>
        {
            var changed = false;
            if (!GameDllsPresent)
                changed |= ImportGame(interactive: !SessionState.GetBool(AskedKey, false));
            changed |= SyncMod();
            if (changed)
                AssetDatabase.Refresh();
        };
        EditorApplication.focusChanged += focused =>
        {
            if (focused && SyncMod())
                AssetDatabase.Refresh();
        };
    }

    [MenuItem("Lethal Company/Setup/Import Game DLLs...")]
    private static void ImportGameMenu()
    {
        EditorPrefs.DeleteKey(FolderPref);
        if (ImportGame(interactive: true))
            AssetDatabase.Refresh();
    }

    [MenuItem("Lethal Company/Setup/Update CustomItems.dll")]
    private static void SyncModMenu()
    {
        if (SyncMod(force: true))
            AssetDatabase.Refresh();
        else
            Debug.LogWarning("CustomItems.dll не найдена: сначала соберите мод (cd CustomItems && dotnet build -c Release)");
    }

    private static bool ImportGame(bool interactive)
    {
        var managed = FindManaged(EditorPrefs.GetString(FolderPref, ""));
        foreach (var folder in DefaultGameFolders)
            managed ??= FindManaged(folder);

        if (managed == null && interactive)
        {
            SessionState.SetBool(AskedKey, true);
            if (!EditorUtility.DisplayDialog("Lethal Company",
                    "Чтобы настраивать предметы в Unity, нужны Assembly-CSharp.dll и Unity.Netcode.Runtime.dll из игры. " +
                    "Укажите папку игры (где лежит Lethal Company.exe). В репозиторий они не попадут.",
                    "Выбрать папку", "Позже"))
                return false;
            var picked = EditorUtility.OpenFolderPanel("Папка Lethal Company", "", "");
            managed = FindManaged(picked);
            if (managed == null)
            {
                Debug.LogError("В выбранной папке нет Lethal Company_Data/Managed. Меню: Lethal Company → Setup → Import Game DLLs");
                return false;
            }
        }
        if (managed == null)
        {
            Debug.LogWarning("Нет DLL игры в " + GameDir + ". Меню: Lethal Company → Setup → Import Game DLLs");
            return false;
        }

        EditorPrefs.SetString(FolderPref, Directory.GetParent(Directory.GetParent(managed).FullName).FullName);
        foreach (var (file, guid) in GameDlls)
            Copy(Path.Combine(managed, file), GameDir, guid);
        Debug.Log("DLL игры скопированы из " + managed);
        return true;
    }

    // Принимает и корень игры, и сразу Managed
    private static string FindManaged(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return null;
        foreach (var candidate in new[] { folder, Path.Combine(folder, "Lethal Company_Data", "Managed") })
            if (File.Exists(Path.Combine(candidate, "Assembly-CSharp.dll")))
                return candidate;
        return null;
    }

    // Копирует свежую сборку мода, если она новее той, что лежит в Unity
    private static bool SyncMod(bool force = false)
    {
        string newest = null;
        foreach (var build in ModBuilds)
            if (File.Exists(build) && (newest == null || File.GetLastWriteTimeUtc(build) > File.GetLastWriteTimeUtc(newest)))
                newest = build;
        if (newest == null)
            return false;

        var target = Path.Combine(ModDir, ModDll);
        if (!force && File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(newest))
            return false;
        Copy(newest, ModDir, ModGuid);
        Debug.Log("CustomItems.dll обновлена из " + newest);
        return true;
    }

    private static void Copy(string source, string dir, string guid)
    {
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(source));
        File.Copy(source, target, true);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        // .meta пишем сами: постоянный GUID и выключенная проверка ссылок
        // (иначе Unity откажется грузить DLL, которой не хватает BepInEx, Steamworks и т.п.)
        var meta = target + ".meta";
        if (!File.Exists(meta))
            File.WriteAllText(meta, PluginMeta.Replace("{guid}", guid));
    }

    private const string PluginMeta =
@"fileFormatVersion: 2
guid: {guid}
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 1
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  userData:
  assetBundleName:
  assetBundleVariant:
";
}
