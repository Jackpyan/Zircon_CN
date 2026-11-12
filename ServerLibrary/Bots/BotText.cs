using Library;
using Library.SystemModels;
using Server.Envir;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace Server.Bots
{
    /// <summary>
    /// 假人文本本地化：把枚举翻成中文，别让后台/日志里漏出 <c>FireBall</c>、<c>Meat</c> 这种内部名。
    ///
    /// <para>为什么要单独一个类：这些名字有三个来源，写在一处才好维护 ——</para>
    /// <list type="bullet">
    /// <item><b>技能</b>：<see cref="MagicType"/> 枚举上**没有** <c>[Description]</c>，
    /// 权威中文名在 <c>System.db</c> 的 <see cref="MagicInfo.Name"/> 里（客户端技能面板显示的就是它），
    /// 所以查 <see cref="SEnvir.MagicInfoList"/>。</item>
    /// <item><b>物品类型</b>：<see cref="ItemType"/> 带 <c>[Description]</c>（如 <c>Meat</c> → 肉类），直接读特性。</item>
    /// <item><b>查不到</b>：原样返回枚举名 —— 宁可露出英文，也不能把名字变成空串。</item>
    /// </list>
    ///
    /// <para>⚠ <see cref="MagicInfoList"/> 由游戏主线程持有、后台线程也会读，
    /// 所以这里**不直接遍历** <c>Binding</c>，而是按集合实例缓存成字典：
    /// 换库（<c>MagicInfoList</c> 被替换）时自动重建。</para>
    /// </summary>
    public static class BotText
    {
        private static readonly object SyncRoot = new object();

        private static IList<MagicInfo> _cachedSource;
        private static Dictionary<MagicType, string> _magicNames = new Dictionary<MagicType, string>();

        private static Dictionary<ItemType, string> _itemNames;

        /// <summary>技能中文名。查不到就返回枚举名。</summary>
        public static string Magic(MagicType type)
        {
            if (type == MagicType.None) return "无";

            Dictionary<MagicType, string> map = MagicNames();

            return map.TryGetValue(type, out string name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : type.ToString();
        }

        /// <summary>物品类型中文名（读 <c>[Description]</c>）。查不到就返回枚举名。</summary>
        public static string Item(ItemType type)
        {
            if (_itemNames == null)
            {
                Dictionary<ItemType, string> map = new Dictionary<ItemType, string>();

                foreach (ItemType value in Enum.GetValues(typeof(ItemType)))
                {
                    MemberInfo member = typeof(ItemType).GetMember(value.ToString()).FirstOrDefault();
                    string description = member?.GetCustomAttribute<DescriptionAttribute>()?.Description;

                    // Nothing 是唯一没有 Description 的，按「无」处理，别在界面上露出英文。
                    if (string.IsNullOrWhiteSpace(description)) description = value == ItemType.Nothing ? "无" : value.ToString();

                    map[value] = description;
                }

                _itemNames = map;
            }

            return _itemNames.TryGetValue(type, out string name) ? name : type.ToString();
        }

        private static Dictionary<MagicType, string> MagicNames()
        {
            IList<MagicInfo> source = SEnvir.MagicInfoList?.Binding;

            // 快路径：没换库就直接用缓存（绝大多数调用走这里）。
            if (ReferenceEquals(source, _cachedSource) && _magicNames.Count > 0) return _magicNames;

            Dictionary<MagicType, string> map = new Dictionary<MagicType, string>();

            if (source != null)
            {
                lock (SyncRoot)
                {
                    // 双重检查：进锁前可能已经有别的线程建好了。
                    if (ReferenceEquals(source, _cachedSource) && _magicNames.Count > 0) return _magicNames;

                    // 拷贝一份再遍历 —— 主线程随时可能增删，直接 foreach 会抛「集合已修改」。
                    MagicInfo[] snapshot = source.ToArray();

                    foreach (MagicInfo info in snapshot)
                    {
                        if (info == null || string.IsNullOrWhiteSpace(info.Name)) continue;
                        if (map.ContainsKey(info.Magic)) continue; // 同类型多条时取第一条

                        map[info.Magic] = info.Name;
                    }

                    _magicNames = map;
                    _cachedSource = source;
                }
            }

            return map;
        }
    }
}
