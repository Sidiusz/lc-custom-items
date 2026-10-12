using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Пример предмета: резиновая уточка. ЛКМ пищит и привлекает монстров шумом.
    /// Использование синхронизирует сама игра (Sync Use Function в ассете Item), поэтому
    /// своих RPC и NetcodePatcher здесь не нужно.
    /// </summary>
    public class RubberDuckItem : CustomGrabbable
    {
        public AudioClip squeakClip;

        private const float NoiseRange = 12f;
        private const float NoiseLoudness = 0.6f;

        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            if (!buttonDown || squeakClip == null || audioSource == null)
                return;

            audioSource.pitch = Random.Range(0.9f, 1.1f);
            audioSource.PlayOneShot(squeakClip);
            WalkieTalkie.TransmitOneShotAudio(audioSource, squeakClip);

            // Шум слышат монстры; считаем его только на владельце, чтобы не дублировать
            if (IsOwner)
            {
                RoundManager.Instance.PlayAudibleNoise(transform.position, NoiseRange, NoiseLoudness,
                    0, isInElevator && StartOfRound.Instance.hangarDoorsClosed);
            }
        }
    }
}
