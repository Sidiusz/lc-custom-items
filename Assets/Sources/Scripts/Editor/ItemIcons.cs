// Иконки предметов для инвентаря: рендер префаба в PNG 256×256 с прозрачным фоном.
//   Assets/Sources/Art/Textures/<Предмет>/<Предмет>Icon.png  →  Item.itemIcon
//
// Сами рисуются для предметов без иконки (при создании ассетов и перед сборкой бандла).
// Перерисовать все: Lethal Company → Render Item Icons (например, после смены модели).
// Иконку, которую вы положили в Item руками из другого места, скрипт не заменяет.
//
// Рендер через PreviewRenderUtility (как превью в инспекторе). HDRP не отдаёт альфу,
// поэтому кадр снимается дважды, на чёрном и белом фоне, и прозрачность считается по разнице.
// Экспозиция фиксируется своим Volume и подбирается по кадру, иначе автоэкспозиция HDRP пересвечивает.
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
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
    }

    private static Texture2D RenderPrefab(GameObject prefab, Vector3 angles)
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

            preview.lights[0].intensity = 1.3f;
            preview.lights[0].transform.rotation = Quaternion.Euler(35f, angles.y + 35f, 0f);
            preview.lights[1].intensity = 0.8f;
            preview.lights[1].transform.rotation = Quaternion.Euler(-20f, angles.y - 150f, 0f);
            preview.ambientColor = new Color(0.45f, 0.45f, 0.45f);

            // Экспозиция: в профиле HDRP она автоматическая и на почти пустом кадре
            // выкручивается так, что предмет выходит белым. Ставим свою, фиксированную,
            // и подбираем её по самому кадру, чтобы яркие места предмета были около 0.9.
            var exposure = AddFixedExposure(preview);
            var ev = 0f;
            Texture2D black = null;
            for (var i = 0; i < 6; i++)
            {
                if (black != null)
                    Object.DestroyImmediate(black);
                SetExposure(exposure, ev);
                black = Shot(preview, Color.black);
                if (exposure == null)
                    break;
                var bright = Brightness(black);
                if (bright < 0f)
                    break; // предмета на кадре нет: подбирать нечего
                var step = bright >= 0.995f ? 2f // пересвет, насколько сильный, не видно
                    : bright < 0.02f ? -3f
                    : Mathf.Log(Mathf.Pow(bright, 2.2f) / Mathf.Pow(0.9f, 2.2f), 2f);
                if (Mathf.Abs(step) < 0.15f)
                    break;
                ev += step; // больше EV — темнее
            }
            var white = Shot(preview, Color.white);
            var result = Matte(black, white);
            Object.DestroyImmediate(black);
            Object.DestroyImmediate(white);
            if (exposure != null)
                Object.DestroyImmediate(exposure.Profile);
            return result;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Не удалось нарисовать иконку {prefab.name}: {e.Message}");
            return null;
        }
        finally
        {
            preview.Cleanup();
        }
    }

    // Яркость светлых мест предмета (95-й перцентиль max(r,g,b) по непустым пикселям кадра на чёрном фоне),
    // -1, если предмета на кадре нет
    private static float Brightness(Texture2D shot)
    {
        var values = shot.GetPixels().Select(c => Mathf.Max(c.r, Mathf.Max(c.g, c.b))).Where(v => v > 0.004f).ToArray();
        if (values.Length < 16)
            return -1f;
        Array.Sort(values);
        return values[(int)(values.Length * 0.95f)];
    }

    private class ExposureOverride
    {
        public ScriptableObject Profile;
        public object FixedExposure; // FloatParameter
    }

    // Глобальный Volume в сцене превью с фиксированной экспозицией и без тонмаппинга (цвета как в текстуре).
    // Типы HDRP ищем по имени, как и HDAdditionalCameraData.
    private static ExposureOverride AddFixedExposure(PreviewRenderUtility preview)
    {
        var volumeType = FindType("UnityEngine.Rendering.Volume");
        var profileType = FindType("UnityEngine.Rendering.VolumeProfile");
        var exposureType = FindType("UnityEngine.Rendering.HighDefinition.Exposure");
        var tonemappingType = FindType("UnityEngine.Rendering.HighDefinition.Tonemapping");
        if (volumeType == null || profileType == null || exposureType == null)
            return null;
        try
        {
            var profile = ScriptableObject.CreateInstance(profileType);
            profile.hideFlags = HideFlags.HideAndDontSave;
            var add = profileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
            var exposure = add.Invoke(profile, new object[] { exposureType, true });
            SetParameter(exposure, "mode", Enum.Parse(exposureType.Assembly.GetType("UnityEngine.Rendering.HighDefinition.ExposureMode"), "Fixed"));
            SetParameter(exposure, "compensation", 0f);
            if (tonemappingType != null)
            {
                var tonemapping = add.Invoke(profile, new object[] { tonemappingType, true });
                SetParameter(tonemapping, "mode", Enum.Parse(tonemappingType.Assembly.GetType("UnityEngine.Rendering.HighDefinition.TonemappingMode"), "None"));
            }

            var go = new GameObject("IconVolume");
            preview.AddSingleGO(go);
            var volume = go.AddComponent(volumeType);
            // В SRP Core 14 isGlobal — свойство, в других версиях — поле
            var isGlobalProp = volumeType.GetProperty("isGlobal");
            if (isGlobalProp != null)
                isGlobalProp.SetValue(volume, true);
            else
                volumeType.GetField("isGlobal").SetValue(volume, true);
            volumeType.GetField("priority").SetValue(volume, 10000f);
            volumeType.GetField("sharedProfile").SetValue(volume, profile);
            return new ExposureOverride { Profile = profile, FixedExposure = exposureType.GetField("fixedExposure").GetValue(exposure) };
        }
        catch (Exception e)
        {
            Debug.LogWarning("Не удалось зафиксировать экспозицию HDRP для иконок: " + e.Message);
            return null;
        }
    }

    private static void SetExposure(ExposureOverride exposure, float ev)
    {
        if (exposure != null)
            exposure.FixedExposure.GetType().GetProperty("value").SetValue(exposure.FixedExposure, ev);
    }

    private static void SetParameter(object component, string name, object value)
    {
        var parameter = component.GetType().GetField(name).GetValue(component);
        parameter.GetType().GetProperty("value").SetValue(parameter, value);
        parameter.GetType().GetProperty("overrideState").SetValue(parameter, true);
    }

    private static Type FindType(string fullName) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType(fullName, false))
        .FirstOrDefault(t => t != null);

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

    private static Texture2D Shot(PreviewRenderUtility preview, Color background)
    {
        preview.camera.clearFlags = CameraClearFlags.SolidColor;
        preview.camera.backgroundColor = background;
        SetHdrpBackground(preview.camera, background);
        preview.BeginStaticPreview(new Rect(0, 0, Size, Size));
        preview.Render(true, false);
        return preview.EndStaticPreview();
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
