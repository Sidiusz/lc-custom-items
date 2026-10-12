// Пустые заглушки остальных типов игры, на которые ссылается SidiuszCustomItems.Items.dll.
// В ассетах они не хранятся, но без них Unity не загружает классы предметов
// («Could not load signature … PlayerControllerB») и AddComponent возвращает null.
// Нужны только сами типы: поля и методы игры здесь не повторяем.
using Unity.Netcode;
using UnityEngine;

namespace GameNetcodeStuff
{
    public class PlayerControllerB : NetworkBehaviour { }
}

public enum CauseOfDeath { Unknown }
public enum ScreenShakeType { Small }
public interface IHittable { }

public class AllItemsList : ScriptableObject { }
public class EndOfGameStats { }
public class EnemyAI : NetworkBehaviour { }
public class EnemyAICollisionDetect : MonoBehaviour { }
public class GameNetworkManager : MonoBehaviour { }
public class HUDManager : NetworkBehaviour { }
public class RoundManager : NetworkBehaviour { }
public class SoundManager : NetworkBehaviour { }
public class StartOfRound : NetworkBehaviour { }
public class WalkieTalkie : GrabbableObject { }
public class TimeOfDay : NetworkBehaviour { }
public class SelectableLevel : ScriptableObject { }
public enum LevelWeatherType { None = -1 }
public class Battery { }
public class BoomboxItem : GrabbableObject { }
public class DeadBodyInfo : MonoBehaviour { }
public class EnemyType : ScriptableObject { }
public class Terminal : NetworkBehaviour { }
public class ItemDropship : NetworkBehaviour { }
