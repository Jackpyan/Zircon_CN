using Client.Controls;
using Client.Envir;
using Client.Models;
using Client.UserModels;
using Library;
using Library.SystemModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using C = Library.Network.ClientPackets;

namespace Client.Scenes.Views
{
    /// <summary>
    /// 辅助功能主窗口（按 A 键打开）。
    /// 移植自 A端 mir3_cb\zircon-legend-client\Client\Scenes\Views\BigPatchDialog.cs。
    ///
    /// 已完成：阶段 1（窗口骨架）、阶段 2（常用标签页）。
    /// 后续阶段按 辅助功能移植.md 逐个加入：辅助 / 保护 / 聊天 / 便签 / 记录 / 拾取 / 魔法 / 帮助。
    /// </summary>
    public sealed class BigPatchDialog : DXWindow
    {
        #region Properties

        public DXTabControl TabControl;

        public DXCommonlyTab Commonly;
        public DXPlayerHelperTab Helper;
        public DXProtectionTab Protect;
        public DXAutoPickItemTab AutoPick;
        public DXAnsweringTab Answering;
        public DXUserNoteBookTab NoteBook;
        public DXSystemMsgRecordTab MsgRecord;
        public DXMagicHelperTab Magic;
        public DXViewRangeObjectTab ViewRange;

        /// <summary>拾取过滤配置（按角色存盘）。角色数据就绪后才创建，见 EnsureAutoPickInitialized()。</summary>
        public CItemFilter PickFilter;

        public override WindowType Type => WindowType.BigPatchBox;
        public override bool CustomSize => false;
        public override bool AutomaticVisibility => true;

        #endregion

        public BigPatchDialog()
        {
            TitleLabel.Text = "辅助功能";
            HasFooter = false;

            // 与 A端保持一致：客户端区域 550 x 428
            SetClientSize(new Size(550, 428));

            TabControl = new DXTabControl
            {
                Parent = this,
                Location = ClientArea.Location,
                Size = ClientArea.Size,
            };

            Commonly = new DXCommonlyTab
            {
                Parent = TabControl,
                TabButton = { Label = { Text = "常用", Hint = "一些常用的功能设置" } },
            };

            Helper = new DXPlayerHelperTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "辅助", Hint = "游戏相关的一些自定义调整" } },
            };

            Protect = new DXProtectionTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "保护", Hint = "自动使用物品，以及遇到特殊情况的自动应对方案" } },
            };

            Answering = new DXAnsweringTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "聊天", Hint = "自动回复以及自动喊话" } },
            };

            NoteBook = new DXUserNoteBookTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "便签", Hint = "方便用户记录一些文本" } },
            };

            MsgRecord = new DXSystemMsgRecordTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "记录", Hint = "查看系统日志文件" } },
            };

            AutoPick = new DXAutoPickItemTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "拾取", Hint = "设置哪些物品需要自动拾取" } },
            };

            Magic = new DXMagicHelperTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "魔法", Hint = "自动换符 / 锁定目标设置" } },
            };

            ViewRange = new DXViewRangeObjectTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "帮助", Hint = "辅助功能使用说明" } },
            };
        }

        #region 外部同步入口

        /// <summary>
        /// 角色登录信息到达时调用：按职业显示对应的自动施法分组框。
        /// 移植自 A端 BigPatchDialog.cs:159-187。
        /// 注意：喝药链的同步已由 <see cref="DXProtectionTab.UpdateLinks"/> 单独负责。
        /// </summary>
        public void UpdateLinks(StartInformation info)
        {
            if (info == null) return;

            if (Helper != null)
            {
                Helper.Warrior.Visible = info.Class == MirClass.Warrior;
                Helper.Wizard.Visible = info.Class == MirClass.Wizard;
                Helper.Taoist.Visible = info.Class == MirClass.Taoist;
                Helper.Assassin.Visible = info.Class == MirClass.Assassin;
            }

            // 登录后重建技能缓存（战士高频技能 + 三个下拉框）
            Helper?.ResetSkillCache();
            Helper?.ReadySkillInfo();
            Helper?.UpdateMagic();
        }

        /// <summary>
        /// 角色数据变化（学新技能、升级等）时刷新依赖用户数据的控件。
        /// 移植自 A端 BigPatchDialog.cs:881-885。由 GameScene.UserChanged() 调用。
        /// </summary>
        public void UserChanged()
        {
            // 角色数据就绪后再处理喝药配置。
            // UserObject 构造期间 GameScene.Game.User 仍是 null，那时调用的 Protect.UpdateLinks()
            // 里 LoadLocalSettings() 取不到角色名会直接返回，导致：
            //   1) 按角色区分的 xxx-AutoPotion.ini 永远读不到；
            //   2) 换角色后也不会生成新角色的配置文件。
            // 这里在 User 赋值完成后再跑一次，并落盘一份，保证每个角色都有自己的文件。
            if (Protect != null)
            {
                Protect.UpdateLinks();
                Protect.SaveLocalSettings();
            }

            Helper?.ResetSkillCache();
            Helper?.ReadySkillInfo();
            Helper?.UpdateMagic();

            // 登录 / 换角色时强制关闭挂机。
            // Config.开始挂机 会随 Zircon.ini 持久化，若上次退出时仍在挂机，
            // 重新登录会立刻自动开打（A 端进入游戏时同样强制置 false）。
            if (Config.开始挂机)
            {
                Config.开始挂机 = false;
                Helper?.AndroidPlayer?.SetSilentState(false);
            }

            // 拾取过滤按角色存盘，文件名需要角色名 → 角色数据就绪后再初始化
            EnsureAutoPickInitialized();

            // 便签同样按角色存盘
            NoteBook?.BindUser(GameScene.Game?.User?.Name);

            // 魔法页：把已学技能同步到 Config.magics（自动换符的数据源）
            Magic?.UpdateMagic();
        }

        #region 拾取过滤

        /// <summary>
        /// 拾取过滤惰性初始化（文件名带角色名，必须等 User 就绪）。
        /// 由 UserChanged() 与 NeedPick() 调用。
        /// </summary>
        private void EnsureAutoPickInitialized()
        {
            if (PickFilter != null || AutoPick == null) return;

            string name = GameScene.Game?.User?.Name;
            string fileName = string.IsNullOrEmpty(name) ? "./PickupFilter.ini" : $"./{name}-PickupFilter.ini";

            // 旧版单一文件自动迁移到按角色文件
            try
            {
                if (!string.IsNullOrEmpty(name) && File.Exists("./PickupFilter.ini") && !File.Exists(fileName))
                    File.Move("./PickupFilter.ini", fileName);
            }
            catch
            {
                // 迁移失败不影响使用，退化为各自独立文件
            }

            PickFilter = new CItemFilter();
            PickFilter.Initialize(fileName);

            AutoPick.BindFilter(PickFilter);
        }

        /// <summary>该物品是否在白名单里（角色拾取或宠物拾取任一勾选）。</summary>
        public bool NeedPick(ItemInfo item)
        {
            if (item == null) return false;

            EnsureAutoPickInitialized();

            if (PickFilter != null && PickFilter.dictItems.TryGetValue(item.Index, out CItemFilterSet set))
                return set.pick || set.picks;

            return false;
        }

        /// <summary>
        /// 物品在地面上是否显示为白色。
        /// 移植自 A 端 ItemObject.IsWhiteItem：碎片、带附加属性、非普通品质都不算白色。
        /// ⚠ 用 Values.Count（条目数），不要用 Stats.Count（那是绝对值求和）。
        /// </summary>
        public static bool IsWhiteItem(ClientUserItem item)
        {
            if (item?.Info == null) return false;

            if (item.Info.ItemEffect == ItemEffect.ItemPart) return false;

            if (item.AddedStats?.Values != null && item.AddedStats.Values.Count > 0) return false;

            return item.Info.Rarity == Rarity.Common;
        }

        /// <summary>
        /// 按品质判断是否该捡：非白色物品。
        /// 金币/经验不参与品质判定（仍走白名单）。
        /// </summary>
        private static bool IsPickableByColour(ItemObject itemObject)
        {
            ClientUserItem item = itemObject?.Item;

            if (item?.Info == null) return false;

            // B 端没有 ItemEffect.Gold（金币走 CurrencyInfo），只需排除经验球
            if (item.Info.ItemEffect == ItemEffect.Experience) return false;

            return !IsWhiteItem(item);
        }

        /// <summary>该地面物品是否在白名单里（角色拾取）。</summary>
        private bool ShouldPick(ItemObject itemObject)
        {
            if (itemObject?.Item?.Info == null) return false;

            ItemInfo info = itemObject.Item.Info;
            int index = info.Index;

            // 碎片：地面物品用附加属性里的 ItemIndex 指代具体碎片
            if (info.ItemEffect == ItemEffect.ItemPart)
                index = itemObject.Item.AddedStats[Stat.ItemIndex];

            if (PickFilter != null && PickFilter.dictItems.TryGetValue(index, out CItemFilterSet set) && set.pick)
                return true;

            return Config.拾取非白色物品 && IsPickableByColour(itemObject);
        }

        /// <summary>
        /// 找到距离 <paramref name="center"/> 最近的可拾取地面物品。
        /// 供挂机 AI 判断「是否应该先去捡东西，而不是马上去打下一只怪」。
        /// </summary>
        public ItemObject FindPickableItem(Point center, int range)
        {
            GameScene scene = GameScene.Game;

            if (scene?.MapControl?.Objects == null) return null;

            EnsureAutoPickInitialized();

            if (PickFilter == null) return null;

            ItemObject best = null;
            int bestDistance = range + 1;

            foreach (MapObject ob in scene.MapControl.Objects)
            {
                if (ob == null || ob.Race != ObjectType.Item) continue;

                ItemObject itemObject = (ItemObject)ob;

                if (!ShouldPick(itemObject)) continue;

                int distance = Functions.Distance(center, itemObject.CurrentLocation);

                if (distance > range || distance >= bestDistance) continue;

                bestDistance = distance;
                best = itemObject;
            }

            return best;
        }

        /// <summary>
        /// 快速自动拾取：按拾取页的白名单 / 品质规则，**逐个指定格子**发送拾取包。
        ///
        /// 为什么需要服务端配合：B 端原本的 `C.PickUp` 无参，服务端会扫描半径内
        /// 第一件物品就捡，客户端无法指定目标 → 会把白装垃圾一起捡起来。
        /// 阶段 6 给 `C.PickUp` 加了 `Location`，这里就能精确指定了。
        ///
        /// 返回 true 表示本帧已发出一次拾取。
        /// </summary>
        public bool PickupItems()
        {
            GameScene scene = GameScene.Game;

            if (scene?.User == null || scene.Observer || scene.User.Dead) return false;
            if (scene.MapControl?.Cells == null) return false;
            if (CEnvir.Now < scene.PickUpTime) return false;

            EnsureAutoPickInitialized();

            if (PickFilter == null) return false;

            int radius = scene.User.Stats[Stat.PickUpRadius];
            Point user = scene.User.CurrentLocation;

            int minX = Math.Max(user.X - radius, 0);
            int maxX = Math.Min(user.X + radius, scene.MapControl.Width - 1);
            int minY = Math.Max(user.Y - radius, 0);
            int maxY = Math.Min(user.Y + radius, scene.MapControl.Height - 1);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    Cell cell = scene.MapControl.Cells[x, y];

                    if (cell?.Objects == null) continue;

                    foreach (MapObject ob in cell.Objects)
                    {
                        if (ob == null || ob.Race != ObjectType.Item) continue;

                        if (!ShouldPick((ItemObject)ob)) continue;

                        CEnvir.Enqueue(new C.PickUp { Location = new Point(x, y) });

                        // 节流：服务端一次只捡一件，给一点间隔避免刷包
                        scene.PickUpTime = CEnvir.Now.AddMilliseconds(300);

                        return true;
                    }
                }
            }

            return false;
        }

        #endregion

        /// <summary>
        /// 私聊自动回复。由 <see cref="GameScene.ReceiveChat(string, MessageType, List{ClientUserItem})"/>
        /// 在 <c>Config.自动回复</c> 开启时调用。移植自 A 端 BigPatchDialog.ReceiveChat。
        /// </summary>
        public void ReceiveChat(string message, MessageType type)
        {
            if (type != MessageType.WhisperIn && type != MessageType.GMWhisperIn) return;

            string reply = Answering?.SelectedReplyText;

            if (string.IsNullOrEmpty(reply) || string.IsNullOrEmpty(message)) return;

            // 私聊消息格式通常是 "名字=>内容"，取发送者名字回过去
            string[] parts = message.Split(new[] { '=', '>' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0) return;

            string name = parts[0].Trim();

            if (name.Length == 0) return;

            CEnvir.Enqueue(new C.Chat { Text = "/" + name + " " + reply });
        }

        /// <summary>
        /// 挂机剩余时间更新。移植自 A端 BigPatchDialog.cs:189-217。
        /// ⚠️ B端没有 S.AutoTimeChanged 包，本方法暂无人调用，
        /// 剩余时间标签会一直显示「0 小时 0 分钟 0 秒」，等阶段 6 补包后接入。
        /// </summary>
        public void OnTimerChanged(long autoTime)
        {
            if (Helper?.TimeLable == null) return;

            TimeSpan timeSpan = new TimeSpan(0, 0, (int)autoTime);
            Helper.TimeLable.Text = $"{timeSpan.Hours + timeSpan.Days * 24} 小时 {timeSpan.Minutes} 分钟 {timeSpan.Seconds} 秒";
        }

        #endregion

        #region 控件工厂

        /// <summary>
        /// 创建复选框。
        /// B端 DXCheckBox 与 A端的差异：
        /// 1. 没有 Text 属性，文字挂在 Label 上；
        /// 2. 没有 AutoSize，尺寸由 Label.DisplayArea + Box.DisplayArea 自动计算；
        /// 3. UpdateControl() 是 private，不需要也不允许手动调用；
        /// 4. 有 SetSilentState(bool) 可静默赋值，不触发 CheckedChanged（避免初始化时误写配置）。
        /// </summary>
        public static DXCheckBox CreateCheckBox(DXControl parent, string name, int x, int y, EventHandler<EventArgs> changed, bool @checked = false)
        {
            DXCheckBox box = new DXCheckBox
            {
                Parent = parent,
                Location = new Point(x, y),
            };

            box.Label.Text = name;
            box.SetSilentState(@checked);

            if (changed != null)
                box.CheckedChanged += changed;

            return box;
        }

        #endregion

        #region IDisposable

        protected override void Dispose(bool disposing)
        {
            // 必须在 base.Dispose 之前存盘：base 会销毁全部子控件，之后这些页签已不可用
            PickFilter?.Save();
            NoteBook?.Save();
            Answering?.SaveSaywords();

            base.Dispose(disposing);

            if (disposing)
            {
                Commonly = null;
                Helper = null;
                Protect = null;
                AutoPick = null;
                PickFilter = null;
                NoteBook = null;
                MsgRecord = null;
                ViewRange = null;
                Answering = null;
                Magic = null;

                if (TabControl != null)
                {
                    if (!TabControl.IsDisposed)
                        TabControl.Dispose();

                    TabControl = null;
                }
            }
        }

        #endregion

        #region 分组框控件（A端自定义，B端没有，随本文件移植）

        /// <summary>
        /// 带标题的分组框。
        /// 移植自 A端 BigPatchDialog.cs:1093-1150，但**边框绘制方式不同**：
        /// A端重写 UpdateBorderInformation 生成 5 点折线，让上边框在标题处断开（标题凸出在框顶）；
        /// B端 DXControl.DrawBorder() 只用 BorderInformation 是否为 null 作开关，固定绘制矩形四条边，
        /// 折线坐标被完全忽略（见 DXControl.cs:1787-1805）。
        /// 因此这里不再重写 UpdateBorderInformation，标题显示在框内左上角，无缺口。
        /// 这是渲染机制差异，不影响功能。
        /// </summary>
        public class DXGroupBox : DXControl
        {
            public DXLabel Name;

            public DXGroupBox()
            {
                Name = new DXLabel
                {
                    Parent = this,
                    Outline = true,
                    ForeColour = Color.FromArgb(70, 58, 35),
                    Location = new Point(5, 10),
                };

                BorderColour = Color.FromArgb(70, 58, 35);
                Border = true;
            }
        }

        #endregion

        #region 常用标签页

        /// <summary>
        /// 常用标签页。移植自 A端 BigPatchDialog.cs:1293-2105。
        /// 三列布局：常用 / 战斗 / 物品，下方为天气（隐藏）、鼠标中键、刷新包裹、特殊命令。
        /// </summary>
        public class DXCommonlyTab : DXTab
        {
            #region Properties

            public DXGroupBox GroupNormal;
            public DXGroupBox GroupWar;
            public DXGroupBox GroupItem;
            public DXGroupBox GroupWeather;
            public DXComboBox CombWeather;

            public DXLabel LabCommand;
            public DXComboBox CombCmdBox;
            public DXButton BtSubmit;

            public DXGroupBox GroupMouseMiddle;
            public DXCheckBox ChkCallMounts;
            public DXCheckBox ChkCastingMagic;
            public DXComboBox CombMiddleMouse;

            public DXButton RefreshBag;
            public DXButton ReloadConfig;

            public DXCheckBox ShowItemNames;
            public DXCheckBox ChkAutoPick;
            public DXCheckBox ChkTabPick;
            public DXCheckBox ChkAutoBook;

            #endregion

            private const int ColX = 15;      // 复选框左边距
            private const int RowHeight = 25; // 行间距

            public DXCommonlyTab()
            {
                Border = true;

                GroupNormal = CreateGroup("常用", new Size(120, 405));
                GroupWar = CreateGroup("战斗", new Size(120, 405));
                GroupItem = CreateGroup("物品", new Size(120, 405));

                // 天气分组在 A端就已被隐藏（选项全部注释掉），这里保留占位，待后续阶段接入
                GroupWeather = CreateGroup("天气", new Size(120, 32));
                GroupWeather.Visible = false;

                GroupMouseMiddle = CreateGroup("鼠标中键", new Size(120, 132));

                BuildNormalGroup();
                BuildWarGroup();
                BuildItemGroup();
                BuildWeatherGroup();
                BuildMouseMiddleGroup();
                BuildBottomButtons();
            }

            #region 分组框创建

            private DXGroupBox CreateGroup(string title, Size size)
            {
                DXGroupBox group = new DXGroupBox
                {
                    Parent = this,
                    Size = size,
                    Location = new Point(10, 0),
                };

                group.Name.Text = title;
                return group;
            }

            #endregion

            #region 常用

            private void BuildNormalGroup()
            {
                CHK_ITEM_SET[] items =
                {
                    new CHK_ITEM_SET("免蜡烛", Config.免蜡烛, (o, e) =>
                    {
                        Config.免蜡烛 = ((DXCheckBox)o).Checked;
                        // B端光照逻辑在 MapControl.LLayer（嵌套类 Light）上，A端是 MapControl.UpdateLights()
                        GameScene.Game?.MapControl?.LLayer?.UpdateLights();
                    }),
                    new CHK_ITEM_SET("名字显示", Config.ShowPlayerNames, (o, e) =>
                    {
                        Config.ShowPlayerNames = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("清理尸体", Config.清理尸体, (o, e) =>
                    {
                        Config.清理尸体 = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("在安全处有效", Config.在安全处有效, (o, e) =>
                    {
                        Config.在安全处有效 = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("Boss提示", Config.Boss提示, (o, e) =>
                    {
                        Config.Boss提示 = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("自动特修物品", Config.SpecialRepair, (o, e) =>
                    {
                        Config.SpecialRepair = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("跑不停", Config.跑不停, (o, e) =>
                    {
                        bool state = ((DXCheckBox)o).Checked;
                        Config.跑不停 = state;

                        if (GameScene.Game != null)
                            GameScene.Game.AutoRun = state;
                    }),
                    new CHK_ITEM_SET("怪物信息", Config.MonsterBoxVisible, (o, e) =>
                    {
                        Config.MonsterBoxVisible = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("自动关组", Config.自动关组, (o, e) =>
                    {
                        Config.自动关组 = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("怪物名字显示", Config.ShowMonsterNames, (o, e) =>
                    {
                        Config.ShowMonsterNames = ((DXCheckBox)o).Checked;
                    }),
                };

                int y = 30;
                foreach (CHK_ITEM_SET item in items)
                {
                    CreateCheckBox(GroupNormal, item.Name, ColX, y, item.Method, item.State);
                    y += 24;
                }

                GroupNormal.Size = new Size(GroupNormal.Size.Width, y);
            }

            #endregion

            #region 战斗

            private void BuildWarGroup()
            {
                CHK_ITEM_SET[] items =
                {
                    new CHK_ITEM_SET("免SHIFT", Config.免SHIFT, (o, e) =>
                    {
                        Config.免SHIFT = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("数字飘血", Config.ShowDamageNumbers, (o, e) =>
                    {
                        Config.ShowDamageNumbers = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("关闭经验提示", Config.关闭经验提示, (o, e) =>
                    {
                        Config.关闭经验提示 = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("Ctrl + 1-5 控制随从", Config.启用Ctrl数字控制宠物, (o, e) =>
                    {
                        Config.启用Ctrl数字控制宠物 = ((DXCheckBox)o).Checked;
                    }),
                };

                int y = 30;
                foreach (CHK_ITEM_SET item in items)
                {
                    CreateCheckBox(GroupWar, item.Name, ColX, y, item.Method, item.State);
                    y += RowHeight;
                }

                GroupWar.Size = new Size(GroupWar.Size.Width, y);
            }

            #endregion

            #region 物品

            private void BuildItemGroup()
            {
                int y = 30;

                ShowItemNames = CreateCheckBox(GroupItem, "物品名称显示", ColX, y,
                    (o, e) => Config.ShowItemNames = ShowItemNames.Checked, Config.ShowItemNames);
                y += RowHeight;

                // 「快速自动捡取」与「Tab捡取」互斥
                ChkAutoPick = CreateCheckBox(GroupItem, "快速自动捡取", ColX, y,
                    (o, e) => Config.快速自动拾取 = ChkAutoPick.Checked, Config.快速自动拾取);
                ChkAutoPick.CheckedChanged += (o, e) =>
                {
                    if (ChkAutoPick.Checked)
                        ChkTabPick.Checked = false;
                };
                y += RowHeight;

                ChkTabPick = CreateCheckBox(GroupItem, "Tab捡取", ColX, y,
                    (o, e) => Config.Tab捡取 = ChkTabPick.Checked, Config.Tab捡取);
                ChkTabPick.CheckedChanged += (o, e) =>
                {
                    if (ChkTabPick.Checked)
                        ChkAutoPick.Checked = false;
                };
                y += RowHeight;

                // A端原逻辑：勾选技能书时复用 Tab捡取 的互斥判断（历史遗留，无害，保持行为一致）
                ChkAutoBook = CreateCheckBox(GroupItem, "自动学习技能书", ColX, y,
                    (o, e) => Config.自动学习技能书 = ChkAutoBook.Checked, Config.自动学习技能书);
                ChkAutoBook.CheckedChanged += (o, e) =>
                {
                    if (ChkTabPick.Checked)
                        ChkAutoPick.Checked = false;
                };
                y += RowHeight;

                GroupItem.Size = new Size(GroupItem.Size.Width, y);
            }

            #endregion

            #region 天气（A端已隐藏，仅占位）

            private void BuildWeatherGroup()
            {
                CombWeather = new DXComboBox
                {
                    Parent = GroupWeather,
                    Location = new Point(35, 45),
                };

                // A端此处原本有「未选择 / 晴 / 雾 / 燃烧的雾 / 雪 / 花瓣雨 / 雨」等选项，
                // 但全部被注释掉，且 GroupWeather.Visible = false。这里同样不填选项，
                // 待后续阶段接入 WeatherSetting 时再启用。
                GroupWeather.Size = new Size(GroupWar.Size.Width, CombWeather.Size.Height + RowHeight * 2);
            }

            #endregion

            #region 鼠标中键

            private void BuildMouseMiddleGroup()
            {
                int y = 30;

                ChkCallMounts = CreateCheckBox(GroupMouseMiddle, "骑马", ColX, y, (o, e) =>
                {
                    if (ChkCallMounts.Checked)
                        ChkCastingMagic.Checked = !ChkCallMounts.Checked;

                    Config.是否开启鼠标中间按钮自动使用坐骑 = ChkCallMounts.Checked;
                }, Config.是否开启鼠标中间按钮自动使用坐骑);
                y += RowHeight;

                ChkCastingMagic = CreateCheckBox(GroupMouseMiddle, "魔法", ColX, y, (o, e) =>
                {
                    if (ChkCastingMagic.Checked)
                        ChkCallMounts.Checked = !ChkCastingMagic.Checked;

                    Config.是否开启鼠标中间按钮自动使用技能 = ChkCastingMagic.Checked;
                }, Config.是否开启鼠标中间按钮自动使用技能);

                CombMiddleMouse = new DXComboBox
                {
                    Parent = GroupMouseMiddle,
                };

                AddFKeyItems(CombMiddleMouse);
                CombMiddleMouse.SelectedItemChanged += (o, e) =>
                {
                    DXListBoxItem selected = CombMiddleMouse.ListBox.SelectedItem;
                    if (selected?.Item != null)
                        Config.鼠标中间按钮使用F几的技能 = (int)selected.Item;
                };
                CombMiddleMouse.ListBox.SelectItem(Config.鼠标中间按钮使用F几的技能);
                CombMiddleMouse.Size = new Size(50, 18);
                CombMiddleMouse.Location = new Point(ColX + ChkCastingMagic.DisplayArea.Right, y);

                y += RowHeight;
                GroupMouseMiddle.Size = new Size(GroupMouseMiddle.Size.Width, y + 10);
            }

            /// <summary>
            /// 向 ComboBox 填充「空 + F1 ~ F12」，Item 值：空 = 0，F1 = 1 … F12 = 12。
            /// </summary>
            private static void AddFKeyItems(DXComboBox combo)
            {
                new DXListBoxItem
                {
                    Parent = combo.ListBox,
                    Label = { Text = "空" },
                    Item = 0,
                };

                for (int i = 1; i <= 12; i++)
                {
                    new DXListBoxItem
                    {
                        Parent = combo.ListBox,
                        Label = { Text = "F" + i },
                        Item = i,
                    };
                }
            }

            #endregion

            #region 底部按钮与特殊命令

            private void BuildBottomButtons()
            {
                RefreshBag = new DXButton
                {
                    Parent = this,
                    Size = new Size(70, 18),
                    ButtonType = ButtonType.SmallButton,
                    Label = { Text = "刷新包裹" },
                };
                RefreshBag.MouseClick += (o, e) =>
                {
                    if (GameScene.Game == null || GameScene.Game.Observer) return;

                    // A端发的是 SortBagItem，B端等价物是 ItemSort（带 Grid 参数）
                    CEnvir.Enqueue(new C.ItemSort { Grid = GridType.Inventory });
                };

                ReloadConfig = new DXButton
                {
                    Parent = this,
                    Size = new Size(70, 18),
                    ButtonType = ButtonType.SmallButton,
                    Label = { Text = "重置设置" },
                };
                ReloadConfig.MouseClick += (o, e) => { };

                LabCommand = new DXLabel
                {
                    Parent = this,
                    Text = "特殊命令",
                };

                CombCmdBox = new DXComboBox
                {
                    Parent = this,
                    Size = new Size(100, 18),
                };

                new DXListBoxItem
                {
                    Parent = CombCmdBox.ListBox,
                    Label = { Text = "空" },
                    Item = null,
                };

                string[] commands =
                {
                    "@允许召唤",
                    "@队伍召唤",
                    "@宠物技能3",
                    "@宠物技能5",
                    "@宠物技能7",
                    "@宠物技能10",
                    "@宠物技能11",
                    "@宠物技能13",
                    "@宠物技能15",
                    "@允许交易",
                    "@允许加入行会",
                    "@退出行会",
                    "@属性提取",
                    "@摇骰子",
                };

                for (int i = 0; i < commands.Length; i++)
                {
                    new DXListBoxItem
                    {
                        Parent = CombCmdBox.ListBox,
                        Label = { Text = commands[i] },
                        Item = i,
                    };
                }

                CombCmdBox.ListBox.SelectItem(null);

                BtSubmit = new DXButton
                {
                    Parent = this,
                    Size = new Size(50, 16),
                    ButtonType = ButtonType.SmallButton,
                    Label = { Text = "执行" },
                };
                BtSubmit.MouseClick += (o, e) =>
                {
                    if (GameScene.Game == null || GameScene.Game.Observer) return;

                    string text = CombCmdBox?.SelectedLabel?.Text;
                    if (!string.IsNullOrEmpty(text) && text != "空")
                        CEnvir.Enqueue(new C.Chat { Text = text });
                };
            }

            #endregion

            #region 布局

            public override void OnSizeChanged(Size oValue, Size nValue)
            {
                base.OnSizeChanged(oValue, nValue);

                int colWidth = (Size.Width - 40) / 3;

                if (GroupNormal != null)
                {
                    GroupNormal.Location = new Point(10, 0);
                    GroupNormal.Size = new Size(colWidth, GroupNormal.Size.Height);
                }

                if (GroupWar != null && GroupNormal != null)
                {
                    GroupWar.Location = new Point(GroupNormal.Location.X + GroupNormal.Size.Width + 10, 0);
                    GroupWar.Size = new Size(colWidth, GroupWar.Size.Height);
                }

                if (GroupItem != null && GroupWar != null)
                {
                    GroupItem.Location = new Point(GroupWar.Location.X + GroupWar.Size.Width + 10, 0);
                    GroupItem.Size = new Size(colWidth, GroupItem.Size.Height);
                }

                if (GroupWeather != null && GroupWar != null)
                {
                    GroupWeather.Location = new Point(GroupWar.Location.X, GroupWar.Location.Y + GroupWar.Size.Height);
                    GroupWeather.Size = new Size(GroupWar.Size.Width, GroupWeather.Size.Height);
                }

                if (GroupMouseMiddle != null && GroupItem != null)
                {
                    GroupMouseMiddle.Location = new Point(GroupItem.Location.X, GroupItem.Location.Y + GroupItem.Size.Height + 10);
                    GroupMouseMiddle.Size = new Size(GroupItem.Size.Width, GroupMouseMiddle.Size.Height);
                }

                if (RefreshBag != null && GroupMouseMiddle != null)
                {
                    RefreshBag.Location = new Point(GroupMouseMiddle.Location.X + 5,
                        GroupMouseMiddle.Location.Y + GroupMouseMiddle.Size.Height + 10);
                }

                if (RefreshBag == null || ReloadConfig == null)
                    return;

                ReloadConfig.Location = new Point(RefreshBag.Location.X + RefreshBag.Size.Width + 20, RefreshBag.Location.Y);

                if (LabCommand == null || CombCmdBox == null || BtSubmit == null)
                    return;

                int cmdLabelY = RefreshBag.Location.Y + RefreshBag.Size.Height + 20;
                LabCommand.Location = new Point(RefreshBag.Location.X, cmdLabelY);

                int cmdComboY = cmdLabelY + LabCommand.Size.Height + 5;
                CombCmdBox.Location = new Point(RefreshBag.Location.X, cmdComboY);

                BtSubmit.Location = new Point(CombCmdBox.Location.X + CombCmdBox.Size.Width + 5, cmdComboY);
            }

            #endregion

            /// <summary>
            /// A端用一个结构体数组批量描述复选框，便于循环创建。
            /// </summary>
            public struct CHK_ITEM_SET
            {
                public string Name;
                public bool State;
                public EventHandler<EventArgs> Method;

                public CHK_ITEM_SET(string name, bool state, EventHandler<EventArgs> method)
                {
                    Name = name;
                    State = state;
                    Method = method;
                }
            }
        }

        #endregion

        #region 保护标签页

        /// <summary>
        /// 自动喝药的一行：物品格 + 血量% + 魔法% + 启用 + 上下移。
        ///
        /// 与 B端原有的顶层类 <see cref="Client.Scenes.Views.AutoPotionRow"/> 的关键差异：
        /// A端 UI 用**百分比**（0-100），发给服务端时按当前最大 HP/MP 换算成绝对值；
        /// B端原版直接用绝对值（0-50000）。本移植保留 A端的百分比方案，
        /// 并在本地 ini 缓存百分比，避免换装备后 maxHP 变化导致阈值失准。
        ///
        /// 注意：本类是 BigPatchDialog 的嵌套类，与命名空间级的 AutoPotionRow 不冲突。
        /// </summary>
        public sealed class AutoPotionRow : DXControl
        {
            #region Properties

            private ItemInfo _UseItem;
            private int _Index;

            public DXLabel IndexLabel;
            public DXLabel HealthLabel;
            public DXLabel ManaLabel;
            public DXItemCell ItemCell;
            public DXNumberBox HealthTargetBox;
            public DXNumberBox ManaTargetBox;
            public DXCheckBox EnabledCheckBox;
            public DXButton UpButton;
            public DXButton DownButton;

            public ItemInfo UseItem
            {
                get => _UseItem;
                set
                {
                    if (_UseItem == value) return;

                    ItemInfo oldValue = _UseItem;
                    _UseItem = value;

                    OnUseItemChanged(oldValue, value);
                }
            }
            public event EventHandler<EventArgs> UseItemChanged;
            public void OnUseItemChanged(ItemInfo oValue, ItemInfo nValue)
            {
                UseItemChanged?.Invoke(this, EventArgs.Empty);
            }

            public int Index
            {
                get => _Index;
                set
                {
                    if (_Index == value) return;

                    int oldValue = _Index;
                    _Index = value;

                    OnIndexChanged(oldValue, value);
                }
            }
            public event EventHandler<EventArgs> IndexChanged;
            public void OnIndexChanged(int oValue, int nValue)
            {
                IndexChanged?.Invoke(this, EventArgs.Empty);

                IndexLabel.Text = (Index + 1).ToString();
                ItemCell.Slot = Index;

                UpButton.Enabled = Index > 0;
                DownButton.Enabled = Index < Globals.MaxAutoPotionCount - 1;
            }

            #endregion

            public AutoPotionRow()
            {
                Size = new Size(260, 46);
                Border = true;
                BorderColour = Color.FromArgb(198, 166, 99);

                UpButton = new DXButton
                {
                    Index = 44,
                    LibraryFile = LibraryFile.Interface,
                    Location = new Point(5, 5),
                    Parent = this,
                    Enabled = false,
                };
                UpButton.MouseClick += (o, e) => SwapRow(-1);

                DownButton = new DXButton
                {
                    Index = 46,
                    LibraryFile = LibraryFile.Interface,
                    Location = new Point(5, 29),
                    Parent = this,
                };
                DownButton.MouseClick += (o, e) => SwapRow(1);

                // B端 DXItemCell 只有无参构造，尺寸由 GridType.AutoPotion 决定（A端是 new DXItemCell(38, 38)）
                ItemCell = new DXItemCell
                {
                    Parent = this,
                    Location = new Point(20, 5),
                    AllowLink = true,
                    FixedBorder = true,
                    Border = true,
                    GridType = GridType.AutoPotion,
                };

                IndexLabel = new DXLabel
                {
                    Parent = ItemCell,
                    Text = (Index + 1).ToString(),
                    Font = new Font(Config.FontName, CEnvir.FontSize(8F), FontStyle.Italic),
                    IsControl = false,
                    Location = new Point(-2, -1),
                };

                HealthTargetBox = new DXNumberBox
                {
                    Parent = this,
                    Location = new Point(105, 5),
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 100,
                    MinValue = 0,
                    UpButton = { Location = new Point(63, 1) },
                };
                HealthTargetBox.ValueTextBox.ValueChanged += (o, e) => SendUpdate();

                ManaTargetBox = new DXNumberBox
                {
                    Parent = this,
                    Location = new Point(105, 25),
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 100,
                    MinValue = 0,
                    UpButton = { Location = new Point(63, 1) },
                };
                ManaTargetBox.ValueTextBox.ValueChanged += (o, e) => SendUpdate();

                HealthLabel = new DXLabel
                {
                    Parent = this,
                    IsControl = false,
                    Text = "血量%:",
                };
                HealthLabel.Location = new Point(
                    HealthTargetBox.Location.X - HealthLabel.Size.Width,
                    HealthTargetBox.Location.Y + (HealthTargetBox.Size.Height - HealthLabel.Size.Height) / 2);

                ManaLabel = new DXLabel
                {
                    Parent = this,
                    IsControl = false,
                    Text = "魔法%:",
                };
                ManaLabel.Location = new Point(
                    ManaTargetBox.Location.X - ManaLabel.Size.Width,
                    ManaTargetBox.Location.Y + (ManaTargetBox.Size.Height - ManaLabel.Size.Height) / 2);

                EnabledCheckBox = new DXCheckBox
                {
                    Parent = this,
                    Label = { Text = "启用" },
                };
                EnabledCheckBox.CheckedChanged += (o, e) => SendUpdate();
                EnabledCheckBox.Location = new Point(Size.Width - EnabledCheckBox.Size.Width - 5, 5);
            }

            #region Methods

            /// <summary>
            /// 与相邻行交换内容（direction = -1 上移，+1 下移）。
            /// </summary>
            private void SwapRow(int direction)
            {
                DXProtectionTab tab = GameScene.Game?.BigPatchBox?.Protect;
                if (tab == null) return;

                int target = Index + direction;
                if (target < 0 || target >= tab.Rows.Length) return;

                AutoPotionRow other = tab.Rows[target];
                if (other == null) return;

                tab.Updating = true;

                ItemInfo info = ItemCell.QuickInfo;
                long hp = HealthTargetBox.Value;
                long mp = ManaTargetBox.Value;
                bool enabled = EnabledCheckBox.Checked;

                ItemCell.QuickInfo = other.ItemCell.QuickInfo;
                HealthTargetBox.Value = other.HealthTargetBox.Value;
                ManaTargetBox.Value = other.ManaTargetBox.Value;
                EnabledCheckBox.Checked = other.EnabledCheckBox.Checked;

                other.ItemCell.QuickInfo = info;
                other.HealthTargetBox.Value = hp;
                other.ManaTargetBox.Value = mp;
                other.EnabledCheckBox.Checked = enabled;

                tab.Updating = false;

                SendUpdate();
                other.SendUpdate();
            }

            /// <summary>
            /// 把 UI 上的百分比换算成绝对值发给服务端，并把百分比缓存到本地 ini。
            /// </summary>
            public void SendUpdate()
            {
                DXProtectionTab tab = GameScene.Game?.BigPatchBox?.Protect;
                if (tab == null || tab.Updating) return;
                if (GameScene.Game.Observer) return;

                // 最大 HP/MP 无效时不发送，避免算出 0 或负数阈值
                Stats stats = GameScene.Game.User?.Stats;
                if (stats == null) return;

                int maxHP = stats[Stat.Health];
                int maxMP = stats[Stat.Mana];
                if (maxHP <= 0 || maxMP <= 0) return;

                int absoluteHealth = (int)(maxHP * HealthTargetBox.Value / 100f);
                int absoluteMana = (int)(maxMP * ManaTargetBox.Value / 100f);

                if (absoluteHealth < 0 || absoluteHealth > maxHP) return;
                if (absoluteMana < 0 || absoluteMana > maxMP) return;

                CEnvir.Enqueue(new C.AutoPotionLinkChanged
                {
                    Slot = Index,
                    LinkIndex = ItemCell.Item?.Info.Index ?? -1,
                    Enabled = EnabledCheckBox.Checked,
                    Health = absoluteHealth,
                    Mana = absoluteMana,
                });

                tab.SaveLocalSettings();
            }

            #endregion

            #region IDisposable

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);

                if (!disposing) return;

                _UseItem = null;
                UseItemChanged = null;
                _Index = 0;
                IndexChanged = null;

                if (IndexLabel != null)
                {
                    if (!IndexLabel.IsDisposed) IndexLabel.Dispose();
                    IndexLabel = null;
                }

                if (HealthLabel != null)
                {
                    if (!HealthLabel.IsDisposed) HealthLabel.Dispose();
                    HealthLabel = null;
                }

                if (ManaLabel != null)
                {
                    if (!ManaLabel.IsDisposed) ManaLabel.Dispose();
                    ManaLabel = null;
                }

                if (ItemCell != null)
                {
                    if (!ItemCell.IsDisposed) ItemCell.Dispose();
                    ItemCell = null;
                }

                if (HealthTargetBox != null)
                {
                    if (!HealthTargetBox.IsDisposed) HealthTargetBox.Dispose();
                    HealthTargetBox = null;
                }

                if (ManaTargetBox != null)
                {
                    if (!ManaTargetBox.IsDisposed) ManaTargetBox.Dispose();
                    ManaTargetBox = null;
                }

                if (EnabledCheckBox != null)
                {
                    if (!EnabledCheckBox.IsDisposed) EnabledCheckBox.Dispose();
                    EnabledCheckBox = null;
                }

                if (UpButton != null)
                {
                    if (!UpButton.IsDisposed) UpButton.Dispose();
                    UpButton = null;
                }

                if (DownButton != null)
                {
                    if (!DownButton.IsDisposed) DownButton.Dispose();
                    DownButton = null;
                }
            }

            #endregion
        }

        /// <summary>
        /// 保护标签页：8 行自动喝药配置。移植自 A端 BigPatchDialog.cs:3344-3491。
        /// 与服务端的 AutoPotionLinkChanged 包、AutoPotionLink 表对接（B端均已存在，无需改服务端）。
        /// </summary>
        public class DXProtectionTab : DXTab
        {
            public ClientAutoPotionLink[] Links;
            public AutoPotionRow[] Rows;

            /// <summary>
            /// 隐藏基类 DXTab.Updating，避免与服务端同步时的重入判断互相干扰。
            /// TabControl 内部通过 DXTab 类型访问的是基类字段，不受影响。
            /// </summary>
            public new bool Updating;

            public DXProtectionTab()
            {
                Links = new ClientAutoPotionLink[Globals.MaxAutoPotionCount];
                Rows = new AutoPotionRow[Globals.MaxAutoPotionCount];

                for (int i = 0; i < Rows.Length; i++)
                {
                    Rows[i] = new AutoPotionRow
                    {
                        Parent = this,
                        Location = new Point(5, 5 + 50 * i),
                        Index = i,
                    };
                }
            }

            /// <summary>
            /// 服务端下发喝药链后同步到 UI。本地 ini 里已有百分比时优先用本地值
            /// （服务端只存绝对值，换装备后会失准），本地没有才用服务端绝对值反算百分比。
            /// </summary>
            public void UpdateLinks()
            {
                Updating = true;

                LoadLocalSettings();

                foreach (ClientAutoPotionLink link in Links)
                {
                    if (link == null || link.Slot < 0 || link.Slot >= Rows.Length) continue;

                    AutoPotionRow row = Rows[link.Slot];
                    if (row == null) continue;

                    row.ItemCell.QuickInfo = Globals.ItemInfoList.Binding.FirstOrDefault(x => x.Index == link.LinkInfoIndex);

                    if (row.HealthTargetBox.Value == 0 && row.ManaTargetBox.Value == 0)
                    {
                        int maxHP = GameScene.Game?.User?.Stats[Stat.Health] ?? 0;
                        int maxMP = GameScene.Game?.User?.Stats[Stat.Mana] ?? 0;

                        // 角色数据还没就绪时（UserObject 构造期间 GameScene.Game.User 仍为 null）
                        // 不能反算百分比：除数会退化成 1，算出 100% 并污染本地配置与服务端阈值。
                        // 等 GameScene.UserChanged() 里再调一次本方法，那时才有真实的 MaxHP/MaxMP。
                        if (maxHP > 0 && maxMP > 0)
                        {
                            long healthPercent = (long)(link.Health * 100f / maxHP);
                            long manaPercent = (long)(link.Mana * 100f / maxMP);

                            row.HealthTargetBox.Value = Math.Max(0, Math.Min(100, healthPercent));
                            row.ManaTargetBox.Value = Math.Max(0, Math.Min(100, manaPercent));
                        }
                    }

                    row.EnabledCheckBox.Checked = link.Enabled;
                }

                Updating = false;
            }

            /// <summary>
            /// 最大 HP/MP 变化时重发所有已启用的行，把百分比重新换算成新的绝对值阈值。
            /// </summary>
            public void ResendAllAutoPotionLinks()
            {
                if (Rows == null || GameScene.Game?.User == null) return;

                Stats stats = GameScene.Game.User.Stats;
                if (stats == null || stats[Stat.Health] <= 0 || stats[Stat.Mana] <= 0) return;

                foreach (AutoPotionRow row in Rows)
                {
                    if (row != null && row.EnabledCheckBox.Checked)
                        row.SendUpdate();
                }
            }

            #region 本地百分比缓存

            private const string LocalSettingsFileSuffix = "-AutoPotion.ini";

            private static string LocalSettingsPath
            {
                get
                {
                    string character = GameScene.Game?.User?.Name;
                    if (string.IsNullOrWhiteSpace(character)) return null;

                    // 角色名可能含文件系统非法字符，替换掉
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                        character = character.Replace(c, '_');

                    return $"./{character}{LocalSettingsFileSuffix}";
                }
            }

            public void SaveLocalSettings()
            {
                string path = LocalSettingsPath;
                if (path == null) return;

                try
                {
                    using (System.IO.StreamWriter writer = new System.IO.StreamWriter(path))
                    {
                        for (int i = 0; i < Rows.Length; i++)
                        {
                            if (Rows[i] == null) continue;

                            writer.WriteLine($"[Slot{i}]");
                            writer.WriteLine($"HealthPercent={Rows[i].HealthTargetBox.Value}");
                            writer.WriteLine($"ManaPercent={Rows[i].ManaTargetBox.Value}");
                        }
                    }
                }
                catch
                {
                    // 写失败不影响游戏，忽略
                }
            }

            public void LoadLocalSettings()
            {
                string path = LocalSettingsPath;
                if (path == null || !System.IO.File.Exists(path)) return;

                try
                {
                    int currentSlot = -1;

                    foreach (string line in System.IO.File.ReadAllLines(path))
                    {
                        string trimmed = line.Trim();

                        if (trimmed.StartsWith("[Slot") && trimmed.EndsWith("]"))
                        {
                            if (int.TryParse(trimmed.Substring(5, trimmed.Length - 6), out int slot))
                                currentSlot = slot;

                            continue;
                        }

                        if (currentSlot < 0 || currentSlot >= Rows.Length || Rows[currentSlot] == null) continue;

                        if (trimmed.StartsWith("HealthPercent="))
                        {
                            if (long.TryParse(trimmed.Substring(14), out long value))
                                Rows[currentSlot].HealthTargetBox.Value = value;
                        }
                        else if (trimmed.StartsWith("ManaPercent="))
                        {
                            if (long.TryParse(trimmed.Substring(12), out long value))
                                Rows[currentSlot].ManaTargetBox.Value = value;
                        }
                    }
                }
                catch
                {
                    // 读失败按无本地配置处理
                }
            }

            #endregion
        }

        #endregion

        #region 辅助标签页

        /// <summary>
        /// 辅助标签页：按职业分组的自动施法开关 + 自动技能 1/2 + 挂机设置。
        /// 移植自 A端 BigPatchDialog.cs:2106-3022。
        ///
        /// ⚠️ 阶段 4 只做「配置界面」，不含运行逻辑：
        ///   - A端每个开关还会发 C.AutoFightConfChanged 给服务端，本阶段**全部不发送**。
        ///     原因：B端服务端没有 Process(C.AutoFightConfChanged) 重载，收到未处理的包会
        ///     抛 NotImplementedException → BaseConnection 直接 Disconnecting=true 踢下线。
        ///     必须等阶段 6 服务端处理实现后，才能在同一版本里一起开启发送。
        ///   - A端的 AutoSkills()/AutoHealing()/PerformHardPause() 等 14 个运行方法、
        ///     以及 MapControl 里的挂机打怪 AI（约 300 行）属阶段 4b，本阶段不移植。
        ///     因此：勾选这些开关**不会真正自动施法或挂机**，只写 Config 并持久化。
        /// </summary>
        public class DXPlayerHelperTab : DXTab
        {
            #region Properties

            // 职业分组框：同一位置重叠，靠 Visible 只显示当前职业那个
            public DXGroupBox Warrior;
            public DXGroupBox Wizard;
            public DXGroupBox Taoist;
            public DXGroupBox Assassin;

            public DXGroupBox AutoSkill;
            public DXGroupBox Android;

            // 战士
            public DXCheckBox AutoFlamingSword;
            public DXCheckBox AutoDragobRise;
            public DXCheckBox AutoBladeStorm;
            public DXCheckBox AutoSwiftBlade;
            public DXCheckBox AutoThunderStrike;
            public DXCheckBox AutoDefiance;
            public DXCheckBox AutoMight;
            public DXCheckBox AutoReflectDamage;
            public DXCheckBox AutoEndurance;
            public DXCheckBox AutoHalfMoon;
            public DXCheckBox AutoDestructiveSurge;

            // 法师
            public DXCheckBox AutoMagicShield;
            public DXCheckBox AutoRenounce;
            public DXCheckBox AutoThunder;
            public DXCheckBox AutoWizardSkill;

            // 道士
            public DXCheckBox AutoPoisonDust;
            public DXCheckBox AutoAmulet;
            public DXCheckBox AutoCelestial;
            public DXCheckBox AutoTaoistSkill;
            public DXCheckBox AutoStrengthOfFaith;
            public DXCheckBox AutoLifeSteal;
            public DXCheckBox AutoMagicResistance;
            public DXCheckBox AutoResilience;
            public DXCheckBox AutoElementalSuperiority;
            public DXCheckBox AutoBloodLust;
            public DXCheckBox AutoKeepLife;

            // 刺客
            public DXCheckBox AutoFourFlowers;
            public DXCheckBox AutoEvasion;
            public DXCheckBox AutoRagingWind;

            // 自动技能 1 / 2
            public DXComboBox CombSkill1;
            public DXNumberBox NumbSkill1;
            public DXCheckBox AutoSkill_1;
            public DXComboBox CombSkill2;
            public DXNumberBox NumbSkill2;
            public DXCheckBox AutoSkill_2;

            public DXCheckBox ChkPauseAutoSkills;

            // 挂机
            public DXLabel TimeLable;
            public DXCheckBox AndroidPlayer;
            public DXCheckBox AndroidPoisonDust;
            public DXCheckBox AndroidEluded;
            public DXCheckBox AndroidBackCastle;
            public DXComboBox AndroidSkills;
            public DXCheckBox AndroidLongRange;
            public DXNumberBox AndroidCoordX;
            public DXNumberBox AndroidCoordY;
            public DXNumberBox AndroidCoordRange;
            public DXCheckBox AndroidLockRange;
            public DXNumberBox AndroidBackCastleMinPHValue;
            public DXCheckBox AndroidMinPHBackCastle;
            public DXNumberBox AndroidRandomMinPHValue;
            public DXCheckBox AndroidMinPHRandom;
            public DXNumberBox TimeBoxRandom;
            public DXCheckBox ChkAutoRandom;
            public DXNumberBox ExpTimeBoxRandom;
            public DXCheckBox ExpAutoRandom;

            // 挂机统计
            public DXLabel SummerLabelEx, SummerLabelKill, SummerLabelGold, SummerLabelHuntGold;
            public DXLabel SummerLabelExfen, SummerLabelKillfen, SummerLabelGoldfen, SummerLabelHuntGoldfen;
            public DXLabel Guajishijian;

            public int skilledCount;
            public decimal totalExperience, totalGold, totalHuntGold;
            private static DateTime LastTimes = DateTime.Now;

            #endregion

            private const int ColX = 15;       // 复选框左边距
            private const int Col2 = ColX + 120; // 第二列

            public DXPlayerHelperTab()
            {
                BuildClassGroups();
                BuildAutoSkillGroup();
                BuildAndroidGroup();

                // 职业分组框默认全部隐藏，由 BigPatchDialog.UpdateLinks 按职业打开其中一个
                Warrior.Visible = false;
                Wizard.Visible = false;
                Taoist.Visible = false;
                Assassin.Visible = false;

                ChkPauseAutoSkills = CreateCheckBox(this, "暂停以上全部辅助、定时施法", 15, 30,
                    (o, e) => Config.暂停辅助功能 = ChkPauseAutoSkills.Checked,
                    Config.暂停辅助功能);
                ChkPauseAutoSkills.Hint = "勾选后将暂停所有职业技能辅助功能和自动施法，但保留设置";
            }

            #region 分组框创建

            private DXGroupBox CreateGroup(string title)
            {
                DXGroupBox group = new DXGroupBox
                {
                    Parent = this,
                    Size = new Size(120, 32),
                    Location = new Point(10, 0),
                };

                group.Name.Text = title;
                return group;
            }

            #endregion

            #region 职业自动施法

            private void BuildClassGroups()
            {
                Warrior = CreateGroup("战士");
                Wizard = CreateGroup("法师");
                Taoist = CreateGroup("道士");
                Assassin = CreateGroup("刺客");

                int y = 30;

                // ---- 战士 ----
                AutoFlamingSword = CreateCheckBox(Warrior, "自动烈火", ColX, y,
                    (o, e) => Config.自动烈火 = AutoFlamingSword.Checked, Config.自动烈火);
                AutoDragobRise = CreateCheckBox(Warrior, "自动翔空", Col2, y,
                    (o, e) => Config.自动翔空 = AutoDragobRise.Checked, Config.自动翔空);

                y += 25;
                AutoBladeStorm = CreateCheckBox(Warrior, "自动莲月", ColX, y,
                    (o, e) => Config.自动莲月 = AutoBladeStorm.Checked, Config.自动莲月);

                y += 35;
                AutoMight = CreateCheckBox(Warrior, "自动破血", ColX, y,
                    (o, e) => Config.自动破血 = AutoMight.Checked, Config.自动破血);
                AutoDefiance = CreateCheckBox(Warrior, "自动铁布衫", Col2, y,
                    (o, e) => Config.自动铁布衫 = AutoDefiance.Checked, Config.自动铁布衫);

                y += 25;
                AutoEndurance = CreateCheckBox(Warrior, "自动金刚之躯", ColX, y,
                    (o, e) => Config.自动金刚之躯 = AutoEndurance.Checked, Config.自动金刚之躯);
                AutoReflectDamage = CreateCheckBox(Warrior, "自动移花接木", Col2, y,
                    (o, e) => Config.自动移花接木 = AutoReflectDamage.Checked, Config.自动移花接木);

                y += 35;
                AutoDestructiveSurge = CreateCheckBox(Warrior, "智能十方斩", ColX, y,
                    (o, e) => Config.自动十方斩 = AutoDestructiveSurge.Checked, Config.自动十方斩);
                AutoHalfMoon = CreateCheckBox(Warrior, "智能半月弯刀", Col2, y,
                    (o, e) => Config.自动半月弯刀 = AutoHalfMoon.Checked, Config.自动半月弯刀);

                y += 25;
                AutoSwiftBlade = CreateCheckBox(Warrior, "自动快刀斩马", ColX, y,
                    (o, e) => Config.自动快刀斩马 = AutoSwiftBlade.Checked, Config.自动快刀斩马);
                AutoThunderStrike = CreateCheckBox(Warrior, "自动天雷锤", Col2, y,
                    (o, e) => Config.自动天雷锤 = AutoThunderStrike.Checked, Config.自动天雷锤);

                // ---- 法师 ----
                y = 30;
                AutoMagicShield = CreateCheckBox(Wizard, "自动魔法盾", ColX, y,
                    (o, e) => Config.自动魔法盾 = AutoMagicShield.Checked, Config.自动魔法盾);

                y += 25;
                AutoRenounce = CreateCheckBox(Wizard, "自动凝血", ColX, y,
                    (o, e) => Config.自动凝血 = AutoRenounce.Checked, Config.自动凝血);

                y += 25;
                AutoThunder = CreateCheckBox(Wizard, "自动天打雷劈", ColX, y,
                    (o, e) => Config.自动天打雷劈 = AutoThunder.Checked, Config.自动天打雷劈);

                y += 25;
                AutoWizardSkill = CreateCheckBox(Wizard, "自动连续技能", ColX, y,
                    (o, e) => Config.自动法师连续技能 = AutoWizardSkill.Checked, Config.自动法师连续技能);

                // ---- 道士 ----
                y = 30;
                AutoPoisonDust = CreateCheckBox(Taoist, "自动换毒", ColX, y,
                    (o, e) => Config.自动换毒 = AutoPoisonDust.Checked, Config.自动换毒);
                AutoAmulet = CreateCheckBox(Taoist, "自动换符", Col2, y,
                    (o, e) => Config.自动换符 = AutoAmulet.Checked, Config.自动换符);

                y += 25;
                AutoCelestial = CreateCheckBox(Taoist, "自动阴阳盾", ColX, y,
                    (o, e) => Config.自动阴阳盾 = AutoCelestial.Checked, Config.自动阴阳盾);
                AutoTaoistSkill = CreateCheckBox(Taoist, "自动连续技能", Col2, y,
                    (o, e) => Config.自动道士连续技能 = AutoTaoistSkill.Checked, Config.自动道士连续技能);

                y += 25;
                AutoLifeSteal = CreateCheckBox(Taoist, "自动吸星大法", ColX, y,
                    (o, e) => Config.自动吸星大法 = AutoLifeSteal.Checked, Config.自动吸星大法);
                AutoMagicResistance = CreateCheckBox(Taoist, "自动施放幽灵盾", Col2, y,
                    (o, e) => Config.自动施放幽灵盾 = AutoMagicResistance.Checked, Config.自动施放幽灵盾);

                y += 25;
                AutoStrengthOfFaith = CreateCheckBox(Taoist, "有宠物时自动移花接玉", ColX, y,
                    (o, e) => Config.有宠物时自动移花接玉 = AutoStrengthOfFaith.Checked, Config.有宠物时自动移花接玉);

                y += 25;
                AutoResilience = CreateCheckBox(Taoist, "自动施放神圣战甲", ColX, y,
                    (o, e) => Config.自动施放神圣战甲术 = AutoResilience.Checked, Config.自动施放神圣战甲术);

                y += 25;
                AutoBloodLust = CreateCheckBox(Taoist, "自动给宠物施放猛虎强势", ColX, y,
                    (o, e) => Config.自动给宠物施放猛虎强势 = AutoBloodLust.Checked, Config.自动给宠物施放猛虎强势);

                y += 25;
                AutoElementalSuperiority = CreateCheckBox(Taoist, "自动施放强魔震法", ColX, y,
                    (o, e) => Config.自动强魔震法 = AutoElementalSuperiority.Checked, Config.自动强魔震法);

                y += 25;
                AutoKeepLife = CreateCheckBox(Taoist, "生命低于一半时自动施展秒影并恢复血量", ColX, y,
                    (o, e) => Config.自动施展秒影恢复血量 = AutoKeepLife.Checked, Config.自动施展秒影恢复血量);

                // ---- 刺客 ----
                y = 30;
                AutoFourFlowers = CreateCheckBox(Assassin, "自动四花", ColX, y,
                    (o, e) => Config.自动四花 = AutoFourFlowers.Checked, Config.自动四花);

                y += 25;
                AutoEvasion = CreateCheckBox(Assassin, "自动风之闪避", ColX, y,
                    (o, e) => Config.自动风之闪避 = AutoEvasion.Checked, Config.自动风之闪避);

                y += 25;
                AutoRagingWind = CreateCheckBox(Assassin, "自动风之守护", ColX, y,
                    (o, e) => Config.自动风之守护 = AutoRagingWind.Checked, Config.自动风之守护);
            }

            #endregion

            #region 自动技能 1 / 2

            private void BuildAutoSkillGroup()
            {
                AutoSkill = CreateGroup("自动技能");

                int y = 35;

                DXLabel label1 = new DXLabel
                {
                    Parent = AutoSkill,
                    Text = "技能Ⅰ",
                    Hint = "根据定义的时间间隔(秒单位)自动释放技能",
                };
                label1.Location = new Point(ColX, y);

                CombSkill1 = new DXComboBox
                {
                    Parent = AutoSkill,
                    Size = new Size(90, 18),
                    Location = new Point(label1.Location.X + label1.Size.Width + 5, y),
                };
                CombSkill1.SelectedItemChanged += (o, e) => OnAutoSkillSelected(1);

                NumbSkill1 = new DXNumberBox
                {
                    Parent = AutoSkill,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 50000,
                    MinValue = 0,
                    Value = Config.自动技能1多长时间使用一次,
                    UpButton = { Location = new Point(63, 1) },
                };
                NumbSkill1.ValueTextBox.ValueChanged += (o, e) => Config.自动技能1多长时间使用一次 = NumbSkill1.Value;
                NumbSkill1.Location = new Point(CombSkill1.Location.X + CombSkill1.Size.Width + 5, y);

                // A端此复选框手工创建：无文本、只有 Hint（B端 DXCheckBox 需用 Label.Text 设文字，
                // 但这里 A端本来就没有可见文字，故只设 Hint）
                AutoSkill_1 = new DXCheckBox
                {
                    Parent = AutoSkill,
                    Hint = "自动技能①",
                };
                AutoSkill_1.SetSilentState(Config.是否开启自动技能1);
                AutoSkill_1.Location = new Point(NumbSkill1.Location.X + NumbSkill1.Size.Width + 5, y + 2);
                AutoSkill_1.CheckedChanged += (o, e) => Config.是否开启自动技能1 = AutoSkill_1.Checked;

                int y2 = y + 25;

                DXLabel label2 = new DXLabel
                {
                    Parent = AutoSkill,
                    Text = "技能Ⅱ",
                    Hint = "根据定义的时间间隔(秒单位)自动释放技能",
                };
                label2.Location = new Point(ColX, y2);

                CombSkill2 = new DXComboBox
                {
                    Parent = AutoSkill,
                    Size = new Size(90, 18),
                    Location = new Point(label2.Location.X + label2.Size.Width + 5, y2),
                };
                CombSkill2.SelectedItemChanged += (o, e) => OnAutoSkillSelected(2);

                NumbSkill2 = new DXNumberBox
                {
                    Parent = AutoSkill,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 50000,
                    MinValue = 0,
                    Value = Config.自动技能2多长时间使用一次,
                    UpButton = { Location = new Point(63, 1) },
                };
                NumbSkill2.ValueTextBox.ValueChanged += (o, e) => Config.自动技能2多长时间使用一次 = NumbSkill2.Value;
                NumbSkill2.Location = new Point(CombSkill2.Location.X + CombSkill2.Size.Width + 5, y2);

                AutoSkill_2 = new DXCheckBox
                {
                    Parent = AutoSkill,
                    Hint = "自动技能②",
                };
                AutoSkill_2.SetSilentState(Config.是否开启自动技能2);
                AutoSkill_2.Location = new Point(NumbSkill2.Location.X + NumbSkill2.Size.Width + 5, y2 + 2);
                AutoSkill_2.CheckedChanged += (o, e) => Config.是否开启自动技能2 = AutoSkill_2.Checked;

                AutoSkill.Size = new Size(125, y2 + 25 + 5);
            }

            /// <summary>
            /// 选中技能后写入 Config，并把间隔抬到该技能的冷却时间（秒）。
            /// A端逻辑：仅当当前间隔小于技能 CD 时才抬高，不覆盖用户设的更大值。
            /// </summary>
            private void OnAutoSkillSelected(int index)
            {
                DXComboBox combo = index == 1 ? CombSkill1 : CombSkill2;
                DXNumberBox numb = index == 1 ? NumbSkill1 : NumbSkill2;

                if (!(combo.SelectedItem is MagicType magic) || magic == MagicType.None) return;

                foreach (MagicInfo info in Globals.MagicInfoList.Binding)
                {
                    if (info.Magic != magic) continue;

                    if (index == 1)
                        Config.自动技能1 = magic;
                    else
                        Config.自动技能2 = magic;

                    long seconds = info.Delay / 1000;
                    if (numb.Value < seconds)
                        numb.Value = seconds;

                    break;
                }
            }

            #endregion

            #region 挂机

            private void BuildAndroidGroup()
            {
                Android = CreateGroup("挂机");

                int x = ColX;
                int y = 30;

                // 剩余挂机时间
                DXLabel timeTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "剩时间:",
                    Outline = true,
                    Location = new Point(x, y),
                };

                TimeLable = new DXLabel
                {
                    Parent = Android,
                    Text = "0 小时 0 分钟 0 秒",
                    Outline = true,
                    Border = false,
                    ForeColour = Color.Cyan,
                    Location = new Point(timeTitle.Location.X + timeTitle.Size.Width - 8, y),
                };

                AndroidPlayer = CreateCheckBox(Android, "开始挂机", x + 170, y, OnHangupToggled, Config.开始挂机);

                // 注：A端此处会检查 User.AutoTime 与地图 AllowRT 后发包启动挂机 AI。
                // B端没有 User.AutoTime 字段，也没有挂机 AI（阶段 4b），故仅保留地图检查。

                y += 25;
                AndroidPoisonDust = CreateCheckBox(Android, "自动上毒", x, y,
                    (o, e) => Config.自动上毒 = AndroidPoisonDust.Checked, Config.自动上毒);
                AndroidEluded = CreateCheckBox(Android, "自动躲避", x + 85, y,
                    (o, e) => Config.自动躲避 = AndroidEluded.Checked, Config.自动躲避);
                AndroidBackCastle = CreateCheckBox(Android, "死亡回城", x + 170, y,
                    (o, e) => Config.死亡回城 = AndroidBackCastle.Checked, Config.死亡回城);

                y += 25;
                AndroidLongRange = CreateCheckBox(Android, "法系挂机", x, y,
                    (o, e) => Config.是否远战挂机 = AndroidLongRange.Checked, Config.是否远战挂机);

                DXLabel skillTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "技能",
                    Outline = true,
                    Location = new Point(x + 70, y),
                };

                AndroidSkills = new DXComboBox
                {
                    Parent = Android,
                    Size = new Size(120, 18),
                    Location = new Point(x + 70 + skillTitle.Size.Width + 5, y),
                };
                AndroidSkills.SelectedItemChanged += (o, e) =>
                {
                    DXListBoxItem selected = AndroidSkills.ListBox.SelectedItem;

                    if (selected?.Item == null)
                    {
                        // 选择了「不使用技能」
                        Config.远战挂机是否使用技能 = false;
                        Config.挂机自动技能 = MagicType.None;
                    }
                    else
                    {
                        Config.远战挂机是否使用技能 = true;
                        Config.挂机自动技能 = (MagicType)selected.Item;
                    }
                };

                y += 40;
                DXLabel xTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "X坐标:",
                    Outline = true,
                    Location = new Point(x, y),
                };
                AndroidCoordX = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 1024,
                    MinValue = 1,
                    Value = Config.范围挂机坐标.X,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + xTitle.Size.Width, y),
                };
                AndroidCoordX.ValueTextBox.ValueChanged += (o, e) =>
                    Config.范围挂机坐标 = new Point((int)AndroidCoordX.Value, Config.范围挂机坐标.Y);

                DXLabel yTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "Y坐标:",
                    Outline = true,
                    Location = new Point(x + 120, y),
                };
                AndroidCoordY = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 1024,
                    MinValue = 1,
                    Value = Config.范围挂机坐标.Y,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + 120 + yTitle.Size.Width, y),
                };
                AndroidCoordY.ValueTextBox.ValueChanged += (o, e) =>
                    Config.范围挂机坐标 = new Point(Config.范围挂机坐标.X, (int)AndroidCoordY.Value);

                y += 25;
                DXLabel rangeTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "范围 :",
                    Outline = true,
                    Location = new Point(x, y),
                };
                AndroidCoordRange = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 1024,
                    MinValue = 1,
                    Value = Config.范围距离,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + rangeTitle.Size.Width + 5, y),
                };
                AndroidCoordRange.ValueTextBox.ValueChanged += (o, e) => Config.范围距离 = AndroidCoordRange.Value;

                AndroidLockRange = CreateCheckBox(Android, "范围挂机", x + 170, y,
                    (o, e) => Config.范围挂机 = AndroidLockRange.Checked, Config.范围挂机);

                // 回城保护
                y += 50;
                DXLabel hpTitle1 = new DXLabel
                {
                    Parent = Android,
                    Text = "血量低于 % :",
                    Outline = true,
                    Hint = "　　百分比值",
                    Location = new Point(x, y),
                };
                AndroidBackCastleMinPHValue = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 100,
                    MinValue = 1,
                    Value = Config.血量剩下百分之多少时自动回城,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + hpTitle1.Size.Width, y),
                };
                AndroidBackCastleMinPHValue.ValueTextBox.ValueChanged += (o, e) =>
                    Config.血量剩下百分之多少时自动回城 = AndroidBackCastleMinPHValue.Value;

                AndroidMinPHBackCastle = CreateCheckBox(Android, "回城保护", x + 170, y,
                    (o, e) => Config.是否开启回城保护 = AndroidMinPHBackCastle.Checked, Config.是否开启回城保护);

                // 随机保护
                y += 20;
                DXLabel hpTitle2 = new DXLabel
                {
                    Parent = Android,
                    Text = "血量低于 % :",
                    Outline = true,
                    Hint = "　　百分比值",
                    Location = new Point(x, y),
                };
                AndroidRandomMinPHValue = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 100,
                    MinValue = 1,
                    Value = Config.血量剩下百分之多少时自动随机,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + hpTitle2.Size.Width, y),
                };
                AndroidRandomMinPHValue.ValueTextBox.ValueChanged += (o, e) =>
                    Config.血量剩下百分之多少时自动随机 = AndroidRandomMinPHValue.Value;

                AndroidMinPHRandom = CreateCheckBox(Android, "随机保护", x + 170, y,
                    (o, e) => Config.是否开启随机保护 = AndroidMinPHRandom.Checked, Config.是否开启随机保护);

                // 定时随机
                y += 20;
                DXLabel intervalTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "每间隔(秒) :",
                    Outline = true,
                    Location = new Point(x, y),
                };
                TimeBoxRandom = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 10000,
                    MinValue = 5,
                    Value = Config.隔多少秒自动随机一次,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + intervalTitle.Size.Width, y),
                };
                TimeBoxRandom.ValueTextBox.ValueChanged += (o, e) => Config.隔多少秒自动随机一次 = TimeBoxRandom.Value;

                ChkAutoRandom = CreateCheckBox(Android, "自动随机", x + 170, y,
                    (o, e) => Config.是否开启每间隔自动随机 = ChkAutoRandom.Checked, Config.是否开启每间隔自动随机);
                ChkAutoRandom.CheckedChanged += (o, e) =>
                {
                    if (ChkAutoRandom.Checked)
                        ExpAutoRandom.Checked = false;
                };

                // 无经验随机
                y += 20;
                DXLabel expTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "无经验(秒) :",
                    Outline = true,
                    Location = new Point(x, y),
                };
                ExpTimeBoxRandom = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 10000,
                    MinValue = 5,
                    Value = Config.多少秒无经验或者未杀死目标自动随机,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + expTitle.Size.Width, y),
                };
                ExpTimeBoxRandom.ValueTextBox.ValueChanged += (o, e) =>
                    Config.多少秒无经验或者未杀死目标自动随机 = ExpTimeBoxRandom.Value;

                ExpAutoRandom = CreateCheckBox(Android, "自动随机", x + 170, y,
                    (o, e) => Config.是否开启指定时间无经验或者未杀死目标自动随机 = ExpAutoRandom.Checked,
                    Config.是否开启指定时间无经验或者未杀死目标自动随机);
                ExpAutoRandom.CheckedChanged += (o, e) =>
                {
                    if (ExpAutoRandom.Checked)
                        ChkAutoRandom.Checked = false;
                };

                BuildStatistics();
            }

            /// <summary>
            /// 「开始挂机」开关。
            /// 阶段 4：只写 Config + 保留地图限制检查，不启动挂机 AI、不发包。
            /// 阶段 4b 需在此处接入 MapControl 的挂机逻辑。
            /// </summary>
            private void OnHangupToggled(object sender, EventArgs e)
            {
                Config.开始挂机 = AndroidPlayer.Checked;

                if (!AndroidPlayer.Checked) return;

                // 挂机 AI 接管移动。B 端 ProcessInput 只在 AutoPathRoute == null 时才被调用，
                // 若此时存在服务端下发的自动寻路，必须先取消，否则挂机不会生效。
                GameScene.Game?.CancelAutoPath();

                MapInfo mapInfo = GameScene.Game?.MapControl?.MapInfo;
                if (mapInfo != null && !mapInfo.AllowRT)
                {
                    // 不允许挂机的地图，直接撤销勾选（用 SetSilentState 避免重入触发一次多余的「结束挂机」）
                    AndroidPlayer.SetSilentState(false);
                    Config.开始挂机 = false;
                    GameScene.Game.ReceiveChat("目前您在不允许使用自动打怪功能的地图，因此不能挂机", MessageType.System);
                }
            }

            #endregion

            #region 挂机统计

            private void BuildStatistics()
            {
                int x = ColX;

                SummerLabelKill = new DXLabel { Parent = Android, Location = new Point(x, 285), Text = "杀怪数量:0" };
                SummerLabelEx = new DXLabel { Parent = Android, Location = new Point(110, 285), Text = "获得经验:0" };
                SummerLabelHuntGold = new DXLabel { Parent = Android, Location = new Point(x, 305), Text = "获得赏金:0" };
                SummerLabelGold = new DXLabel { Parent = Android, Location = new Point(110, 305), Text = "获得金币:0" };
                SummerLabelKillfen = new DXLabel { Parent = Android, Location = new Point(x, 325), Text = "分钟杀怪:0" };
                SummerLabelExfen = new DXLabel { Parent = Android, Location = new Point(110, 325), Text = "分钟经验:0" };
                SummerLabelHuntGoldfen = new DXLabel { Parent = Android, Location = new Point(x, 345), Text = "分钟赏金:0" };
                SummerLabelGoldfen = new DXLabel { Parent = Android, Location = new Point(110, 345), Text = "分钟金币:0" };
                Guajishijian = new DXLabel { Parent = Android, Location = new Point(x, 365), Text = "统计时间:0 小时 0 分钟 0 秒" };

                DXButton resetButton = new DXButton
                {
                    Parent = Android,
                    Size = new Size(40, 18),
                    Location = new Point(210, 365),
                    ButtonType = ButtonType.SmallButton,
                    Label = { Text = "重置" },
                };
                resetButton.MouseClick += (o, e) => ResetStatistics();
            }

            private void ResetStatistics()
            {
                skilledCount = 0;
                totalExperience = 0;
                totalGold = 0;
                totalHuntGold = 0;

                SummerLabelKill.Text = "杀怪数量:0";
                SummerLabelEx.Text = "获得经验:0";
                SummerLabelHuntGold.Text = "获得赏金:0";
                SummerLabelGold.Text = "获得金币:0";
                SummerLabelKillfen.Text = "分钟杀怪:0";
                SummerLabelExfen.Text = "分钟经验:0";
                SummerLabelHuntGoldfen.Text = "分钟赏金:0";
                SummerLabelGoldfen.Text = "分钟金币:0";
                Guajishijian.Text = "统计时间:0 小时 0 分钟 0 秒";

                LastTimes = DateTime.Now;
            }

            /// <summary>
            /// 由 GameScene 在获得经验时调用。
            /// ⚠️ 阶段 4 尚未接入调用点，统计面板会一直显示 0，等阶段 4b 一起接。
            /// </summary>
            public void GainedExperience(decimal amount)
            {
                TimeSpan elapsed = DateTime.Now - LastTimes;
                int minutes = elapsed.Minutes + elapsed.Hours * 60 + 1;

                Guajishijian.Text = $"统计时间:{elapsed.Hours + elapsed.Days * 24} 小时 {elapsed.Minutes} 分钟 {elapsed.Seconds} 秒";

                totalExperience += amount;
                if (amount <= 0) return;

                skilledCount++;
                SummerLabelKill.Text = $"杀怪数量:{skilledCount:#,##0}";
                SummerLabelKillfen.Text = $"分钟杀怪:{skilledCount / minutes:#,##0}";
                SummerLabelEx.Text = $"获得经验:{totalExperience:#,##0}";
                SummerLabelExfen.Text = $"分钟经验:{totalExperience / minutes:#,##0}";
            }

            /// <summary>由 GameScene 在获得金币时调用（阶段 4b 接入）。</summary>
            public void GainedGold(decimal amount)
            {
                TimeSpan elapsed = DateTime.Now - LastTimes;
                int minutes = elapsed.Minutes + elapsed.Hours * 60 + 1;

                totalGold += amount;
                SummerLabelGold.Text = $"获得金币:{totalGold:#,##0}";
                SummerLabelGoldfen.Text = $"分钟金币:{totalGold / minutes:#,##0}";
            }

            /// <summary>由 GameScene 在获得赏金时调用（阶段 4b 接入）。</summary>
            public void GainedHuntGold(decimal amount)
            {
                TimeSpan elapsed = DateTime.Now - LastTimes;
                int minutes = elapsed.Minutes + elapsed.Hours * 60 + 1;

                totalHuntGold += amount;
                SummerLabelHuntGold.Text = $"获得赏金:{totalHuntGold:#,##0}";
                SummerLabelHuntGoldfen.Text = $"分钟赏金:{totalHuntGold / minutes:#,##0}";
            }

            #endregion

            #region 技能下拉框填充

            /// <summary>
            /// 用当前角色已学技能填充三个下拉框（自动技能1、自动技能2、挂机技能）。
            /// 必须由外部在登录完成 / 技能变化时调用，否则下拉框为空。
            /// 移植自 A端 BigPatchDialog.cs:2938-2991。
            /// </summary>
            public void UpdateMagic()
            {
                if (GameScene.Game?.User == null) return;

                // 先清空，避免重复调用导致选项累积（A端原版没清，会重复追加）
                ClearListBoxItems(CombSkill1);
                ClearListBoxItems(CombSkill2);
                ClearListBoxItems(AndroidSkills);

                new DXListBoxItem
                {
                    Parent = AndroidSkills.ListBox,
                    Label = { Text = "不使用技能" },
                    Item = null,
                };

                foreach (ClientUserMagic magic in GameScene.Game.User.Magics.Values)
                {
                    MagicInfo info = magic.Info;
                    if (info == null) continue;
                    if (info.School == MagicSchool.Passive || info.School == MagicSchool.None) continue;

                    new DXListBoxItem
                    {
                        Parent = CombSkill1.ListBox,
                        Label = { Text = info.Name },
                        Item = info.Magic,
                    };

                    new DXListBoxItem
                    {
                        Parent = CombSkill2.ListBox,
                        Label = { Text = info.Name },
                        Item = info.Magic,
                    };

                    // 开关型/被动型技能不能作为挂机技能（只会切换状态不产生攻击）
                    // 注意：不影响自动技能 1/2 下拉框，定时开关型技能是有意义的
                    if (MapControl.NonAutoSkillMagics.Contains(info.Magic)) continue;

                    new DXListBoxItem
                    {
                        Parent = AndroidSkills.ListBox,
                        Label = { Text = info.Name },
                        Item = info.Magic,
                    };
                }

                CombSkill1.ListBox.SelectItem(Config.自动技能1);
                CombSkill2.ListBox.SelectItem(Config.自动技能2);

                // 旧配置里存的开关型技能无法作为挂机技能，加载时重置为「不使用技能」
                if (Config.挂机自动技能 != MagicType.None && MapControl.NonAutoSkillMagics.Contains(Config.挂机自动技能))
                {
                    Config.远战挂机是否使用技能 = false;
                    Config.挂机自动技能 = MagicType.None;
                }

                if (!Config.远战挂机是否使用技能 || Config.挂机自动技能 == MagicType.None)
                    AndroidSkills.ListBox.SelectItem(null);
                else
                    AndroidSkills.ListBox.SelectItem(Config.挂机自动技能);
            }

            /// <summary>
            /// 清空下拉框的列表项（只销毁 DXListBoxItem，保留 DXListBox 自带的滚动条）。
            ///
            /// ⚠ 绝对不能用 ListBox.Controls.Clear()：
            /// DXListBox 构造时把 ScrollBar 以 Parent = this 挂进了 Controls，
            /// Clear() 会把滚动条一起摘掉，滚动条变成「_Parent 仍指向 ListBox、但不在 Controls 里」的孤儿。
            /// 之后退出游戏销毁控件树时，DXListBox.Dispose 看到 ScrollBar 还没 IsDisposed，
            /// 又去调 ScrollBar.Dispose()，而此刻 parent.Controls 已被置 null，
            /// DXControl.Dispose 里的 parent?.Controls.Remove(this) 就会抛 NullReferenceException，
            /// 被 BaseConnection.Process 重抛后直接崩掉客户端。
            /// </summary>
            private static void ClearListBoxItems(DXComboBox combo)
            {
                if (combo == null) return;

                // 先断开选中引用，避免指向即将被销毁的项（此时项还活着，安全）
                combo.SelectedItem = null;

                DXListBox listBox = combo.ListBox;
                if (listBox == null) return;

                listBox.SelectedItem = null;

                // 快照后再销毁：item.Dispose() 会把自己从 Controls 里移除，不能直接遍历原集合
                foreach (DXControl control in listBox.Controls.ToArray())
                {
                    if (control is DXListBoxItem item && !item.IsDisposed)
                        item.Dispose();
                }

                listBox.UpdateScrollBar();
            }

            #endregion

            #region 自动施法运行逻辑（阶段 4b）

            // 战士高频技能缓存，避免每帧遍历 User.Magics
            private ClientUserMagic FlamingSword;
            private ClientUserMagic DragonRise;
            private ClientUserMagic BladeStorm;
            private ClientUserMagic SwiftBlade;
            private ClientUserMagic SeismicSlam;

            private DateTime AutoSkillsTime;
            private DateTime _ProtectTime;
            private DateTime _lastTownReviveTime;
            private bool _pausedAutoHangupForHeal;

            /// <summary>缓存战士几个高频技能。移植自 A 端 BigPatchDialog.cs:237-299。</summary>
            public void ReadySkillInfo()
            {
                GameScene scene = GameScene.Game;
                if (scene?.User == null) return;

                if (FlamingSword == null) FlamingSword = scene.GetMagic(MagicType.FlamingSword);
                if (DragonRise == null) DragonRise = scene.GetMagic(MagicType.DragonRise);
                if (BladeStorm == null) BladeStorm = scene.GetMagic(MagicType.BladeStorm);
                if (SwiftBlade == null) SwiftBlade = scene.GetMagic(MagicType.SwiftBlade);
                if (SeismicSlam == null) SeismicSlam = scene.GetMagic(MagicType.SeismicSlam);
            }

            /// <summary>切角色 / 学新技能后清空缓存。</summary>
            public void ResetSkillCache()
            {
                FlamingSword = null;
                DragonRise = null;
                BladeStorm = null;
                SwiftBlade = null;
                SeismicSlam = null;
            }

            /// <summary>
            /// 刺客「四花」连锁：开花 → 白莲 → 红莲 → 暗灭茶花。
            /// 移植自 A 端 BigPatchDialog.cs:219-233。
            /// </summary>
            public void CastFourFlowers()
            {
                GameScene scene = GameScene.Game;
                if (!Config.自动四花 || scene?.User == null || scene.User.Class != MirClass.Assassin) return;

                if (!scene.User.Buffs.Any(x =>
                    x.Type == BuffType.FullBloom || x.Type == BuffType.WhiteLotus || x.Type == BuffType.RedLotus))
                    scene.UseMagic(MagicType.FullBloom);

                if (scene.User.Buffs.Any(x => x.Type == BuffType.FullBloom))
                    scene.UseMagic(MagicType.WhiteLotus);

                if (scene.User.Buffs.Any(x => x.Type == BuffType.WhiteLotus))
                    scene.UseMagic(MagicType.RedLotus);

                if (scene.User.Buffs.Any(x => x.Type == BuffType.RedLotus))
                    scene.UseMagic(MagicType.SweetBrier);
            }

            /// <summary>
            /// 只有角色确实学会了该技能才施放，并返回是否放出去了。
            ///
            /// ⚠ 不能直接 `scene.UseMagic(...); return;`：
            /// 未学会时 UseMagic 是空操作，但 return 依然执行，
            /// 会让 AutoSkills 每次都在这里提前返回，把后面的技能全部饿死。
            /// 典型现象：战士勾了「自动铁布衫」但没学该技能时，「自动烈火」永远不生效。
            /// </summary>
            private static bool TryCast(GameScene scene, MagicType type, MapObject target = null)
            {
                if (scene?.User == null) return false;
                if (scene.GetMagic(type) == null) return false;

                scene.UseMagic(type, target);
                return true;
            }

            /// <summary>
            /// 自动施法主逻辑，按职业分发。移植自 A 端 BigPatchDialog.cs:300-630。
            /// </summary>
            public void AutoSkills()
            {
                if (Config.暂停辅助功能) return;

                GameScene scene = GameScene.Game;
                if (scene?.User == null) return;

                if (CEnvir.Now < AutoSkillsTime || CEnvir.Now < scene.User.NextMagicTime) return;
                if (scene.User.Horse != HorseType.None) return;

                AutoSkillsTime = CEnvir.Now.AddMilliseconds(300);

                switch (scene.User.Class)
                {
                    case MirClass.Warrior:
                        {
                            if (Config.自动铁布衫 && scene.User.Buffs.All(x => x.Type != BuffType.Defiance)
                                && TryCast(scene, MagicType.Defiance))
                                return;

                            if (Config.自动金刚之躯 && scene.User.Buffs.All(x => x.Type != BuffType.Endurance))
                            {
                                ClientUserMagic m = scene.GetMagic(MagicType.Endurance);
                                if (m != null && m.NextCast < CEnvir.Now)
                                {
                                    scene.UseMagic(m);
                                    return;
                                }
                            }

                            // 破血只在血量高于 80% 时用
                            if (Config.自动破血 && scene.User.Buffs.All(x => x.Type != BuffType.Might)
                                && scene.User.CurrentHP > scene.User.Stats[Stat.Health] * 0.8
                                && TryCast(scene, MagicType.Might))
                                return;

                            if (Config.自动移花接木 && scene.User.Buffs.All(x => x.Type != BuffType.ReflectDamage))
                            {
                                ClientUserMagic m = scene.GetMagic(MagicType.ReflectDamage);
                                if (m != null && m.NextCast < CEnvir.Now)
                                {
                                    scene.UseMagic(m);
                                    return;
                                }
                            }

                            // 莲月/烈火/翔空/快刀斩马/天雷锤只在战斗状态下执行
                            //
                            // ⚠ 三个蓄力型技能必须判断「是否已蓄力」：
                            // 服务端 FlamingSword/BladeStorm/DragonRise 的 Toggle() 在已蓄力时会回一条
                            // ChargeFail（「无法积蓄 XX 的能量」）。客户端若每 0.5 秒无脑重发，
                            // 聊天栏就会被这条提示刷屏。
                            if (IsInCombat())
                            {
                                if (Config.自动莲月 && BladeStorm != null && !scene.User.CanBladeStorm && CEnvir.Now > BladeStorm.NextCast)
                                    scene.UseMagic(MagicType.BladeStorm);
                                else if (Config.自动烈火 && FlamingSword != null && !scene.User.CanFlamingSword && CEnvir.Now > FlamingSword.NextCast)
                                    scene.UseMagic(MagicType.FlamingSword);
                                else if (Config.自动翔空 && DragonRise != null && !scene.User.CanDragonRise && CEnvir.Now > DragonRise.NextCast)
                                    scene.UseMagic(MagicType.DragonRise);
                                else if (Config.自动快刀斩马 && SwiftBlade != null && CEnvir.Now > SwiftBlade.NextCast)
                                {
                                    if (HasCurrentTarget() != null)
                                        scene.UseMagic(MagicType.SwiftBlade);
                                }
                                else if (Config.自动天雷锤 && SeismicSlam != null && CEnvir.Now > SeismicSlam.NextCast)
                                {
                                    int? distance = HasCurrentTarget();
                                    if (distance != null && distance < 4)
                                    {
                                        MapObject target = MapObject.TargetObject != null && scene.CanAttackTarget(MapObject.TargetObject)
                                            ? MapObject.TargetObject
                                            : scene.MouseObject;
                                        scene.UseMagic(MagicType.SeismicSlam, target);
                                    }
                                }
                            }

                            // 半月弯刀 / 十方斩：看周围可攻击目标数量
                            if ((Config.自动半月弯刀 || Config.自动十方斩) && IsInCombat(4))
                            {
                                int halfCount = 0;
                                int surgeCount = 0;
                                Point loc = scene.User.CurrentLocation;
                                MirDirection dir = scene.User.Direction;

                                if (CanAttack(Functions.Move(loc, dir))) halfCount++;
                                if (CanAttack(Functions.Move(loc, Functions.ShiftDirection(dir, -1)))) halfCount++;
                                if (CanAttack(Functions.Move(loc, Functions.ShiftDirection(dir, 1)))) halfCount++;
                                if (CanAttack(Functions.Move(loc, Functions.ShiftDirection(dir, 2)))) halfCount++;

                                if (Config.自动十方斩)
                                {
                                    if (CanAttack(Functions.Move(loc, Functions.ShiftDirection(dir, 3)))) surgeCount++;
                                    if (CanAttack(Functions.Move(loc, Functions.ShiftDirection(dir, 4)))) surgeCount++;
                                    if (CanAttack(Functions.Move(loc, Functions.ShiftDirection(dir, 5)))) surgeCount++;
                                    if (CanAttack(Functions.Move(loc, Functions.ShiftDirection(dir, 6)))) surgeCount++;
                                }

                                // B 端把 CanDestructiveBlow 改名为 CanDestructiveSurge
                                if (Config.自动十方斩 && surgeCount >= 3)
                                {
                                    if (!scene.User.CanDestructiveSurge)
                                        scene.UseMagic(MagicType.DestructiveSurge);
                                    else if (scene.User.CanThrusting)
                                        scene.UseMagic(MagicType.Thrusting);
                                }
                                else
                                {
                                    if (scene.User.CanDestructiveSurge)
                                        scene.UseMagic(MagicType.DestructiveSurge);
                                    else if (Config.自动半月弯刀 && halfCount >= 3)
                                    {
                                        if (!scene.User.CanHalfMoon)
                                            scene.UseMagic(MagicType.HalfMoon);
                                        else if (scene.User.CanThrusting)
                                            scene.UseMagic(MagicType.Thrusting);
                                    }
                                    else
                                    {
                                        if (scene.User.CanHalfMoon)
                                            scene.UseMagic(MagicType.HalfMoon);
                                        else if (!scene.User.CanThrusting)
                                            scene.UseMagic(MagicType.Thrusting);
                                    }
                                }
                            }

                            break;
                        }

                    case MirClass.Wizard:
                        {
                            if (Config.自动魔法盾 && scene.User.Buffs.All(x => x.Type != BuffType.MagicShield)
                                && TryCast(scene, MagicType.MagicShield))
                                return;

                            if (Config.自动凝血 && scene.User.Buffs.All(x => x.Type != BuffType.Renounce)
                                && TryCast(scene, MagicType.Renounce))
                                return;

                            if (Config.自动天打雷劈 && scene.User.Buffs.All(x => x.Type != BuffType.JudgementOfHeaven)
                                && TryCast(scene, MagicType.JudgementOfHeaven))
                                return;

                            // 自动法师连续技能：重复上一次成功施放的攻击法术
                            if (Config.自动法师连续技能
                                && scene.LastMagic != null
                                && (scene.CanAttackTarget(scene.LastTarget) || scene.CanAttackTarget(scene.MouseObject)))
                            {
                                MapObject target = scene.CanAttackTarget(scene.LastTarget) ? scene.LastTarget : scene.MouseObject;
                                if (!Functions.InRange(scene.User.CurrentLocation, target.CurrentLocation, Globals.MagicRange)) break;

                                MagicHelper helper = scene.GetMagicHelpper(scene.LastMagic.Info.Magic);
                                if (helper == null) break;
                                if (!helper.LockMonster && target.Race == ObjectType.Monster) break;
                                if (!helper.LockPlayer && target.Race == ObjectType.Player) break;

                                switch (scene.LastMagic.Info.Magic)
                                {
                                    case MagicType.FireBall:
                                    case MagicType.LightningBall:
                                    case MagicType.IceBolt:
                                    case MagicType.GustBlast:
                                    case MagicType.ElectricShock:
                                    case MagicType.AdamantineFireBall:
                                    case MagicType.ThunderBolt:
                                    case MagicType.IceBlades:
                                    case MagicType.Cyclone:
                                    case MagicType.ScortchedEarth:
                                    case MagicType.LightningBeam:
                                    case MagicType.FrozenEarth:
                                    case MagicType.BlowEarth:
                                    case MagicType.ExpelUndead:
                                    case MagicType.FireStorm:
                                    case MagicType.LightningWave:
                                    case MagicType.IceStorm:
                                    case MagicType.DragonTornado:
                                    case MagicType.GreaterFrozenEarth:
                                    case MagicType.ChainLightning:
                                    case MagicType.MeteorShower:
                                    case MagicType.Asteroid:
                                        scene.UseMagic(scene.LastMagic.Info.Magic, target);
                                        break;
                                }
                            }

                            break;
                        }

                    case MirClass.Taoist:
                        {
                            AutoHealing();

                            if (Config.自动阴阳盾 && scene.User.Buffs.All(x => x.Type != BuffType.CelestialLight)
                                && TryCast(scene, MagicType.CelestialLight))
                                return;

                            if (Config.自动强魔震法 && scene.User.Buffs.All(x => x.Type != BuffType.ElementalSuperiority)
                                && TryCast(scene, MagicType.ElementalSuperiority, scene.User))
                                return;

                            if (Config.有宠物时自动移花接玉 && scene.User.Buffs.All(x => x.Type != BuffType.StrengthOfFaith)
                                && scene.GetMagic(MagicType.StrengthOfFaith) != null)
                            {
                                foreach (MapObject ob in scene.MapControl.Objects)
                                {
                                    if (!(ob is MonsterObject mon) || mon.Dead || mon.PetOwner != scene.User.Name) continue;
                                    if (mon.CompanionObject != null) continue;

                                    scene.UseMagic(MagicType.StrengthOfFaith);
                                    return;
                                }
                            }

                            if (Config.自动吸星大法 && scene.User.Buffs.All(x => x.Type != BuffType.LifeSteal)
                                && TryCast(scene, MagicType.LifeSteal))
                                return;

                            if (Config.自动施放幽灵盾 && scene.User.Buffs.All(x => x.Type != BuffType.MagicResistance)
                                && TryCast(scene, MagicType.MagicResistance, scene.User))
                                return;

                            if (Config.自动施放神圣战甲术 && scene.User.Buffs.All(x => x.Type != BuffType.Resilience)
                                && TryCast(scene, MagicType.Resilience, scene.User))
                                return;

                            // 注：A 端此处还有「开始挂机时调 GameScene.AutoPoison()」，
                            // B 端没有 AutoPoison（挂机 AI 属后续阶段），暂不移植。

                            if (Config.自动施放幽灵盾 || Config.自动给宠物施放猛虎强势 || Config.自动施放神圣战甲术)
                            {
                                foreach (MapObject ob in scene.MapControl.Objects)
                                {
                                    if (!(ob is MonsterObject mon) || mon.Dead || mon.PetOwner != scene.User.Name) continue;
                                    if (mon.CompanionObject != null) continue;
                                    if (!Functions.InRange(scene.User.CurrentLocation, mon.CurrentLocation, Globals.MagicRange)) continue;

                                    // 注：A 端这里还有 SummonedTime + 4 秒稳定期检查，B 端无该字段，已省略。

                                    if (Config.自动施放幽灵盾 && !mon.VisibleBuffs.ContainsKey(BuffType.MagicResistance)
                                        && TryCast(scene, MagicType.MagicResistance, ob))
                                        return;

                                    if (Config.自动施放神圣战甲术 && !mon.VisibleBuffs.ContainsKey(BuffType.Resilience)
                                        && TryCast(scene, MagicType.Resilience, ob))
                                        return;

                                    if (Config.自动给宠物施放猛虎强势 && !mon.VisibleBuffs.ContainsKey(BuffType.BloodLust)
                                        && TryCast(scene, MagicType.BloodLust, ob))
                                        return;
                                }
                            }

                            if (scene.User.Buffs.Any(x => x.Type == BuffType.Transparency || x.Type == BuffType.Invisibility))
                                return;

                            // 自动道士连续技能
                            if (Config.自动道士连续技能
                                && scene.LastMagic != null
                                && (scene.CanAttackTarget(scene.LastTarget) || scene.CanAttackTarget(scene.MouseObject)))
                            {
                                MapObject target = scene.CanAttackTarget(scene.LastTarget) ? scene.LastTarget : scene.MouseObject;
                                if (!Functions.InRange(scene.User.CurrentLocation, target.CurrentLocation, Globals.MagicRange)) break;

                                MagicHelper helper = scene.GetMagicHelpper(scene.LastMagic.Info.Magic);
                                if (helper == null) break;
                                if (!helper.LockMonster && target.Race == ObjectType.Monster) break;
                                if (!helper.LockPlayer && target.Race == ObjectType.Player) break;

                                switch (scene.LastMagic.Info.Magic)
                                {
                                    case MagicType.Heal:
                                    case MagicType.ExplosiveTalisman:
                                    case MagicType.EvilSlayer:
                                    case MagicType.GreaterEvilSlayer:
                                    case MagicType.ImprovedExplosiveTalisman:
                                        scene.UseMagic(scene.LastMagic.Info.Magic, target);
                                        break;
                                }
                            }

                            break;
                        }

                    case MirClass.Assassin:
                        {
                            if (Config.自动风之闪避 && scene.User.Buffs.All(x => x.Type != BuffType.Evasion))
                                scene.UseMagic(MagicType.Evasion);

                            if (!Config.自动风之守护 || scene.User.Buffs.All(x => x.Type != BuffType.RagingWind))
                                return;

                            scene.UseMagic(MagicType.RagingWind);
                            break;
                        }
                }
            }

            /// <summary>
            /// 道士自动治疗：血量低于一半时隐身 + 群疗/治愈，并临时暂停挂机。
            /// 移植自 A 端 BigPatchDialog.cs:632-717。
            /// </summary>
            private void AutoHealing()
            {
                GameScene scene = GameScene.Game;
                if (scene?.User == null) return;

                // 血量恢复过半后自动解除临时暂停
                if (_pausedAutoHangupForHeal)
                {
                    if (scene.User.CurrentHP > scene.User.Stats[Stat.Health] * 0.5)
                    {
                        Config.开始挂机 = true;
                        AndroidPlayer?.SetSilentState(true);
                        _pausedAutoHangupForHeal = false;
                    }
                }

                if (!Config.自动施展秒影恢复血量) return;

                MapObject target = null;

                if (scene.User.CurrentHP < scene.User.Stats[Stat.Health] / 2)
                {
                    target = scene.User;
                }
                else
                {
                    foreach (MapObject ob in scene.MapControl.Objects)
                    {
                        if (!(ob is MonsterObject mon) || mon.Dead) continue;
                        if (mon.PetOwner != scene.User.Name || mon.CompanionObject != null) continue;

                        if (!scene.DataDictionary.TryGetValue(mon.ObjectID, out var monData)) continue;
                        if (monData.Health > 0 && monData.Health < monData.MaxHealth / 2)
                        {
                            target = mon;
                            break;
                        }
                    }
                }

                if (target == null) return;

                // 自己血量低于 50% 时临时暂停挂机
                if (target == scene.User)
                {
                    double hpRatio = (double)scene.User.CurrentHP / scene.User.Stats[Stat.Health];
                    if (hpRatio < 0.5 && !_pausedAutoHangupForHeal && Config.开始挂机)
                    {
                        scene.ReceiveChat("血量低于50，隐身治疗，并暂停挂机（强制停止行为）", MessageType.Hint);
                        Config.开始挂机 = false;
                        AndroidPlayer?.SetSilentState(false);
                        _pausedAutoHangupForHeal = true;
                        PerformHardPause();
                    }
                }

                if (scene.User.Buffs.All(x => x.Type != BuffType.Transparency))
                {
                    if (scene.GetMagic(MagicType.Transparency) != null)
                    {
                        scene.UseMagic(MagicType.Transparency, null);
                        return;
                    }
                }
                else if ((target == scene.User && scene.User.Buffs.All(x => x.Type != BuffType.Heal)) || target != scene.User)
                {
                    if (scene.GetMagic(MagicType.MassHeal) != null)
                    {
                        scene.UseMagic(MagicType.MassHeal, target);
                        return;
                    }

                    if (scene.GetMagic(MagicType.Heal) != null)
                        scene.UseMagic(MagicType.Heal, target);
                }
            }

            /// <summary>
            /// 强制停止玩家所有自动行为：清目标、取消寻路、清动作队列并站立。
            /// 移植自 A 端 BigPatchDialog.cs:719-751。
            /// B 端没有 MapControl.CurrentPath，改用 GameScene.CancelAutoPath()。
            /// </summary>
            private void PerformHardPause()
            {
                try
                {
                    MapObject.TargetObject = null;
                    MapObject.MouseObject = null;
                    MapObject.MagicObject = null;

                    GameScene scene = GameScene.Game;
                    scene?.CancelAutoPath();

                    if (MapObject.User != null)
                    {
                        MapObject.User.ActionQueue.Clear();
                        MapObject.User.NextActionTime = CEnvir.Now;
                        MapObject.User.SetAction(new ObjectAction(MirAction.Standing, MapObject.User.Direction, MapObject.User.CurrentLocation));
                    }

                    scene?.ReceiveChat("已强制停止挂机行为：目标/路径/动作队列已清除。", MessageType.Hint);
                }
                catch (Exception ex)
                {
                    CEnvir.SaveError(ex.ToString());
                }
            }

            /// <summary>指定格子附近是否有可攻击目标。移植自 A 端 BigPatchDialog.cs:752-765。</summary>
            public bool CanAttack(Point pi)
            {
                GameScene scene = GameScene.Game;
                if (scene?.MapControl == null) return false;

                if (pi.X < 0 || pi.X >= scene.MapControl.Width || pi.Y < 0 || pi.Y >= scene.MapControl.Height)
                    return false;

                List<MapObject> objects = scene.MapControl.Cells[pi.X, pi.Y].Objects;
                if (objects == null) return false;

                foreach (MapObject ob in objects)
                    if (scene.CanAttackTarget(ob))
                        return true;

                return false;
            }

            /// <summary>是否处于战斗状态。移植自 A 端 BigPatchDialog.cs:771-774。</summary>
            public bool IsInCombat(int seconds = 2)
            {
                GameScene scene = GameScene.Game;
                if (scene?.User == null) return false;

                return CEnvir.Now < scene.User.CombatTime.AddSeconds(seconds);
            }

            /// <summary>
            /// 是否有可攻击的当前目标，返回距离。移植自 A 端 BigPatchDialog.cs:780-799。
            /// </summary>
            public int? HasCurrentTarget()
            {
                GameScene scene = GameScene.Game;
                if (scene?.User == null) return null;

                MapObject target = null;

                if (MapObject.TargetObject != null && scene.CanAttackTarget(MapObject.TargetObject))
                    target = MapObject.TargetObject;
                else if (scene.MouseObject != null && scene.CanAttackTarget(scene.MouseObject))
                    target = scene.MouseObject;

                if (target == null) return null;

                return Functions.Distance(scene.User.CurrentLocation, target.CurrentLocation);
            }

            /// <summary>
            /// 每帧驱动的辅助总入口。移植自 A 端 BigPatchDialog.cs:801-877。
            /// 注：A 端此处还有 PickupItems（依赖拾取页 AutoPick，属阶段 5）与
            /// ExpAutoRandoms（逻辑可疑，见文档 5.9），本阶段均未移植。
            /// </summary>
            public void UpdateAutoAssist()
            {
                GameScene scene = GameScene.Game;
                if (scene?.User == null) return;

                CastFourFlowers();

                if (Config.在安全处有效)
                    AutoSkills();
                else if (!scene.User.InSafeZone)
                    AutoSkills();

                if (Config.自动关组)
                {
                    if (scene.GroupBox.AllowGroup)
                        CEnvir.Enqueue(new C.GroupSwitch { Allow = !scene.GroupBox.AllowGroup });
                }

                // 随机保护
                if (Config.是否开启随机保护)
                {
                    float ratio = Config.血量剩下百分之多少时自动随机 / 100f;
                    if (scene.User.CurrentHP < scene.User.Stats[Stat.Health] * ratio && CEnvir.Now > _ProtectTime)
                    {
                        DXItemCell cell = scene.InventoryBox.Grid.Grid.FirstOrDefault(x => x?.Item?.Info.ItemName == "随机传送卷");
                        if (cell != null && cell.UseItem())
                            _ProtectTime = CEnvir.Now.AddSeconds(5.0);
                    }
                }

                // 回城保护
                if (Config.是否开启回城保护)
                {
                    float ratio = Config.血量剩下百分之多少时自动回城 / 100f;
                    if (scene.User.CurrentHP < scene.User.Stats[Stat.Health] * ratio)
                    {
                        DXItemCell cell = scene.InventoryBox.Grid.Grid.FirstOrDefault(x => x?.Item?.Info.ItemName == "回城卷");
                        if (cell != null && cell.UseItem())
                            Config.是否开启回城保护 = false;
                    }
                }

                // 自动学习技能书（常用页开关，B 端此前只有配置没有运行逻辑）
                if (Config.自动学习技能书 && CEnvir.Now > scene.UseItemTime && scene.User.Horse == HorseType.None)
                {
                    if (!AutoUseBook(scene.InventoryBox.Grid.Grid))
                        AutoUseBook(scene.CompanionBox.InventoryGrid.Grid);
                }

                if (!Config.开始挂机) return;

                // 死亡回城（3 秒节流，避免每帧发包）
                if (scene.User.Dead && Config.死亡回城 && CEnvir.Now > _lastTownReviveTime)
                {
                    CEnvir.Enqueue(new C.TownRevive());
                    _lastTownReviveTime = CEnvir.Now.AddSeconds(3.0);
                }

                // 定时自动随机
                if (Config.是否开启每间隔自动随机 && CEnvir.Now > _ProtectTime)
                {
                    DXItemCell cell = scene.InventoryBox.Grid.Grid.FirstOrDefault(x => x?.Item?.Info.ItemName == "随机传送卷");
                    if (cell != null && cell.UseItem())
                        _ProtectTime = CEnvir.Now.AddSeconds(Config.隔多少秒自动随机一次);
                }
            }

            /// <summary>背包里找一本能学的技能书并使用。移植自 A 端 BigPatchDialog.cs:879-893。</summary>
            private bool AutoUseBook(DXItemCell[] grid)
            {
                if (grid == null) return false;

                GameScene scene = GameScene.Game;

                foreach (DXItemCell cell in grid)
                {
                    if (cell?.Item?.Info == null || cell.Locked) continue;
                    if (cell.Item.Info.ItemType != ItemType.Book) continue;
                    if (!scene.CanUseItem(cell.Item)) continue;

                    scene.UseItemTime = CEnvir.Now.AddMilliseconds(500);
                    cell.Locked = true;
                    CEnvir.Enqueue(new C.ItemUse { Link = new CellLinkInfo { GridType = cell.GridType, Slot = cell.Slot, Count = 1 } });
                    return true;
                }

                return false;
            }

            #endregion

            #region 布局

            public override void OnSizeChanged(Size oValue, Size nValue)
            {
                base.OnSizeChanged(oValue, nValue);

                int x = 5;
                int y = 5;
                int width = (Size.Width - 15) / 2;

                // 左列：职业分组框（四者重叠，靠 Visible 切换）在上，自动技能在下
                int classHeight = Size.Height - 50 - AutoSkill.Size.Height - 15;

                foreach (DXGroupBox group in new[] { Warrior, Wizard, Taoist, Assassin })
                {
                    if (group == null) continue;

                    group.Size = new Size(width, classHeight);
                    group.Location = new Point(x, y);
                }

                AutoSkill.Size = new Size(width, AutoSkill.Size.Height);
                AutoSkill.Location = new Point(x, y + classHeight);

                if (ChkPauseAutoSkills != null)
                    ChkPauseAutoSkills.Location = new Point(AutoSkill.Location.X + 5, AutoSkill.Location.Y + AutoSkill.Size.Height + 5);

                // 右列：挂机
                if (Android != null)
                {
                    Android.Size = new Size(width, Size.Height - 10);
                    Android.Location = new Point(x + width + 5, y);
                }
            }

            #endregion
        }

        #endregion

        #region 拾取页（阶段 5）

        /// <summary>单个物品的拾取配置。移植自 A 端 CItemFilterSet。</summary>
        public class CItemFilterSet
        {
            public int idx { get; set; }
            public string name { get; set; }
            public ItemType type { get; set; }
            public Rarity rarity { get; set; }
            public bool hasPart { get; set; }
            public bool canHaveStats { get; set; }

            public bool hint;          // 地面提示
            public bool pick;          // 角色拾取
            public bool picks { get; set; }   // 宠物拾取
            public bool show { get; set; } = true; // 显示
        }

        /// <summary>
        /// 拾取白名单。移植自 A 端 CItemFilter。
        ///
        /// 与 A 端的差异：
        /// 1. A 端用 `for (ItemType t = Nothing; t &lt;= Fabao; t++)` 枚举类型，B 端没有 Fabao，
        ///    改为 `Enum.GetValues(typeof(ItemType))`；
        /// 2. B 端 `ItemInfo` 没有 `BlockMonsterDrop` 字段，跳过该过滤；
        /// 3. 不做网络同步（B 端服务端没有对应过滤包，发了会掉线），只落盘本地 ini。
        /// </summary>
        public class CItemFilter
        {
            public bool Inited;
            public string FileName;

            public List<CItemFilterSet> Items { get; } = new List<CItemFilterSet>();
            public Dictionary<int, CItemFilterSet> dictItems { get; } = new Dictionary<int, CItemFilterSet>();

            public CItemFilter()
            {
                foreach (ItemInfo itemInfo in Globals.ItemInfoList.Binding)
                {
                    if (itemInfo == null || string.IsNullOrEmpty(itemInfo.ItemName)) continue;

                    CItemFilterSet set = new CItemFilterSet
                    {
                        name = itemInfo.ItemName,
                        idx = itemInfo.Index,
                        type = itemInfo.ItemType,
                        rarity = itemInfo.Rarity,
                        hasPart = itemInfo.ItemEffect == ItemEffect.ItemPart,
                        canHaveStats = itemInfo.ItemType == ItemType.Weapon
                                    || itemInfo.ItemType == ItemType.Armour
                                    || itemInfo.ItemType == ItemType.Helmet
                                    || itemInfo.ItemType == ItemType.Necklace
                                    || itemInfo.ItemType == ItemType.Bracelet
                                    || itemInfo.ItemType == ItemType.Ring
                                    || itemInfo.ItemType == ItemType.Shoes
                                    || itemInfo.ItemType == ItemType.Shield,
                    };

                    Items.Add(set);

                    // 索引重复时保留第一个（A 端会写错误日志，这里静默跳过）
                    if (dictItems.ContainsKey(itemInfo.Index)) continue;

                    dictItems.Add(itemInfo.Index, set);
                }
            }

            public void Initialize(string file)
            {
                FileName = file;

                try
                {
                    if (!File.Exists(FileName))
                    {
                        File.Create(FileName)?.Close();
                        Inited = true;
                        return;
                    }

                    foreach (string line in File.ReadAllLines(FileName))
                    {
                        if (string.IsNullOrEmpty(line) || line[0] == ';') continue;

                        string[] parts = line.Split(',');
                        if (parts.Length < 6) continue;

                        if (!int.TryParse(parts[0].Trim(), out int index)) continue;
                        if (!dictItems.TryGetValue(index, out CItemFilterSet set)) continue;

                        if (bool.TryParse(parts[2].Trim(), out bool value)) set.hint = value;
                        if (bool.TryParse(parts[3].Trim(), out value)) set.pick = value;
                        if (bool.TryParse(parts[4].Trim(), out value)) set.picks = value;
                        if (bool.TryParse(parts[5].Trim(), out value)) set.show = value;
                    }
                }
                catch (Exception ex)
                {
                    CEnvir.SaveException(ex);
                }

                Inited = true;
            }

            public void Save()
            {
                if (string.IsNullOrEmpty(FileName)) return;

                try
                {
                    string[] contents = new string[Items.Count];

                    for (int i = 0; i < Items.Count; i++)
                    {
                        CItemFilterSet set = Items[i];
                        contents[i] = $"{set.idx}, {set.name}, {set.hint}, {set.pick}, {set.picks}, {set.show}";
                    }

                    File.WriteAllLines(FileName, contents);
                }
                catch (Exception ex)
                {
                    CEnvir.SaveException(ex);
                }
            }
        }

        /// <summary>
        /// 拾取页。移植自 A 端 DXAutoPickItemTab（5 列：名称 / 提示 / 角色拾取 / 宠物拾取 / 显示）。
        ///
        /// 与 A 端的差异：
        /// 1. 去掉 `SycFilters` 网络同步（B 端服务端没有过滤包，发送会断线）；
        /// 2. 「重置」原为「把全部物品重新加一遍」（会重复累积），改为「取消全部勾选」；
        /// 3. 列全选不再用 `ItemView.Controls[1]`（依赖控件内部顺序），改用公开的 `ItemView.Items`。
        /// </summary>
        public class DXAutoPickItemTab : DXTab
        {
            private const int ColumnNameWidth = 158;
            private const int ColumnCheckWidth = 58;

            public DXButton Search;
            public DXComboBox CombTypeBox;
            public DXTextBox sTextBox;
            public DXButton ResetButton;
            public DXButton DelButton;
            public DXListView ItemView;
            public DXCheckBox ChkNonWhite;
            public DXButton BtnSelectNonWhite;

            public CItemFilter ItemFilter { get; private set; }

            /// <summary>
            /// 物品表有上千行，每行 6 个控件；若在登录时（BigPatchDialog 构造期）就全部建好，
            /// 会拖慢登录。改为「页签首次可见时才构建」。
            /// </summary>
            private bool _pendingRefresh;

            public DXAutoPickItemTab()
            {
                CombTypeBox = new DXComboBox
                {
                    Parent = this,
                    Size = new Size(100, 18),
                    Location = new Point(10, 5),
                    DropDownHeight = 198,
                };

                CombTypeBox.SelectedItemChanged += (o, e) => RefreshItems(CombTypeBox.SelectedItem as ItemType?);

                DXListBoxItem all = new DXListBoxItem
                {
                    Parent = CombTypeBox.ListBox,
                    Item = null,
                };
                all.Label.Text = "全部";

                Type itemTypeEnum = typeof(ItemType);

                foreach (ItemType itemType in Enum.GetValues(typeof(ItemType)))
                {
                    DescriptionAttribute attr = itemTypeEnum.GetMember(itemType.ToString())[0].GetCustomAttribute<DescriptionAttribute>();

                    DXListBoxItem row = new DXListBoxItem
                    {
                        Parent = CombTypeBox.ListBox,
                        Item = itemType,
                    };
                    row.Label.Text = attr?.Description ?? itemType.ToString();
                }

                CombTypeBox.ListBox.SelectItem(null);

                sTextBox = new DXTextBox
                {
                    Parent = this,
                    Size = new Size(120, 20),
                    Location = new Point(CombTypeBox.Location.X + CombTypeBox.Size.Width + 5, CombTypeBox.Location.Y),
                };

                sTextBox.TextBox.KeyPress += (o, e) =>
                {
                    if (e.KeyChar != (char)Keys.Enter) return;

                    SearchItem();
                    e.Handled = true;
                };

                Search = new DXButton
                {
                    Parent = this,
                    Size = new Size(64, 18),
                    Label = { Text = "搜索" },
                    ButtonType = ButtonType.SmallButton,
                    Location = new Point(sTextBox.Location.X + sTextBox.Size.Width + 5, sTextBox.Location.Y),
                };
                Search.MouseClick += (o, e) => SearchItem();

                ResetButton = new DXButton
                {
                    Parent = this,
                    Size = new Size(64, 18),
                    Label = { Text = "重置" },
                    ButtonType = ButtonType.SmallButton,
                    Location = new Point(Search.Location.X + Search.Size.Width + 5, Search.Location.Y),
                };
                ResetButton.MouseClick += (o, e) => ResetAll();

                DelButton = new DXButton
                {
                    Parent = this,
                    Size = new Size(64, 18),
                    Label = { Text = "清空" },
                    ButtonType = ButtonType.SmallButton,
                    Location = new Point(ResetButton.Location.X + ResetButton.Size.Width + 5, ResetButton.Location.Y),
                };
                DelButton.MouseClick += (o, e) => ItemView?.RemoveAll();

                ChkNonWhite = new DXCheckBox
                {
                    Parent = this,
                    Label = { Text = "拾取非白色物品" },
                    Checked = Config.拾取非白色物品,
                    Size = new Size(120, 18),
                    Location = new Point(10, 30),
                };
                ChkNonWhite.CheckedChanged += (o, e) => Config.拾取非白色物品 = ChkNonWhite.Checked;

                BtnSelectNonWhite = new DXButton
                {
                    Parent = this,
                    Size = new Size(80, 18),
                    Label = { Text = "勾选非白" },
                    ButtonType = ButtonType.SmallButton,
                    Location = new Point(150, 30),
                };
                BtnSelectNonWhite.MouseClick += (o, e) => SelectNonWhite();

                ItemView = new DXListView
                {
                    Parent = this,
                    Location = new Point(5, 55),
                    Size = new Size(410, 340),
                    ItemBorder = false,
                };

                ItemView.InsertColumn(0U, "物品名称", ColumnNameWidth, 24, "  物品的名字");
                ItemView.InsertColumn(1U, CreateHeaderCheckBox("提示", Color.Green, 1));
                ItemView.InsertColumn(2U, CreateHeaderCheckBox("角色拾取", Color.Empty, 2));
                ItemView.InsertColumn(3U, CreateHeaderCheckBox("宠物拾取", Color.Empty, 3));
                ItemView.InsertColumn(4U, CreateHeaderCheckBox("显示", Color.Empty, 4));
            }

            /// <summary>绑定过滤数据（由 BigPatchDialog 在角色数据就绪后调用）。</summary>
            public void BindFilter(CItemFilter filter)
            {
                ItemFilter = filter;
                _pendingRefresh = true;

                if (IsVisible)
                {
                    _pendingRefresh = false;
                    RefreshItems();
                }
            }

            public override void OnIsVisibleChanged(bool oValue, bool nValue)
            {
                base.OnIsVisibleChanged(oValue, nValue);

                if (!nValue || !_pendingRefresh || ItemFilter == null) return;

                _pendingRefresh = false;
                RefreshItems();
            }

            private DXCheckBox CreateHeaderCheckBox(string text, Color backColour, int subItem)
            {
                DXCheckBox box = new DXCheckBox
                {
                    Size = new Size(ColumnCheckWidth, 24),
                    Label = { Text = text },
                    BackColour = backColour,
                };

                box.CheckedChanged += (o, e) =>
                {
                    if (!(o is DXCheckBox source) || ItemView == null) return;

                    // 逐行赋 Checked（会触发各行 CheckedChanged，从而写回 ItemFilter）
                    foreach (DXControl row in ItemView.Items.Controls)
                    {
                        if (subItem >= row.Controls.Count) continue;
                        if (row.Controls[subItem] is DXCheckBox target)
                            target.Checked = source.Checked;
                    }
                };

                return box;
            }

            private DXCheckBox CreateRowCheckBox(bool value, int index, int field)
            {
                DXCheckBox box = new DXCheckBox
                {
                    Size = new Size(ColumnCheckWidth, 20),
                    Checked = value,
                    Tag = index,
                };

                box.CheckedChanged += (o, e) =>
                {
                    if (!(o is DXCheckBox source) || ItemFilter == null) return;
                    if (!(source.Tag is int i)) return;
                    if (i < 0 || i >= ItemFilter.Items.Count) return;

                    CItemFilterSet set = ItemFilter.Items[i];

                    switch (field)
                    {
                        case 0: set.hint = source.Checked; break;
                        case 1: set.pick = source.Checked; break;
                        case 2: set.picks = source.Checked; break;
                        case 3: set.show = source.Checked; break;
                    }
                };

                return box;
            }

            private static Color GetNameColour(CItemFilterSet set)
            {
                // 碎片：地面恒为 暗灰 / 灰绿 / 粉紫
                if (set.hasPart)
                {
                    switch (set.rarity)
                    {
                        case Rarity.Common: return Color.DarkGray;
                        case Rarity.Superior: return Color.DarkSeaGreen;
                        case Rarity.Elite: return Color.MediumOrchid;
                    }
                }
                // 可能带随机属性：取该品质「带属性」时的醒目色
                else if (set.canHaveStats)
                {
                    switch (set.rarity)
                    {
                        case Rarity.Common: return Color.LightSkyBlue;
                        case Rarity.Superior: return Color.Lime;
                        case Rarity.Elite: return Color.Magenta;
                    }
                }
                // 普通品质 + 无附加属性 + 非碎片 → 地面恒为白
                else
                {
                    switch (set.rarity)
                    {
                        case Rarity.Common: return Color.Gainsboro;
                        case Rarity.Superior: return Color.LightGreen;
                        case Rarity.Elite: return Color.Violet;
                    }
                }

                return Color.Gainsboro;
            }

            private void SearchItem()
            {
                string text = sTextBox.TextBox.Text;

                if (string.IsNullOrEmpty(text)) return;

                ItemView.SortByName(text);
            }

            private void ResetAll()
            {
                if (ItemFilter == null) return;

                foreach (CItemFilterSet set in ItemFilter.Items)
                {
                    set.hint = false;
                    set.pick = false;
                    set.picks = false;
                    set.show = true;
                }

                RefreshItems(CombTypeBox.SelectedItem as ItemType?);
            }

            private void SelectNonWhite()
            {
                if (ItemFilter == null) return;

                foreach (CItemFilterSet set in ItemFilter.Items)
                {
                    if (set.rarity == Rarity.Common) continue;

                    set.pick = true;
                }

                RefreshItems(CombTypeBox.SelectedItem as ItemType?);
            }

            private void RefreshItems(ItemType? type = null)
            {
                if (ItemFilter == null || ItemView == null) return;

                _pendingRefresh = false;

                ItemView.RemoveAll();

                for (int index = 0; index < ItemFilter.Items.Count; index++)
                {
                    CItemFilterSet set = ItemFilter.Items[index];

                    if (type != null && set.type != type) continue;
                    if (string.IsNullOrEmpty(set.name)) continue;

                    uint nItem = ItemView.InsertItem(uint.MaxValue, set.name);

                    if (ItemView.GetItem(nItem, 0U) is DXLabel nameLabel)
                        nameLabel.ForeColour = GetNameColour(set);

                    ItemView.SetItem(nItem, 1U, CreateRowCheckBox(set.hint, index, 0));
                    ItemView.SetItem(nItem, 2U, CreateRowCheckBox(set.pick, index, 1));
                    ItemView.SetItem(nItem, 3U, CreateRowCheckBox(set.picks, index, 2));
                    ItemView.SetItem(nItem, 4U, CreateRowCheckBox(set.show, index, 3));
                }

                ItemView.UpdateItems();
            }

            public override void OnSizeChanged(Size oValue, Size nValue)
            {
                base.OnSizeChanged(oValue, nValue);

                if (ItemView == null) return;

                ItemView.Location = new Point(5, 55);
                ItemView.Size = new Size(Math.Max(Size.Width - 10, 100), Math.Max(Size.Height - 60, 100));
            }
        }

        #endregion

        #region 聊天 / 便签 / 记录 / 帮助（阶段 5）

        /// <summary>
        /// 聊天页：自动回复、自动喊话、屏蔽白字。移植自 A 端 DXAnsweringTab。
        ///
        /// 与 A 端的差异：
        /// 1. A 端用 `System.Timers.Timer` 在**线程池线程**里发包；这里改为每帧由
        ///    `GameScene.Process` 调 `ProcessAutoShout()`，不跨线程；
        /// 2. A 端每次把整个输入框文本当一条消息发出去；这里改为随机取一行；
        /// 3. 自动回复/自动喊话开关都持久化到 ini（A 端喊话开关不存盘）。
        /// </summary>
        public class DXAnsweringTab : DXTab
        {
            private static readonly string[] PresetReplies =
            {
                "您好，我有事不在",
                "我去吃饭了....",
                "挂机中.....",
                "挂机练功 请勿打扰",
            };

            public DXTextView InputBox;
            public DXCheckBox ChkMsgNotify;
            public DXCheckBox ChkAutoReplay;
            public DXComboBox CombAutoReplayText;
            public DXCheckBox ChkAutoSayWords;
            public DXNumberBox AutoSayInterval;
            public DXLabel IntervalLeft;
            public DXLabel IntervalRight;
            public DXCheckBox ChkSaveSayRecord;
            public DXCheckBox ChkShieldNpcWords;
            public DXCheckBox ChkShieldMonsterWords;

            private DateTime _nextShoutTime;
            private bool _sayTextLoaded;

            public DXAnsweringTab()
            {
                // ---- 自动回复 ----
                ChkAutoReplay = new DXCheckBox
                {
                    Parent = this,
                    Label = { Text = "自动回复" },
                    Location = new Point(10, 15),
                    Checked = Config.自动回复,
                };
                ChkAutoReplay.CheckedChanged += (o, e) => Config.自动回复 = ChkAutoReplay.Checked;

                CombAutoReplayText = new DXComboBox
                {
                    Parent = this,
                    Size = new Size(220, 18),
                    Location = new Point(110, 15),
                };

                CombAutoReplayText.SelectedItemChanged += (o, e) =>
                {
                    if (CombAutoReplayText.SelectedItem is int index)
                        Config.自动回复时用第几行句子 = index;
                };

                for (int i = 0; i < PresetReplies.Length; i++)
                {
                    DXListBoxItem item = new DXListBoxItem
                    {
                        Parent = CombAutoReplayText.ListBox,
                        Item = i,
                    };
                    item.Label.Text = PresetReplies[i];
                }

                int replyIndex = Math.Max(0, Math.Min(Config.自动回复时用第几行句子, PresetReplies.Length - 1));
                CombAutoReplayText.ListBox.SelectItem(replyIndex);

                // ---- 来消息提示（A 端仅 UI，无实际逻辑）----
                ChkMsgNotify = new DXCheckBox
                {
                    Parent = this,
                    Label = { Text = "来消息声音提示" },
                    Location = new Point(10, 45),
                };

                // ---- 自动喊话 ----
                ChkAutoSayWords = new DXCheckBox
                {
                    Parent = this,
                    Label = { Text = "自动喊话" },
                    Location = new Point(10, 75),
                    Checked = Config.自动喊话,
                };
                ChkAutoSayWords.CheckedChanged += (o, e) => Config.自动喊话 = ChkAutoSayWords.Checked;

                IntervalLeft = new DXLabel
                {
                    Parent = this,
                    Text = "间隔:",
                    Outline = true,
                    Location = new Point(110, 77),
                };

                AutoSayInterval = new DXNumberBox
                {
                    Parent = this,
                    ValueTextBox = { Size = new Size(70, 18) },
                    MinValue = 5000,
                    MaxValue = 600000,
                    Value = Config.多少秒一次自动喊话,
                    Location = new Point(150, 75),
                };
                AutoSayInterval.ValueTextBox.ValueChanged += (o, e) => Config.多少秒一次自动喊话 = AutoSayInterval.Value;

                IntervalRight = new DXLabel
                {
                    Parent = this,
                    Text = "毫秒",
                    Outline = true,
                    Location = new Point(225, 77),
                };

                ChkSaveSayRecord = new DXCheckBox
                {
                    Parent = this,
                    Label = { Text = "保存喊话内容" },
                    Location = new Point(270, 75),
                };
                ChkSaveSayRecord.CheckedChanged += (o, e) =>
                {
                    if (ChkSaveSayRecord.Checked) SaveSaywords();
                };

                // ---- 屏蔽白字（A 端只存配置，未接显示过滤）----
                ChkShieldNpcWords = new DXCheckBox
                {
                    Parent = this,
                    Label = { Text = "屏蔽NPC白字" },
                    Location = new Point(10, 105),
                    Checked = Config.是否关闭NPC话,
                };
                ChkShieldNpcWords.CheckedChanged += (o, e) => Config.是否关闭NPC话 = ChkShieldNpcWords.Checked;

                ChkShieldMonsterWords = new DXCheckBox
                {
                    Parent = this,
                    Label = { Text = "屏蔽怪物白字" },
                    Location = new Point(150, 105),
                    Checked = Config.是否关闭怪物话,
                };
                ChkShieldMonsterWords.CheckedChanged += (o, e) => Config.是否关闭怪物话 = ChkShieldMonsterWords.Checked;

                // ---- 喊话内容（每行一条）----
                InputBox = new DXTextView
                {
                    Parent = this,
                    ReadOnly = false,
                    Visible = false,
                    Location = new Point(10, 135),
                    Size = new Size(510, 250),
                };

                InputBox.TextBox.ScrollBars = ScrollBars.Vertical;
                InputBox.TextBox.MaxLength = 2000;

                LoadSaywords();
            }

            private void LoadSaywords()
            {
                if (_sayTextLoaded) return;

                _sayTextLoaded = true;

                if (Config.自动喊话内内容 == null)
                    Config.自动喊话内内容 = new List<string>();

                InputBox.TextBox.Text = string.Join("\r\n", Config.自动喊话内内容);
            }

            /// <summary>把输入框内容按行写回 Config。</summary>
            public void SaveSaywords()
            {
                if (InputBox?.TextBox == null) return;

                if (Config.自动喊话内内容 == null)
                    Config.自动喊话内内容 = new List<string>();

                Config.自动喊话内内容.Clear();

                foreach (string line in InputBox.TextBox.Text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();

                    if (trimmed.Length == 0) continue;

                    Config.自动喊话内内容.Add(trimmed);
                }
            }

            /// <summary>自动回复使用的句子。</summary>
            public string SelectedReplyText
            {
                get
                {
                    int index = Math.Max(0, Math.Min(Config.自动回复时用第几行句子, PresetReplies.Length - 1));
                    return PresetReplies[index];
                }
            }

            /// <summary>
            /// 自动喊话。每帧由 GameScene.Process 调用（避免 Timer 跨线程发包）。
            /// 每次随机取一行，间隔取配置值（下限 5 秒）。
            /// </summary>
            public void ProcessAutoShout()
            {
                if (!Config.自动喊话) return;
                if (Config.自动喊话内内容 == null || Config.自动喊话内内容.Count == 0) return;
                if (CEnvir.Now < _nextShoutTime) return;

                _nextShoutTime = CEnvir.Now.AddMilliseconds(Math.Max(Config.多少秒一次自动喊话, 5000));

                string text = Config.自动喊话内内容[CEnvir.Random.Next(Config.自动喊话内内容.Count)];

                if (string.IsNullOrEmpty(text)) return;

                CEnvir.Enqueue(new C.Chat { Text = text });
            }

            public override void OnSizeChanged(Size oValue, Size nValue)
            {
                base.OnSizeChanged(oValue, nValue);

                if (InputBox == null) return;

                InputBox.Location = new Point(10, 135);
                InputBox.Size = new Size(Math.Max(Size.Width - 20, 100), Math.Max(Size.Height - 145, 80));
                InputBox.Visible = true;
            }
        }

        /// <summary>
        /// 多行只读/可编辑文本视图。移植自 A 端 DXTextView。
        ///
        /// 原理：B 端 DXTextBox 的 CreateTexture 用 `TextBox.DrawToBitmap` **离屏渲染**，
        /// 即使原生 TextBox 不可见也能把文字画进纹理，所以这里隐藏原生控件即可获得纯展示效果。
        /// </summary>
        public class DXTextView : DXTextBox
        {
            public DXTextView()
            {
                TextBox.Visible = false;
                Border = true;
                Editable = true;
                TextBox.AcceptsReturn = true;
                TextBox.Multiline = true;
                TextBox.WordWrap = false;
                TextBox.ForeColor = Color.DarkOrange;
                TextBox.ScrollBars = ScrollBars.Vertical;
            }
        }

        /// <summary>
        /// 便签页。
        /// ⚠ A 端这个页签**没有任何存盘逻辑**（关窗口内容就丢），这里补上按角色存盘：
        /// `{角色名}-Note.txt`，切换角色时先存旧的再载入新的。
        /// </summary>
        public class DXUserNoteBookTab : DXTab
        {
            private const int MaxNoteLength = 4096;

            public DXTextView NoteView;

            private string _fileName;
            private bool _loaded;

            public DXUserNoteBookTab()
            {
                NoteView = new DXTextView
                {
                    Parent = this,
                    ReadOnly = false,
                    Visible = false,
                };

                NoteView.TextBox.ScrollBars = ScrollBars.None;
                NoteView.TextBox.MaxLength = MaxNoteLength;
            }

            /// <summary>角色数据就绪后绑定便签文件并载入内容。</summary>
            public void BindUser(string charName)
            {
                string file = string.IsNullOrEmpty(charName) ? "./Note.txt" : $"./{charName}-Note.txt";

                if (_fileName == file) return;

                if (_fileName != null) Save();

                _fileName = file;
                _loaded = true;

                try
                {
                    NoteView.TextBox.Text = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
                }
                catch (Exception ex)
                {
                    CEnvir.SaveException(ex);
                }
            }

            public void Save()
            {
                if (!_loaded || string.IsNullOrEmpty(_fileName) || NoteView?.TextBox == null) return;

                try
                {
                    File.WriteAllText(_fileName, NoteView.TextBox.Text ?? string.Empty);
                }
                catch (Exception ex)
                {
                    CEnvir.SaveException(ex);
                }
            }

            public override void OnSizeChanged(Size oValue, Size nValue)
            {
                base.OnSizeChanged(oValue, nValue);

                if (NoteView == null) return;

                NoteView.Location = new Point(10, 10);
                NoteView.Size = new Size(Math.Max(Size.Width - 20, 100), Math.Max(Size.Height - 20, 100));
                NoteView.Visible = true;
            }
        }

        /// <summary>
        /// 记录页：列出 `.\SysLogs\` 下的日志文件，双击用记事本打开。
        /// 移植自 A 端 DXSystemMsgRecordTab，`WinExec` 改为 `Process.Start`（免 P/Invoke）。
        /// </summary>
        public class DXSystemMsgRecordTab : DXTab
        {
            private const string LogFolder = "./SysLogs";

            public DXListBox LogList;

            public DXSystemMsgRecordTab()
            {
                LogList = new DXListBox
                {
                    Parent = this,
                    Size = new Size(120, DXControl.DefaultHeight),
                    Location = new Point(5, 5),
                };

                RefreshLogs();
            }

            /// <summary>重新扫描日志目录（页签显示时调用，避免只在构造时扫一次）。</summary>
            public void RefreshLogs()
            {
                if (LogList == null) return;

                // 清空旧项：只销毁 DXListBoxItem，绝不动 DXListBox 自带的滚动条
                List<DXControl> olds = new List<DXControl>();

                foreach (DXControl control in LogList.Controls)
                    if (control is DXListBoxItem) olds.Add(control);

                foreach (DXControl control in olds)
                    control.Dispose();

                DirectoryInfo dir = new DirectoryInfo(LogFolder);

                if (!dir.Exists) return;

                foreach (FileInfo file in dir.GetFiles())
                {
                    DXListBoxItem item = new DXListBoxItem
                    {
                        Parent = LogList,
                        Hint = "双击 打开记录文件",
                        Tag = file.FullName,
                    };

                    item.Label.Text = file.Name;

                    item.MouseDoubleClick += (o, e) =>
                    {
                        if (!(o is DXListBoxItem source)) return;
                        if (!(source.Tag is string path)) return;

                        try
                        {
                            System.Diagnostics.Process.Start("notepad.exe", path);
                        }
                        catch (Exception ex)
                        {
                            CEnvir.SaveException(ex);
                        }
                    };
                }
            }

            public override void OnIsVisibleChanged(bool oValue, bool nValue)
            {
                base.OnIsVisibleChanged(oValue, nValue);

                if (nValue) RefreshLogs();
            }

            public override void OnSizeChanged(Size oValue, Size nValue)
            {
                base.OnSizeChanged(oValue, nValue);

                if (LogList == null) return;

                LogList.Location = new Point(5, 5);
                LogList.Size = new Size(Math.Max(Size.Width - 10, 100), Math.Max(Size.Height - 10, 100));
            }
        }

        /// <summary>
        /// 魔法页：列出已学技能，可设置「锁人 / 锁怪」与「毒符设定」，写回 `Config.magics`，
        /// 供阶段 4b 已移植的 `GetMagicHelpper` / `TakeAmulet` 自动换符使用。
        /// 移植自 A 端 DXMagicHelperTab。
        ///
        /// 与 A 端的差异：
        /// 1. 去掉「扩展」空列（A 端该列内容恒为空）；
        /// 2. 快捷键列改为**只读显示**（A 端支持在列上按 F1-F12 重新绑定并发 `MagicKey` 包，
        ///    涉及 B 端按键表差异，暂不移植，快捷键仍可在游戏内魔法面板设置）；
        /// 3. `CEnvir.NeedAmulet` → `GameScene.NeedAmulet`；`Functions.GetEnumDesc` 本地实现。
        /// </summary>
        public class DXMagicHelperTab : DXTab
        {
            private static List<ItemInfo> Amulets;
            private static List<ItemInfo> Poisons;

            public DXListView MagicView;

            private bool _pendingRefresh;

            public DXMagicHelperTab()
            {
                MagicView = new DXListView
                {
                    Parent = this,
                    Location = new Point(10, 10),
                    Size = new Size(510, 380),
                    ItemBorder = false,
                };

                MagicView.InsertColumn(0U, "魔法名称", 120, 24, "只显示已学过的技能");
                MagicView.InsertColumn(1U, "等级", 40, 24, "技能等级");
                MagicView.InsertColumn(2U, "快捷键", 90, 24, "在游戏内魔法面板设置");
                MagicView.InsertColumn(3U, "锁人", 45, 24, "设置自动锁定人");
                MagicView.InsertColumn(4U, "锁怪", 45, 24, "设置自动锁定怪");
                MagicView.InsertColumn(5U, "毒符设定", 150, 24, "道士的自动换符设置");
            }

            private static void FillItemLists()
            {
                if (Amulets == null)
                {
                    Amulets = new List<ItemInfo>();

                    foreach (ItemInfo item in Globals.ItemInfoList.Binding)
                        if (item.ItemType == ItemType.Amulet) Amulets.Add(item);
                }

                if (Poisons == null)
                {
                    Poisons = new List<ItemInfo>();

                    foreach (ItemInfo item in Globals.ItemInfoList.Binding)
                        if (item.ItemType == ItemType.Poison) Poisons.Add(item);
                }
            }

            private static string GetSpellKeyText(SpellKey key)
            {
                if (key == SpellKey.None) return string.Empty;

                DescriptionAttribute attr = typeof(SpellKey).GetMember(key.ToString())[0].GetCustomAttribute<DescriptionAttribute>();

                return (attr?.Description ?? key.ToString()).Replace('\n', ' ');
            }

            /// <summary>重建技能列表。角色数据就绪或页签显示时调用。</summary>
            public void UpdateMagic()
            {
                if (GameScene.Game?.User == null || MagicView == null) return;

                // 页签不可见时先不建（技能行数不多，但下拉框较多，避免登录期开销）
                if (!IsVisible)
                {
                    _pendingRefresh = true;
                    return;
                }

                _pendingRefresh = false;

                FillItemLists();

                if (Config.magics == null) Config.magics = new List<MagicHelper>();

                MagicView.RemoveAll();

                foreach (KeyValuePair<MagicInfo, ClientUserMagic> pair in GameScene.Game.User.Magics)
                {
                    ClientUserMagic magic = pair.Value;

                    if (magic?.Info == null) continue;
                    if (magic.Info.School == MagicSchool.Passive || magic.Info.School == MagicSchool.None) continue;
                    if (magic.Info.Magic == MagicType.None) continue;

                    MagicHelper helper = Config.magics.FirstOrDefault(x => x != null && x.TypeID == magic.Info.Magic);

                    if (helper == null)
                    {
                        helper = new MagicHelper
                        {
                            TypeID = magic.Info.Magic,
                            Name = magic.Info.Name,
                            Key = magic.Set1Key,
                            LockPlayer = false,
                            LockMonster = false,
                            Amulet = -1,
                        };

                        Config.magics.Add(helper);
                    }

                    helper.obj = magic;
                    helper.Name = magic.Info.Name;

                    uint nItem = MagicView.InsertItem(uint.MaxValue, magic.Info.Name);

                    DXControl row = MagicView.Items.Controls[(int)nItem];
                    row.Tag = helper;

                    MagicView.SetItem(nItem, 1U, magic.Level.ToString());
                    MagicView.SetItem(nItem, 2U, GetSpellKeyText(magic.Set1Key));
                    MagicView.SetItem(nItem, 3U, CreateLockCheckBox(helper, true, helper.LockPlayer));
                    MagicView.SetItem(nItem, 4U, CreateLockCheckBox(helper, false, helper.LockMonster));
                    MagicView.SetItem(nItem, 5U, CreateAmuletCombo(magic, helper));
                }

                MagicView.UpdateItems();
            }

            public override void OnIsVisibleChanged(bool oValue, bool nValue)
            {
                base.OnIsVisibleChanged(oValue, nValue);

                if (nValue && _pendingRefresh) UpdateMagic();
            }

            private static DXCheckBox CreateLockCheckBox(MagicHelper helper, bool lockPlayer, bool value)
            {
                DXCheckBox box = new DXCheckBox
                {
                    Size = new Size(20, 20),
                    Checked = value,
                };

                box.CheckedChanged += (o, e) =>
                {
                    if (!(o is DXCheckBox source)) return;

                    if (lockPlayer) helper.LockPlayer = source.Checked;
                    else helper.LockMonster = source.Checked;
                };

                return box;
            }

            private static DXComboBox CreateAmuletCombo(ClientUserMagic magic, MagicHelper helper)
            {
                DXComboBox combo = new DXComboBox
                {
                    Size = new Size(140, 18),
                };

                DXListBoxItem none = new DXListBoxItem
                {
                    Parent = combo.ListBox,
                    Item = -1,
                };
                none.Label.Text = "未选择";

                if (magic.Info.Magic == MagicType.PoisonDust)
                {
                    DXListBoxItem alternate = new DXListBoxItem
                    {
                        Parent = combo.ListBox,
                        Item = 0,
                    };
                    alternate.Label.Text = "红绿毒交替";

                    foreach (ItemInfo item in Poisons)
                    {
                        DXListBoxItem li = new DXListBoxItem
                        {
                            Parent = combo.ListBox,
                            Item = item.Index,
                        };
                        li.Label.Text = item.ItemName;
                    }
                }
                else if (GameScene.NeedAmulet(magic.Info))
                {
                    foreach (ItemInfo item in Amulets)
                    {
                        DXListBoxItem li = new DXListBoxItem
                        {
                            Parent = combo.ListBox,
                            Item = item.Index,
                        };
                        li.Label.Text = item.ItemName;
                    }
                }
                else
                {
                    // 该技能不需要符/毒
                    none.Label.Text = string.Empty;
                    none.Enabled = false;
                    combo.Enabled = false;
                }

                combo.SelectedItemChanged += (o, e) =>
                {
                    if (combo.SelectedItem is int index)
                        helper.Amulet = index;
                };

                combo.ListBox.SelectItem(helper.Amulet);

                return combo;
            }

            public override void OnSizeChanged(Size oValue, Size nValue)
            {
                base.OnSizeChanged(oValue, nValue);

                if (MagicView == null) return;

                MagicView.Location = new Point(10, 10);
                MagicView.Size = new Size(Math.Max(Size.Width - 20, 100), Math.Max(Size.Height - 20, 100));
            }
        }

        /// <summary>
        /// 帮助页。A 端这个页签是空壳（只有一句 `Objects = new List&lt;MapObject&gt;()`），
        /// 这里补一段使用说明，至少不是一片空白。
        /// </summary>
        public class DXViewRangeObjectTab : DXTab
        {
            private static readonly string[] Lines =
            {
                "辅助功能使用说明",
                "",
                "常用：免蜡烛、免助跑、免SHIFT、数字显血、自动学习技能书等。",
                "辅助：按职业分组的自动施法，以及挂机设置。",
                "保护：自动喝药、随机/回城保护等。",
                "拾取：设置哪些物品需要自动拾取（过滤配置按角色保存在 -PickupFilter.ini）。",
                "便签：随手记录，按角色保存到 -Note.txt。",
                "记录：查看 ./SysLogs 目录下的日志文件，双击打开。",
                "",
                "提示：挂机只能在「允许随机传送」的地图使用；",
                "      换到不允许挂机的地图会自动关闭并提示。",
            };

            public DXViewRangeObjectTab()
            {
                int y = 12;

                foreach (string line in Lines)
                {
                    DXLabel label = new DXLabel
                    {
                        Parent = this,
                        Text = line,
                        Outline = true,
                        Location = new Point(15, y),
                    };

                    y += label.Size.Height + 4;
                }
            }
        }

        #endregion
    }
}
