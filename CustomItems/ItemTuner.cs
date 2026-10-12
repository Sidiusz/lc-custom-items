using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CustomItems
{
    /// <summary>
    /// Окно настройки предметов прямо в игре (включается в конфиге, открывается клавишей).
    /// Ползунки и поля меняют оффсеты в руке, поворот и высоту на полу и размер на лету;
    /// «Сохранить» пишет их в BepInEx/config/SidiuszCustomItems.tuning.txt,
    /// и при следующем запуске они применяются сами.
    /// Пока окно открыто, мышь свободна, а камера, ходьба и действия с предметом
    /// отключены, чтобы клик по окну не стрелял и не пил.
    /// </summary>
    internal class ItemTuner : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }
        public static Key ToggleKey = Key.F8;

        private static readonly float[] PositionSteps = { 0.001f, 0.005f, 0.01f, 0.05f };
        private static readonly float[] RotationSteps = { 1f, 5f, 15f, 45f };
        private static readonly float[] ScaleSteps = { 0.01f, 0.05f, 0.1f };

        private Rect window = new Rect(20, 60, 460, 640);
        private Vector2 scroll;
        private Item selected;
        private ItemTuning edit;
        private Item lastHeld;
        private int positionStep = 2, rotationStep = 1, scaleStep = 1;
        private string status = "";
        private readonly Dictionary<string, string> textBuffers = new Dictionary<string, string>();

        private bool savedLook, savedMove;
        private PlayerControllerB blockedPlayer;

        private static PlayerControllerB LocalPlayer =>
            GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[ToggleKey].wasPressedThisFrame)
                SetOpen(!IsOpen);

            if (!IsOpen)
                return;

            // Открыли меню паузы или умерли — закрываемся, чтобы не мешать игре
            var player = LocalPlayer;
            if (player != null && (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen || player.isPlayerDead))
            {
                SetOpen(false, lockCursor: false);
                return;
            }

            // Если в руке предмет мода — сразу выбираем его
            var held = player != null && player.currentlyHeldObjectServer != null
                ? player.currentlyHeldObjectServer.itemProperties
                : null;
            if (held != null && held != lastHeld && ItemTuningStore.Items.Contains(held))
                Select(held);
            lastHeld = held;

            BlockPlayer(true);
        }

        private void LateUpdate()
        {
            if (!IsOpen)
                return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void SetOpen(bool open, bool lockCursor = true)
        {
            if (IsOpen == open)
                return;
            IsOpen = open;
            if (open)
            {
                if (selected == null && ItemTuningStore.Items.Count > 0)
                    Select(ItemTuningStore.Items[0]);
                lastHeld = null;
            }
            else
            {
                BlockPlayer(false);
                // Мышь прячем, только если мы в рейсе и не в меню/терминале: в главном меню она нужна
                var player = LocalPlayer;
                if (lockCursor && player != null && !player.inTerminalMenu &&
                    (player.quickMenuManager == null || !player.quickMenuManager.isMenuOpen))
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
            }
        }

        private void BlockPlayer(bool block)
        {
            if (block)
            {
                var player = LocalPlayer;
                if (player == null || blockedPlayer == player)
                    return;
                blockedPlayer = player;
                savedLook = player.disableLookInput;
                savedMove = player.disableMoveInput;
                player.disableLookInput = true;
                player.disableMoveInput = true;
            }
            else if (blockedPlayer != null)
            {
                blockedPlayer.disableLookInput = savedLook;
                blockedPlayer.disableMoveInput = savedMove;
                blockedPlayer = null;
            }
        }

        private void Select(Item item)
        {
            selected = item;
            edit = ItemTuningStore.Current(item);
            textBuffers.Clear();
            status = "";
        }

        private void OnGUI()
        {
            if (!IsOpen)
                return;
            window = GUILayout.Window(0x51D105, window, DrawWindow, $"Настройка предметов  ({ToggleKey} — закрыть)");
        }

        private void DrawWindow(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.Label("Предмет (выбирается сам, если взять его в руку):");
            var items = ItemTuningStore.Items;
            for (var i = 0; i < items.Count; i += 3)
            {
                GUILayout.BeginHorizontal();
                for (var j = i; j < Math.Min(i + 3, items.Count); j++)
                {
                    var name = items[j].itemName + (ItemTuningStore.HasSaved(items[j]) ? " *" : "");
                    if (GUILayout.Toggle(items[j] == selected, name, "Button") && items[j] != selected)
                        Select(items[j]);
                }
                GUILayout.EndHorizontal();
            }

            if (selected == null)
            {
                GUILayout.Label("У мода нет предметов.");
                GUILayout.EndScrollView();
                GUI.DragWindow();
                return;
            }

            var before = edit.Clone();

            Header("В руке: смещение (м)");
            positionStep = StepPicker(positionStep, PositionSteps);
            edit.PositionOffset = VectorField("pos", edit.PositionOffset, -0.6f, 0.6f, PositionSteps[positionStep]);

            Header("В руке: поворот (°)");
            rotationStep = StepPicker(rotationStep, RotationSteps);
            edit.RotationOffset = VectorField("rot", edit.RotationOffset, -180f, 180f, RotationSteps[rotationStep]);

            Header("На полу: наклон (°, Y случайный)");
            var rest = edit.RestingRotation;
            rest.x = FloatField("rest.x", "X", rest.x, -180f, 180f, RotationSteps[rotationStep]);
            rest.z = FloatField("rest.z", "Z", rest.z, -180f, 180f, RotationSteps[rotationStep]);
            edit.RestingRotation = rest;

            Header("На полу: высота центра над полом (м)");
            edit.VerticalOffset = FloatField("vert", "", edit.VerticalOffset, 0f, 1f, PositionSteps[positionStep]);

            Header("Размер (множитель к префабу)");
            scaleStep = StepPicker(scaleStep, ScaleSteps);
            edit.Scale = Mathf.Max(0.05f, FloatField("scale", "", edit.Scale, 0.1f, 5f, ScaleSteps[scaleStep]));

            if (!Same(before, edit))
            {
                ItemTuningStore.Apply(selected, edit);
                status = "Изменено, не сохранено";
            }

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Сохранить"))
            {
                try
                {
                    ItemTuningStore.Save(selected, edit);
                    status = "Сохранено в " + ItemTuningStore.FilePath;
                }
                catch (Exception e)
                {
                    status = "Не удалось сохранить: " + e.Message;
                }
            }
            if (GUILayout.Button("Отменить правки"))
                Revert(ItemTuningStore.SavedOrOriginal(selected), "Вернул последнее сохранённое");
            if (GUILayout.Button("Как в ассете"))
            {
                Revert(ItemTuningStore.Original(selected), "Вернул значения из ассета");
                ItemTuningStore.Forget(selected);
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Скопировать значения для ассета Item"))
            {
                GUIUtility.systemCopyBuffer = ItemTuningStore.ForAsset(selected, edit);
                Plugin.Log.LogInfo("Значения для ассета:\n" + GUIUtility.systemCopyBuffer);
                status = "Скопировано в буфер обмена (и в лог)";
            }

            GUILayout.Label(status);
            GUILayout.Label("Смещение и поворот в руке видны сразу. Чтобы проверить пол, выбросите предмет: " +
                            "лежащие экземпляры тоже обновляются.");

            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private void Revert(ItemTuning t, string message)
        {
            edit = t;
            ItemTuningStore.Apply(selected, edit);
            textBuffers.Clear();
            status = message;
        }

        private static void Header(string text)
        {
            GUILayout.Space(6);
            GUILayout.Label("<b>" + text + "</b>");
        }

        private static int StepPicker(int current, float[] steps)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Шаг:", GUILayout.Width(36));
            for (var i = 0; i < steps.Length; i++)
            {
                if (GUILayout.Toggle(i == current, ItemTuningStore.F(steps[i]), "Button", GUILayout.Width(52)))
                    current = i;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return current;
        }

        private Vector3 VectorField(string key, Vector3 v, float min, float max, float step)
        {
            v.x = FloatField(key + ".x", "X", v.x, min, max, step);
            v.y = FloatField(key + ".y", "Y", v.y, min, max, step);
            v.z = FloatField(key + ".z", "Z", v.z, min, max, step);
            return v;
        }

        // Строка: подпись, «−», ползунок, «+», поле для точного значения.
        // Поле хранит введённый текст, пока в нём курсор, чтобы можно было набрать «-0.»
        private float FloatField(string key, string label, float value, float min, float max, float step)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(16));
            if (GUILayout.Button("−", GUILayout.Width(26)))
                value -= step;

            var slid = GUILayout.HorizontalSlider(value, Mathf.Min(min, value), Mathf.Max(max, value), GUILayout.Width(210));
            if (!Mathf.Approximately(slid, value))
                value = Mathf.Round(slid / step) * step;

            if (GUILayout.Button("+", GUILayout.Width(26)))
                value += step;

            GUI.SetNextControlName(key);
            var focused = GUI.GetNameOfFocusedControl() == key;
            var shown = focused && textBuffers.TryGetValue(key, out var buffer) ? buffer : ItemTuningStore.F(value);
            var typed = GUILayout.TextField(shown, GUILayout.Width(70));
            if (focused)
            {
                textBuffers[key] = typed;
                if (typed != shown && float.TryParse(typed.Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var parsed))
                    value = parsed;
            }
            else
            {
                textBuffers.Remove(key);
            }
            GUILayout.EndHorizontal();

            // Убираем хвосты вроде 0.30000001 от сложения шагов
            return (float)Math.Round(value, 4);
        }

        private static bool Same(ItemTuning a, ItemTuning b) =>
            a.PositionOffset == b.PositionOffset && a.RotationOffset == b.RotationOffset &&
            a.RestingRotation == b.RestingRotation && Mathf.Approximately(a.VerticalOffset, b.VerticalOffset) &&
            Mathf.Approximately(a.Scale, b.Scale);

        /// <summary>
        /// Пока окно открыто, клики и колесо мыши не должны доходить до игрока:
        /// иначе клик по ползунку стреляет из пистолета, а колесо листает слоты.
        /// </summary>
        [HarmonyPatch]
        internal static class BlockPlayerInput
        {
            private static readonly string[] Handlers =
            {
                "ActivateItem_performed", "ItemSecondaryUse_performed", "ItemTertiaryUse_performed",
                "Interact_performed", "Discard_performed", "ScrollMouse_performed", "QEItemInteract_performed",
                "InspectItem_performed", "UseUtilitySlot_performed", "Jump_performed", "Crouch_performed",
            };

            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var name in Handlers)
                {
                    var method = AccessTools.Method(typeof(PlayerControllerB), name);
                    if (method != null)
                        yield return method;
                }
            }

            private static bool Prefix() => !IsOpen;
        }
    }
}
