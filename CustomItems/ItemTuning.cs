using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace CustomItems
{
    /// <summary>
    /// Значения, которые настраиваются в игре окном ItemTuner: как предмет держится в руке,
    /// как лежит на полу и его размер. Размер хранится множителем к масштабу префаба из ассета.
    /// </summary>
    internal class ItemTuning
    {
        public Vector3 PositionOffset;
        public Vector3 RotationOffset;
        public Vector3 RestingRotation;
        public float VerticalOffset;
        public float Scale = 1f;

        public ItemTuning Clone() => (ItemTuning)MemberwiseClone();

        public static ItemTuning FromItem(Item item, float scale) => new ItemTuning
        {
            PositionOffset = item.positionOffset,
            RotationOffset = item.rotationOffset,
            RestingRotation = item.restingRotation,
            VerticalOffset = item.verticalOffset,
            Scale = scale,
        };
    }

    /// <summary>
    /// Хранит и применяет настройки из BepInEx/config/SidiuszCustomItems.tuning.txt.
    /// Файл пишет кнопка «Сохранить» в окне настройки; при старте мод накладывает его
    /// поверх значений из ассетов Item. Строки предмета можно удалить руками, тогда
    /// вернутся значения из ассета.
    /// </summary>
    internal static class ItemTuningStore
    {
        private const string FileName = "SidiuszCustomItems.tuning.txt";

        private static string path;
        private static readonly Dictionary<string, ItemTuning> Saved = new Dictionary<string, ItemTuning>();
        private static readonly Dictionary<Item, ItemTuning> Originals = new Dictionary<Item, ItemTuning>();
        private static readonly Dictionary<Item, Vector3> BaseScales = new Dictionary<Item, Vector3>();
        private static readonly Dictionary<Item, float> Scales = new Dictionary<Item, float>();

        /// <summary>Предметы мода в порядке регистрации.</summary>
        public static readonly List<Item> Items = new List<Item>();

        public static string FilePath => path;

        public static void Load(string configDir)
        {
            path = Path.Combine(configDir, FileName);
            Saved.Clear();
            if (!File.Exists(path))
                return;

            try
            {
                Parse(File.ReadAllLines(path));
                Plugin.Log.LogInfo($"Настройки предметов из {FileName}: {Saved.Count}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Не удалось прочитать {path}: {e.Message}");
            }
        }

        /// <summary>Запоминает значения из ассета и накладывает сохранённые настройки, если они есть.</summary>
        public static void Register(Item item)
        {
            if (Originals.ContainsKey(item))
                return;
            Items.Add(item);
            BaseScales[item] = item.spawnPrefab.transform.localScale;
            Scales[item] = 1f;
            Originals[item] = ItemTuning.FromItem(item, 1f);

            if (Saved.TryGetValue(item.itemName, out var saved))
                Apply(item, saved);
        }

        public static ItemTuning Current(Item item) => ItemTuning.FromItem(item, Scales[item]);
        public static ItemTuning Original(Item item) => Originals[item].Clone();
        public static ItemTuning SavedOrOriginal(Item item) =>
            Saved.TryGetValue(item.itemName, out var t) ? t.Clone() : Original(item);

        public static Vector3 PrefabScale(Item item, float scale) => BaseScales[item] * scale;

        /// <summary>
        /// Применяет значения к ассету Item и префабу, а также ко всем экземплярам этого
        /// предмета в мире: в руке игра перечитывает оффсеты каждый кадр, а лежащим на полу
        /// поворот и высоту поправляем сами.
        /// </summary>
        public static void Apply(Item item, ItemTuning t)
        {
            var previousVertical = item.verticalOffset;
            item.positionOffset = t.PositionOffset;
            item.rotationOffset = t.RotationOffset;
            item.restingRotation = t.RestingRotation;
            item.verticalOffset = t.VerticalOffset;

            Scales[item] = t.Scale;
            var scale = PrefabScale(item, t.Scale);
            item.spawnPrefab.transform.localScale = scale;

            foreach (var obj in UnityEngine.Object.FindObjectsOfType<GrabbableObject>())
            {
                if (obj.itemProperties != item)
                    continue;
                obj.transform.localScale = scale;
                obj.originalScale = scale;
                if (obj.isHeld || obj.isPocketed)
                    continue;
                var yaw = obj.transform.eulerAngles.y;
                obj.transform.rotation = Quaternion.Euler(t.RestingRotation.x, yaw, t.RestingRotation.z);
                obj.transform.position += Vector3.up * (t.VerticalOffset - previousVertical);
            }
        }

        public static void Save(Item item, ItemTuning t)
        {
            Saved[item.itemName] = t.Clone();
            Write();
        }

        /// <summary>Убирает предмет из файла: при следующем запуске будут значения из ассета.</summary>
        public static void Forget(Item item)
        {
            if (Saved.Remove(item.itemName))
                Write();
        }

        public static bool HasSaved(Item item) => Saved.ContainsKey(item.itemName);

        /// <summary>Текст для переноса значений в ассет Item и префаб в Unity.</summary>
        public static string ForAsset(Item item, ItemTuning t)
        {
            var s = PrefabScale(item, t.Scale);
            return $"{item.itemName} (Assets/Sources/Data/Items/{item.name}.asset)\n" +
                   $"  Position Offset: {Vec(t.PositionOffset)}\n" +
                   $"  Rotation Offset: {Vec(t.RotationOffset)}\n" +
                   $"  Resting Rotation: {Vec(t.RestingRotation)}\n" +
                   $"  Vertical Offset: {F(t.VerticalOffset)}\n" +
                   $"  Scale префаба {item.spawnPrefab.name}: {Vec(s)}";
        }

        private static void Write()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Настройки предметов из игрового окна настройки (SidiuszCustomItems).");
            sb.AppendLine("# Накладываются поверх ассетов Item при запуске. Удалите блок предмета,");
            sb.AppendLine("# чтобы вернуть значения из ассета. Scale — множитель к масштабу префаба.");
            foreach (var pair in Saved.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var t = pair.Value;
                sb.AppendLine();
                sb.AppendLine($"[{pair.Key}]");
                sb.AppendLine($"PositionOffset = {Csv(t.PositionOffset)}");
                sb.AppendLine($"RotationOffset = {Csv(t.RotationOffset)}");
                sb.AppendLine($"RestingRotation = {Csv(t.RestingRotation)}");
                sb.AppendLine($"VerticalOffset = {F(t.VerticalOffset)}");
                sb.AppendLine($"Scale = {F(t.Scale)}");
            }
            File.WriteAllText(path, sb.ToString());
        }

        private static void Parse(string[] lines)
        {
            ItemTuning current = null;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    current = new ItemTuning();
                    Saved[line.Substring(1, line.Length - 2)] = current;
                    continue;
                }

                var eq = line.IndexOf('=');
                if (current == null || eq < 0)
                    continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "PositionOffset": current.PositionOffset = ParseVec(value); break;
                    case "RotationOffset": current.RotationOffset = ParseVec(value); break;
                    case "RestingRotation": current.RestingRotation = ParseVec(value); break;
                    case "VerticalOffset": current.VerticalOffset = ParseFloat(value); break;
                    case "Scale": current.Scale = ParseFloat(value); break;
                }
            }
        }

        private static Vector3 ParseVec(string s)
        {
            var p = s.Split(',');
            return new Vector3(ParseFloat(p[0]), ParseFloat(p[1]), ParseFloat(p[2]));
        }

        private static float ParseFloat(string s) => float.Parse(s.Trim(), CultureInfo.InvariantCulture);

        internal static string F(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
        private static string Csv(Vector3 v) => $"{F(v.x)}, {F(v.y)}, {F(v.z)}";
        private static string Vec(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";
    }
}
