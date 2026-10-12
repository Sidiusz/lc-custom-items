using System.Collections.Generic;
using LethalLib.Modules;
using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Собирает из голой модели готовый сетевой предмет: Item, NetworkObject,
    /// коллайдер, Rigidbody, AudioSource, скан-ноду и скрипт поведения.
    /// </summary>
    internal static class ItemFactory
    {
        private const string PropTag = "PhysicsProp";

        public static T Create<T>(AssetBundle bundle, CustomItemDef def) where T : CustomGrabbable
        {
            var model = bundle.LoadAsset<GameObject>(def.ModelAsset);
            if (model == null)
            {
                Plugin.Log.LogError($"В бандле нет модели '{def.ModelAsset}' для '{def.Name}'");
                return null;
            }

            var item = ScriptableObject.CreateInstance<Item>();
            item.name = def.Name;
            item.itemName = def.Name;
            item.isScrap = def.IsScrap;
            item.minValue = def.MinValue;
            item.maxValue = def.MaxValue;
            item.weight = def.Weight;
            item.twoHanded = def.TwoHanded;
            item.isConductiveMetal = def.IsConductive;
            item.itemSpawnsOnGround = true;
            item.canBeGrabbedBeforeGameStart = true;
            item.allowDroppingAheadOfPlayer = true;
            item.syncUseFunction = def.SyncUse;
            item.toolTips = def.ToolTips;
            item.positionOffset = def.PositionOffset;
            item.rotationOffset = def.RotationOffset;
            item.restingRotation = def.RestingRotation;
            item.verticalOffset = def.VerticalOffset;
            item.spawnPositionTypes = new List<ItemGroup>();
            item.canBeInspected = false;
            if (!string.IsNullOrEmpty(def.IconAsset))
                item.itemIcon = bundle.LoadAsset<Sprite>(def.IconAsset);

            // Пустой сетевой префаб от LethalLib (NetworkObject + стабильный хеш + регистрация)
            var prefab = NetworkPrefabs.CreateNetworkPrefab(def.Name);
            prefab.tag = PropTag;
            SetLayer(prefab, LayerMask.NameToLayer("Props"));

            var visual = Object.Instantiate(model, prefab.transform);
            visual.name = "Model";
            visual.transform.localPosition = Vector3.zero;
            foreach (var c in visual.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);
            foreach (var rb in visual.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(rb);
            SetLayer(visual, LayerMask.NameToLayer("Props"));

            var bounds = LocalBounds(prefab.transform, visual);
            var collider = prefab.AddComponent<BoxCollider>();
            collider.center = bounds.center;
            collider.size = bounds.size;

            var body = prefab.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var audio = prefab.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = 2f;
            audio.maxDistance = 30f;

            AddScanNode(prefab, def, bounds.center);

            var behaviour = prefab.AddComponent<T>();
            behaviour.itemProperties = item;
            behaviour.grabbable = true;
            behaviour.grabbableToEnemies = true;
            behaviour.mainObjectRenderer = visual.GetComponentInChildren<MeshRenderer>();
            behaviour.templateItemName = def.TemplateItem;

            item.spawnPrefab = prefab;
            return behaviour;
        }

        // Скан-нода: предмет виден на сканере (ПКМ), и игра может выставить ему цену.
        private static void AddScanNode(GameObject prefab, CustomItemDef def, Vector3 center)
        {
            var node = new GameObject("ScanNode");
            node.transform.SetParent(prefab.transform, false);
            node.transform.localPosition = center;
            node.layer = LayerMask.NameToLayer("ScanNode");

            var collider = node.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = Vector3.one * 0.5f;

            var scan = node.AddComponent<ScanNodeProperties>();
            scan.headerText = def.Name;
            scan.subText = def.IsScrap ? "Value: " : "";
            scan.nodeType = def.IsScrap ? 2 : 0; // 2 = скрап (зелёная рамка), 0 = обычный
            scan.minRange = 1;
            scan.maxRange = 13;
            scan.requiresLineOfSight = true;
            scan.creatureScanID = -1;
        }

        // Границы мешей в координатах корня. Считаем по мешам, а не по Renderer.bounds,
        // потому что префаб неактивен и у рендереров ещё нет мировых границ.
        private static Bounds LocalBounds(Transform root, GameObject visual)
        {
            var result = new Bounds(Vector3.zero, Vector3.zero);
            var first = true;

            void Encapsulate(Transform t, Mesh mesh)
            {
                if (mesh == null)
                    return;
                var m = root.worldToLocalMatrix * t.localToWorldMatrix;
                var b = mesh.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    if (first) { result = new Bounds(p, Vector3.zero); first = false; }
                    else result.Encapsulate(p);
                }
            }

            foreach (var mf in visual.GetComponentsInChildren<MeshFilter>(true))
                Encapsulate(mf.transform, mf.sharedMesh);
            foreach (var smr in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Encapsulate(smr.transform, smr.sharedMesh);

            if (first)
                result = new Bounds(Vector3.zero, Vector3.one * 0.3f);
            return result;
        }

        private static void SetLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;
        }
    }
}
