using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace CustomItems.Items
{
    /// <summary>
    /// Рвота и отрыжка от Егермейстера. Когда блевать, решает сам выпивший (его клиент):
    /// при VomitAfterSips и больше действующих глотках время от времени, а «слабые» игроки
    /// (WeakPlayers) сразу после каждого глотка и ещё рыгают. Событие уходит хосту и от него
    /// всем, и каждый клиент сам играет звук, рисует струю и проверяет, не попали ли в него:
    /// тогда его экран заливает рвотой, которая за HitSeconds становится прозрачной.
    /// </summary>
    public class VomitEffects : MonoBehaviour
    {
        // Задаются из конфига в Plugin
        public static string[] WeakPlayers = { "Senya", "Shakabuy" };
        public static int VomitAfterSips = 2;

        public const float HitSeconds = 15f;
        private const float HitHold = 3f;     // сколько секунд экран залит полностью
        private const float HitRange = 3.2f;
        private const float HitAngle = 40f;

        private const float VomitEveryMin = 18f, VomitEveryMax = 32f;
        private const float BurpEveryMin = 8f, BurpEveryMax = 18f;
        private const float VomitSeconds = 1.8f; // выпивший стоит согнувшись

        private const byte KindVomit = 0, KindBurp = 1;
        private const string EventMessage = ModInfo.Guid + ".vomit";

        /// <summary>Материал для брызг и лужи: берётся у жидкости в бутылке (HDRP Lit).</summary>
        public static Material SourceMaterial;

        private static VomitEffects instance;
        private static CustomMessagingManager registeredOn;
        private static AudioClip[] vomitClips, burpClips;
        private static Material vomitMaterial;
        private static Texture2D splatTexture;

        private float nextVomit = -1f, nextBurp = -1f;
        private Overlay overlay;

        private static VomitEffects Instance
        {
            get
            {
                if (instance == null)
                {
                    var go = new GameObject("CustomItems.VomitEffects");
                    DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    instance = go.AddComponent<VomitEffects>();
                }
                return instance;
            }
        }

        public static void Register()
        {
            _ = Instance;
            var messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null || messaging == registeredOn)
                return;
            messaging.RegisterNamedMessageHandler(EventMessage, OnEventMessage);
            registeredOn = messaging;
        }

        public static bool IsWeak(PlayerControllerB player)
        {
            var me = Normalize(player != null ? player.playerUsername : null);
            return me.Length > 0 && WeakPlayers.Select(Normalize).Any(n => n.Length > 0 && me.StartsWith(n));
        }

        private static string Normalize(string s) =>
            new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        /// <summary>Глоток выпит (вызывается на всех клиентах, решает только сам выпивший).</summary>
        public static void OnSip(PlayerControllerB player)
        {
            if (player == null || player != GameNetworkManager.Instance?.localPlayerController)
                return;
            var self = Instance;
            if (IsWeak(player))
            {
                // Слабый: рыгнул и почти сразу блюёт
                self.StartCoroutine(self.WeakSip(player));
                self.nextVomit = Time.time + Random.Range(VomitEveryMin, VomitEveryMax);
                self.nextBurp = Time.time + Random.Range(BurpEveryMin, BurpEveryMax);
            }
        }

        private IEnumerator WeakSip(PlayerControllerB player)
        {
            yield return new WaitForSeconds(0.8f);
            Send(player, KindBurp);
            yield return new WaitForSeconds(Random.Range(1.8f, 3f));
            Send(player, KindVomit);
        }

        private void Update()
        {
            overlay?.Update();

            var local = GameNetworkManager.Instance?.localPlayerController;
            if (local == null || local.isPlayerDead || !local.isPlayerControlled)
            {
                nextVomit = nextBurp = -1f;
                return;
            }

            var sips = DrunkEffects.ActiveSips(local);
            var weak = IsWeak(local);
            if (sips == 0)
            {
                nextVomit = nextBurp = -1f;
                return;
            }

            // Перебрал: первый раз тошнит через несколько секунд после глотка, потом время от времени
            var sick = weak || sips >= VomitAfterSips;
            if (!sick)
                nextVomit = -1f;
            else if (nextVomit < 0f)
                nextVomit = Time.time + (weak ? Random.Range(VomitEveryMin, VomitEveryMax) : Random.Range(5f, 10f));

            if (sick && Time.time >= nextVomit)
            {
                nextVomit = Time.time + Random.Range(VomitEveryMin, VomitEveryMax);
                if (!AdrenalineEffects.IsAsleep(local))
                    Send(local, KindVomit);
            }

            if (weak && nextBurp >= 0f && Time.time >= nextBurp)
            {
                nextBurp = Time.time + Random.Range(BurpEveryMin, BurpEveryMax);
                Send(local, KindBurp);
            }
        }

        // ---- Сеть: выпивший -> хост -> все остальные ----

        private static void Send(PlayerControllerB player, byte kind)
        {
            var variant = (byte)Random.Range(0, 256);
            Play(player, kind, variant);

            var net = NetworkManager.Singleton;
            if (net == null || !net.IsListening || net.CustomMessagingManager == null)
                return;
            if (net.IsServer)
                Relay(net, (ulong)player.playerClientId, kind, variant, net.LocalClientId);
            else
                Write(net, NetworkManager.ServerClientId, (ulong)player.playerClientId, kind, variant);
        }

        private static void Relay(NetworkManager net, ulong playerId, byte kind, byte variant, ulong except)
        {
            foreach (var clientId in net.ConnectedClientsIds)
            {
                if (clientId != NetworkManager.ServerClientId && clientId != except)
                    Write(net, clientId, playerId, kind, variant);
            }
        }

        private static void Write(NetworkManager net, ulong to, ulong playerId, byte kind, byte variant)
        {
            using var writer = new FastBufferWriter(sizeof(ulong) + 2, Allocator.Temp);
            writer.WriteValueSafe(playerId);
            writer.WriteValueSafe(kind);
            writer.WriteValueSafe(variant);
            net.CustomMessagingManager.SendNamedMessage(EventMessage, to, writer, NetworkDelivery.Reliable);
        }

        private static void OnEventMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong playerId);
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out byte variant);
            var net = NetworkManager.Singleton;
            if (net == null)
                return;
            if (net.IsServer)
                Relay(net, playerId, kind, variant, sender);
            else if (sender != NetworkManager.ServerClientId)
                return;

            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || playerId >= (ulong)players.Length)
                return;
            var player = players[(int)playerId];
            if (player != null && player != GameNetworkManager.Instance?.localPlayerController)
                Play(player, kind, variant);
        }

        // ---- Что видят и слышат все ----

        private static void Play(PlayerControllerB player, byte kind, byte variant)
        {
            if (player == null || player.isPlayerDead)
                return;
            LoadClips();
            var clips = kind == KindVomit ? vomitClips : burpClips;
            var clip = clips.Length > 0 ? clips[variant % clips.Length] : null;

            if (clip != null)
                PlayAt(player, clip, kind == KindVomit ? 1f : 0.8f);
            if (kind == KindVomit)
                Instance.StartCoroutine(Instance.Vomit(player, clip != null ? clip.length : 2f));
        }

        private IEnumerator Vomit(PlayerControllerB player, float length)
        {
            var isLocal = player == GameNetworkManager.Instance?.localPlayerController;
            var start = Time.time;
            if (isLocal)
            {
                HUDManager.Instance?.ShakeCamera(ScreenShakeType.Small);
                player.disableMoveInput = true;
            }

            // Струя идёт чуть позже начала звука, пока он не кончится
            yield return new WaitForSeconds(0.25f);
            var hitChecked = false;
            var end = Time.time + Mathf.Clamp(length * 0.8f, 0.8f, 2.6f);
            while (Time.time < end && player != null && !player.isPlayerDead)
            {
                var mouth = Mouth(player, out var forward);
                for (var i = 0; i < 2; i++)
                    Drop.Spawn(mouth, forward);
                if (!hitChecked && Time.time > end - 1f)
                {
                    hitChecked = true;
                    CheckHit(player, mouth, forward);
                }
                yield return new WaitForSeconds(0.04f);
            }
            if (!hitChecked && player != null)
            {
                var mouth = Mouth(player, out var forward);
                CheckHit(player, mouth, forward);
            }

            if (isLocal)
            {
                var rest = start + VomitSeconds - Time.time;
                if (rest > 0f)
                    yield return new WaitForSeconds(rest);
                if (player != null && !AdrenalineEffects.IsAsleep(player))
                    player.disableMoveInput = false;
            }
        }

        // Попали ли в нас: стоим перед выпившим, близко и не за стеной
        private void CheckHit(PlayerControllerB vomiter, Vector3 mouth, Vector3 forward)
        {
            var local = GameNetworkManager.Instance?.localPlayerController;
            if (local == null || local == vomiter || local.isPlayerDead || local.gameplayCamera == null)
                return;
            var face = local.gameplayCamera.transform.position;
            var to = face - mouth;
            if (to.magnitude > HitRange)
                return;
            var flatTo = new Vector3(to.x, 0f, to.z);
            var flatForward = new Vector3(forward.x, 0f, forward.z);
            if (flatTo.sqrMagnitude < 0.01f || Vector3.Angle(flatForward, flatTo) > HitAngle)
                return;
            var mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (Physics.Linecast(mouth, face, mask, QueryTriggerInteraction.Ignore))
                return;

            overlay ??= new Overlay();
            overlay.Hit();
            HUDManager.Instance?.ShakeCamera(ScreenShakeType.Small);
        }

        private static Vector3 Mouth(PlayerControllerB player, out Vector3 forward)
        {
            var isLocal = player == GameNetworkManager.Instance?.localPlayerController;
            var look = player.gameplayCamera != null ? player.gameplayCamera.transform : null;
            Vector3 head;
            if (isLocal && look != null)
                head = look.position;
            else if (player.playerGlobalHead != null)
                head = player.playerGlobalHead.position;
            else
                head = player.transform.position + Vector3.up * 2.2f;

            // Взгляд по горизонте берём от тела (он синхронизирован у всех), вниз немного наклоняем
            forward = player.transform.forward;
            return head + forward * 0.25f - Vector3.up * 0.15f;
        }

        private static void PlayAt(PlayerControllerB player, AudioClip clip, float volume)
        {
            var go = new GameObject("CustomItems.VomitSound");
            var parent = player.playerGlobalHead != null ? player.playerGlobalHead : player.transform;
            go.transform.SetParent(parent, false);
            var source = go.AddComponent<AudioSource>();
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 2f;
            source.maxDistance = 30f;
            source.dopplerLevel = 0f;
            source.pitch = Random.Range(0.93f, 1.07f);
            var mixer = SoundManager.Instance?.tempAudio1?.outputAudioMixerGroup;
            if (mixer != null)
                source.outputAudioMixerGroup = mixer;
            source.PlayOneShot(clip, volume);
            WalkieTalkie.TransmitOneShotAudio(source, clip, volume);
            Destroy(go, clip.length / source.pitch + 0.2f);
        }

        // ---- Звуки: wav, зашитые в DLL (Tools/Jagermeister/vomit_sounds.py) ----

        private static void LoadClips()
        {
            if (vomitClips != null)
                return;
            var vomit = new List<AudioClip>();
            var burp = new List<AudioClip>();
            var assembly = typeof(VomitEffects).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().OrderBy(n => n))
            {
                if (!name.EndsWith(".wav"))
                    continue;
                var list = name.Contains(".Vomit") ? vomit : name.Contains(".Burp") ? burp : null;
                if (list == null)
                    continue;
                try
                {
                    using var stream = assembly.GetManifestResourceStream(name);
                    var clip = Wav.Load(stream, Path.GetFileNameWithoutExtension(name));
                    if (clip != null)
                        list.Add(clip);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CustomItems] Не удалось прочитать {name}: {e.Message}");
                }
            }
            vomitClips = vomit.ToArray();
            burpClips = burp.ToArray();
        }

        // ---- Капли и лужа ----

        private static Material VomitMaterial()
        {
            if (vomitMaterial != null)
                return vomitMaterial;
            var shader = Shader.Find("HDRP/Lit");
            vomitMaterial = SourceMaterial != null ? new Material(SourceMaterial) : shader != null ? new Material(shader) : null;
            if (vomitMaterial == null)
                return null;
            var color = new Color(0.55f, 0.48f, 0.12f, 1f);
            if (vomitMaterial.HasProperty("_BaseColor"))
                vomitMaterial.SetColor("_BaseColor", color);
            if (vomitMaterial.HasProperty("_Color"))
                vomitMaterial.SetColor("_Color", color);
            if (vomitMaterial.HasProperty("_Smoothness"))
                vomitMaterial.SetFloat("_Smoothness", 0.85f);
            return vomitMaterial;
        }

        /// <summary>Капля летит по параболе, на полу превращается в лужицу. Без коллайдеров и физики.</summary>
        private class Drop : MonoBehaviour
        {
            private Vector3 velocity;
            private float age;
            private int mask;

            public static void Spawn(Vector3 from, Vector3 forward)
            {
                var material = VomitMaterial();
                if (material == null)
                    return;
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(go.GetComponent<Collider>());
                go.name = "CustomItems.VomitDrop";
                go.GetComponent<MeshRenderer>().sharedMaterial = material;
                go.transform.position = from + Random.insideUnitSphere * 0.04f;
                go.transform.localScale = Vector3.one * Random.Range(0.04f, 0.08f);
                var drop = go.AddComponent<Drop>();
                drop.velocity = (forward * Random.Range(1.6f, 2.6f) + Vector3.down * 0.4f + Random.insideUnitSphere * 0.35f);
                drop.mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            }

            private void Update()
            {
                age += Time.deltaTime;
                if (age > 3f)
                {
                    Destroy(gameObject);
                    return;
                }
                velocity += Physics.gravity * Time.deltaTime;
                var step = velocity * Time.deltaTime;
                if (Physics.Raycast(transform.position, step.normalized, out var hit, step.magnitude + 0.02f, mask,
                        QueryTriggerInteraction.Ignore))
                {
                    if (Random.value < 0.35f)
                        Puddle.Spawn(hit.point, hit.normal);
                    Destroy(gameObject);
                    return;
                }
                transform.position += step;
            }
        }

        private class Puddle : MonoBehaviour
        {
            private const float Life = 25f;
            private float age;
            private Vector3 scale;

            public static void Spawn(Vector3 at, Vector3 normal)
            {
                var material = VomitMaterial();
                if (material == null)
                    return;
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(go.GetComponent<Collider>());
                go.name = "CustomItems.VomitPuddle";
                go.GetComponent<MeshRenderer>().sharedMaterial = material;
                go.transform.position = at + normal * 0.005f;
                go.transform.rotation = Quaternion.FromToRotation(Vector3.up, normal)
                                        * Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                var size = Random.Range(0.12f, 0.3f);
                var p = go.AddComponent<Puddle>();
                p.scale = new Vector3(size, 0.012f, size * Random.Range(0.6f, 1f));
                go.transform.localScale = p.scale;
            }

            private void Update()
            {
                age += Time.deltaTime;
                if (age > Life)
                    Destroy(gameObject);
                else if (age > Life - 3f)
                    transform.localScale = scale * ((Life - age) / 3f);
            }
        }

        // ---- Рвота на экране у того, в кого попали ----

        private class Overlay
        {
            private GameObject root;
            private RawImage splat;
            private Image tint;
            private float hitAt = -100f;

            public void Hit()
            {
                if (root == null)
                    Create();
                hitAt = Time.time;
                // Каждое попадание немного по-другому: сдвиг и отражение текстуры
                splat.uvRect = new Rect(Random.Range(-0.1f, 0.1f), Random.Range(-0.05f, 0.05f),
                    Random.value < 0.5f ? 1f : -1f, 1f);
                Update();
            }

            public void Update()
            {
                if (root == null)
                    return;
                var age = Time.time - hitAt;
                var local = GameNetworkManager.Instance?.localPlayerController;
                if (age >= HitSeconds || local == null || local.isPlayerDead)
                {
                    root.SetActive(false);
                    return;
                }
                root.SetActive(true);
                var a = age < HitHold ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (age - HitHold) / (HitSeconds - HitHold));
                splat.color = new Color(1f, 1f, 1f, a);
                tint.color = new Color(0.45f, 0.4f, 0.08f, 0.55f * a);
                // Чуть сползает вниз
                var rt = splat.rectTransform;
                rt.anchoredPosition = new Vector2(0f, -age * 4f);
            }

            private void Create()
            {
                root = new GameObject("CustomItems.VomitOverlay");
                UnityEngine.Object.DontDestroyOnLoad(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 999;

                var t = new GameObject("Tint", typeof(RectTransform), typeof(Image));
                t.transform.SetParent(root.transform, false);
                tint = t.GetComponent<Image>();
                Stretch((RectTransform)t.transform, 0f);

                var s = new GameObject("Splat", typeof(RectTransform), typeof(RawImage));
                s.transform.SetParent(root.transform, false);
                splat = s.GetComponent<RawImage>();
                splat.texture = SplatTexture();
                Stretch((RectTransform)s.transform, 60f);
            }

            private static void Stretch(RectTransform rt, float bleed)
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = new Vector2(-bleed, -bleed);
                rt.offsetMax = new Vector2(bleed, bleed);
            }
        }

        // Пятна рвоты: сумма кругов (metaballs) с кусочками и потёками вниз, почти всё закрыто
        private static Texture2D SplatTexture()
        {
            if (splatTexture != null)
                return splatTexture;
            const int w = 256, h = 160;
            var rng = new System.Random(1312);
            float R() => (float)rng.NextDouble();

            var blobs = new List<Vector3>();
            for (var i = 0; i < 26; i++)
                blobs.Add(new Vector3(R() * w, R() * h, 18f + R() * 40f));
            // Потёки: вытянутые вниз цепочки кругов
            for (var i = 0; i < 10; i++)
            {
                var x = R() * w;
                var y = h * (0.3f + R() * 0.7f);
                var r = 6f + R() * 6f;
                for (var k = 0; k < 6; k++)
                    blobs.Add(new Vector3(x + (R() - 0.5f) * 3f, y - k * r * 1.1f, r * (1f - k * 0.1f)));
            }
            var chunks = new List<Vector3>();
            for (var i = 0; i < 120; i++)
                chunks.Add(new Vector3(R() * w, R() * h, 1.5f + R() * 3.5f));

            var pixels = new Color32[w * h];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var field = 0f;
                foreach (var b in blobs)
                {
                    var dx = x - b.x;
                    var dy = y - b.y;
                    field += b.z * b.z / (dx * dx + dy * dy + 1f);
                }
                // Сверху и в центре гуще всего: оттуда прилетело
                var alpha = Mathf.Clamp01((field - 0.7f) * 1.6f);
                var n = Mathf.PerlinNoise(x * 0.06f, y * 0.06f);
                var col = Color.Lerp(new Color(0.42f, 0.36f, 0.06f), new Color(0.78f, 0.7f, 0.25f), n);
                foreach (var c in chunks)
                {
                    var dx = x - c.x;
                    var dy = y - c.y;
                    if (dx * dx + dy * dy < c.z * c.z)
                    {
                        col = Color.Lerp(col, new Color(0.6f, 0.38f, 0.12f), 0.8f);
                        alpha = Mathf.Max(alpha, 0.9f);
                    }
                }
                // Блики, чтобы выглядело мокрым
                var gloss = Mathf.PerlinNoise(x * 0.15f + 40f, y * 0.15f);
                if (gloss > 0.78f)
                    col = Color.Lerp(col, Color.white, (gloss - 0.78f) * 2f);
                col.a = Mathf.Min(alpha, 0.97f);
                pixels[y * w + x] = col;
            }

            splatTexture = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            splatTexture.SetPixels32(pixels);
            splatTexture.Apply();
            return splatTexture;
        }
    }

    /// <summary>Чтение PCM16 wav в AudioClip.</summary>
    internal static class Wav
    {
        public static AudioClip Load(Stream stream, string name)
        {
            if (stream == null)
                return null;
            using var reader = new BinaryReader(stream);
            if (new string(reader.ReadChars(4)) != "RIFF")
                return null;
            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
                return null;
            int channels = 1, rate = 44100, bits = 16;
            while (stream.Position + 8 <= stream.Length)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadInt32();
                if (id == "fmt ")
                {
                    reader.ReadInt16();
                    channels = reader.ReadInt16();
                    rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    bits = reader.ReadInt16();
                    reader.ReadBytes(size - 16);
                }
                else if (id == "data")
                {
                    if (bits != 16)
                        return null;
                    var bytes = reader.ReadBytes(size);
                    var samples = new float[bytes.Length / 2];
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                    var clip = AudioClip.Create(name, samples.Length / channels, channels, rate, false);
                    clip.SetData(samples, 0);
                    return clip;
                }
                else
                {
                    reader.ReadBytes(size + (size & 1));
                }
            }
            return null;
        }
    }
}
