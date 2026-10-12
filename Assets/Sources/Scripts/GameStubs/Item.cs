// Заглушка ассета Item из игры (Assembly-CSharp). Только сериализуемые поля, без логики.
// Лежит в Scripts/GameStubs без asmdef (asmdef есть только в Runtime/ и Editor/), поэтому компилируется в Assembly-CSharp проекта: в бандле ассет
// ссылается на «Assembly-CSharp::Item», и в игре это тот самый Item игры.
// Имена и типы полей должны совпадать с игрой (сверено с Lethal Company v81).
// Значения по умолчанию игры неизвестны (в справочных DLL нет тел методов), поэтому
// здесь их нет: важные поля выставляет ItemAssets при создании ассета.
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Lethal Company/Item", fileName = "NewItem")]
public class Item : ScriptableObject
{
    public int itemId;
    public string itemName;
    [Space] public List<ItemGroup> spawnPositionTypes = new List<ItemGroup>();
    [Space] public bool twoHanded;
    public bool twoHandedAnimation;
    public bool disableHandsOnWall;
    public bool canBeGrabbedBeforeGameStart;
    public bool disallowUtilitySlot;
    [Space] public float weight;
    public bool itemIsTrigger;
    public bool holdButtonUse;
    public bool itemSpawnsOnGround;
    [Space] public bool isConductiveMetal;
    [Header("Scrap / shop")] public bool isScrap;
    public int creditsWorth;
    public bool lockedInDemo;
    public int highestSalePercentage;
    [Space] public int maxValue;
    public int minValue;
    public GameObject spawnPrefab;
    [Space, Header("Battery")] public bool requiresBattery;
    public float batteryUsage;
    public bool automaticallySetUsingPower;
    [Space] public Sprite itemIcon;
    [Space, Header("Player animations")] public string grabAnim;
    public string useAnim;
    public string pocketAnim;
    public string throwAnim;
    [Space] public float grabAnimationTime;
    [Header("Player SFX")] public AudioClip grabSFX;
    public AudioClip dropSFX;
    public AudioClip pocketSFX;
    public AudioClip throwSFX;
    [Header("Netcode")] public bool syncGrabFunction;
    public bool syncUseFunction;
    public bool syncDiscardFunction;
    public bool syncInteractLRFunction;
    [Header("Save data")] public bool saveItemVariable;
    [Header("MISC")] public bool isDefensiveWeapon;
    [Space] public string[] toolTips;
    public float verticalOffset;
    public int floorYOffset;
    public bool allowDroppingAheadOfPlayer;
    public Vector3 restingRotation;
    public Vector3 rotationOffset;
    public Vector3 positionOffset;
    public bool meshOffset;
    public Mesh[] meshVariants;
    public Material[] materialVariants;
    [Space] public AudioClip[] clinkAudios;
    public bool usableInSpecialAnimations;
    public bool canBeInspected;
}
