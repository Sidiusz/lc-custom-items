namespace CustomItems
{
    /// <summary>
    /// Язык текстов мода (конфиг: [General] Language = ru / en). Названия и подсказки
    /// предметов задаются в ItemRegistry (nameRu, toolTipsRu), здесь только фразы из кода.
    /// </summary>
    public static class Lang
    {
        public static bool Russian = true;

        public static string T(string en, string ru) => Russian ? ru : en;
    }
}
