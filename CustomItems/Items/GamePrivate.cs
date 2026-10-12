using System;
using System.Collections.Generic;
using System.Reflection;

namespace CustomItems.Items
{
    /// <summary>
    /// Закрытые поля и методы игры. Сборка видит их открытыми (публицированные GameLibs),
    /// но Mono в игре проверяет доступ и бросает FieldAccessException / MethodAccessException,
    /// поэтому к private-членам обращаемся только через отражение.
    /// </summary>
    public static class GamePrivate
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<(Type, string), FieldInfo> Fields = new Dictionary<(Type, string), FieldInfo>();
        private static readonly Dictionary<(Type, string), MethodInfo> Methods = new Dictionary<(Type, string), MethodInfo>();

        /// <summary>Значение закрытого поля; нет поля — <paramref name="fallback"/>.</summary>
        public static T Get<T>(object obj, string name, T fallback = default)
        {
            if (obj == null)
                return fallback;
            var field = Field(obj.GetType(), name);
            return field != null && field.GetValue(obj) is T value ? value : fallback;
        }

        /// <summary>Вызвать закрытый метод; false — метода нет.</summary>
        public static bool Call(object obj, string name, params object[] args)
        {
            if (obj == null)
                return false;
            var method = Method(obj.GetType(), name);
            if (method == null)
                return false;
            method.Invoke(obj, args);
            return true;
        }

        private static FieldInfo Field(Type type, string name)
        {
            if (Fields.TryGetValue((type, name), out var field))
                return field;
            for (var t = type; t != null && field == null; t = t.BaseType)
                field = t.GetField(name, Flags | BindingFlags.DeclaredOnly);
            return Fields[(type, name)] = field;
        }

        private static MethodInfo Method(Type type, string name)
        {
            if (Methods.TryGetValue((type, name), out var method))
                return method;
            for (var t = type; t != null && method == null; t = t.BaseType)
                method = t.GetMethod(name, Flags | BindingFlags.DeclaredOnly);
            return Methods[(type, name)] = method;
        }
    }
}
