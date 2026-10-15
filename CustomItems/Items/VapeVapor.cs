using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Random = UnityEngine.Random;

namespace CustomItems.Items
{
    /// <summary>
    /// Пар вейпа. Облако из двух частей:
    ///  - клубы: полупрозрачные круглые картинки, всегда повёрнутые к камере, вылетают изо рта,
    ///    тормозят, расползаются и тают;
    ///  - туман: локальный объёмный туман HDRP (как пар у перекрытого вентиля), растёт вокруг
    ///    места выдоха и долго (VaporSeconds) редеет, медленно поднимаясь.
    /// Эффекты живут на отдельном объекте: вейп можно выбросить, а облако должно дотаять.
    /// Ассетов не нужно: текстура и материал создаются в коде.
    /// </summary>
    internal class VapeVapor : MonoBehaviour
    {
        private const int MaxPuffs = 400;
        private const int MaxClouds = 24;

        private static VapeVapor host;
        private static Material puffMaterial;
        private static Texture2D puffTexture;
        private static Mesh quad;
        private static MaterialPropertyBlock block;

        private readonly List<Puff> puffs = new List<Puff>();
        private readonly List<Cloud> clouds = new List<Cloud>();

        private class Puff
        {
            public Vector3 Position, Velocity;
            public float Age, Life, Size, Grow, Alpha, Spin, Angle;
            public Color Color;
        }

        private class Cloud
        {
            public GameObject Go;
            public LocalVolumetricFog Fog;
            public float Age, Life, Size, Density;
            public Vector3 Drift;
        }

        private static VapeVapor Host
        {
            get
            {
                if (host == null)
                {
                    var go = new GameObject("CustomItems.VapeVapor");
                    DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    host = go.AddComponent<VapeVapor>();
                }
                return host;
            }
        }

        public static void Run(IEnumerator routine) => Host.StartCoroutine(routine);

        /// <summary>
        /// Выдох игрока: hold — сколько секунд тянул (от него размер облака), life — сколько висит туман.
        /// </summary>
        public static void Exhale(PlayerControllerB player, float hold, Color color, float life)
        {
            if (player == null || player.gameplayCamera == null || hold < 0.15f)
                return;
            Host.StartCoroutine(Host.Exhaling(player, hold, color, life));
        }

        /// <summary>Клуб пара из точки во все стороны (перегрев, взрыв). power ~1 — небольшой, 2-3 — большой.</summary>
        public static void Burst(Vector3 at, float power, Color color, float life)
        {
            var h = Host;
            var count = Mathf.RoundToInt(14 * power);
            for (var i = 0; i < count; i++)
            {
                var dir = Random.onUnitSphere;
                dir.y = Mathf.Abs(dir.y) * 0.8f + 0.2f;
                h.Spawn(at, dir * Random.Range(0.6f, 2.2f) * Mathf.Sqrt(power), color, Random.Range(0.1f, 0.25f) * power,
                    Random.Range(4f, 8f));
            }
            h.AddCloud(at, 0.8f + 0.7f * power, 0.7f, life, color);
        }

        private IEnumerator Exhaling(PlayerControllerB player, float hold, Color color, float life)
        {
            var amount = Mathf.Clamp(hold, 0.2f, 10f);
            var seconds = 0.5f + amount * 0.35f;     // дольше тянул — дольше выдыхает
            var rate = 10f + amount * 5f;            // клубов в секунду
            var isLocal = player == GameNetworkManager.Instance?.localPlayerController;
            var spawned = 0f;
            Vector3 mouth = default, forward = default;
            for (var t = 0f; t < seconds; t += Time.deltaTime)
            {
                if (player == null || player.gameplayCamera == null)
                    break;
                var cam = player.gameplayCamera.transform;
                // У себя пар начинается чуть дальше, чтобы не залеплять весь экран
                mouth = cam.position + cam.forward * (isLocal ? 0.35f : 0.18f) - cam.up * 0.08f;
                forward = cam.forward;
                var strength = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / seconds)) * 0.6f + 0.4f;
                spawned += rate * Time.deltaTime;
                while (spawned >= 1f)
                {
                    spawned -= 1f;
                    var dir = (forward + Random.insideUnitSphere * 0.18f - Vector3.up * 0.05f).normalized;
                    Spawn(mouth, dir * Random.Range(0.9f, 1.8f) * (0.6f + 0.4f * strength) * (1f + amount * 0.08f),
                        color, Random.Range(0.06f, 0.12f), Random.Range(3.5f, 6f) + amount * 0.6f);
                }
                yield return null;
            }
            if (player != null)
            {
                // Облако висит перед лицом там, куда дули
                AddCloud(mouth + forward * (0.5f + amount * 0.12f), 0.5f + amount * 0.3f,
                    Mathf.Clamp01(0.25f + amount * 0.1f), life * Mathf.Clamp(0.35f + amount * 0.15f, 0.35f, 1f), color);
            }
        }

        private void Spawn(Vector3 at, Vector3 velocity, Color color, float size, float life)
        {
            if (puffs.Count >= MaxPuffs)
                puffs.RemoveAt(0);
            puffs.Add(new Puff
            {
                Position = at, Velocity = velocity, Life = life, Size = size, Grow = Random.Range(0.25f, 0.45f),
                Alpha = Random.Range(0.35f, 0.6f), Spin = Random.Range(-30f, 30f), Angle = Random.Range(0f, 360f),
                Color = color,
            });
        }

        // ---------------- туман

        private void AddCloud(Vector3 at, float size, float density, float life, Color color)
        {
            if (clouds.Count >= MaxClouds)
            {
                Destroy(clouds[0].Go);
                clouds.RemoveAt(0);
            }
            var go = new GameObject("CustomItems.VapeCloud");
            go.transform.position = at;
            LocalVolumetricFog fog = null;
            try
            {
                fog = go.AddComponent<LocalVolumetricFog>();
                fog.parameters.albedo = color;
                fog.parameters.size = Vector3.one * 0.1f;
                fog.parameters.meanFreePath = 50f;
                fog.parameters.falloffMode = LocalVolumetricFogFalloffMode.Exponential;
                fog.parameters.positiveFade = Vector3.one * 0.6f;
                fog.parameters.negativeFade = Vector3.one * 0.6f;
                fog.parameters.distanceFadeStart = 60f;
                fog.parameters.distanceFadeEnd = 80f;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CustomItems] Туман пара не создался: {e.Message}");
            }
            clouds.Add(new Cloud
            {
                Go = go, Fog = fog, Life = Mathf.Max(3f, life), Size = size, Density = density,
                Drift = new Vector3(Random.Range(-0.03f, 0.03f), 0.05f, Random.Range(-0.03f, 0.03f)),
            });
        }

        private void Update()
        {
            var dt = Time.deltaTime;
            for (var i = clouds.Count - 1; i >= 0; i--)
            {
                var c = clouds[i];
                c.Age += dt;
                if (c.Age >= c.Life || c.Go == null)
                {
                    if (c.Go != null)
                        Destroy(c.Go);
                    clouds.RemoveAt(i);
                    continue;
                }
                c.Go.transform.position += c.Drift * dt;
                if (c.Fog == null)
                    continue;
                // Быстро раздувается за пару секунд, потом медленно растёт и редеет
                var k = c.Age / c.Life;
                var size = c.Size * (Mathf.SmoothStep(0.3f, 1f, Mathf.Clamp01(c.Age / 2f)) + 0.6f * k);
                c.Fog.parameters.size = new Vector3(size * 1.3f, size, size * 1.3f);
                // Плотность: mean free path малый — густо. От густого к прозрачному к концу жизни
                var thick = c.Density * (1f - k) * (1f - k);
                c.Fog.parameters.meanFreePath = Mathf.Lerp(80f, 0.6f, thick);
            }
        }

        // ---------------- клубы

        private void LateUpdate()
        {
            if (puffs.Count == 0)
                return;
            var cam = ActiveCamera();
            var material = PuffMaterial();
            if (cam == null || material == null)
            {
                puffs.Clear();
                return;
            }
            var dt = Time.deltaTime;
            block ??= new MaterialPropertyBlock();
            var faceCam = cam.transform.rotation;
            for (var i = puffs.Count - 1; i >= 0; i--)
            {
                var p = puffs[i];
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    puffs.RemoveAt(i);
                    continue;
                }
                // Воздух тормозит клуб, тёплый пар чуть поднимается
                p.Velocity *= Mathf.Exp(-2.2f * dt);
                p.Velocity += Vector3.up * (0.06f * dt);
                p.Position += p.Velocity * dt;
                p.Angle += p.Spin * dt;
                var k = p.Age / p.Life;
                var size = p.Size + p.Grow * Mathf.Sqrt(p.Age);
                var alpha = p.Alpha * Mathf.Clamp01(p.Age / 0.15f) * (1f - k) * (1f - k);
                var color = p.Color;
                color.a = alpha;
                block.SetColor("_UnlitColor", color);
                block.SetColor("_BaseColor", color);
                var m = Matrix4x4.TRS(p.Position, faceCam * Quaternion.Euler(0f, 0f, p.Angle), Vector3.one * size);
                Graphics.DrawMesh(quad, m, material, 0, null, 0, block, ShadowCastingMode.Off, false);
            }
        }

        private static Camera ActiveCamera()
        {
            var round = StartOfRound.Instance;
            if (round != null && round.activeCamera != null)
                return round.activeCamera;
            return Camera.main;
        }

        private static Material PuffMaterial()
        {
            if (puffMaterial != null)
                return puffMaterial;
            var shader = Shader.Find("HDRP/Unlit");
            if (shader == null)
                return null;
            puffTexture ??= SoftCircle();
            quad ??= MakeQuad();
            var m = new Material(shader) { name = "VapePuff" };
            m.SetTexture("_UnlitColorMap", puffTexture);
            m.SetColor("_UnlitColor", Color.white);
            // Прозрачный материал HDRP, собранный в коде: те же свойства и ключевые слова,
            // что выставил бы инспектор (Surface Type = Transparent, Blend = Alpha)
            m.SetFloat("_SurfaceType", 1f);
            m.SetFloat("_BlendMode", 0f);
            m.SetFloat("_AlphaCutoffEnable", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.One);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_AlphaSrcBlend", (float)BlendMode.One);
            m.SetFloat("_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_TransparentZWrite", 0f);
            m.SetFloat("_CullMode", (float)CullMode.Off);
            m.SetFloat("_ZTestTransparent", (float)CompareFunction.LessEqual);
            m.SetFloat("_EnableFogOnTransparent", 1f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.EnableKeyword("_BLENDMODE_ALPHA");
            m.EnableKeyword("_BLENDMODE_PRESERVE_SPECULAR_LIGHTING");
            m.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            try
            {
                HDMaterial.ValidateMaterial(m);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CustomItems] Материал пара: {e.Message}");
            }
            puffMaterial = m;
            return m;
        }

        // Белый круг с мягким краем и лёгкими разводами, чтобы клубы не были одинаковыми кружками
        private static Texture2D SoftCircle()
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "VapePuff" };
            var seed = Random.Range(0f, 100f);
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var dx = x / (n - 1f) * 2f - 1f;
                var dy = y / (n - 1f) * 2f - 1f;
                var d = Mathf.Sqrt(dx * dx + dy * dy);
                var noise = Mathf.PerlinNoise(seed + x * 0.11f, seed + y * 0.11f);
                var a = Mathf.Clamp01(1f - d) * Mathf.Clamp01(1f - d) * (0.6f + 0.6f * noise);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a)));
            }
            tex.Apply(true);
            return tex;
        }

        private static Mesh MakeQuad()
        {
            var mesh = new Mesh { name = "VapePuff" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
