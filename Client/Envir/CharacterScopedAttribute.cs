using System;

namespace Client.Envir
{
    /// <summary>
    /// 标记「按角色保存」的配置项。
    ///
    /// 这些属性同时带 <see cref="ConfigPropertyIgnore"/>，因此**不会**写进 Zircon.ini；
    /// 改由 <see cref="CharacterConfigStore"/> 存到 `{角色名}-BigPatch.ini`。
    ///
    /// 为什么要分开：`Config` 全是静态字段，Zircon.ini 是全局的，
    /// 同一个账号下不同角色（或同机不同账号）会互相覆盖配置。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class CharacterScopedAttribute : Attribute
    {
    }
}
