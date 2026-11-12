using Library;
using Library.SystemModels;
using Server.DBModels;
using Server.Envir;
using Server.Models;
using System;
using System.Linq;
using C = Library.Network.ClientPackets;

namespace Server.Bots
{
    /// <summary>单个假人的上线阶段。</summary>
    public enum BotPhase
    {
        /// <summary>没上线（等调度拉起）。</summary>
        Offline = 0,
        /// <summary>已建好连接对象，正投递给主循环。</summary>
        Connecting = 1,
        /// <summary>等主循环把连接收编进 SEnvir.Connections。</summary>
        WaitJoin = 2,
        /// <summary>已发登录包，等进选角界面。</summary>
        Logging = 3,
        /// <summary>已发进图包，等 PlayerObject 创建。</summary>
        Entering = 4,
        /// <summary>在线上，行为交给 BotBrain。</summary>
        Online = 5,
        /// <summary>正在下线（StopGame → Disconnect）。</summary>
        Stopping = 6,
        /// <summary>连续多次上线失败，不再自动拉起（后台可重置）。</summary>
        Broken = 7,
    }

    /// <summary>
    /// 一个假人的生命周期状态机。**全部在游戏线程内同步推进**，不用 async/await ——
    /// 每一步之间靠"下次允许执行时间"拉开，避免 30 个假人同一帧涌进去。
    /// <para>
    /// 关键约束（D1 实测）：
    /// 连接必须走 <c>SEnvir.NewConnections</c> 队列，主循环会顺手做 <c>IPCount[ip]++</c>，
    /// 而 <c>Disconnect()</c> 里有对应的 <c>--</c>；绕过队列会让减到负数直接抛 KeyNotFound。
    /// </para>
    /// </summary>
    public sealed class BotSession
    {
        /// <summary>单个阶段最长等待，超时算一次失败。</summary>
        private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(20);

        public BotProfile Profile { get; }
        public BotPhase Phase { get; private set; } = BotPhase.Offline;

        public SConnection Connection { get; private set; }
        public BotBrain Brain { get; private set; }
        public CharacterInfo Character { get; private set; }

        public string LastError { get; private set; } = string.Empty;
        public int Failures { get; private set; }
        public DateTime OnlineSince { get; private set; } = DateTime.MinValue;
        public DateTime NextStepTime { get; private set; }

        /// <summary>
        /// 后台单人开关。false = 这个序号永久留在离线（调度器不拉起），用于
        /// 「这号名字看着不对，先关掉」这类运营操作，不影响其它假人。
        /// </summary>
        public bool Enabled { get; private set; } = true;

        /// <summary>
        /// 本次在线期间「赚到」的金币（P4-1 经济观测）。
        /// <para>
        /// 靠快照采样算出来：每次采样拿当前金币和上一次比，**只累加正向差额**。
        /// 这样买药/修理（负向）不会被当成负产出，而「卖货 + 打怪掉钱」都是正向，
        /// 不需要在每条产出路径上埋点。缺点是一次采样窗口里「先买后赚」会低估一点，
        /// 但 2 秒一次采样、买药几分钟一次，误差可以忽略。
        /// </para>
        /// </summary>
        public long GoldEarned { get; private set; }

        /// <summary>上一次采样到的金币；-1 = 还没采过（上线第一帧）。</summary>
        private long _lastGoldSample = -1;

        private DateTime _phaseDeadline;
        private bool _newbieKitGranted;

        public BotSession(BotProfile profile)
        {
            Profile = profile;
        }

        public bool IsOnline => Phase == BotPhase.Online;
        public bool IsBroken => Phase == BotPhase.Broken;
        public bool IsBusy => Phase != BotPhase.Offline && Phase != BotPhase.Broken;

        /// <summary>
        /// 采一次金币（快照重建时调用，约 2 秒一次）。
        /// 必须在游戏线程调用 —— 调用方 <see cref="BotRoster"/> 的 RebuildSnapshot 就在游戏线程。
        /// </summary>
        public void SampleGold(long gold)
        {
            if (_lastGoldSample >= 0 && gold > _lastGoldSample)
                GoldEarned += gold - _lastGoldSample;

            _lastGoldSample = gold;
        }

        #region 外部入口

        /// <summary>开始上线流程。已有连接/角色时复用（重启后等级装备都还在）。</summary>
        public void Begin(BotOptions options)
        {
            if (IsBusy) return;

            Phase = BotPhase.Connecting;
            LastError = string.Empty;
            NextStepTime = SEnvir.Now;
            _phaseDeadline = SEnvir.Now + PhaseTimeout;

            // 每次上线重新开始统计「本次在线赚了多少」（P4-1），不要跨重启累加。
            GoldEarned = 0;
            _lastGoldSample = -1;

            BotLog.Info(Profile.Seq, "开始上线");
        }

        /// <summary>开始下线流程：先 StopGame 存档，再断连接。</summary>
        public void RequestStop()
        {
            if (Phase == BotPhase.Offline || Phase == BotPhase.Stopping) return;

            Phase = BotPhase.Stopping;
            NextStepTime = SEnvir.Now;
            _phaseDeadline = SEnvir.Now + PhaseTimeout;
        }

        /// <summary>后台手动重置（把 Broken 拉回可调度状态）。</summary>
        public void Reset()
        {
            if (Connection != null)
            {
                try { Connection.Disconnect(); } catch { /* 忽略：连接可能已失效 */ }
            }

            Connection = null;
            Brain = null;
            Character = null;
            Failures = 0;
            _newbieKitGranted = false;
            Phase = BotPhase.Offline;
            LastError = string.Empty;
            GoldEarned = 0;
            _lastGoldSample = -1;

            BotLog.System(string.Format("BOT#{0:00} 已被后台重置", Profile.Seq));
        }

        /// <summary>后台单人开关：关掉就立刻优雅下线，并且调度器之后不再拉起。</summary>
        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;

            if (!enabled && Phase != BotPhase.Offline)
                RequestStop();

            BotLog.System(string.Format("BOT#{0:00} 已被后台{1}", Profile.Seq, enabled ? "启用" : "停用"));
        }

        /// <summary>后台「立即补给」：只有在线才有意义。</summary>
        public bool RequestRestock()
        {
            if (Phase != BotPhase.Online || Brain == null) return false;

            Brain.RequestRestock();

            return true;
        }

        /// <summary>
        /// 后台「传送」：把在线假人挪到指定地图坐标。走 <see cref="MapObject.Teleport"/>，
        /// 与 GM 命令同一条路径（会处理视野、Buff、AutoPath 收尾）。
        /// </summary>
        public bool Teleport(int mapIndex, System.Drawing.Point location)
        {
            PlayerObject player = Connection?.Player;

            if (player == null || Phase != BotPhase.Online) return false;

            MapInfo info = SEnvir.MapInfoList.Binding.FirstOrDefault(x => x.Index == mapIndex);

            if (info == null) return false;

            Map map = SEnvir.GetMap(info);

            if (map == null) return false;

            player.Teleport(map, location);

            BotLog.Info(Profile.Seq, string.Format("后台传送到 {0}（{1},{2}）", info.Description, location.X, location.Y));

            return true;
        }

        #endregion

        #region 状态推进

        /// <summary>由 <see cref="BotRoster"/> 在主循环里调用。</summary>
        public void Process(BotOptions options)
        {
            if (Phase == BotPhase.Offline || Phase == BotPhase.Broken) return;

            if (SEnvir.Now < NextStepTime) return;

            if (SEnvir.Now > _phaseDeadline)
            {
                Fail(string.Format("{0} 阶段超时", Phase));
                return;
            }

            switch (Phase)
            {
                case BotPhase.Connecting:
                    StepConnect(options);
                    break;
                case BotPhase.WaitJoin:
                    StepWaitJoin(options);
                    break;
                case BotPhase.Logging:
                    StepLogin(options);
                    break;
                case BotPhase.Entering:
                    StepEnter(options);
                    break;
                case BotPhase.Online:
                    StepOnline();
                    break;
                case BotPhase.Stopping:
                    StepStop();
                    break;
            }
        }

        private void StepConnect(BotOptions options)
        {
            if (Connection == null)
            {
                Connection = new SConnection(string.Format("bot:{0:00}", Profile.Seq), true);

                // 必须走队列：主循环负责 IPCount++，Disconnect 里负责 --，绕过去会减成负数崩掉。
                SEnvir.NewConnections.Enqueue(Connection);
            }

            Phase = BotPhase.WaitJoin;
            Schedule(options);
        }

        private void StepWaitJoin(BotOptions options)
        {
            if (Connection == null || !Connection.Connected || !SEnvir.Connections.Contains(Connection))
            {
                // 还没被收编，下一轮再看（20 秒超时会兜底）。
                Schedule(options);
                return;
            }

            AccountInfo account = BotFactory.EnsureAccount(Profile);

            if (account == null)
            {
                Fail("账号创建失败");
                return;
            }

            Connection.Process(new C.Login
            {
                EMailAddress = account.EMailAddress,
                Password = Profile.Password,
                CheckSum = "bot",
            });

            Phase = BotPhase.Logging;
            _phaseDeadline = SEnvir.Now + PhaseTimeout;
            Schedule(options);
        }

        private void StepLogin(BotOptions options)
        {
            if (Connection == null || Connection.Stage != GameStage.Select || Connection.Account == null)
            {
                Schedule(options);
                return;
            }

            bool created;
            Character = BotFactory.EnsureCharacter(Connection.Account, Profile, out created);

            if (Character == null)
            {
                Fail("角色创建失败");
                return;
            }

            // 老号也走一次「启动资源」判断：GrantNewbieKit 内部已经做成幂等的
            // （金币够就跳过），所以这里不必区分新老号。
            // 原来是 `= !created` —— 老号直接置 true 意味着永远不再判断，
            // 结果假人一旦花光钱就再也拿不到启动资金，只能陷入
            // 「买药失败 → 回城卖货 → 没货可卖 → 再买药失败」的活锁
            // （D5 探针实测：24/30 个假人卡在城里，只有 2 个在打怪）。
            _newbieKitGranted = false;

            // 顶号保护：同一个角色在 Config.RelogDelay（默认 10 秒）内不允许再次进图。
            // 真人会收到「延迟 N 秒」的提示然后重试；假人没有客户端去处理 StartGameResult.Delayed，
            // 硬发包只会卡在 Entering 直到阶段超时（实测：后台「踢下线」后 5 秒被水位拉起，
            // 立刻重连必撞 10 秒顶号保护 → Entering 超时 → 退避 5 秒才重试成功）。
            // 所以这里自己等到能进图再发包。
            TimeSpan sinceLastLogin = SEnvir.Now - Character.LastLogin;

            if (sinceLastLogin < Config.RelogDelay)
            {
                TimeSpan wait = Config.RelogDelay - sinceLastLogin;

                NextStepTime = SEnvir.Now + wait;

                // 等待时间不能算进阶段超时，否则等完就已经超时报错了。
                _phaseDeadline = SEnvir.Now + PhaseTimeout + wait;

                BotLog.Debug(Profile.Seq, string.Format("顶号保护：还需等 {0:0.0} 秒才能进图", wait.TotalSeconds));

                return;
            }

            Connection.Process(new C.StartGame { CharacterIndex = Character.Index });

            Phase = BotPhase.Entering;
            _phaseDeadline = SEnvir.Now + PhaseTimeout;
            Schedule(options);
        }

        private void StepEnter(BotOptions options)
        {
            if (Connection == null || Connection.Player == null || Connection.Stage != GameStage.Game)
            {
                Schedule(options);
                return;
            }

            PlayerObject player = Connection.Player;

            player.Bot = new BotBrain(player, Profile);
            Brain = player.Bot;

            // 启动资源（金币 + 红蓝）：GrantNewbieKit 自己判幂等，这里只负责「本轮是否已发」。
            if (!_newbieKitGranted)
            {
                if (BotFactory.GrantNewbieKit(player, Profile.Seq))
                    _newbieKitGranted = true;
            }
            else
            {
                // 留痕：区分「根本没调用」和「调用了但被判定跳过」。
                // 之前查「50 万金币不生效」时两者在日志上完全一样（都没有输出），
                // 只能靠反复猜。这条 Warn 让下次一眼能分清。
                BotLog.Warn(Profile.Seq, "启动资源：本轮已发放过，跳过重复判断");
            }

            Phase = BotPhase.Online;
            Failures = 0;
            OnlineSince = SEnvir.Now;
            _phaseDeadline = DateTime.MaxValue;
            NextStepTime = SEnvir.Now;

            BotLog.Info(Profile.Seq, string.Format("已上线：{0} 等级 {1}，位置 {2}（{3},{4}）",
                player.Name,
                player.Level,
                player.CurrentMap?.Info?.Description ?? "-",
                player.CurrentLocation.X,
                player.CurrentLocation.Y));
        }

        private void StepOnline()
        {
            if (Connection == null || !Connection.Connected || Connection.Player == null)
            {
                Fail("上线后连接或角色丢失");
            }
        }

        /// <summary>
        /// 把这一段在线时长累计到角色上（2026-11-11，跨重启持久化）。
        /// <para>
        /// 抽成 public static 是为了能单独验证 —— 「累计在线时长」只在假人走正常下线流程时才发生，
        /// 强杀进程（崩溃）不会触发，自动化测试里很难自然造出来。
        /// </para>
        /// </summary>
        public static void AccumulateOnlineTime(CharacterInfo character, DateTime onlineSince)
        {
            if (character == null || onlineSince == DateTime.MinValue) return;

            long seconds = (long)(SEnvir.Now - onlineSince).TotalSeconds;

            if (seconds > 0)
                character.BotOnlineSeconds += seconds;
        }

        private void StepStop()
        {
            if (Brain != null)
            {
                Brain = null;

                if (Connection?.Player != null)
                    Connection.Player.Bot = null;
            }

            if (Connection != null)
            {
                // Disconnect → CleanUp → Player.StopGame()，存档会走正常路径；
                // 不能直接把对象丢掉，否则等级装备全丢。
                try
                {
                    Connection.Disconnect();
                }
                catch (Exception ex)
                {
                    BotLog.Warn(Profile.Seq, string.Format("断线时异常：{0}", ex.Message));
                }
            }

            // 本次在线时长累计落库（2026-11-11）：在线时长是「跨重启」指标，
            // 必须在 Character 置空前累加，否则数据会丢。
            AccumulateOnlineTime(Character, OnlineSince);

            Connection = null;
            Character = null;
            Phase = BotPhase.Offline;
            OnlineSince = DateTime.MinValue;

            BotLog.Info(Profile.Seq, "已下线");
        }

        #endregion

        #region 失败处理

        private void Schedule(BotOptions options)
        {
            NextStepTime = SEnvir.Now + options.NextStepDelay();
        }

        private void Fail(string reason)
        {
            LastError = reason;
            Failures++;

            BotLog.Warn(Profile.Seq, string.Format("上线失败（第 {0} 次）：{1}", Failures, reason));

            try
            {
                Connection?.Disconnect();
            }
            catch
            {
                // 连接本身就可能是坏的，忽略。
            }

            Connection = null;
            Brain = null;

            if (Failures >= Config.BotMaxFailures)
            {
                Phase = BotPhase.Broken;
                BotLog.Error(Profile.Seq, string.Format("连续失败 {0} 次，标记 Broken，不再自动拉起", Failures));
                return;
            }

            // 退避：5s / 30s / 5min
            double delaySeconds = Failures switch
            {
                1 => 5,
                2 => 30,
                _ => 300,
            };

            Phase = BotPhase.Offline;
            NextStepTime = SEnvir.Now.AddSeconds(delaySeconds);
        }

        #endregion

        /// <summary>后台展示用。</summary>
        public string Describe()
        {
            string phase = Phase switch
            {
                BotPhase.Offline => "离线",
                BotPhase.Connecting => "建连接",
                BotPhase.WaitJoin => "等待入列",
                BotPhase.Logging => "登录中",
                BotPhase.Entering => "进图中",
                BotPhase.Online => "在线",
                BotPhase.Stopping => "下线中",
                BotPhase.Broken => "故障",
                _ => Phase.ToString(),
            };

            return string.Format("{0}　{1}{2}", phase, Profile.CharacterName,
                string.IsNullOrEmpty(LastError) ? string.Empty : "　" + LastError);
        }
    }
}
