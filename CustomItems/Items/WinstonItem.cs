using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Плюшевый Уинстон: по ЛКМ говорит «Уинстон». Звук слышат все игроки
    /// (SyncUse = true в описании), через рацию и монстры.
    /// </summary>
    public class WinstonItem : CustomGrabbable
    {
        public AudioClip voiceClip;

        private const float NoiseRange = 10f;
        private const float NoiseLoudness = 0.5f;

        public override void ItemActivate(bool used, bool buttonDown = true)
        {
            base.ItemActivate(used, buttonDown);
            if (!buttonDown || voiceClip == null || audioSource == null)
                return;

            audioSource.pitch = Random.Range(0.95f, 1.05f);
            audioSource.PlayOneShot(voiceClip);
            WalkieTalkie.TransmitOneShotAudio(audioSource, voiceClip);

            // Шум слышат монстры; считаем его только на владельце, чтобы не дублировать
            if (IsOwner)
            {
                RoundManager.Instance.PlayAudibleNoise(transform.position, NoiseRange, NoiseLoudness,
                    0, isInElevator && StartOfRound.Instance.hangarDoorsClosed);
            }
        }
    }
}
