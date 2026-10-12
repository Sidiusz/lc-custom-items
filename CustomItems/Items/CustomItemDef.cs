using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Описание предмета. Всё, что в обычном моде настраивается в Unity на ассете Item,
    /// здесь задаётся в коде, а из Unity нужна только модель (и по желанию звуки/иконка).
    /// </summary>
    public class CustomItemDef
    {
        /// <summary>Название в игре (сканер, терминал, подсказки).</summary>
        public string Name;

        /// <summary>Имя префаба с моделью в AssetBundle.</summary>
        public string ModelAsset;

        /// <summary>Имя спрайта-иконки в AssetBundle (необязательно).</summary>
        public string IconAsset;

        /// <summary>
        /// Ванильный предмет, у которого берутся анимации рук, звуки подбора/броска,
        /// иконка (если своей нет) и аудио-микшер. Если не найден, берётся любой
        /// одноручный скрап.
        /// </summary>
        public string TemplateItem = "Rubber Ducky";

        public bool IsScrap = true;
        /// <summary>Значения как в Item: игра умножает их на 0.4.</summary>
        public int MinValue = 60;
        public int MaxValue = 150;
        public int DefaultRarity = 30;

        public bool DefaultInShop = false;
        public int DefaultPrice = 25;

        /// <summary>1.0 = 0 фунтов, 1.05 = 5 фунтов, 1.2 = 20 фунтов.</summary>
        public float Weight = 1.05f;
        public bool TwoHanded = false;
        public bool IsConductive = false;

        public string[] ToolTips = new string[0];

        /// <summary>Как предмет держится в руке и лежит на полу (подгоняется в игре).</summary>
        public Vector3 PositionOffset = Vector3.zero;
        public Vector3 RotationOffset = Vector3.zero;
        public Vector3 RestingRotation = Vector3.zero;
        public float VerticalOffset = 0.1f;

        /// <summary>Ставить true, если предмет что-то делает по ЛКМ и это должны видеть все.</summary>
        public bool SyncUse = true;
    }
}
