// Положите этот файл в Assets/Editor/ своего Unity-проекта.
// Меню: Lethal Company → Build AssetBundles.
// Если Unity-проект лежит в папке Unity/ этого репозитория, бандлы сразу попадут в Bundles/,
// откуда их берёт сборка мода. Иначе — в папку AssetBundles/ рядом с Unity-проектом.
using System.IO;
using UnityEditor;

public static class BuildBundles
{
    [MenuItem("Lethal Company/Build AssetBundles")]
    public static void Build()
    {
        var output = Directory.Exists("../Bundles") ? "../Bundles" : "AssetBundles";
        Directory.CreateDirectory(output);
        BuildPipeline.BuildAssetBundles(output, BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);
        EditorUtility.RevealInFinder(output);
    }
}
