// Собирает префаб игрушечного пистолета из сгенерированных ассетов
// (Tools/generate_toy_gun.py → Assets/Items/ToyGun/) и помечает его и звук
// бандлом customitems. Срабатывает сам при импорте этих файлов, если префаба
// ещё нет; пересобрать вручную: Lethal Company → Rebuild Toy Gun Prefab.
using System.IO;
using UnityEditor;
using UnityEngine;

public class ToyGunSetup : AssetPostprocessor
{
    private const string Dir = "Assets/Items/ToyGun";
    private const string ModelPath = Dir + "/ToyGunModel.obj";
    private const string TexturePath = Dir + "/ToyGunTexture.png";
    private const string SoundPath = Dir + "/ToyGunShot.wav";
    private const string MaterialPath = Dir + "/ToyGunMaterial.mat";
    private const string PrefabPath = Dir + "/ToyGun.prefab";
    private const string Bundle = "customitems";

    // Дуло в координатах модели (см. MUZZLE в Tools/generate_toy_gun.py)
    private static readonly Vector3 MuzzlePosition = new Vector3(0f, 0.067f, 0.185f);

    private void OnPreprocessTexture()
    {
        if (assetPath != TexturePath)
            return;
        // Текстура это палитра из цветных ячеек: без мипмапов цвета не смешиваются
        var importer = (TextureImporter)assetImporter;
        importer.mipmapEnabled = false;
        importer.filterMode = FilterMode.Point;
    }

    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        foreach (var path in imported)
        {
            if (path.StartsWith(Dir + "/") && !path.EndsWith(".prefab") && !path.EndsWith(".mat")
                && !File.Exists(PrefabPath))
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
            Debug.LogWarning("Toy gun: нет модели или текстуры в " + Dir + ", запустите Tools/generate_toy_gun.py");
            return;
        }

        var shader = Shader.Find("HDRP/Lit");
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
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

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);

        AssetImporter.GetAtPath(PrefabPath).assetBundleName = Bundle;
        var sound = AssetImporter.GetAtPath(SoundPath);
        if (sound != null)
            sound.assetBundleName = Bundle;
        AssetDatabase.SaveAssets();
        Debug.Log("Toy gun: префаб собран (" + PrefabPath + "), бандл " + Bundle);
    }
}
