using System.Linq;
using CustomItems.Items;
using UnityEngine;

namespace CustomItems
{
    /// <summary>
    /// «Алиса, взлетаем», «закрой двери», «купи про-фонарик»: колонка делает то же, что рычаг,
    /// кнопки дверей и терминал. Выполняет хост (он слышит вопросы), игра сама рассылает остальным.
    /// </summary>
    public static class AliceShip
    {
        public const string Takeoff = "Launch/Takeoff", Land = "Launch/Land", LaunchBusy = "Launch/Busy",
            DoorsClosed = "Doors/Closed", DoorsOpened = "Doors/Opened", DoorsLocked = "Doors/Locked",
            Bought = "Buy/Done", BuyNoMoney = "Buy/NoMoney", BuyFull = "Buy/Full",
            LightsOn = "Lights/On", LightsOff = "Lights/Off";

        // Сколько предметов везёт дропшип за раз (как в терминале)
        private const int DropshipLimit = 12;

        public static void Install()
        {
            YandexStationItem.LaunchCheck = LaunchCheck;
            YandexStationItem.Launch = Launch;
            YandexStationItem.Doors = Doors;
            YandexStationItem.Buy = Buy;
            YandexStationItem.Lights = Lights;
        }

        private static string[] Busy => new[] { LaunchBusy, AliceRoute.Busy, "Refuse" };

        // null — рычаг можно дёрнуть
        private static string[] LaunchCheck()
        {
            var round = StartOfRound.Instance;
            var lever = Object.FindObjectOfType<StartMatchLever>();
            if (round == null || lever == null)
                return new[] { "Refuse" };
            if (round.travellingToNewLevel || round.shipIsLeaving || !round.inShipPhase && !round.shipHasLanded)
                return Busy;
            if (lever.triggerScript != null && !lever.triggerScript.interactable)
                return Busy;
            // Дедлайн: садиться можно только в Компании
            var tod = TimeOfDay.Instance;
            if (round.inShipPhase && tod != null && tod.daysUntilDeadline <= 0 && round.currentLevel != null && round.currentLevel.levelID != 3)
                return new[] { "Refuse" };
            return null;
        }

        // Как будто дёрнули рычаг: на орбите — садимся, на луне — взлетаем
        private static string[] Launch()
        {
            var refuse = LaunchCheck();
            if (refuse != null)
                return refuse;
            var round = StartOfRound.Instance;
            var lever = Object.FindObjectOfType<StartMatchLever>();
            var land = round.inShipPhase;
            lever.leverHasBeenPulled = land;
            GamePrivate.Call(lever, "PullLeverAnim", land); // private в игре
            lever.PlayLeverPullEffectsServerRpc(land);
            lever.PullLever();
            Plugin.Log.LogInfo("Алиса: рычаг, " + (land ? "посадка" : "взлёт"));
            return land ? new[] { Land, "Ok" } : new[] { Takeoff, "Events/Takeoff", "Ok" };
        }

        // Как кнопки дверей: только на луне (на орбите заблокированы), закрыть — пока не перегрелись
        private static string[] Doors(bool close)
        {
            var round = StartOfRound.Instance;
            var door = Object.FindObjectOfType<HangarShipDoor>();
            if (round == null || door == null)
                return new[] { "Refuse" };
            if (!door.buttonsEnabled || close && (door.overheated || door.doorPower <= 0f))
                return new[] { DoorsLocked, "Refuse" };
            door.PlayDoorAnimation(close);
            Plugin.Log.LogInfo("Алиса: двери " + (close ? "закрыты" : "открыты"));
            return close ? new[] { DoorsClosed, "Ok" } : new[] { DoorsOpened, "Ok" };
        }

        // Как выключатель на стене: игра сама рассылает всем
        private static string[] Lights(bool on)
        {
            var lights = Object.FindObjectOfType<ShipLights>();
            if (lights == null)
                return new[] { "Refuse" };
            if (lights.areLightsOn != on)
                lights.SetShipLightsServerRpc(on);
            Plugin.Log.LogInfo("Алиса: свет " + (on ? "включён" : "выключен"));
            return on ? new[] { LightsOn, "Ok" } : new[] { LightsOff, "Ok" };
        }

        // Как терминал: по цене со скидкой, кредиты списываются, доставка дропшипом
        private static string[] Buy(string name, int count)
        {
            var terminal = Object.FindObjectOfType<Terminal>();
            if (terminal == null || terminal.buyableItemsList == null)
                return new[] { "Refuse" };
            var key = Simplify(name);
            var index = -1;
            for (var i = 0; i < terminal.buyableItemsList.Length; i++)
            {
                var item = terminal.buyableItemsList[i];
                if (item != null && Simplify(item.itemName) == key)
                    index = i;
            }
            if (index < 0)
                return new[] { YandexStationItem.BuyUnknown, "Unknown" };

            count = Mathf.Clamp(count, 1, DropshipLimit);
            var already = terminal.orderedItemsFromTerminal?.Count ?? 0;
            if (already + count > DropshipLimit)
                return new[] { BuyFull, "Refuse" };
            var sale = terminal.itemSalesPercentages != null && index < terminal.itemSalesPercentages.Length ? terminal.itemSalesPercentages[index] : 100;
            var cost = (int)(terminal.buyableItemsList[index].creditsWorth * (sale / 100f)) * count;
            if (terminal.groupCredits < cost)
                return new[] { BuyNoMoney, AliceRoute.NoMoney, "Refuse" };

            var items = Enumerable.Repeat(index, count).ToArray();
            terminal.BuyItemsServerRpc(items, terminal.groupCredits - cost, already + count);
            // На всякий случай, если игра не добавила заказ сама (хост вызывает ServerRpc напрямую)
            if (terminal.orderedItemsFromTerminal != null && terminal.orderedItemsFromTerminal.Count < already + count)
                terminal.orderedItemsFromTerminal.AddRange(items);
            terminal.numberOfItemsInDropship = terminal.orderedItemsFromTerminal?.Count ?? already + count;
            Plugin.Log.LogInfo($"Алиса: купила {terminal.buyableItemsList[index].itemName} x{count} за {cost}");
            return new[] { Bought, "Events/Purchase", "Ok" };
        }

        private static string Simplify(string word) =>
            new string((word ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
