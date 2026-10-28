using Library;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Client.Envir
{
    /// <summary>
    /// 辅助功能（[BigPatch] 段里跟角色相关的部分）的按角色持久化。
    ///
    /// 背景：`Config` 是静态类，所有字段都写进全局的 `Zircon.ini`。
    /// 但「自动烈火」「挂机技能」「保护血量百分比」这类设置明显是**跟角色走**的，
    /// 放全局会导致：同一账号换个角色、或同一台机器换个账号，配置互相覆盖。
    ///
    /// 做法：
    /// - 这些属性在 `Config` 上同时标了 `[ConfigPropertyIgnore]`（不进 Zircon.ini）
    ///   和 `[CharacterScoped]`（由本类接管）；
    /// - 本类用反射读写，落到 `./{角色名}-BigPatch.ini`；
    /// - **必须在构造 GameScene / BigPatchDialog 之前调用 Load**，
    ///   否则界面控件会先按上一个角色的值建好。
    /// </summary>
    public static class CharacterConfigStore
    {
        private const string FileSuffix = "-BigPatch.ini";

        /// <summary>List 元素分隔符（正常文本里不会出现）。</summary>
        private const char ListSeparator = '\u0001';

        /// <summary>List 元素内部字段分隔符。</summary>
        private const char FieldSeparator = '\u0002';

        private static readonly PropertyInfo[] ScopedProperties = BuildPropertyList();

        /// <summary>当前已加载的角色名，用于避免重复加载 / 存错文件。</summary>
        public static string CurrentCharacter { get; private set; }

        private static PropertyInfo[] BuildPropertyList()
        {
            List<PropertyInfo> list = new List<PropertyInfo>();

            foreach (PropertyInfo property in typeof(Config).GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                if (property.GetCustomAttribute<CharacterScopedAttribute>() == null) continue;

                list.Add(property);
            }

            return list.ToArray();
        }

        public static string GetPath(string character)
        {
            if (string.IsNullOrWhiteSpace(character)) return null;

            foreach (char c in Path.GetInvalidFileNameChars())
                character = character.Replace(c, '_');

            return $"./{character}{FileSuffix}";
        }

        /// <summary>
        /// 载入指定角色的配置。文件不存在时保持当前值（即代码里的默认值）。
        /// </summary>
        public static void Load(string character)
        {
            // 切换角色前先把上一个角色的配置存下来
            // （离开游戏时 GameScene 不一定被 Dispose，这里兜底）
            if (!string.IsNullOrEmpty(CurrentCharacter) && CurrentCharacter != character)
                Save();

            CurrentCharacter = character;

            string path = GetPath(character);

            if (path == null) return;

            if (!File.Exists(path))
            {
                // 首次为该角色建文件：把旧的全局 Zircon.ini 里 [BigPatch] 的值迁移过来，
                // 避免升级后用户已配置的项全部回到默认值。
                MigrateFromGlobalIni();
                Save();
                return;
            }

            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string trimmed = line.Trim();

                    if (trimmed.Length == 0) continue;
                    if (trimmed[0] == '[' || trimmed[0] == ';') continue;

                    int split = trimmed.IndexOf('=');
                    if (split <= 0) continue;

                    ApplyEntry(trimmed.Substring(0, split).Trim(), trimmed.Substring(split + 1).Trim());
                }
            }
            catch (Exception ex)
            {
                CEnvir.SaveException(ex);
            }
        }

        private static void ApplyEntry(string key, string value)
        {
            foreach (PropertyInfo property in ScopedProperties)
            {
                if (property.Name != key) continue;

                try
                {
                    property.SetValue(null, ParseValue(property.PropertyType, value));
                }
                catch
                {
                    // 单个字段解析失败不影响其它字段
                }

                return;
            }
        }

        #region 旧全局配置迁移

        private static readonly Regex MagicRegex = new Regex(
            @"\[N:\s?(?<N>[A-Za-z0-9\u4e00-\u9fa5]{0,16}),\s?T:\s?(?<T>[0-9]{1,3}),\s?K:\s?(?<K>[0-9]{1,3}),\s?P:\s?(?<P>[0-9]{1,3}),\s?M:\s?(?<M>[0-9]{1,3}),\s?A:\s?(?<A>[0-9-]{1,3})\]",
            RegexOptions.Compiled);

        /// <summary>把 Zircon.ini 里 [BigPatch] 段的旧值搬过来（只做一次，文件已存在时不会执行）。</summary>
        private static void MigrateFromGlobalIni()
        {
            const string globalPath = "./Zircon.ini";

            if (!File.Exists(globalPath)) return;

            try
            {
                bool inSection = false;

                foreach (string line in File.ReadAllLines(globalPath))
                {
                    string trimmed = line.Trim();

                    if (trimmed.Length == 0) continue;

                    if (trimmed[0] == '[')
                    {
                        inSection = trimmed.Equals("[BigPatch]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inSection) continue;

                    int split = trimmed.IndexOf('=');
                    if (split <= 0) continue;

                    ApplyLegacyEntry(trimmed.Substring(0, split).Trim(),
                                     UnescapeIni(trimmed.Substring(split + 1).Trim()));
                }
            }
            catch (Exception ex)
            {
                CEnvir.SaveException(ex);
            }
        }

        private static string UnescapeIni(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            return value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        private static void ApplyLegacyEntry(string key, string value)
        {
            foreach (PropertyInfo property in ScopedProperties)
            {
                if (property.Name != key) continue;

                try
                {
                    property.SetValue(null, ParseLegacyValue(property.PropertyType, value));
                }
                catch
                {
                }

                return;
            }
        }

        private static object ParseLegacyValue(Type type, string value)
        {
            if (type == typeof(List<string>))
            {
                List<string> list = new List<string>();

                if (string.IsNullOrEmpty(value)) return list;

                foreach (string item in value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    list.Add(UnescapeListString(item));

                return list;
            }

            if (type == typeof(List<MagicHelper>))
            {
                List<MagicHelper> list = new List<MagicHelper>();

                if (string.IsNullOrEmpty(value)) return list;

                foreach (Match match in MagicRegex.Matches(value))
                {
                    list.Add(new MagicHelper
                    {
                        Name = match.Groups["N"].Value,
                        TypeID = (MagicType)int.Parse(match.Groups["T"].Value, CultureInfo.InvariantCulture),
                        Key = (SpellKey)int.Parse(match.Groups["K"].Value, CultureInfo.InvariantCulture),
                        LockPlayer = int.Parse(match.Groups["P"].Value, CultureInfo.InvariantCulture) > 0,
                        LockMonster = int.Parse(match.Groups["M"].Value, CultureInfo.InvariantCulture) > 0,
                        Amulet = int.Parse(match.Groups["A"].Value, CultureInfo.InvariantCulture),
                    });
                }

                return list;
            }

            return ParseValue(type, value);
        }

        /// <summary>还原 FormatStringList 里的 `\\` / `\\p` / `\\n` 转义。</summary>
        private static string UnescapeListString(string entry)
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

        /// <summary>
        /// 保存当前角色的配置。角色名未加载时直接返回。
        /// </summary>
        public static void Save()
        {
            string path = GetPath(CurrentCharacter);

            if (path == null) return;

            try
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("[BigPatch]");

                foreach (PropertyInfo property in ScopedProperties)
                {
                    object value = property.GetValue(null);

                    builder.Append(property.Name).Append('=').AppendLine(FormatValue(property.PropertyType, value));
                }

                File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                CEnvir.SaveException(ex);
            }
        }

        #region 类型转换

        private static string FormatValue(Type type, object value)
        {
            if (value == null) return string.Empty;

            if (type == typeof(bool)) return (bool)value ? "True" : "False";
            if (type == typeof(int)) return ((int)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(long)) return ((long)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(string)) return (string)value;
            if (type == typeof(MagicType)) return ((int)(MagicType)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(Point))
            {
                Point point = (Point)value;
                return $"{point.X},{point.Y}";
            }

            if (type == typeof(List<string>))
            {
                List<string> list = (List<string>)value;
                if (list == null || list.Count == 0) return string.Empty;

                StringBuilder builder = new StringBuilder();

                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) builder.Append(ListSeparator);

                    // 换行/分隔符替换掉，避免破坏格式
                    builder.Append((list[i] ?? string.Empty).Replace("\r", " ").Replace("\n", " "));
                }

                return builder.ToString();
            }

            if (type == typeof(List<MagicHelper>))
            {
                List<MagicHelper> list = (List<MagicHelper>)value;
                if (list == null || list.Count == 0) return string.Empty;

                StringBuilder builder = new StringBuilder();

                for (int i = 0; i < list.Count; i++)
                {
                    MagicHelper helper = list[i];
                    if (helper == null) continue;

                    if (builder.Length > 0) builder.Append(ListSeparator);

                    builder.Append(helper.Name ?? string.Empty).Append(FieldSeparator)
                           .Append((int)helper.TypeID).Append(FieldSeparator)
                           .Append((int)helper.Key).Append(FieldSeparator)
                           .Append(helper.LockPlayer ? 1 : 0).Append(FieldSeparator)
                           .Append(helper.LockMonster ? 1 : 0).Append(FieldSeparator)
                           .Append(helper.Amulet);
                }

                return builder.ToString();
            }

            return value.ToString();
        }

        private static object ParseValue(Type type, string value)
        {
            if (type == typeof(bool)) return string.Equals(value, "True", StringComparison.OrdinalIgnoreCase) || value == "1";
            if (type == typeof(int)) return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0;
            if (type == typeof(long)) return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0L;
            if (type == typeof(string)) return value;
            if (type == typeof(MagicType)) return int.TryParse(value, out int m) ? (MagicType)m : MagicType.None;

            if (type == typeof(Point))
            {
                string[] parts = value.Split(',');

                if (parts.Length != 2) return Point.Empty;
                if (!int.TryParse(parts[0], out int x)) return Point.Empty;
                if (!int.TryParse(parts[1], out int y)) return Point.Empty;

                return new Point(x, y);
            }

            if (type == typeof(List<string>))
            {
                List<string> list = new List<string>();

                if (string.IsNullOrEmpty(value)) return list;

                foreach (string item in value.Split(ListSeparator))
                    list.Add(item);

                return list;
            }

            if (type == typeof(List<MagicHelper>))
            {
                List<MagicHelper> list = new List<MagicHelper>();

                if (string.IsNullOrEmpty(value)) return list;

                foreach (string item in value.Split(ListSeparator))
                {
                    string[] fields = item.Split(FieldSeparator);

                    if (fields.Length < 6) continue;

                    list.Add(new MagicHelper
                    {
                        Name = fields[0],
                        TypeID = (MagicType)int.Parse(fields[1], CultureInfo.InvariantCulture),
                        Key = (SpellKey)int.Parse(fields[2], CultureInfo.InvariantCulture),
                        LockPlayer = fields[3] == "1",
                        LockMonster = fields[4] == "1",
                        Amulet = int.Parse(fields[5], CultureInfo.InvariantCulture),
                    });
                }

                return list;
            }

            return value;
        }

        #endregion
    }
}
