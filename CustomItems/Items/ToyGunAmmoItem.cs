using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Пачка пулек для игрушечного пистолета (магазин терминала). Занимает слот
    /// инвентаря; при перезарядке <see cref="ToyGunItem"/> забирает из неё пульки,
    /// пустая пачка исчезает. Остаток сохраняется вместе с кораблём.
    /// </summary>
    public class ToyGunAmmoItem : CustomGrabbable
    {
        /// <summary>Пулек в новой пачке. Задаётся из конфига.</summary>
        public static int BoxSize = 24;
        /// <summary>Строка подсказки: {0} пулек в пачке. Плагин переводит её.</summary>
        public static string TipFormat = "Pellets in box : {0}";

        // -1: ещё не задано, в Start станет полной пачкой
        [System.NonSerialized] public int pellets = -1;

        public override void Start()
        {
            base.Start();
            if (pellets < 0)
                pellets = BoxSize;
        }

        public override int GetItemDataToSave() => pellets;

        public override void LoadItemSaveData(int saveData) => pellets = Mathf.Max(0, saveData);

        public override void SetControlTipsForItem()
        {
            HUDManager.Instance.ChangeControlTipMultiple(new[] { string.Format(TipFormat, Mathf.Max(pellets, 0)) },
                holdingItem: true, itemProperties);
        }
    }
}
