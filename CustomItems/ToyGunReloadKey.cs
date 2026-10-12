using CustomItems.Items;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CustomItems
{
    /// <summary>
    /// Клавиша перезарядки игрушечного пистолета из конфига (по умолчанию R). Нажатие
    /// идёт через ванильный ItemInteractLeftRightOnClient, так же как E, поэтому
    /// перезарядка синхронизируется сама. Сборка предметов не ссылается на Input System
    /// (её грузит и Unity), поэтому клавиша ловится здесь, в плагине.
    /// </summary>
    internal class ToyGunReloadKey : MonoBehaviour
    {
        public static Key Key = Key.R;

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard[Key].wasPressedThisFrame || ItemTuner.IsOpen)
                return;

            var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (player == null || !(player.currentlyHeldObjectServer is ToyGunItem gun))
                return;
            if (player.isTypingChat || player.inTerminalMenu || player.isPlayerDead || player.isGrabbingObjectAnimation
                || player.inSpecialInteractAnimation || player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                return;

            gun.ItemInteractLeftRightOnClient(true);
        }
    }
}
