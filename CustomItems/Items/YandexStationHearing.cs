using GameNetcodeStuff;
using UnityEngine;

namespace CustomItems.Items
{
    public partial class YandexStationItem
    {
        /// <summary>
        /// Ждёт ли ближайшая к игроку колонка его слов без «Алиса»: проснулась и слушает, задала вопрос
        /// («Вы уверены?») или ждёт, во что играть. Проснулась на «Алиса» другого игрока — этого не ждёт.
        /// Такую фразу плагин распознаёт точнее (Whisper), как и с «Алиса».
        /// </summary>
        public static bool IsHearing(PlayerControllerB speaker)
        {
            if (speaker == null)
                return false;
            var station = HearingStation(speaker);
            return station != null && (Time.time < station.listenUntil && station.ListensTo(speaker)
                                       || station.asking != null && Time.time < station.askUntil
                                       || Time.time < station.gameAnswerUntil
                                       || Time.time < station.followUntil); // «а Паша?» сразу после ответа
        }

        /// <summary>Включена ли хоть одна колонка (тогда пора загружать распознавание).</summary>
        public static bool AnyOn
        {
            get
            {
                foreach (var station in All)
                {
                    if (station != null && station.IsOn)
                        return true;
                }
                return false;
            }
        }
    }
}
