using System;
using System.Collections.Generic;
using UnityEngine;

namespace CustomItems
{
    /// <summary>
    /// Список предметов мода и где они появляются: скрапом на лунах и/или в магазине,
    /// плюс мебель корабля (раздел Decor терминала).
    /// Ассет лежит в Assets/Sources/Data/ItemRegistry.asset и попадает в бандл,
    /// мод регистрирует ровно то, что в нём перечислено. Всё остальное о предмете
    /// (название, стоимость скрапа, вес, хват, оффсеты) хранится в его ассете Item.
    /// Редкость, магазин и цена ещё и попадают в конфиг BepInEx как значения по умолчанию,
    /// так что их можно менять без пересборки.
    /// </summary>
    [CreateAssetMenu(menuName = "Lethal Company/Item Registry", fileName = "ItemRegistry")]
    public class ItemRegistry : ScriptableObject
    {
        public List<Entry> items = new List<Entry>();

        [Tooltip("Мебель корабля: покупается в разделе Decor терминала и ставится через режим строительства (B)")]
        public List<FurnitureEntry> furniture = new List<FurnitureEntry>();

        [Serializable]
        public class Entry
        {
            [Tooltip("Ассет Item из Assets/Sources/Data/Items. Префаб берётся из его Spawn Prefab.")]
            public Item item;

            [Tooltip("Шанс появления как скрап (вес относительно других предметов, 0 = не спавнится). " +
                     "Работает, только если в Item включён Is Scrap.")]
            public int rarity = 30;

            [Tooltip("Продавать в магазине терминала")]
            public bool inShop;

            [Tooltip("Цена в магазине")]
            public int price = 25;
        }

        [Serializable]
        public class FurnitureEntry
        {
            [Tooltip("Название в магазине терминала (по нему же покупают: buy <название>) и секция конфига")]
            public string name;

            [Tooltip("Префаб мебели: модель, NetworkObject, AudioSource, скрипт и дочерние PlacementCollider и InteractTrigger " +
                     "с BoxCollider. Компоненты мебели игры мод добавляет сам при загрузке.")]
            public GameObject prefab;

            [Tooltip("Цена в магазине")]
            public int price = 120;
        }
    }
}
