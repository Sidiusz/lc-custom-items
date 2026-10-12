using System.Collections;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Анимация «поднёс ко рту» от ванильного баллончика TZP-Inhalant: тот же параметр
    /// аниматора игрока, только держим его недолго. Вызывать на всех клиентах (из ItemActivate
    /// с синхронизацией использования), тогда движение видят и остальные.
    /// </summary>
    public class DrinkAnimation : MonoBehaviour
    {
        private const string Parameter = "useTZPItem";
        private static readonly int ParameterHash = Animator.StringToHash(Parameter);

        private static DrinkAnimation runner;

        /// <summary>Кто-то отпил (для Яндекс Колонки): номер глотка и где.</summary>
        public static int Sips;
        public static Vector3 LastSipAt;

        /// <summary>Отметить глоток для колонки, если анимацию ставит сам предмет (Егермейстер).</summary>
        public static void CountSip(PlayerControllerB player)
        {
            if (player == null)
                return;
            Sips++;
            LastSipAt = player.transform.position;
        }

        public static void Play(PlayerControllerB player, float seconds = 1.2f)
        {
            CountSip(player);
            var animator = player != null ? player.playerBodyAnimator : null;
            if (animator == null || !animator.parameters.Any(p => p.nameHash == ParameterHash))
                return;
            // Корутина живёт не на предмете: если бутылку выбросят посреди глотка, рука всё равно опустится
            if (runner == null)
            {
                var go = new GameObject("CustomItems.DrinkAnimation") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                runner = go.AddComponent<DrinkAnimation>();
            }
            runner.StartCoroutine(Hold(animator, seconds));
        }

        private static IEnumerator Hold(Animator animator, float seconds)
        {
            animator.SetBool(ParameterHash, true);
            yield return new WaitForSeconds(seconds);
            if (animator != null)
                animator.SetBool(ParameterHash, false);
        }
    }
}
