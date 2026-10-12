using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// База для всех предметов мода. Висит на префабе предмета (Assets/Sources/Prefabs).
    /// При первом появлении в мире докручивает то, что в ассете Item оставлено пустым:
    /// анимации и звуки подбора/броска берёт у ванильного предмета, а материалы переводит
    /// на шейдер игры (HDRP/Lit), чтобы модель не была розовой.
    /// </summary>
    public abstract class CustomGrabbable : GrabbableObject
    {
        [Tooltip("Ванильный предмет, у которого берутся анимации рук, звуки подбора/броска, " +
                 "иконка (если своей нет) и аудио-микшер. Если не найден, берётся любой скрап с той же хваткой.")]
        public string templateItemName = "Rubber Ducky";

        protected AudioSource audioSource;

        private static readonly HashSet<Item> PreparedItems = new HashSet<Item>();
        private static readonly Dictionary<Material, Material> ConvertedMaterials = new Dictionary<Material, Material>();
        private static Shader hdrpLit;

        public override void Start()
        {
            audioSource = GetComponent<AudioSource>();

            if (!PreparedItems.Contains(itemProperties))
            {
                FillFromTemplate();
                PreparedItems.Add(itemProperties);
            }

            var template = FindTemplate();
            var mixerSource = template != null && template.spawnPrefab != null
                ? template.spawnPrefab.GetComponent<AudioSource>()
                : null;
            if (audioSource != null && mixerSource != null)
                audioSource.outputAudioMixerGroup = mixerSource.outputAudioMixerGroup;

            FixMaterials();
            base.Start();
        }

        private Item FindTemplate()
        {
            var items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null)
                return null;
            return items.FirstOrDefault(i => i != null && i.itemName == templateItemName)
                ?? items.FirstOrDefault(i => i != null && i.isScrap && i.twoHanded == itemProperties.twoHanded && i.grabSFX != null);
        }

        private void FillFromTemplate()
        {
            var t = FindTemplate();
            if (t == null)
            {
                Debug.LogWarning($"[CustomItems] Не нашёл ванильный предмет-шаблон для '{itemProperties.itemName}'");
                return;
            }

            var p = itemProperties;
            // В сохранённом ассете пустые строки, а не null, поэтому проверяем на пустоту
            if (string.IsNullOrEmpty(p.grabAnim)) p.grabAnim = t.grabAnim;
            if (string.IsNullOrEmpty(p.useAnim)) p.useAnim = t.useAnim;
            if (string.IsNullOrEmpty(p.pocketAnim)) p.pocketAnim = t.pocketAnim;
            if (string.IsNullOrEmpty(p.throwAnim)) p.throwAnim = t.throwAnim;
            if (p.grabAnimationTime <= 0f) p.grabAnimationTime = t.grabAnimationTime;
            if (p.grabSFX == null) p.grabSFX = t.grabSFX;
            if (p.dropSFX == null) p.dropSFX = t.dropSFX;
            if (p.pocketSFX == null) p.pocketSFX = t.pocketSFX;
            if (p.throwSFX == null) p.throwSFX = t.throwSFX;
            if (p.itemIcon == null) p.itemIcon = t.itemIcon;
            if (p.clinkAudios == null || p.clinkAudios.Length == 0) p.clinkAudios = t.clinkAudios;
            p.floorYOffset = t.floorYOffset;
            // Двуручным нужна поза «держу двумя руками», её берём у двуручного шаблона
            if (p.twoHanded && t.twoHanded)
                p.twoHandedAnimation = t.twoHandedAnimation;
        }

        // Модель из обычного Unity-проекта приходит со Standard-шейдером, которого в игре
        // нет. Пересоздаём материал на HDRP/Lit с той же текстурой и цветом.
        private void FixMaterials()
        {
            if (hdrpLit == null)
                hdrpLit = Shader.Find("HDRP/Lit");
            if (hdrpLit == null)
                return;

            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                var changed = false;
                for (var i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || m.shader == hdrpLit || m.shader.name.StartsWith("HDRP/"))
                        continue;
                    if (!ConvertedMaterials.TryGetValue(m, out var converted))
                    {
                        converted = new Material(hdrpLit) { name = m.name + " (HDRP)" };
                        if (m.HasProperty("_MainTex") && m.mainTexture != null)
                            converted.SetTexture("_BaseColorMap", m.mainTexture);
                        if (m.HasProperty("_Color"))
                            converted.SetColor("_BaseColor", m.color);
                        converted.SetFloat("_Smoothness", 0.3f);
                        ConvertedMaterials[m] = converted;
                    }
                    mats[i] = converted;
                    changed = true;
                }
                if (changed)
                    r.sharedMaterials = mats;
            }
        }
    }
}
