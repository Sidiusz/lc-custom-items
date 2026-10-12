using System;
using UnityEngine;

namespace CustomItems.Items
{
    public partial class YandexStationItem
    {
        /// <summary>
        /// Понимание вопроса по смыслу, а не по словам: (вопрос, тема по словам, её похожесть, почему) →
        /// тема как в intents.txt или null. Дописывает в «почему», что решил смысл. Ставит плагин
        /// (AliceSemantic); не задан или модель ещё грузится — понимает только по словам, как раньше.
        /// </summary>
        public delegate string SemanticHook(string command, string fuzzyTopic, float fuzzyScore, ref string why);

        public static SemanticHook SemanticMatch;

        // По словам не нашли или нашли неуверенно — спрашиваем смысл. Ошибка смысла не ломает ответ
        private static string SemanticTopic(string command, string intent, float score, ref string why)
        {
            var hook = SemanticMatch;
            if (hook == null)
                return intent;
            try
            {
                return hook(command, intent, score, ref why);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Алиса] смысл: " + e);
                return intent;
            }
        }
    }
}
