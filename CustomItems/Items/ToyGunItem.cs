using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Игрушечный пистолет: по ЛКМ стреляет пулькой, которая летит по дуге.
    ///
    /// Сеть: SyncUse = true, поэтому игра сама вызывает ItemActivate у всех игроков.
    /// Каждый клиент рисует свою копию пульки (траектория одна и та же: от камеры
    /// стрелка), а урон считает только владелец пистолета, через ванильные RPC игры
    /// (HitEnemy для монстров, DamagePlayerFromOtherClient для игроков). Своих RPC
    /// нет, NetcodePatcher не нужен.
    ///
    /// Урон в игре целочисленный (у игрока 100 HP, у монстров HP считается в ударах
    /// лопатой), поэтому 0.1 урона копится на каждой цели отдельно: каждые 10 попаданий
    /// в одну цель наносят ей 1 единицу урона.
    /// </summary>
    public class ToyGunItem : CustomGrabbable
    {
        /// <summary>Урон за одну пульку (дробная часть копится). Задаётся из конфига.</summary>
        public static float DamagePerPellet = 0.1f;

        public AudioClip shotClip;

        private const float Cooldown = 0.2f;
        private const float NoiseRange = 10f;
        private const float NoiseLoudness = 0.4f;

        // Накопленный дробный урон по целям (ключ: InstanceID монстра или игрока)
        private static readonly Dictionary<int, float> PendingDamage = new Dictionary<int, float>();

        private Transform muzzle;

        public override void Start()
        {
            base.Start();
            useCooldown = Cooldown;
            muzzle = FindChild(transform, "Muzzle");
        }

        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            if (!buttonDown || playerHeldBy == null)
                return;

            if (audioSource != null && shotClip != null)
            {
                audioSource.pitch = Random.Range(0.92f, 1.08f);
                audioSource.PlayOneShot(shotClip);
                WalkieTalkie.TransmitOneShotAudio(audioSource, shotClip);
            }

            var cam = playerHeldBy.gameplayCamera.transform;
            var muzzlePos = muzzle != null ? muzzle.position : cam.position + cam.forward * 0.3f;
            ToyGunPellet.Spawn(cam.position, cam.forward, muzzlePos, playerHeldBy, IsOwner);

            if (IsOwner)
            {
                RoundManager.Instance.PlayAudibleNoise(transform.position, NoiseRange, NoiseLoudness,
                    0, isInElevator && StartOfRound.Instance.hangarDoorsClosed);
            }
        }

        /// <summary>Вызывается пулькой на клиенте стрелка при попадании.</summary>
        internal static void ApplyHit(Collider target, Vector3 direction, PlayerControllerB shooter)
        {
            var enemyCollider = target.GetComponent<EnemyAICollisionDetect>();
            if (enemyCollider != null && enemyCollider.mainScript != null)
            {
                var whole = Accumulate(enemyCollider.mainScript.GetInstanceID());
                if (whole > 0)
                    ((IHittable)enemyCollider).Hit(whole, direction, shooter, true, -1);
                return;
            }

            var player = target.GetComponentInParent<PlayerControllerB>();
            if (player != null && player != shooter && !player.isPlayerDead)
            {
                var whole = Accumulate(player.GetInstanceID());
                if (whole > 0)
                    player.DamagePlayerFromOtherClientServerRpc(whole, direction, (int)shooter.playerClientId);
            }
        }

        private static int Accumulate(int key)
        {
            PendingDamage.TryGetValue(key, out var pending);
            pending += DamagePerPellet;
            // Небольшой запас: 10 * 0.1f в float даёт 0.99999994
            var whole = Mathf.FloorToInt(pending + 1e-4f);
            PendingDamage[key] = Mathf.Max(0f, pending - whole);
            return whole;
        }

        private static Transform FindChild(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name)
                    return t;
            return null;
        }
    }

    /// <summary>
    /// Пулька. Физику считает от камеры стрелка (так попадание совпадает с прицелом и
    /// не проходит сквозь стену перед дулом), а рисуется от дула, плавно сводя
    /// смещение к нулю за первые доли секунды.
    /// </summary>
    internal class ToyGunPellet : MonoBehaviour
    {
        private const float Speed = 28f;
        private const float Gravity = 6f;
        private const float Lifetime = 1.5f;
        private const float Radius = 0.012f;
        private const float VisualBlendTime = 0.12f;

        private static Material material;
        private static int hitMask = -1;

        private Vector3 position;
        private Vector3 velocity;
        private Vector3 visualOffset;
        private float age;
        private PlayerControllerB shooter;
        private bool dealsDamage;

        public static void Spawn(Vector3 origin, Vector3 direction, Vector3 muzzle, PlayerControllerB shooter, bool dealsDamage)
        {
            if (hitMask == -1)
            {
                var enemies = LayerMask.NameToLayer("Enemies");
                var props = LayerMask.NameToLayer("Props");
                hitMask = StartOfRound.Instance.collidersAndRoomMaskAndDefault | StartOfRound.Instance.playersMask
                    | (enemies >= 0 ? 1 << enemies : 0);
                // Предметы (в том числе сам пистолет в руке) пульку не останавливают
                if (props >= 0)
                    hitMask &= ~(1 << props);
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "ToyGunPellet";
            Destroy(go.GetComponent<Collider>());
            go.transform.localScale = Vector3.one * Radius * 2f;
            go.transform.position = muzzle;

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = GetMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var trail = go.AddComponent<TrailRenderer>();
            trail.sharedMaterial = GetMaterial();
            trail.time = 0.06f;
            trail.startWidth = Radius * 1.6f;
            trail.endWidth = 0f;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var pellet = go.AddComponent<ToyGunPellet>();
            pellet.position = origin;
            pellet.velocity = direction.normalized * Speed;
            pellet.visualOffset = muzzle - origin;
            pellet.shooter = shooter;
            pellet.dealsDamage = dealsDamage;
        }

        private static Material GetMaterial()
        {
            if (material != null)
                return material;
            // Светится сама по себе, чтобы пульку было видно в тёмных коридорах
            var unlit = Shader.Find("HDRP/Unlit");
            material = new Material(unlit != null ? unlit : Shader.Find("HDRP/Lit")) { name = "ToyGunPellet" };
            var color = new Color(1f, 0.85f, 0.2f);
            material.SetColor("_UnlitColor", color);
            material.SetColor("_BaseColor", color);
            return material;
        }

        private void Update()
        {
            var dt = Time.deltaTime;
            age += dt;
            if (age > Lifetime)
            {
                Destroy(gameObject);
                return;
            }

            var step = velocity * dt;
            velocity += Vector3.down * (Gravity * dt);
            var distance = step.magnitude;

            if (distance > 0f && TryHit(position, step / distance, distance, out var hit))
            {
                if (dealsDamage && shooter != null)
                    ToyGunItem.ApplyHit(hit.collider, step / distance, shooter);
                Destroy(gameObject);
                return;
            }

            position += step;
            var blend = Mathf.Clamp01(1f - age / VisualBlendTime);
            transform.position = position + visualOffset * blend;
        }

        private bool TryHit(Vector3 from, Vector3 dir, float distance, out RaycastHit result)
        {
            result = default;
            var hits = Physics.SphereCastAll(from, Radius, dir, distance, hitMask, QueryTriggerInteraction.Collide);
            var best = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.distance >= best || !Counts(h.collider))
                    continue;
                best = h.distance;
                result = h;
            }
            return best < float.MaxValue;
        }

        private bool Counts(Collider c)
        {
            var enemy = c.GetComponent<EnemyAICollisionDetect>();
            if (enemy != null)
                return enemy.mainScript != null && !enemy.mainScript.isEnemyDead;
            if (c.isTrigger)
                return false;
            var player = c.GetComponentInParent<PlayerControllerB>();
            if (player != null)
                return player != shooter && !player.isPlayerDead;
            return true;
        }
    }
}
