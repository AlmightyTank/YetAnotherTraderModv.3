using System;
using System.Reflection;

namespace YetAnotherTraderMod.Client.Services
{
    public static class ReflectionValueReader
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

        public static object TryReadObject(object obj, string name)
        {
            if (obj == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var type = obj.GetType();

            while (type != null)
            {
                var prop = type.GetProperty(name, Flags);
                if (prop != null)
                {
                    return prop.GetValue(obj, null);
                }

                var field = type.GetField(name, Flags);
                if (field != null)
                {
                    return field.GetValue(obj);
                }

                type = type.BaseType;
            }

            return null;
        }

        public static string TryReadString(object obj, string name)
        {
            var value = TryReadObject(obj, name);
            return value == null ? null : value.ToString();
        }

        public static float? TryReadFloat(object obj, string name)
        {
            var value = TryReadObject(obj, name);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToSingle(value);
            }
            catch
            {
                return null;
            }
        }

        public static bool? TryReadBool(object obj, string name)
        {
            var value = TryReadObject(obj, name);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return null;
            }
        }
    }
}
