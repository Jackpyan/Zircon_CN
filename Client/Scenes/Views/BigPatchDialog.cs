using Client.Controls;
using Client.Envir;
using Client.UserModels;
using Library;
using System;
using System.Drawing;
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
        }

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
            base.Dispose(disposing);

            if (disposing)
            {
                Commonly = null;

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
    }
}
