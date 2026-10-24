using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CustomItems.Items
{
    /// <summary>
    /// Модели вейпов без Unity-бандла: меши строятся в игре из встроенных в DLL файлов
    /// Models/Vape&lt;Модель&gt;.mdl.gz (их пишет Tools/Vape/model.py), материалы HDRP/Lit — в коде.
    /// Объекты те же, что в FBX: Body, Panel, Cap, Mouthpiece, Hole, Bezel, Screen, Logo..., Glass,
    /// Liquid, Chimney, Button, Stripe; пустышка Mouth. Низ модели на y = 0 (пивот предмета).
    /// </summary>
    public static class VapeModels
    {
        private const string ModelPrefix = "SidiuszCustomItems.Models.";
        private const string TexturePrefix = "SidiuszCustomItems.Textures.";

        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

        /// <summary>Собрать модель Vape&lt;style&gt; дочерними объектами root. Возвращает границы видимых мешей.</summary>
        public static Bounds Build(Transform root, string style, int layer)
        {
            var bounds = new Bounds();
            var first = true;
            foreach (var part in Load("Vape" + style))
            {
                var go = new GameObject(part.Name) { layer = layer };
                go.transform.SetParent(root, false);
                if (part.Point.HasValue)
                {
                    go.transform.localPosition = part.Point.Value;
                    continue;
                }
                var mesh = new Mesh { name = $"Vape{style}{part.Name}" };
                if (part.Vertices.Count > 65000)
                    mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(part.Vertices);
                mesh.SetNormals(part.Normals);
                mesh.SetUVs(0, part.Uvs);
                mesh.SetTriangles(part.Triangles, 0);
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = MaterialFor(style, part.Name);
                if (first)
                    bounds = mesh.bounds;
                else
                    bounds.Encapsulate(mesh.bounds);
                first = false;
            }
            return bounds;
        }

        private class Part
        {
            public string Name;
            public Vector3? Point;
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int> Triangles = new List<int>();
        }

        private static List<Part> Load(string name)
        {
            var parts = new List<Part>();
            using var stream = typeof(VapeModels).Assembly.GetManifestResourceStream(ModelPrefix + name + ".mdl.gz")
                               ?? throw new FileNotFoundException("Нет встроенной модели " + name);
            using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
            Part part = null;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 2)
                    continue;
                var f = line.Split(' ');
                switch (f[0])
                {
                    case "o":
                        parts.Add(part = new Part { Name = f[1] });
                        break;
                    case "p":
                        part.Point = new Vector3(F(f[1]), F(f[2]), F(f[3]));
                        break;
                    case "v":
                        part.Vertices.Add(new Vector3(F(f[1]), F(f[2]), F(f[3])));
                        part.Normals.Add(new Vector3(F(f[4]), F(f[5]), F(f[6])));
                        part.Uvs.Add(new Vector2(F(f[7]), F(f[8])));
                        break;
                    case "t":
                        part.Triangles.Add(int.Parse(f[1], CultureInfo.InvariantCulture));
                        part.Triangles.Add(int.Parse(f[2], CultureInfo.InvariantCulture));
                        part.Triangles.Add(int.Parse(f[3], CultureInfo.InvariantCulture));
                        break;
                }
            }
            return parts;
        }

        private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        // ---------------- материалы (как SetupVape в Editor/ItemAssets.cs)

        private static Color C(int r, int g, int b, float a = 1f) => new Color(r / 255f, g / 255f, b / 255f, a);

        private static Material MaterialFor(string style, string part)
        {
            var gold = C(220, 175, 80);
            var black = C(14, 14, 16);
            switch (part)
            {
                case "Body":
                case "Panel":
                    return style == "Pod"
                        ? Lit($"{style}Body", C(16, 16, 18), 0.6f, 0.2f)
                        : Lit($"{style}Body", Color.white, 0.7f, 0f, $"Vape{style}Body");
                case "Cap":
                    return Lit("Cap", black, 0.6f);
                case "Mouthpiece":
                    return style == "Pixl" ? Lit("PixlMouthpiece", C(255, 90, 205), 0.6f) : Lit("Cap", black, 0.6f);
                case "Hole":
                    return Lit("Trim", C(20, 12, 20), 0.2f);
                case "Bezel":
                    return Lit("Bezel", C(8, 8, 10), 0.9f);
                case "Screen":
                    return Lit("Screen", Color.black, 0.8f); // картинку и свечение ставит VapeScreen
                case "Glass":
                    return Lit("Glass", C(200, 215, 225, 0.25f), 0.8f, 0f, null, true);
                case "Liquid":
                    return Lit("Liquid", C(255, 160, 40, 0.6f), 0.8f, 0f, null, true);
                case "Chimney":
                    return Lit("Chimney", C(140, 140, 150), 0.7f, 0.8f);
                case "Button":
                case "Stripe":
                case "Stripe2":
                    return Lit("Gold", gold, 0.75f, 1f);
                default: // Logo, Logo8000
                    return Lit("Logo", C(240, 240, 245), 0.4f);
            }
        }

        private static Material Lit(string key, Color color, float smoothness, float metallic = 0f,
            string texture = null, bool transparent = false)
        {
            if (Materials.TryGetValue(key, out var cached) && cached != null)
                return cached;
            var shader = Shader.Find("HDRP/Lit");
            var m = new Material(shader != null ? shader : Shader.Find("Standard")) { name = "Vape" + key };
            m.SetColor("_BaseColor", color);
            m.SetColor("_Color", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            if (texture != null)
            {
                var tex = LoadTexture(texture);
                if (tex != null)
                {
                    m.SetTexture("_BaseColorMap", tex);
                    m.SetTexture("_MainTex", tex);
                }
            }
            if (transparent)
            {
                m.SetFloat("_SurfaceType", 1f);
                m.SetFloat("_BlendMode", 0f);
                m.SetFloat("_EnableFogOnTransparent", 1f);
                m.SetFloat("_ZWrite", 0f);
                m.SetFloat("_TransparentZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.EnableKeyword("_BLENDMODE_ALPHA");
                m.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            try
            {
                HDMaterial.ValidateMaterial(m);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CustomItems] Материал вейпа {key}: {e.Message}");
            }
            return Materials[key] = m;
        }

        private static Texture2D LoadTexture(string name)
        {
            using var stream = typeof(VapeModels).Assembly.GetManifestResourceStream(TexturePrefix + name + ".png");
            if (stream == null)
            {
                Debug.LogWarning($"[CustomItems] Нет встроенной текстуры {name}");
                return null;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp };
            tex.LoadImage(ms.ToArray());
            return tex;
        }
    }
}
