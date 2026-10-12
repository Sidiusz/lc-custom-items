using System.Linq;
using CustomItems.Items;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CustomItems
{
    /// <summary>
    /// Колонку на корабле можно переставить как мебель: посмотреть на неё и нажать B (клавиша
    /// режима строительства), дальше она ездит за взглядом по полу, столам и полкам, R крутит,
    /// B или ЛКМ ставит, Esc или ПКМ отменяет. Ставит хост и рассылает всем, игра потом сама
    /// держит её на месте (как упавший предмет) и сохраняет позицию.
    /// </summary>
    internal class AliceFurniture : MonoBehaviour
    {
        private const string PlaceMessage = "SidiuszCustomItems.AlicePlace", PlacedMessage = "SidiuszCustomItems.AlicePlaced";
        private const float Reach = 4f, RotateSpeed = 120f;

        private static CustomMessagingManager registeredOn;
        private InputAction buildAction;

        // Что переставляем сейчас (только у себя, пока не поставили)
        private YandexStationItem moving;
        private Vector3 fromPosition;
        private int fromYRot;
        private float yaw;

        private void Update()
        {
            RegisterMessages();
            var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (player == null || ItemTuner.IsOpen)
                return;
            if (buildAction == null && IngamePlayerSettings.Instance != null && IngamePlayerSettings.Instance.playerInput != null)
                buildAction = IngamePlayerSettings.Instance.playerInput.actions.FindAction("BuildMode", false);
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            var build = buildAction != null ? buildAction.WasPressedThisFrame() : keyboard != null && keyboard.bKey.wasPressedThisFrame;

            if (moving != null)
            {
                if (!CanMove(player, moving) || keyboard != null && keyboard.escapeKey.wasPressedThisFrame
                    || mouse != null && mouse.rightButton.wasPressedThisFrame)
                {
                    Cancel();
                    return;
                }
                if (keyboard != null && keyboard.rKey.isPressed)
                    yaw = (yaw + RotateSpeed * Time.deltaTime) % 360f;
                if (build || mouse != null && mouse.leftButton.wasPressedThisFrame)
                    Confirm();
                return;
            }

            if (!build || player.isTypingChat || player.inTerminalMenu || player.isPlayerDead || !player.isInHangarShipRoom
                || player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                return;
            var cam = player.gameplayCamera.transform;
            if (!Physics.Raycast(cam.position, cam.forward, out var hit, Reach, ~0, QueryTriggerInteraction.Collide))
                return;
            var station = hit.collider.GetComponentInParent<YandexStationItem>();
            if (station == null || !CanMove(player, station))
                return;
            moving = station;
            fromPosition = station.targetFloorPosition;
            fromYRot = station.floorYRot;
            yaw = station.transform.eulerAngles.y;
            HUDManager.Instance?.DisplayTip("Яндекс Станция", "[R] повернуть, [B] / ЛКМ поставить, [Esc] / ПКМ отмена");
        }

        // Пока переставляют — едет за взглядом (у себя), после обычного Update предмета
        private void LateUpdate()
        {
            if (moving == null)
                return;
            var player = GameNetworkManager.Instance?.localPlayerController;
            var round = StartOfRound.Instance;
            if (player == null || round == null)
                return;
            var cam = player.gameplayCamera.transform;
            var end = cam.position + cam.forward * Reach;
            if (Physics.Raycast(cam.position, cam.forward, out var hit, Reach, round.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
                end = hit.point;
            else if (Physics.Raycast(end, Vector3.down, out hit, 10f, round.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
                end = hit.point;
            var world = end + Vector3.up * moving.itemProperties.verticalOffset;
            var local = round.elevatorTransform.InverseTransformPoint(world);
            Apply(moving, local, yaw);
        }

        private static bool CanMove(PlayerControllerB player, YandexStationItem station) =>
            station != null && !station.isHeld && !station.isPocketed && station.isInShipRoom && player.isInHangarShipRoom
            && Vector3.Distance(player.transform.position, station.transform.position) < Reach + 2f;

        private void Cancel()
        {
            if (moving != null)
            {
                moving.targetFloorPosition = fromPosition;
                moving.floorYRot = fromYRot;
                moving.reachedFloorTarget = false;
            }
            moving = null;
        }

        private void Confirm()
        {
            var station = moving;
            moving = null;
            var round = StartOfRound.Instance;
            if (station == null || round == null || NetworkManager.Singleton == null)
                return;
            var local = station.targetFloorPosition;
            using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(float) * 4, Allocator.Temp);
            writer.WriteValueSafe(station.NetworkObjectId);
            writer.WriteValueSafe(local);
            writer.WriteValueSafe(yaw);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(PlaceMessage, NetworkManager.ServerClientId, writer,
                NetworkDelivery.Reliable);
        }

        // Положить предмет на место так, как его кладёт сама игра после падения
        private static void Apply(YandexStationItem station, Vector3 local, float yaw)
        {
            var round = StartOfRound.Instance;
            if (station.transform.parent != round.elevatorTransform)
                station.transform.SetParent(round.elevatorTransform, true);
            station.isInElevator = true;
            station.isInShipRoom = true;
            station.parentObject = null;
            station.startFallingPosition = local;
            station.targetFloorPosition = local;
            station.fallTime = 1f;
            station.hasHitGround = true;
            station.transform.localPosition = local;
            var rest = station.itemProperties.restingRotation;
            station.transform.rotation = Quaternion.Euler(rest.x, yaw, rest.z);
            // Чтобы игра поставила тот же поворот, когда сама «досадит» предмет
            station.floorYRot = Mathf.RoundToInt(yaw - station.itemProperties.floorYOffset - 90f);
            station.reachedFloorTarget = true;
        }

        private static void RegisterMessages()
        {
            var messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null || messaging == registeredOn)
                return;
            messaging.RegisterNamedMessageHandler(PlaceMessage, OnPlace);
            messaging.RegisterNamedMessageHandler(PlacedMessage, OnPlaced);
            registeredOn = messaging;
        }

        // Хост: проверяет и рассылает всем (и себе)
        private static void OnPlace(ulong sender, FastBufferReader reader)
        {
            var net = NetworkManager.Singleton;
            if (net == null || !net.IsServer)
                return;
            reader.ReadValueSafe(out ulong id);
            reader.ReadValueSafe(out Vector3 local);
            reader.ReadValueSafe(out float yaw);
            var station = YandexStationItem.All.FirstOrDefault(s => s != null && s.NetworkObjectId == id);
            if (station == null || station.isHeld || station.isPocketed || local.magnitude > 30f)
                return;
            foreach (var client in net.ConnectedClientsIds)
            {
                using var writer = new FastBufferWriter(sizeof(ulong) + sizeof(float) * 4, Allocator.Temp);
                writer.WriteValueSafe(id);
                writer.WriteValueSafe(local);
                writer.WriteValueSafe(yaw);
                net.CustomMessagingManager.SendNamedMessage(PlacedMessage, client, writer, NetworkDelivery.Reliable);
            }
        }

        private static void OnPlaced(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId)
                return;
            reader.ReadValueSafe(out ulong id);
            reader.ReadValueSafe(out Vector3 local);
            reader.ReadValueSafe(out float yaw);
            var station = YandexStationItem.All.FirstOrDefault(s => s != null && s.NetworkObjectId == id);
            if (station != null && !station.isHeld && StartOfRound.Instance != null)
                Apply(station, local, yaw);
        }
    }
}
