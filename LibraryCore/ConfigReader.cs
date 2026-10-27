using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Library
{
    public static class ConfigReader
    {
        private static readonly Regex HeaderRegex = new Regex(@"^\[(?<Header>.+)\]$", RegexOptions.Compiled);
        private static readonly Regex EntryRegex = new Regex(@"^(?<Key>.*?)=(?<Value>.*)$", RegexOptions.Compiled);
        private static readonly Regex ColorRegex = new Regex(@"\[A:\s?(?<A>[0-9]{1,3}),\s?R:\s?(?<R>[0-9]{1,3}),\s?G:\s?(?<G>[0-9]{1,3}),\s?B:\s?(?<B>[0-9]{1,3})\]", RegexOptions.Compiled);

        /// <summary>
        /// MagicHelper 列表的单元素格式：`[N:名字,T:技能,K:按键,P:锁玩家,M:锁怪,A:符]`，与 A 端一致。
        /// </summary>
        private static readonly Regex MagicRegex = new Regex(@"\[N:\s?(?<N>[A-Za-z0-9\u4e00-\u9fa5]{0,16}),\s?T:\s?(?<T>[0-9]{1,3}),\s?K:\s?(?<K>[0-9]{1,3}),\s?P:\s?(?<P>[0-9]{1,3}),\s?M:\s?(?<M>[0-9]{1,3}),\s?A:\s?(?<A>[0-9-]{1,3})\]", RegexOptions.Compiled);

        public static readonly Dictionary<Type, object> ConfigObjects = new Dictionary<Type, object>();

        private static readonly Dictionary<Type, Dictionary<string, Dictionary<string, string>>> ConfigContents = new Dictionary<Type, Dictionary<string, Dictionary<string, string>>>();

        public static void Load(Assembly assembly)
        {
            Type[] types = assembly.GetTypes();

            foreach (Type type in types)
            {
                ConfigPath config = type.GetCustomAttribute<ConfigPath>();

                if (config == null) continue;

                object ob = null;
                if (!type.IsAbstract || !type.IsSealed)
                    ConfigObjects[type] = ob = Activator.CreateInstance(type);

                if (!config.Disabled)
                    ReadConfig(type, AdjustPath(config.Path, assembly), ob);
            }
        }
        public static void Save(Assembly assembly)
        {
            Type[] types = assembly.GetTypes();

            foreach (Type type in types)
            {
                ConfigPath config = type.GetCustomAttribute<ConfigPath>();

                if (config == null) continue;

                object ob = null;

                if (!type.IsAbstract || !type.IsSealed)
                    ob = ConfigObjects[type];

                if (!config.Disabled)
                    SaveConfig(type, AdjustPath(config.Path, assembly), ob);
            }
        }

        public static string AdjustPath(string originalPath, Assembly assembly)
        {
            var subFolder = GetSubFolder(assembly);
            originalPath = PlatformPath.Normalize(originalPath);

            if (!string.IsNullOrEmpty(subFolder))
            {
                return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PlatformPath.Normalize(subFolder), originalPath));
            }

            return PlatformPath.Resolve(originalPath);
        }

        private static string GetSubFolder(Assembly assembly)
        {
            string assemblyName = assembly.GetName().Name;

            if (assemblyName.StartsWith("Plugin.", StringComparison.OrdinalIgnoreCase))
            {
                return Globals.PluginPath(assemblyName);
            }

            return null;
        }

        private static void ReadConfig(Type type, string path, object ob)
        {
            if (!File.Exists(path)) return;

            PropertyInfo[] properties = type.GetProperties();

            Dictionary<string, Dictionary<string, string>> contents = ConfigContents[type] = new Dictionary<string, Dictionary<string, string>>();

            string[] lines = File.ReadAllLines(path);

            Dictionary<string, string> section = null;

            foreach (string line in lines)
            {
                Match match = HeaderRegex.Match(line);
                if (match.Success)
                {
                    section = new Dictionary<string, string>();
                    contents[match.Groups["Header"].Value] = section;
                    continue;
                }

                if (section == null) continue;

                match = EntryRegex.Match(line);

                if (!match.Success) continue;

                section[match.Groups["Key"].Value] = UnescapeSpecialCharacters(match.Groups["Value"].Value);
            }

            string lastSection = null;

            foreach (PropertyInfo property in properties)
            {
                ConfigSection config = property.GetCustomAttribute<ConfigSection>();

                if (config != null) lastSection = config.Section;

                if (lastSection == null) continue;

                ConfigPropertyIgnore ignore = property.GetCustomAttribute<ConfigPropertyIgnore>();

                if (ignore != null) continue;

                MethodInfo method = typeof(ConfigReader).GetMethod("Read", new[] { typeof(Type), typeof(string), typeof(string), property.PropertyType });

                property.SetValue(ob, method.Invoke(null, new[] { type, lastSection, property.Name, property.GetValue(ob) }));
            }
        }

        private static void SaveConfig(Type type, string path, object ob)
        {
            PropertyInfo[] properties = type.GetProperties();
            Dictionary<string, Dictionary<string, string>> contents = ConfigContents[type] = new Dictionary<string, Dictionary<string, string>>();

            string lastSection = null;

            foreach (PropertyInfo property in properties)
            {
                ConfigSection config = property.GetCustomAttribute<ConfigSection>();

                if (config != null) lastSection = config.Section;

                if (lastSection == null) continue;

                ConfigPropertyIgnore ignore = property.GetCustomAttribute<ConfigPropertyIgnore>();

                if (ignore != null) continue;

                MethodInfo method = typeof(ConfigReader).GetMethod("Write", new[] { typeof(Type), typeof(string), typeof(string), property.PropertyType });

                method.Invoke(ob, new[] { type, lastSection, property.Name, property.GetValue(ob) });
            }

            List<string> lines = new List<string>();

            foreach (KeyValuePair<string, Dictionary<string, string>> header in contents)
            {
                lines.Add($"[{header.Key}]");

                foreach (KeyValuePair<string, string> entries in header.Value)
                {
                    string escapedValue = EscapeSpecialCharacters(entries.Value);

                    lines.Add($"{entries.Key}={escapedValue}");
                }

                lines.Add(string.Empty);
            }

            if (!Directory.Exists(Path.GetDirectoryName(path)))
                Directory.CreateDirectory(Path.GetDirectoryName(path));

            File.WriteAllLines(path, lines, Encoding.Unicode);
        }

        private static string EscapeSpecialCharacters(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            return value.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
        private static string UnescapeSpecialCharacters(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            return value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        private static bool TryGetEntry(Type type, string section, string key, out string value)
        {
            value = null;
            Dictionary<string, Dictionary<string, string>> contents;
            Dictionary<string, string> entries;

            if (!ConfigContents.TryGetValue(type, out contents))
                ConfigContents[type] = contents = new Dictionary<string, Dictionary<string, string>>();

            if (contents.TryGetValue(section, out entries))
                return entries.TryGetValue(key, out value);

            entries = new Dictionary<string, string>();
            contents[section] = entries;

            return false;
        }

        #region Reads
        public static Boolean Read(Type type, string section, string key, Boolean value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                Boolean result;

                if (Boolean.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }

        public static Byte Read(Type type, string section, string key, Byte value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                Byte result;

                if (Byte.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }
        public static Int16 Read(Type type, string section, string key, Int16 value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                Int16 result;

                if (Int16.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }
        public static Int32 Read(Type type, string section, string key, Int32 value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                Int32 result;

                if (Int32.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }
        public static Int64 Read(Type type, string section, string key, Int64 value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                Int64 result;

                if (Int64.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }

        public static SByte Read(Type type, string section, string key, SByte value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                SByte result;

                if (SByte.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }
        public static UInt16 Read(Type type, string section, string key, UInt16 value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                UInt16 result;

                if (UInt16.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }
        public static UInt32 Read(Type type, string section, string key, UInt32 value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                UInt32 result;

                if (UInt32.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }
        public static UInt64 Read(Type type, string section, string key, UInt64 value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                UInt64 result;

                if (UInt64.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }

        public static Single Read(Type type, string section, string key, Single value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                Single result;

                if (Single.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString(CultureInfo.InvariantCulture);

            return value;
        }
        public static Double Read(Type type, string section, string key, Double value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                Double result;

                if (Double.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString(CultureInfo.InvariantCulture);

            return value;
        }
        public static Decimal Read(Type type, string section, string key, Decimal value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                Decimal result;

                if (Decimal.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString(CultureInfo.InvariantCulture);

            return value;
        }

        public static Char Read(Type type, string section, string key, Char value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                Char result;

                if (Char.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }
        public static String Read(Type type, string section, string key, String value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
                return entry;

            ConfigContents[type][section][key] = value;

            return value;
        }

        public static Point Read(Type type, string section, string key, Point value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                string[] data = entry.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                int x, y;

                if (data.Length == 2 && int.TryParse(data[0], out x) && int.TryParse(data[1], out y))
                    return new Point(x, y);
            }

            ConfigContents[type][section][key] = $"{value.X}, {value.Y}";

            return value;
        }
        public static Size Read(Type type, string section, string key, Size value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                string[] data = entry.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                int width, height;

                if (data.Length == 2 && int.TryParse(data[0], out width) && int.TryParse(data[1], out height))
                    return new Size(width, height);
            }

            ConfigContents[type][section][key] = $"{value.Width}, {value.Height}";

            return value;
        }
        public static SizeF Read(Type type, string section, string key, SizeF value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                string[] data = entry.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                float width, height;

                if (data.Length == 2 && float.TryParse(data[0], out width) && float.TryParse(data[1], out height))
                    return new SizeF(width, height);
            }

            ConfigContents[type][section][key] = $"{value.Width}, {value.Height}";

            return value;
        }

        public static TimeSpan Read(Type type, string section, string key, TimeSpan value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                TimeSpan result;

                if (TimeSpan.TryParse(entry, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString();

            return value;
        }
        public static DateTime Read(Type type, string section, string key, DateTime value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry))
            {
                DateTime result;

                if (DateTime.TryParse(entry, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                    return result;
            }

            ConfigContents[type][section][key] = value.ToString(CultureInfo.InvariantCulture);

            return value;
        }

        public static Color Read(Type type, string section, string key, Color value)
        {
            string entry;


            if (TryGetEntry(type, section, key, out entry))
            {
                Match match = ColorRegex.Match(entry);
                if (match.Success)
                {
                    int a = int.Parse(match.Groups["A"].Value);
                    int r = int.Parse(match.Groups["R"].Value);
                    int g = int.Parse(match.Groups["G"].Value);
                    int b = int.Parse(match.Groups["B"].Value);

                    return Color.FromArgb(
                        Math.Min(Byte.MaxValue, Math.Max(Byte.MinValue, a)),
                        Math.Min(Byte.MaxValue, Math.Max(Byte.MinValue, r)),
                        Math.Min(Byte.MaxValue, Math.Max(Byte.MinValue, g)),
                        Math.Min(Byte.MaxValue, Math.Max(Byte.MinValue, b)));
                }
            }

            ConfigContents[type][section][key] = $"[A:{value.A}, R:{value.R}, G:{value.G}, B:{value.B}]";

            return value;
        }

        /// <summary>
        /// 枚举配置项（如 MagicType）。以数值持久化，与 A 端格式一致。
        /// 注意：ConfigReader 按「属性类型」精确匹配 Read 重载，缺这个重载会在
        /// ReadConfig 里 method 为 null 抛 NullReferenceException，导致客户端启动即崩。
        /// </summary>
        public static MagicType Read(Type type, string section, string key, MagicType value)
        {
            string entry;

            if (TryGetEntry(type, section, key, out entry) && ushort.TryParse(entry, out ushort result))
                return (MagicType)result;

            ConfigContents[type][section][key] = ((ushort)value).ToString();

            return value;
        }

        /// <summary>
        /// 技能辅助配置列表（自动换符/换毒）。多元素以 `|` 分隔。
        /// 同样必须有精确类型的重载，否则启动即 NRE。
        /// </summary>
        public static List<MagicHelper> Read(Type type, string section, string key, List<MagicHelper> value)
        {
            string entry;

            if (!TryGetEntry(type, section, key, out entry))
            {
                ConfigContents[type][section][key] = FormatMagicHelpers(value);
                return value;
            }

            List<MagicHelper> list = new List<MagicHelper>();

            foreach (string item in entry.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match match = MagicRegex.Match(item);

                if (!match.Success) continue;

                list.Add(new MagicHelper
                {
                    Name = match.Groups["N"].Value,
                    TypeID = (MagicType)int.Parse(match.Groups["T"].Value),
                    Key = (SpellKey)int.Parse(match.Groups["K"].Value),
                    LockPlayer = int.Parse(match.Groups["P"].Value) > 0,
                    LockMonster = int.Parse(match.Groups["M"].Value) > 0,
                    Amulet = int.Parse(match.Groups["A"].Value),
                });
            }

            return list;
        }

        private static string FormatMagicHelpers(List<MagicHelper> value)
        {
            if (value == null || value.Count == 0) return string.Empty;

            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < value.Count; i++)
            {
                MagicHelper helper = value[i];

                if (helper == null) continue;

                if (builder.Length > 0) builder.Append('|');

                builder.AppendFormat("[N:{0},T:{1},K:{2},P:{3},M:{4},A:{5}]",
                    helper.Name, (int)helper.TypeID, (int)helper.Key,
                    helper.LockPlayer ? 1 : 0, helper.LockMonster ? 1 : 0, helper.Amulet);
            }

            return builder.ToString();
        }

        #endregion

        #region Writes
        public static void Write(Type type, string section, string key, Boolean value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }

        public static void Write(Type type, string section, string key, Byte value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }
        public static void Write(Type type, string section, string key, Int16 value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }
        public static void Write(Type type, string section, string key, Int32 value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }
        public static void Write(Type type, string section, string key, Int64 value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }

        public static void Write(Type type, string section, string key, SByte value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }
        public static void Write(Type type, string section, string key, UInt16 value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }
        public static void Write(Type type, string section, string key, UInt32 value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }
        public static void Write(Type type, string section, string key, UInt64 value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }

        public static void Write(Type type, string section, string key, Single value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString(CultureInfo.InvariantCulture);
        }
        public static void Write(Type type, string section, string key, Double value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString(CultureInfo.InvariantCulture);
        }
        public static void Write(Type type, string section, string key, Decimal value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString(CultureInfo.InvariantCulture);
        }

        public static void Write(Type type, string section, string key, Char value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }
        public static void Write(Type type, string section, string key, String value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value;
        }

        public static void Write(Type type, string section, string key, Point value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = $"{value.X}, {value.Y}";
        }
        public static void Write(Type type, string section, string key, Size value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = $"{value.Width}, {value.Height}";
        }

        public static void Write(Type type, string section, string key, TimeSpan value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString();
        }
        public static void Write(Type type, string section, string key, DateTime value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = value.ToString(CultureInfo.InvariantCulture);
        }
        public static void Write(Type type, string section, string key, Color value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = $"[A:{value.A}, R:{value.R}, G:{value.G}, B:{value.B}]";
        }

        /// <summary>枚举配置项，以数值持久化。</summary>
        public static void Write(Type type, string section, string key, MagicType value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = ((ushort)value).ToString();
        }

        /// <summary>技能辅助配置列表，多元素以 `|` 分隔。</summary>
        public static void Write(Type type, string section, string key, List<MagicHelper> value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = FormatMagicHelpers(value);
        }

        /// <summary>
        /// 字符串列表（如「自动喊话内容」）。多元素以 `|` 分隔，元素内的
        /// `\` `|` 换行 会被转义，避免破坏格式。
        /// 同样必须有精确类型的重载，否则 ConfigReader 反射取不到方法 → 启动 NRE。
        /// </summary>
        public static List<string> Read(Type type, string section, string key, List<string> value)
        {
            string entry;

            if (!TryGetEntry(type, section, key, out entry))
            {
                ConfigContents[type][section][key] = FormatStringList(value);
                return value ?? new List<string>();
            }

            List<string> list = new List<string>();

            if (string.IsNullOrEmpty(entry)) return list;

            foreach (string item in entry.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                list.Add(UnescapeStringListEntry(item));

            return list;
        }

        public static void Write(Type type, string section, string key, List<string> value)
        {
            if (!ConfigContents[type].ContainsKey(section)) ConfigContents[type][section] = new Dictionary<string, string>();

            ConfigContents[type][section][key] = FormatStringList(value);
        }

        private static string FormatStringList(List<string> value)
        {
            if (value == null || value.Count == 0) return string.Empty;

            StringBuilder builder = new StringBuilder();

            foreach (string item in value)
            {
                if (item == null) continue;

                if (builder.Length > 0) builder.Append('|');

                builder.Append(item.Replace("\\", "\\\\").Replace("|", "\\p").Replace("\r", string.Empty).Replace("\n", "\\n"));
            }

            return builder.ToString();
        }

        private static string UnescapeStringListEntry(string entry)
        {
            StringBuilder builder = new StringBuilder(entry.Length);

            for (int i = 0; i < entry.Length; i++)
            {
                char c = entry[i];

                if (c != '\\' || i + 1 >= entry.Length)
                {
                    builder.Append(c);
                    continue;
                }

                char next = entry[++i];

                switch (next)
                {
                    case 'p': builder.Append('|'); break;
                    case 'n': builder.Append('\n'); break;
                    case '\\': builder.Append('\\'); break;
                    default:
                        builder.Append(c);
                        builder.Append(next);
                        break;
                }
            }

            return builder.ToString();
        }
        #endregion
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class ConfigPath : Attribute
    {
        public string Path { get; set; }
        public bool Disabled { get; set; } // Skip the local ini file

        public ConfigPath(string path) : this(path, false) { }

        public ConfigPath(string path, bool disabled)
        {
            Path = path;
            Disabled = disabled;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class ConfigSection : Attribute
    {
        public string Section { get; set; }

        public ConfigSection(string section)
        {
            Section = section;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class ConfigPropertyIgnore : Attribute
    {
        public ConfigPropertyIgnore() { }
    }

}
