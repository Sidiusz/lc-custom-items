// Кладёт в Assets/Plugins библиотеки, без которых Unity не знает типы предметов:
//   Game/        Assembly-CSharp.dll и Unity.Netcode.Runtime.dll из папки игры (Item,
//                GrabbableObject, ScanNodeProperties, NetworkObject) и всё, на что они
//                ссылаются (DunGen, Facepunch, Dissonance...), кроме того, что в Unity уже есть;
//   CustomItems/ CustomItems.Items.dll из CustomItems/bin после dotnet build (скрипты
//                предметов и ItemRegistry; BepInEx и LethalLib ей не нужны).
// Без полного набора зависимостей Unity выгружает DLL («Unloading broken assembly»),
// и префабы сохраняются без скриптов.
// Папка Assets/Plugins в .gitignore: файлы игры в репозиторий не кладём.
//
// DLL игры копируются один раз (меню Lethal Company → Setup → Import Game DLLs),
// CustomItems.Items.dll обновляется сама, когда вы возвращаетесь в Unity после dotnet build.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GameLibraries
{
    private const string PluginsDir = "Assets/Plugins";
    private const string GameDir = PluginsDir + "/Game";
    private const string ModDir = PluginsDir + "/CustomItems";
    private const string FolderPref = "CustomItems.GameFolder";
    private const string AskedKey = "CustomItems.AskedForGameFolder";

    // С этих DLL начинается обход ссылок
    private static readonly string[] GameRoots = { "Assembly-CSharp", "Unity.Netcode.Runtime" };
    private const string ModDll = "CustomItems.Items.dll";
    private static readonly string[] ModBuilds =
    {
        "CustomItems/bin/Release/netstandard2.1/" + ModDll,
        "CustomItems/bin/Debug/netstandard2.1/" + ModDll,
    };
    // Остались от старой схемы, где в Unity клали весь плагин: он тянет BepInEx и не грузится
    private static readonly string[] Obsolete = { ModDir + "/CustomItems.dll" };

    private static readonly string[] DefaultGameFolders =
    {
        @"C:\Program Files (x86)\Steam\steamapps\common\Lethal Company",
        @"D:\SteamLibrary\steamapps\common\Lethal Company",
        @"E:\SteamLibrary\steamapps\common\Lethal Company",
    };

    public static bool GameDllsPresent => GameRoots.All(n => File.Exists(Path.Combine(GameDir, n + ".dll")));

    static GameLibraries()
    {
        EditorApplication.delayCall += () =>
        {
            var changed = RemoveObsolete();
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

    [MenuItem("Lethal Company/Setup/Update CustomItems.Items.dll")]
    private static void SyncModMenu()
    {
        if (SyncMod(force: true))
            AssetDatabase.Refresh();
        else
            Debug.LogWarning(ModDll + " не найдена: сначала соберите мод (cd CustomItems && dotnet build -c Release)");
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
                    "Чтобы настраивать предметы в Unity, нужны библиотеки игры (Assembly-CSharp.dll и то, на что она ссылается). " +
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

        var needed = Closure(managed, out var missing);
        foreach (var file in needed)
            Copy(file, GameDir);
        if (missing.Count > 0)
            Debug.LogWarning("Не нашлись в игре и в Unity (обычно не мешает): " + string.Join(", ", missing));
        Debug.Log($"Скопировано {needed.Count} DLL игры из {managed}:\n" +
                  string.Join("\n", needed.Select(Path.GetFileName)));
        return true;
    }

    // Обходит ссылки от GameRoots и CustomItems.Items.dll. Берёт из Managed всё, чего нет в самом Unity.
    private static List<string> Closure(string managed, out List<string> missing)
    {
        var provided = ProvidedByUnity();
        var result = new List<string>();
        missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(GameRoots);
        foreach (var r in References(NewestModBuild()))
            queue.Enqueue(r);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name) || provided.Contains(name))
                continue;
            var path = Path.Combine(managed, name + ".dll");
            if (!File.Exists(path))
            {
                missing.Add(name);
                continue;
            }
            result.Add(path);
            foreach (var r in References(path))
                queue.Enqueue(r);
        }
        return result;
    }

    // Сборки, которые Unity уже грузит сам: движок, .NET, пакеты проекта
    private static HashSet<string> ProvidedByUnity()
    {
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plugins = Path.GetFullPath(PluginsDir);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic)
                continue;
            string location;
            try { location = asm.Location; } catch { location = ""; }
            // Наши же копии из Assets/Plugins не считаются: их можно обновить
            if (!string.IsNullOrEmpty(location) && Path.GetFullPath(location).StartsWith(plugins, StringComparison.OrdinalIgnoreCase))
                continue;
            provided.Add(asm.GetName().Name);
        }
        foreach (var dir in new[]
                 {
                     "Library/ScriptAssemblies",
                     Path.Combine(EditorApplication.applicationContentsPath, "Managed", "UnityEngine"),
                     Path.Combine(EditorApplication.applicationContentsPath, "Managed"),
                 })
        {
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.dll"))
                    provided.Add(Path.GetFileNameWithoutExtension(f));
        }
        // Своих сборок у проекта с такими именами нет, а игровые нужны обязательно
        foreach (var root in GameRoots)
            provided.Remove(root);
        return provided;
    }

    private static IEnumerable<string> References(string path)
    {
        if (path == null || !File.Exists(path))
            return Enumerable.Empty<string>();
        try
        {
            return Assembly.ReflectionOnlyLoadFrom(path).GetReferencedAssemblies().Select(a => a.Name).ToArray();
        }
        catch (FileLoadException)
        {
            // Уже загружена в reflection-only контекст ранее: ищем её там
            var name = AssemblyName.GetAssemblyName(path).Name;
            var loaded = AppDomain.CurrentDomain.ReflectionOnlyGetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            return loaded != null ? loaded.GetReferencedAssemblies().Select(a => a.Name).ToArray() : Enumerable.Empty<string>();
        }
        catch (Exception e)
        {
            Debug.LogWarning("Не прочитал ссылки " + path + ": " + e.Message);
            return Enumerable.Empty<string>();
        }
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

    private static string NewestModBuild()
    {
        string newest = null;
        foreach (var build in ModBuilds)
            if (File.Exists(build) && (newest == null || File.GetLastWriteTimeUtc(build) > File.GetLastWriteTimeUtc(newest)))
                newest = build;
        return newest;
    }

    // Копирует свежую сборку скриптов мода, если она новее той, что лежит в Unity
    private static bool SyncMod(bool force = false)
    {
        var newest = NewestModBuild();
        if (newest == null)
            return false;
        var target = Path.Combine(ModDir, ModDll);
        if (!force && File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(newest))
            return false;
        Copy(newest, ModDir);
        Debug.Log(ModDll + " обновлена из " + newest);
        return true;
    }

    private static bool RemoveObsolete()
    {
        var removed = false;
        foreach (var path in Obsolete)
        {
            if (!File.Exists(path))
                continue;
            File.Delete(path);
            if (File.Exists(path + ".meta"))
                File.Delete(path + ".meta");
            removed = true;
        }
        return removed;
    }

    private static void Copy(string source, string dir)
    {
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(source));
        File.Copy(source, target, true);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        // .meta пишем сами: постоянный GUID (из имени файла) и выключенная проверка ссылок
        var meta = target + ".meta";
        if (!File.Exists(meta))
            File.WriteAllText(meta, PluginMeta.Replace("{guid}", Guid(Path.GetFileName(source))));
    }

    private static string Guid(string fileName)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("CustomItems/" + fileName));
        return string.Concat(hash.Select(b => b.ToString("x2")));
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
