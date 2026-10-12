using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace CustomItems
{
    /// <summary>
    /// Окно настройки предметов прямо в игре (включается в конфиге, открывается клавишей).
    ///
    /// Две вкладки: «В руке» (Position/Rotation Offset) и «На полу» (Resting Rotation и высота над полом,
    /// которая меряется по нижней точке лежащего предмета). Крутить предмет можно кнопками или мышью
    /// прямо в мире, а камеру можно вынести наружу и облетать предмет, чтобы видеть его со стороны,
    /// как его видят друзья. «Сохранить» пишет значения в BepInEx/config/SidiuszCustomItems.tuning.txt,
    /// и при следующем запуске они применяются сами.
    ///
    /// Пока окно открыто, камера игрока, ходьба и действия с предметом отключены, чтобы клик по окну
    /// не стрелял и не пил.
    /// </summary>
    [DefaultExecutionOrder(30000)] // LateUpdate после игрока и предметов: камера и предмет ставятся последними
    internal class ItemTuner : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }
        public static Key ToggleKey = Key.F8;

        private enum Tab { Hand, Floor }

        private static readonly float[] PositionSteps = { 0.001f, 0.005f, 0.01f, 0.05f };
        private static readonly float[] RotationSteps = { 1f, 5f, 15f, 45f };
        private static readonly float[] ScaleSteps = { 0.01f, 0.05f, 0.1f };

        private Rect window = new Rect(20, 60, 470, 700);
        private Vector2 scroll;
        private Tab tab;
        private Item selected;
        private ItemTuning edit;
        private Item lastHeld;
        private int positionStep = 2, rotationStep = 1, scaleStep = 1;
        private bool keepOnFloor = true;
        private string status = "";
        private readonly Dictionary<string, string> textBuffers = new Dictionary<string, string>();

        // Мышь в мире (вне окна): ЛКМ — крутить предмет, Ctrl+ЛКМ — двигать в руке, ПКМ — облёт камеры
        private bool rotatingItem, orbiting;

        // Вид со стороны
        private bool thirdPerson;
        private float orbitYaw, orbitPitch = 12f, orbitDistance = 1.2f;
        private Vector3 savedCameraPosition;
        private Quaternion savedCameraRotation;
        private ShadowCastingMode savedBodyShadows;
        private bool savedArmsEnabled, savedVisorActive;
        private PlayerControllerB viewPlayer;

        private bool savedLook, savedMove;
        private PlayerControllerB blockedPlayer;

        private static PlayerControllerB LocalPlayer =>
            GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;

        // ---------------------------------------------------------------- цикл

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
            HandleMouse(player);
        }

        private void LateUpdate()
        {
            if (!IsOpen)
                return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (thirdPerson)
                UpdateThirdPerson();
        }

        private void SetOpen(bool open, bool lockCursor = true)
        {
            if (IsOpen == open)
                return;
            IsOpen = open;
            rotatingItem = orbiting = false;
            if (open)
            {
                if (selected == null && ItemTuningStore.Items.Count > 0)
                    Select(ItemTuningStore.Items[0]);
                lastHeld = null;
                return;
            }

            SetThirdPerson(false);
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

        private void Changed()
        {
            ItemTuningStore.Apply(selected, edit);
            status = "Изменено, не сохранено";
        }

        // ---------------------------------------------------------------- какой экземпляр настраиваем

        /// <summary>Выбранный предмет в руке у игрока.</summary>
        private GrabbableObject HeldTarget(PlayerControllerB player)
        {
            var held = player != null ? player.currentlyHeldObjectServer : null;
            return held != null && held.itemProperties == selected ? held : null;
        }

        /// <summary>Ближайший лежащий экземпляр выбранного предмета (до 20 м).</summary>
        private GrabbableObject FloorTarget(PlayerControllerB player)
        {
            if (player == null || selected == null)
                return null;
            // Окно рисуется несколько раз за кадр: ищем лежащий экземпляр один раз
            if (floorTargetFrame == Time.frameCount && floorTargetItem == selected)
                return floorTarget;
            floorTargetFrame = Time.frameCount;
            floorTargetItem = selected;
            return floorTarget = FindObjectsOfType<GrabbableObject>()
                .Where(o => o.itemProperties == selected && !o.isHeld && !o.isPocketed && o.gameObject.activeInHierarchy)
                .Select(o => (o, d: Vector3.Distance(o.transform.position, player.transform.position)))
                .Where(x => x.d < 20f)
                .OrderBy(x => x.d)
                .Select(x => x.o)
                .FirstOrDefault();
        }

        private int floorTargetFrame = -1;
        private Item floorTargetItem;
        private GrabbableObject floorTarget;

        private GrabbableObject Target(PlayerControllerB player) =>
            tab == Tab.Hand ? HeldTarget(player) : FloorTarget(player);

        // ---------------------------------------------------------------- мышь в мире

        private void HandleMouse(PlayerControllerB player)
        {
            var mouse = Mouse.current;
            if (mouse == null || player == null || selected == null)
                return;

            var pos = mouse.position.ReadValue();
            var overWindow = window.Contains(new Vector2(pos.x, Screen.height - pos.y));
            if (mouse.leftButton.wasPressedThisFrame && !overWindow)
                rotatingItem = true;
            if (!mouse.leftButton.isPressed)
                rotatingItem = false;
            if (mouse.rightButton.wasPressedThisFrame && !overWindow)
                orbiting = true;
            if (!mouse.rightButton.isPressed)
                orbiting = false;

            var delta = mouse.delta.ReadValue();
            if (thirdPerson)
            {
                if (orbiting)
                {
                    orbitYaw += delta.x * 0.3f;
                    orbitPitch = Mathf.Clamp(orbitPitch - delta.y * 0.3f, -80f, 80f);
                }
                var wheel = mouse.scroll.ReadValue().y;
                if (!overWindow && Mathf.Abs(wheel) > 0.01f)
                    orbitDistance = Mathf.Clamp(orbitDistance * (1f - Mathf.Sign(wheel) * 0.1f), 0.3f, 6f);
            }

            if (!rotatingItem || delta.sqrMagnitude < 0.0001f)
                return;
            var target = Target(player);
            var cam = player.gameplayCamera != null ? player.gameplayCamera.transform : null;
            if (target == null || cam == null)
                return;

            var keyboard = Keyboard.current;
            var fine = keyboard != null && keyboard.shiftKey.isPressed;
            var move = keyboard != null && keyboard.ctrlKey.isPressed;

            if (tab == Tab.Hand && move && target.parentObject != null)
            {
                // Ctrl: двигаем предмет в плоскости экрана
                var world = (cam.right * delta.x + cam.up * delta.y) * (fine ? 0.0003f : 0.0015f);
                edit.PositionOffset = Round(edit.PositionOffset + Quaternion.Inverse(target.parentObject.rotation) * world, 4);
                Changed();
                return;
            }

            // Крутим как шарик: влево-вправо вокруг вертикали экрана, вверх-вниз вокруг горизонтали
            var k = fine ? 0.08f : 0.4f;
            var turn = Quaternion.AngleAxis(-delta.x * k, cam.up) * Quaternion.AngleAxis(delta.y * k, cam.right);
            if (tab == Tab.Hand && target.parentObject != null)
            {
                var holder = target.parentObject.rotation;
                var offset = Quaternion.Inverse(holder) * turn * holder * Quaternion.Euler(edit.RotationOffset);
                edit.RotationOffset = Euler(offset);
                Changed();
            }
            else if (tab == Tab.Floor)
            {
                var e = (turn * target.transform.rotation).eulerAngles;
                SetResting(target, new Vector3(Angle(e.x), 0f, Angle(e.z)), e.y);
            }
        }

        // ---------------------------------------------------------------- на полу

        private void SetResting(GrabbableObject target, Vector3 resting, float yaw)
        {
            edit.RestingRotation = resting;
            Changed();
            if (target != null)
            {
                target.transform.rotation = Quaternion.Euler(resting.x, yaw, resting.z);
                if (keepOnFloor)
                    SnapToFloor(target);
            }
        }

        /// <summary>
        /// Зазор между нижней точкой лежащего предмета и полом под ним (м).
        /// Плюс — висит в воздухе, минус — провалился. null — пол не нашёлся.
        /// </summary>
        private static float? FloorGap(GrabbableObject target)
        {
            var renderers = target.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled).ToArray();
            if (renderers.Length == 0)
                return null;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);

            var mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            var from = new Vector3(bounds.center.x, bounds.max.y + 0.05f, bounds.center.z);
            var hits = Physics.RaycastAll(from, Vector3.down, bounds.size.y + 3f, mask, QueryTriggerInteraction.Ignore);
            var floor = hits
                .Where(h => !h.collider.transform.IsChildOf(target.transform) && h.point.y < bounds.min.y + 0.3f)
                .OrderBy(h => h.distance)
                .Select(h => (float?)h.point.y)
                .FirstOrDefault();
            return floor.HasValue ? bounds.min.y - floor.Value : (float?)null;
        }

        /// <summary>Подбирает высоту так, чтобы низ предмета касался пола.</summary>
        private void SnapToFloor(GrabbableObject target)
        {
            var gap = FloorGap(target);
            if (!gap.HasValue || Mathf.Abs(gap.Value) < 0.0005f)
                return;
            edit.VerticalOffset = (float)Math.Round(edit.VerticalOffset - gap.Value, 4);
            Changed();
        }

        // ---------------------------------------------------------------- вид со стороны

        private void SetThirdPerson(bool on)
        {
            var player = on ? LocalPlayer : viewPlayer;
            if (on == thirdPerson || player == null || player.gameplayCamera == null)
            {
                thirdPerson = on && player != null && player.gameplayCamera != null;
                return;
            }
            thirdPerson = on;
            var cam = player.gameplayCamera.transform;

            if (on)
            {
                viewPlayer = player;
                savedCameraPosition = cam.localPosition;
                savedCameraRotation = cam.localRotation;
                // Своё тело игра рисует только тенью, а руки и шлем — только для первого лица
                if (player.thisPlayerModel != null)
                {
                    savedBodyShadows = player.thisPlayerModel.shadowCastingMode;
                    player.thisPlayerModel.shadowCastingMode = ShadowCastingMode.On;
                }
                if (player.thisPlayerModelArms != null)
                {
                    savedArmsEnabled = player.thisPlayerModelArms.enabled;
                    player.thisPlayerModelArms.enabled = false;
                }
                if (player.localVisor != null)
                {
                    savedVisorActive = player.localVisor.gameObject.activeSelf;
                    player.localVisor.gameObject.SetActive(false);
                }
                orbitYaw = player.transform.eulerAngles.y + 160f; // спереди и чуть сбоку
                orbitPitch = 12f;
                return;
            }

            cam.localPosition = savedCameraPosition;
            cam.localRotation = savedCameraRotation;
            if (player.thisPlayerModel != null)
                player.thisPlayerModel.shadowCastingMode = savedBodyShadows;
            if (player.thisPlayerModelArms != null)
                player.thisPlayerModelArms.enabled = savedArmsEnabled;
            if (player.localVisor != null)
                player.localVisor.gameObject.SetActive(savedVisorActive);
            // Предметы снова держит рука первого лица
            if (player.ItemSlots != null)
            {
                foreach (var slot in player.ItemSlots)
                    if (slot != null && slot.parentObject == player.serverItemHolder)
                        slot.parentObject = player.localItemHolder;
            }
            viewPlayer = null;
        }

        private void UpdateThirdPerson()
        {
            var player = viewPlayer;
            if (player == null || player.gameplayCamera == null)
            {
                thirdPerson = false;
                return;
            }

            // Предмет в руке ставим в руку полной модели — так его видят остальные игроки
            var held = player.currentlyHeldObjectServer;
            if (held != null && player.serverItemHolder != null)
            {
                // В этом кадре игра уже поставила предмет в руку первого лица: переставляем сами,
                // по той же формуле, что GrabbableObject.LateUpdate
                var hand = player.serverItemHolder;
                held.parentObject = hand;
                held.transform.rotation = hand.rotation * Quaternion.Euler(held.itemProperties.rotationOffset);
                held.transform.position = hand.position + hand.rotation * held.itemProperties.positionOffset;
            }

            var target = Target(player);
            var point = target != null ? Center(target) : player.transform.position + Vector3.up * 1.3f;
            var cam = player.gameplayCamera.transform;
            cam.position = point + Quaternion.Euler(orbitPitch, orbitYaw, 0f) * (Vector3.back * orbitDistance);
            cam.rotation = Quaternion.LookRotation(point - cam.position);
        }

        private static Vector3 Center(GrabbableObject target)
        {
            var renderers = target.GetComponentsInChildren<Renderer>(false);
            if (renderers.Length == 0)
                return target.transform.position;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            return bounds.center;
        }

        // ---------------------------------------------------------------- окно

        private void OnGUI()
        {
            if (!IsOpen)
                return;
            window = GUILayout.Window(0x51D105, window, DrawWindow, $"Настройка предметов  ({ToggleKey} — закрыть)");
        }

        private void DrawWindow(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll);
            var player = LocalPlayer;

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

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(tab == Tab.Hand, "В руке", "Button"))
                tab = Tab.Hand;
            if (GUILayout.Toggle(tab == Tab.Floor, "На полу", "Button"))
                tab = Tab.Floor;
            GUILayout.EndHorizontal();

            var wantThird = GUILayout.Toggle(thirdPerson, " Вид со стороны (ПКМ в мире — облёт, колесо — ближе/дальше)");
            if (wantThird != thirdPerson)
                SetThirdPerson(wantThird);

            GUILayout.Label("Мышью в мире: ЛКМ — крутить предмет" + (tab == Tab.Hand ? ", Ctrl+ЛКМ — двигать" : "") +
                            ", с Shift — точнее.");

            var before = edit.Clone();
            if (tab == Tab.Hand)
                DrawHand(player);
            else
                DrawFloor(player);

            Header("Размер (множитель к префабу)");
            scaleStep = StepPicker(scaleStep, ScaleSteps);
            edit.Scale = Mathf.Max(0.05f, FloatField("scale", "", edit.Scale, 0.1f, 5f, ScaleSteps[scaleStep]));

            if (!Same(before, edit))
                Changed();

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
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private void DrawHand(PlayerControllerB player)
        {
            if (HeldTarget(player) == null)
                GUILayout.Label("Возьмите этот предмет в руку, чтобы видеть изменения.");

            Header("Смещение в руке (м)");
            positionStep = StepPicker(positionStep, PositionSteps);
            edit.PositionOffset = VectorField("pos", edit.PositionOffset, -0.6f, 0.6f, PositionSteps[positionStep]);

            Header("Поворот в руке (кнопки крутят вокруг осей предмета)");
            rotationStep = StepPicker(rotationStep, RotationSteps);
            var rotation = edit.RotationOffset;
            rotation = RotationRow("rot.x", "X", rotation, Vector3.right, 0);
            rotation = RotationRow("rot.y", "Y", rotation, Vector3.up, 1);
            rotation = RotationRow("rot.z", "Z", rotation, Vector3.forward, 2);
            edit.RotationOffset = rotation;
            if (GUILayout.Button("Сбросить поворот"))
                edit.RotationOffset = Vector3.zero;
        }

        private void DrawFloor(PlayerControllerB player)
        {
            var target = FloorTarget(player);
            if (target == null)
            {
                GUILayout.Label("Рядом нет лежащего экземпляра этого предмета.");
                if (HeldTarget(player) != null && GUILayout.Button("Выбросить из рук"))
                    player.DiscardHeldObject();
            }
            else
            {
                var gap = FloorGap(target);
                GUILayout.Label(gap.HasValue
                    ? $"Низ предмета над полом: {gap.Value * 100f:0.0} см" + (Mathf.Abs(gap.Value) < 0.005f ? " (касается)" : "")
                    : "Не вижу пол под предметом");
            }

            Header("Наклон на полу (поворот по Y игра выбирает случайно)");
            rotationStep = StepPicker(rotationStep, RotationSteps);
            var resting = edit.RestingRotation;
            var newResting = RotationRow("rest.x", "X", resting, Vector3.right, 0);
            newResting = RotationRow("rest.z", "Z", newResting, Vector3.forward, 2);
            newResting.y = 0f;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Стоя"))
                newResting = Vector3.zero;
            if (GUILayout.Button("На боку"))
                newResting = new Vector3(0f, 0f, 90f);
            if (GUILayout.Button("Лёжа на спине"))
                newResting = new Vector3(-90f, 0f, 0f);
            if (GUILayout.Button("Вверх дном"))
                newResting = new Vector3(180f, 0f, 0f);
            GUILayout.EndHorizontal();
            if (newResting != resting)
                SetResting(target, newResting, target != null ? target.transform.eulerAngles.y : 0f);

            Header("Высота пивота над полом (Vertical Offset, м)");
            edit.VerticalOffset = FloatField("vert", "", edit.VerticalOffset, -0.5f, 1f, PositionSteps[positionStep]);
            GUILayout.BeginHorizontal();
            GUI.enabled = target != null;
            if (GUILayout.Button("Поставить на пол"))
                SnapToFloor(target);
            GUI.enabled = true;
            keepOnFloor = GUILayout.Toggle(keepOnFloor, " ставить на пол после поворота");
            GUILayout.EndHorizontal();
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

        // Строка поворота: кнопки крутят вокруг оси предмета (без «складывания» осей, как у
        // ползунков углов Эйлера), поле показывает и принимает угол напрямую.
        private Vector3 RotationRow(string key, string label, Vector3 euler, Vector3 axis, int component)
        {
            var step = RotationSteps[rotationStep];
            float? turn = null;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(16));
            if (GUILayout.Button("−90", GUILayout.Width(40))) turn = -90f;
            if (GUILayout.Button("−" + ItemTuningStore.F(step), GUILayout.Width(40))) turn = -step;
            var typed = NumberField(key, euler[component]);
            if (GUILayout.Button("+" + ItemTuningStore.F(step), GUILayout.Width(40))) turn = step;
            if (GUILayout.Button("+90", GUILayout.Width(40))) turn = 90f;
            GUILayout.EndHorizontal();

            if (turn.HasValue)
                return Euler(Quaternion.Euler(euler) * Quaternion.AngleAxis(turn.Value, axis));
            if (typed.HasValue)
                euler[component] = Angle(typed.Value);
            return euler;
        }

        // Строка числа: подпись, «−», ползунок, «+», поле для точного значения.
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

            var typed = NumberField(key, value);
            if (typed.HasValue)
                value = typed.Value;
            GUILayout.EndHorizontal();

            // Убираем хвосты вроде 0.30000001 от сложения шагов
            return (float)Math.Round(value, 4);
        }

        // Поле хранит введённый текст, пока в нём курсор, чтобы можно было набрать «-0.»
        private float? NumberField(string key, float value)
        {
            GUI.SetNextControlName(key);
            var focused = GUI.GetNameOfFocusedControl() == key;
            var shown = focused && textBuffers.TryGetValue(key, out var buffer) ? buffer : ItemTuningStore.F(value);
            var typed = GUILayout.TextField(shown, GUILayout.Width(70));
            if (!focused)
            {
                textBuffers.Remove(key);
                return null;
            }
            textBuffers[key] = typed;
            if (typed != shown && float.TryParse(typed.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return null;
        }

        private static float Angle(float a)
        {
            a = Mathf.Repeat(a + 180f, 360f) - 180f;
            return (float)Math.Round(a, 2);
        }

        private static Vector3 Euler(Quaternion q)
        {
            var e = q.eulerAngles;
            return new Vector3(Angle(e.x), Angle(e.y), Angle(e.z));
        }

        private static Vector3 Round(Vector3 v, int digits) =>
            new Vector3((float)Math.Round(v.x, digits), (float)Math.Round(v.y, digits), (float)Math.Round(v.z, digits));

        private static bool Same(ItemTuning a, ItemTuning b) =>
            a.PositionOffset == b.PositionOffset && a.RotationOffset == b.RotationOffset &&
            a.RestingRotation == b.RestingRotation && Mathf.Approximately(a.VerticalOffset, b.VerticalOffset) &&
            Mathf.Approximately(a.Scale, b.Scale);

        /// <summary>
        /// Пока окно открыто, клики и колесо мыши не должны доходить до игрока:
        /// иначе клик по ползунку стреляет из пистолета, ПКМ сканирует, а колесо листает слоты.
        /// </summary>
        [HarmonyPatch]
        internal static class BlockPlayerInput
        {
            private static readonly (Type type, string method)[] Handlers =
            {
                (typeof(PlayerControllerB), "ActivateItem_performed"),
                (typeof(PlayerControllerB), "ItemSecondaryUse_performed"),
                (typeof(PlayerControllerB), "ItemTertiaryUse_performed"),
                (typeof(PlayerControllerB), "Interact_performed"),
                (typeof(PlayerControllerB), "Discard_performed"),
                (typeof(PlayerControllerB), "ScrollMouse_performed"),
                (typeof(PlayerControllerB), "QEItemInteract_performed"),
                (typeof(PlayerControllerB), "InspectItem_performed"),
                (typeof(PlayerControllerB), "UseUtilitySlot_performed"),
                (typeof(PlayerControllerB), "Jump_performed"),
                (typeof(PlayerControllerB), "Crouch_performed"),
                (typeof(HUDManager), "PingScan_performed"),
            };

            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var (type, name) in Handlers)
                {
                    var method = AccessTools.Method(type, name);
                    if (method != null)
                        yield return method;
                }
            }

            private static bool Prefix() => !IsOpen;
        }
    }
}
