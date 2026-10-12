// Собирает префаб игрушечного пистолета из сгенерированных ассетов
// (Tools/ToyGun/model.py → Assets/Sources/Art/*/ToyGun/) и помечает его и звук
// бандлом customitems. Срабатывает сам при импорте этих файлов, если префаба
// ещё нет; пересобрать вручную: Lethal Company → Rebuild Toy Gun Prefab.
using System.IO;
using UnityEditor;
using UnityEngine;

public class ToyGunSetup : AssetPostprocessor
{
    private const string Art = "Assets/Sources/Art";
    private const string ModelPath = Art + "/Models/ToyGun/ToyGunModel.obj";
    private const string TexturePath = Art + "/Textures/ToyGun/ToyGunTexture.png";
    private const string SoundPath = Art + "/Audio/ToyGun/ToyGunShot.wav";
    private const string MaterialPath = Art + "/Materials/ToyGun/ToyGunMaterial.mat";
    private const string PrefabPath = "Assets/Sources/Prefabs/ToyGun.prefab";

    // Пути генерируемых ассетов: их импорт запускает сборку префаба
    private static readonly string[] SourcePaths = { ModelPath, TexturePath, SoundPath };
    private const string Bundle = "customitems";

    // Дуло в координатах модели (скрипт Tools/ToyGun/model.py печатает её при запуске)
    private static readonly Vector3 MuzzlePosition = new Vector3(0f, 0.084f, 0.125f);

    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        foreach (var path in imported)
        {
            if (System.Array.IndexOf(SourcePaths, path) >= 0 && !File.Exists(PrefabPath))
            {
                EditorApplication.delayCall += () =>
                {
                    if (!File.Exists(PrefabPath))
                        Build();
                };
                return;
            }
        }
    }

    [MenuItem("Lethal Company/Rebuild Toy Gun Prefab")]
    public static void Build()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        if (model == null || texture == null)
        {
            Debug.LogWarning("Toy gun: нет " + ModelPath + " или " + TexturePath + ", запустите Tools/ToyGun/model.py");
            return;
        }

        var shader = Shader.Find("HDRP/Lit");
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            EnsureFolder(Path.GetDirectoryName(MaterialPath));
            material = new Material(shader != null ? shader : Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        material.SetTexture("_BaseColorMap", texture);
        material.SetTexture("_MainTex", texture);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Smoothness", 0.55f);
        EditorUtility.SetDirty(material);

        var root = new GameObject("ToyGun");
        var visual = Object.Instantiate(model, root.transform);
        visual.name = "Mesh";
        foreach (var r in visual.GetComponentsInChildren<Renderer>(true))
        {
            var mats = new Material[r.sharedMaterials.Length];
            for (var i = 0; i < mats.Length; i++)
                mats[i] = material;
            r.sharedMaterials = mats;
        }

        var muzzle = new GameObject("Muzzle");
        muzzle.transform.SetParent(root.transform, false);
        muzzle.transform.localPosition = MuzzlePosition;

        EnsureFolder(Path.GetDirectoryName(PrefabPath));
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);

        AssetImporter.GetAtPath(PrefabPath).assetBundleName = Bundle;
        var sound = AssetImporter.GetAtPath(SoundPath);
        if (sound != null)
            sound.assetBundleName = Bundle;
        AssetDatabase.SaveAssets();
        Debug.Log("Toy gun: префаб собран (" + PrefabPath + "), бандл " + Bundle);
    }

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
