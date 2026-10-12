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
        if (GUILayout.Button("Скопировать для CustomItemDef"))
        {
            EditorGUIUtility.systemCopyBuffer = preview.ToSnippet();
            Debug.Log("Скопировано в буфер:\n" + preview.ToSnippet());
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
