using System.Globalization;
using UnityEngine;

/// <summary>
/// Превью предмета в руке персонажа прямо в редакторе.
/// Ставит предмет относительно точки руки так же, как GrabbableObject.LateUpdate в игре:
///   rotation = holder.rotation * Euler(rotationOffset)
///   position = holder.position + holder.rotation * positionOffset
/// Подобранные значения переносятся в CustomItemDef (PositionOffset / RotationOffset).
/// Вешается на любой объект сцены, НЕ на префаб предмета.
/// </summary>
[ExecuteAlways]
public class HeldItemPreview : MonoBehaviour
{
    [Tooltip("Точка руки из рипнутого игрока: LocalItemHolder (вид от первого лица) или ServerItemHolder (вид со стороны)")]
    public Transform holder;

    [Tooltip("Экземпляр модели предмета в сцене")]
    public Transform item;

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

    /// <summary>Строки для вставки в CustomItemDef.</summary>
    public string ToSnippet()
    {
        return $"PositionOffset = {Vec(positionOffset)},\nRotationOffset = {Vec(rotationOffset)},";
    }

    private static string Vec(Vector3 v)
    {
        return $"new Vector3({F(v.x)}, {F(v.y)}, {F(v.z)})";
    }

    private static string F(float f)
    {
        return f.ToString("0.###", CultureInfo.InvariantCulture) + "f";
    }
}
