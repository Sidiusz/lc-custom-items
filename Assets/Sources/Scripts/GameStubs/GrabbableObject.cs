// Заглушка GrabbableObject из игры (Assembly-CSharp), см. Item.cs. Нужна, чтобы Unity
// узнал скрипты предметов из CustomItems.Items.dll (они наследуются от GrabbableObject)
// и мог сохранить их в префаб. Поля только те, что настраиваются на префабе;
// остальное игра заполняет сама. Методы-пустышки повторяют виртуальные методы игры,
// которые переопределяют скрипты предметов.
using Unity.Netcode;
using UnityEngine;

public abstract class GrabbableObject : NetworkBehaviour
{
    public bool grabbable;
    [Space] public int scrapValue;
    [Space] public float useCooldown;
    [Space] public Item itemProperties;
    public string customGrabTooltip;
    public MeshRenderer mainObjectRenderer;
    [Space] public bool grabbableToEnemies;
    public bool rotateObject;

    public virtual void Start() { }
    public virtual void Update() { }
    public virtual void LateUpdate() { }
    public virtual void ItemActivate(bool used, bool buttonDown = true) { }
    public virtual void EquipItem() { }
    public virtual void PocketItem() { }
    public virtual void DiscardItem() { }
    public virtual void GrabItem() { }
    public virtual int GetItemDataToSave() => 0;
    public virtual void LoadItemSaveData(int saveData) { }
}
