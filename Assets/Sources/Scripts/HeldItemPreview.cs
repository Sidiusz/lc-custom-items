using UnityEngine;

/// <summary>
/// Превью предмета в руке персонажа прямо в редакторе.
/// Ставит предмет относительно точки руки так же, как GrabbableObject.LateUpdate в игре:
///   rotation = holder.rotation * Euler(rotationOffset)
///   position = holder.position + holder.rotation * positionOffset
/// Подобранные значения записываются кнопкой в ассет Item предмета (Position / Rotation Offset).
/// Вешается на любой объект сцены, НЕ на префаб предмета.
/// </summary>
[ExecuteAlways]
public class HeldItemPreview : MonoBehaviour
{
    [Tooltip("Точка руки из рипнутого игрока: LocalItemHolder (вид от первого лица) или ServerItemHolder (вид со стороны)")]
    public Transform holder;

    [Tooltip("Экземпляр модели предмета в сцене")]
    public Transform item;

    [Tooltip("Ассет Item предмета из Assets/Sources/Data/Items: туда кнопкой пишутся оффсеты")]
    public ScriptableObject itemAsset;

    public Vector3 positionOffset;
    public Vector3 rotationOffset;

    private void LateUpdate()
    {
        Apply();
    }

    public void Apply()
    {
        if (holder == null || item == null)
            return;
        item.rotation = holder.rotation * Quaternion.Euler(rotationOffset);
        item.position = holder.position + holder.rotation * positionOffset;
    }
}
