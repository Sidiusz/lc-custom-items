// Иконки предметов для инвентаря: рендер префаба в PNG 256×256 с прозрачным фоном.
//   Assets/Sources/Art/Textures/<Предмет>/<Предмет>Icon.png  →  Item.itemIcon
//
// Сами рисуются для предметов без иконки (при создании ассетов и перед сборкой бандла).
// Перерисовать все: Lethal Company → Render Item Icons (например, после смены модели).
// Иконку, которую вы положили в Item руками из другого места, скрипт не заменяет.
//
// Рендер через PreviewRenderUtility (как превью в инспекторе). HDRP не отдаёт альфу,
// поэтому кадр снимается дважды, на чёрном и белом фоне, и прозрачность считается по разнице.
// Каждый кадр снимается в отдельной сцене превью с одинаковым светом, без своей экспозиции.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class ItemIcons
{
    private const string Textures = "Assets/Sources/Art/Textures";
    private const string ItemsDir = "Assets/Sources/Data/Items";
    private const int Size = 256;

    // Ракурс камеры (Euler) по имени ассета Item. По умолчанию — спереди-сбоку и чуть сверху.
    private static readonly Vector3 DefaultAngles = new Vector3(12f, 155f, 0f);
    private static readonly Dictionary<string, Vector3> Angles = new Dictionary<string, Vector3>
    {
        { "ToyGun", new Vector3(8f, 90f, 0f) }, // пистолет сбоку, дуло вправо
    };

    [MenuItem("Lethal Company/Render Item Icons")]
    private static void RenderAllMenu()
    {
        var count = Render(onlyMissing: false);
        Debug.Log($"Иконки предметов перерисованы: {count}. Пересоберите бандл.");
    }

    /// <summary>Рисует иконки, которых ещё нет. Возвращает число нарисованных.</summary>
    public static int RenderMissing() => Render(onlyMissing: true);

    private static int Render(bool onlyMissing)
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            return 0; // batchmode с -nographics: рисовать нечем

        var count = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { ItemsDir }))
        {
            var itemPath = AssetDatabase.GUIDToAssetPath(guid);
            var item = AssetDatabase.LoadAssetAtPath<ScriptableObject>(itemPath);
            if (item == null || item.GetType().Name != "Item")
                continue;

            var so = new SerializedObject(item);
            var iconProp = so.FindProperty("itemIcon");
            var prefab = so.FindProperty("spawnPrefab")?.objectReferenceValue as GameObject;
            if (iconProp == null || prefab == null)
                continue;

            var name = Path.GetFileNameWithoutExtension(itemPath);
            var iconPath = $"{Textures}/{name}/{name}Icon.png";
            var current = iconProp.objectReferenceValue;
            if (current != null && !IsOurs(current))
                continue; // своя иконка пользователя
            if (onlyMissing && File.Exists(iconPath))
                continue;

            var angles = Angles.TryGetValue(name, out var a) ? a : DefaultAngles;
            var texture = RenderPrefab(prefab, angles);
            if (texture == null)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath));
            File.WriteAllBytes(iconPath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            ImportAsSprite(iconPath);

            iconProp.objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>(iconPath);
            so.ApplyModifiedPropertiesWithoutUndo();
            count++;
        }
        if (count > 0)
            AssetDatabase.SaveAssets();
        return count;
    }

    // Наши иконки: <Предмет>Icon.png в Art/Textures (сгенерированные и старая иконка банки)
    private static bool IsOurs(Object icon)
    {
        var path = AssetDatabase.GetAssetPath(icon);
        return path.StartsWith(Textures + "/") && path.EndsWith("Icon.png");
    }

    private static void ImportAsSprite(string path)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.maxTextureSize = Size;
        importer.textureCompression = TextureImporterCompression.CompressedHQ; // BC7: в 4 раза меньше, без заметных артефактов
        importer.SaveAndReimport();
    }

    private static Texture2D RenderPrefab(GameObject prefab, Vector3 angles)
    {
        try
        {
            var black = Shot(prefab, angles, Color.black);
            if (black == null)
                return null;
            var white = Shot(prefab, angles, Color.white);
            var result = Matte(black, white);
            Object.DestroyImmediate(black);
            Object.DestroyImmediate(white);
            return result;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Не удалось нарисовать иконку {prefab.name}: {e.Message}");
            return null;
        }
    }

    // Каждый кадр снимается в новой сцене превью: при повторном рендере HDRP успевает подстроить
    // автоэкспозицию, кадры на чёрном и белом фоне расходятся, и Matte даёт белёсую картинку
    private static Texture2D Shot(GameObject prefab, Vector3 angles, Color background)
    {
        var preview = new PreviewRenderUtility();
        try
        {
            var go = preview.InstantiatePrefabInScene(prefab);
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return null;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);

            var cam = preview.camera;
            cam.orthographic = true;
            cam.transform.rotation = Quaternion.Euler(angles);
            var distance = bounds.extents.magnitude * 4f + 1f;
            cam.transform.position = bounds.center - cam.transform.forward * distance;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = distance * 2f;
            cam.orthographicSize = FitSize(cam.transform, bounds) * 1.08f;

            preview.lights[0].intensity = 1.6f;
            preview.lights[0].transform.rotation = Quaternion.Euler(35f, angles.y + 35f, 0f);
            preview.lights[1].intensity = 0.9f;
            preview.lights[1].transform.rotation = Quaternion.Euler(-20f, angles.y - 150f, 0f);
            preview.ambientColor = new Color(0.5f, 0.5f, 0.5f);

            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            SetHdrpBackground(cam, background);
            preview.BeginStaticPreview(new Rect(0, 0, Size, Size));
            preview.Render(true, false);
            return preview.EndStaticPreview();
        }
        finally
        {
            preview.Cleanup();
        }
    }

    // Половина большей стороны проекции границ на плоскость камеры
    private static float FitSize(Transform cam, Bounds b)
    {
        var half = 0f;
        for (var i = 0; i < 8; i++)
        {
            var corner = b.center + Vector3.Scale(b.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            var local = cam.InverseTransformPoint(corner);
            half = Mathf.Max(half, Mathf.Abs(local.x), Mathf.Abs(local.y));
        }
        return Mathf.Max(half, 0.01f);
    }

    // HDRP берёт цвет фона из HDAdditionalCameraData. Тип ищем по имени, чтобы не тянуть
    // ссылку на HDRP в asmdef.
    private static void SetHdrpBackground(Camera cam, Color color)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData", false))
            .FirstOrDefault(t => t != null);
        if (type == null)
            return;
        var data = cam.GetComponent(type) ?? cam.gameObject.AddComponent(type);
        var mode = type.GetField("clearColorMode");
        if (mode != null)
            mode.SetValue(data, Enum.Parse(mode.FieldType, "Color"));
        type.GetField("backgroundColorHDR")?.SetValue(data, color);
    }

    // Альфа по разнице кадров на чёрном и белом фоне. Цвет фона берём из угла кадра,
    // чтобы тонемаппинг HDRP не мешал: там точно пусто.
    private static Texture2D Matte(Texture2D black, Texture2D white)
    {
        var b = black.GetPixels();
        var w = white.GetPixels();
        var bg = w[0] - b[0];
        var range = Mathf.Max(0.05f, (bg.r + bg.g + bg.b) / 3f);
        var result = new Color[b.Length];
        for (var i = 0; i < b.Length; i++)
        {
            var diff = ((w[i].r - b[i].r) + (w[i].g - b[i].g) + (w[i].b - b[i].b)) / 3f;
            var alpha = Mathf.Clamp01(1f - diff / range);
            var c = alpha > 0.004f ? b[i] / alpha : Color.clear;
            c.a = alpha;
            result[i] = c;
        }
        var texture = new Texture2D(black.width, black.height, TextureFormat.RGBA32, false);
        texture.SetPixels(result);
        texture.Apply();
        return texture;
    }
}
