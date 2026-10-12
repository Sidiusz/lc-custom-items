// Меню: Lethal Company → Build AssetBundles.
// Бандлы собираются в папку Bundles/ в корне репозитория, откуда их берёт сборка мода.
using System.IO;
using UnityEditor;

public static class BuildBundles
{
    [MenuItem("Lethal Company/Build AssetBundles")]
    public static void Build()
    {
        // Без ассетов предметов бандл получится пустым для мода
        if (!ItemAssets.EnsureForBuild())
            return;

        const string output = "Bundles";
        Directory.CreateDirectory(output);
        BuildPipeline.BuildAssetBundles(output, BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);
        EditorUtility.RevealInFinder(output);
    }
}
