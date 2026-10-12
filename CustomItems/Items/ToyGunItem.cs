using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Игрушечный пистолет: по ЛКМ стреляет пулькой из магазина, пулька летит по дуге.
    /// Перезарядка (R, клавиша в конфиге, или E) пересыпает пульки в магазин из пачек
    /// <see cref="ToyGunAmmoItem"/> в инвентаре; пустая пачка исчезает.
    ///
    /// Сеть: в ассете Item включён Sync Use Function, поэтому игра сама вызывает ItemActivate у всех игроков.
    /// Каждый клиент рисует свою копию пульки (траектория одна и та же: от камеры
    /// стрелка), а урон считает только владелец пистолета, через ванильные RPC игры
    /// (HitEnemy для монстров, DamagePlayerFromOtherClient для игроков). Своих RPC
    /// нет, NetcodePatcher не нужен.
    ///
    /// Урон в игре целочисленный (у игрока 100 HP, у монстров HP считается в ударах
    /// лопатой), поэтому 0.1 урона копится на каждой цели отдельно: каждые 10 попаданий
    /// в одну цель наносят ей 1 единицу урона.
    ///
    /// Патроны: стрельба и перезарядка приходят ко всем игрокам (Sync Use Function и
    /// Sync Interact LR Function в Item), и каждый клиент одинаково считает магазин
    /// и пачки по одинаковым у всех слотам инвентаря. Пустую пачку удаляет владелец
    /// ванильным DestroyItemInSlotAndSync. Магазин сохраняется вместе с кораблём.
    /// </summary>
    public class ToyGunItem : CustomGrabbable
    {
        /// <summary>Урон за одну пульку (дробная часть копится). Задаётся из конфига.</summary>
        public static float DamagePerPellet = 0.1f;

        /// <summary>Ёмкость магазина. Задаётся из конфига.</summary>
        public static int MagazineSize = 12;
        /// <summary>Клавиша перезарядки для подсказки (сама клавиша ловится в плагине).</summary>
        public static string ReloadKeyName = "R";
        /// <summary>Строка подсказки с патронами: {0} в магазине, {1} ёмкость. Плагин переводит её.</summary>
        public static string AmmoTipFormat = "Pellets : {0}/{1}";
        public static string NoAmmoHeader = "Toy gun";
        public static string NoAmmoBody = "No pellets in inventory. Buy them in the terminal store.";

        public AudioClip shotClip;
        public AudioClip reloadClip;
        public AudioClip dryFireClip;

        // -1: ещё не задано, в Start станет полным магазином (найденный на луне пистолет заряжен)
        private int ammo = -1;

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
            if (ammo < 0)
                ammo = MagazineSize;
        }

        public override int GetItemDataToSave() => ammo;

        public override void LoadItemSaveData(int saveData) => ammo = Mathf.Clamp(saveData, 0, MagazineSize);

        public override void SetControlTipsForItem()
        {
            HUDManager.Instance.ChangeControlTipMultiple(new[]
            {
                "Shoot : [LMB]",
                $"Reload : [{ReloadKeyName}]",
                string.Format(AmmoTipFormat, Mathf.Max(ammo, 0), MagazineSize),
            }, holdingItem: true, itemProperties);
        }

        private void RefreshTips()
        {
            if (IsOwner && isHeld && !isPocketed && playerHeldBy != null
                && playerHeldBy == GameNetworkManager.Instance.localPlayerController)
                SetControlTipsForItem();
        }

        /// <summary>Перезарядка (ПКМ-взаимодействие E или клавиша из конфига). Вызывается у всех игроков.</summary>
        public override void ItemInteractLeftRight(bool right)
        {
            base.ItemInteractLeftRight(right);
            if (!right || playerHeldBy == null)
                return;

            var need = MagazineSize - ammo;
            var loaded = 0;
            var slots = playerHeldBy.ItemSlots;
            for (var i = 0; i < slots.Length && need > 0; i++)
            {
                if (!(slots[i] is ToyGunAmmoItem box) || box.pellets <= 0)
                    continue;
                var take = Mathf.Min(need, box.pellets);
                box.pellets -= take;
                ammo += take;
                loaded += take;
                need -= take;
                if (box.pellets == 0 && IsOwner)
                    playerHeldBy.DestroyItemInSlotAndSync(i);
            }

            var clip = loaded > 0 ? reloadClip : dryFireClip;
            if (audioSource != null && clip != null)
            {
                audioSource.pitch = 1f;
                audioSource.PlayOneShot(clip);
            }
            if (loaded == 0 && need > 0 && IsOwner)
                HUDManager.Instance.DisplayTip(NoAmmoHeader, NoAmmoBody);
            RefreshTips();
        }

        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            if (!buttonDown || playerHeldBy == null)
                return;

            if (ammo <= 0)
            {
                if (audioSource != null && dryFireClip != null)
                    audioSource.PlayOneShot(dryFireClip);
                return;
            }
            ammo--;
            RefreshTips();

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
