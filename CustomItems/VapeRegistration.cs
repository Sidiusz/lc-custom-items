using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using CustomItems.Items;
using LethalLib.Modules;
using UnityEngine;

namespace CustomItems
{
    /// <summary>
    /// Вейпы целиком из кода, без Unity-бандла: ассет Item, сетевой префаб (LethalLib), модель
    /// (VapeModels из встроенных мешей), коллайдер, скан-нода, звук и VapeItem. Если в бандле
    /// когда-нибудь появится предмет с тем же названием (собранный в Unity), берётся он, а этот пропускается.
    /// </summary>
    internal static class VapeRegistration
    {
        private class Spec
        {
            public string Style, Name;
            public int Rarity;
            public bool Rechargeable;
            public VapeScreen.Style Screen;
        }

        private static readonly Spec[] Specs =
        {
            new Spec { Style = "Pixl", Name = "PYXL vape", Rarity = 10, Rechargeable = true, Screen = VapeScreen.Style.Pixl },
            new Spec { Style = "Box", Name = "VAPEFIT vape", Rarity = 10, Rechargeable = true, Screen = VapeScreen.Style.Box },
            new Spec { Style = "Pod", Name = "OXYA pod", Rarity = 20, Rechargeable = false, Screen = VapeScreen.Style.Pod },
        };

        public static void Register(ConfigFile config, IEnumerable<string> fromBundle)
        {
            var taken = new HashSet<string>(fromBundle);
            foreach (var spec in Specs.Where(s => !taken.Contains(s.Name)))
            {
                try
                {
                    var item = Create(spec);
                    ItemTuningStore.Register(item);
                    // Как у предметов из бандла: Rarity 10 = 1% скрапа луны, ScrapSpawn пересчитывает вес
                    var chance = config.Bind(spec.Name, "ScrapChance", spec.Rarity / 10f,
                        "Шанс в процентах, что случайный предмет скрапа на луне окажется этим (0 = не спавнится)").Value;
                    var inShop = config.Bind(spec.Name, "InShop", false, "Продавать в магазине терминала").Value;
                    var price = config.Bind(spec.Name, "Price", 40, "Цена в магазине").Value;
                    if (chance > 0f)
                    {
                        LethalLib.Modules.Items.RegisterScrap(item, spec.Rarity, Levels.LevelTypes.All);
                        ScrapSpawn.Chances[item] = chance / 100f;
                    }
                    if (inShop)
                        LethalLib.Modules.Items.RegisterShopItem(item, price);
                    if (chance <= 0f && !inShop)
                        LethalLib.Modules.Items.RegisterItem(item);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError($"Вейп {spec.Name} не создан: {e}");
                }
            }
        }

        private static Item Create(Spec spec)
        {
            var item = ScriptableObject.CreateInstance<Item>();
            item.name = "Vape" + spec.Style;
            item.itemName = spec.Name;
            item.isScrap = true;
            item.minValue = 30;  // игра умножает на 0.4
            item.maxValue = 80;
            item.weight = 1.01f;
            item.twoHanded = false;
            item.itemSpawnsOnGround = true;
            item.canBeGrabbedBeforeGameStart = true;
            item.allowDroppingAheadOfPlayer = true;
            item.isConductiveMetal = false;
            item.canBeInspected = false;
            item.saveItemVariable = true;
            item.holdButtonUse = true;
            item.requiresBattery = true;
            item.batteryUsage = VapeItem.PuffSecondsPerCharge;
            item.toolTips = new[] { "Vape : [LMB]" };
            item.verticalOffset = 0f; // пивот модели внизу
            item.spawnPositionTypes = new List<ItemGroup>();

            var prefab = NetworkPrefabs.CreateNetworkPrefab("SidiuszVape" + spec.Style);
            var model = new GameObject("Model");
            model.transform.SetParent(prefab.transform, false);
            var bounds = VapeModels.Build(model.transform, spec.Style, 0);

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

            var node = new GameObject("ScanNode");
            node.transform.SetParent(prefab.transform, false);
            node.transform.localPosition = bounds.center;
            var trigger = node.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = Vector3.one * 0.5f;
            var scan = node.AddComponent<ScanNodeProperties>();
            scan.headerText = spec.Name;
            scan.subText = "Value: ";
            scan.nodeType = 2; // скрап
            scan.minRange = 1;
            scan.maxRange = 13;
            scan.requiresLineOfSight = true;
            scan.creatureScanID = -1;

            var vape = prefab.AddComponent<VapeItem>();
            vape.templateItemName = "TZP-Inhalant";
            vape.screenStyle = spec.Screen;
            vape.rechargeable = spec.Rechargeable;
            vape.itemProperties = item;
            vape.grabbable = true;
            vape.grabbableToEnemies = true;
            vape.mainObjectRenderer = model.transform.Find("Body")?.GetComponent<MeshRenderer>();

            Plugin.PreparePrefab(prefab); // тег, слои Props/ScanNode, стабильный хеш сети
            item.spawnPrefab = prefab;
            return item;
        }
    }
}
