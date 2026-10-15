using CustomItems.Items;
using HarmonyLib;

namespace CustomItems
{
    /// <summary>
    /// Одноразовый вейп (VapeItem.rechargeable = false, OXYA) зарядка корабля не заряжает:
    /// батарея у него есть только ради экрана и разряда.
    /// </summary>
    [HarmonyPatch]
    internal static class VapeCharger
    {
        [HarmonyPatch(typeof(ItemCharger), nameof(ItemCharger.ChargeItem))]
        [HarmonyPrefix]
        private static bool ChargeItem()
        {
            var held = GameNetworkManager.Instance?.localPlayerController?.currentlyHeldObjectServer;
            if (held is VapeItem vape && !vape.rechargeable)
            {
                HUDManager.Instance.DisplayTip(held.itemProperties.itemName, "Disposable vapes can't be charged.");
                return false;
            }
            return true;
        }
    }
}
