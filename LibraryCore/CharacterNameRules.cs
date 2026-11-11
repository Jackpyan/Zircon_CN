using System;
using System.Text.RegularExpressions;

namespace Library
{
    /// <summary>
    /// 角色名（以及复用同一规则的称号 / 联盟名 / 私聊收件人 / NPC 名 / 商店名）的取值规则。
    /// <para>
    /// **2026-11-10 起支持中文**（对齐 A 端 <c>mir3_cb</c> 的 <c>^([A-Za-z0-9]|[\u4e00-\u9fa5]){2,8}$</c>）：
    /// <list type="bullet">
    /// <item><b>含汉字</b>的名字：2~8 个字符（汉字算 1 个），照顾头顶名牌 / 排行榜的显示宽度；</item>
    /// <item><b>纯字母数字</b>的名字：沿用原来的 3~15 个字符，不收紧也不放宽。</item>
    /// </list>
    /// 客户端与服务端共享 <see cref="LibraryCore"/>，所以这里改一处两边同时生效。
    /// </para>
    /// <para>
    /// 常用汉字区 <c>\u4e00-\u9fa5</c>（20902 字），与 A 端一致；不含生僻字扩展区与 emoji。
    /// </para>
    /// </summary>
    public static class CharacterNameRules
    {
        /// <summary>含汉字的名字的最长字符数（与 A 端 mir3_cb 一致）。</summary>
        public const int MaxChineseNameLength = 8;

        /// <summary>汉字区（常用字 U+4E00~U+9FA5）。</summary>
        public const string ChineseRange = "\u4e00-\u9fa5";

        /// <summary>
        /// 角色名正则：
        /// <list type="bullet">
        /// <item>纯字母数字：3~15（<see cref="Globals.MinCharacterNameLength"/>~<see cref="Globals.MaxCharacterNameLength"/>）；</item>
        /// <item>含汉字：2~8（用前瞻强制「至少有一个汉字」，否则两字符纯英文名会混进来）。</item>
        /// </list>
        /// </summary>
        public static readonly Regex NameRegex = new Regex(
            @"^(?:[A-Za-z0-9]{" + Globals.MinCharacterNameLength + "," + Globals.MaxCharacterNameLength + "}" +
            @"|(?=[A-Za-z0-9\u4e00-\u9fa5]*[\u4e00-\u9fa5])[A-Za-z0-9\u4e00-\u9fa5]{2," + MaxChineseNameLength + "})$",
            RegexOptions.Compiled);

        /// <summary>名字里有没有汉字。</summary>
        public static bool HasChinese(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            foreach (char c in name)
            {
                if (c >= '\u4e00' && c <= '\u9fa5') return true;
            }

            return false;
        }

        /// <summary>这个名字允许的最大长度（含汉字 8 / 纯英文数字 15）。</summary>
        public static int MaxLength(string name) => HasChinese(name) ? MaxChineseNameLength : Globals.MaxCharacterNameLength;

        /// <summary>名字合不合法（客户端与服务端共用这一条判定）。</summary>
        public static bool IsValid(string name) => !string.IsNullOrEmpty(name) && NameRegex.IsMatch(name);

        /// <summary>
        /// 按名字内容截断到合法长度。给「撞名后加数字后缀」这类场景用：
        /// 后缀要占位置，所以先按「上限 − 后缀长度」截断，再拼后缀。
        /// </summary>
        public static string Truncate(string name, int suffixLength)
        {
            if (string.IsNullOrEmpty(name)) return name;

            int max = MaxLength(name);
            int keep = Math.Max(1, max - Math.Max(0, suffixLength));

            return name.Length <= keep ? name : name.Substring(0, keep);
        }
    }
}
