using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(HeldItemPreview))]
public class HeldItemPreviewEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var preview = (HeldItemPreview)target;

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(preview.itemAsset == null))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Взять из Item"))
            {
                var item = new SerializedObject(preview.itemAsset);
                Undo.RecordObject(preview, "Load held item offsets");
                preview.positionOffset = item.FindProperty("positionOffset")?.vector3Value ?? preview.positionOffset;
                preview.rotationOffset = item.FindProperty("rotationOffset")?.vector3Value ?? preview.rotationOffset;
            }
            if (GUILayout.Button("Записать в Item"))
            {
                var item = new SerializedObject(preview.itemAsset);
                var pos = item.FindProperty("positionOffset");
                var rot = item.FindProperty("rotationOffset");
                if (pos == null || rot == null)
                {
                    Debug.LogError("В Item Asset нужен ассет Item игры (Assets/Sources/Data/Items)");
                }
                else
                {
                    pos.vector3Value = preview.positionOffset;
                    rot.vector3Value = preview.rotationOffset;
                    item.ApplyModifiedProperties();
                    Debug.Log("Оффсеты записаны в " + preview.itemAsset.name + ", пересоберите бандл");
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        if (GUILayout.Button("Сбросить оффсеты"))
        {
            Undo.RecordObject(preview, "Reset held item offsets");
            preview.positionOffset = Vector3.zero;
            preview.rotationOffset = Vector3.zero;
        }

        EditorGUILayout.HelpBox(
            "Двигать предмет мышкой нельзя: каждый кадр он встаёт по оффсетам. " +
            "Меняйте Position/Rotation Offset в инспекторе (поля можно тянуть за подпись). " +
            "Позу руки смотрите в окне Animation на модели игрока.",
            MessageType.Info);
    }
}

// В редакторе LateUpdate вызывается не каждый кадр, поэтому при прокрутке анимации
// предмет может отставать от руки. Этот хук обновляет превью на каждом тике редактора.
[InitializeOnLoad]
internal static class HeldItemPreviewUpdater
{
    static HeldItemPreviewUpdater()
    {
        EditorApplication.update += () =>
        {
            if (Application.isPlaying)
                return;
            foreach (var p in Object.FindObjectsOfType<HeldItemPreview>())
                p.Apply();
        };
    }
}
