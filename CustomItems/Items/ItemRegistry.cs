using System;
using System.Collections.Generic;
using UnityEngine;

namespace CustomItems
{
    /// <summary>
    /// Список предметов мода и где они появляются: скрапом на лунах и/или в магазине.
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
    }
}
