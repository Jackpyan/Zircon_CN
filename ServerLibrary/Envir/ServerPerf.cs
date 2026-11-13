using System;
using System.Diagnostics;

namespace Server.Envir
{
    /// <summary>
    /// 主循环耗时埋点（P4-5）。
    /// <para>
    /// 背景：<see cref="SEnvir"/> 原来只有 <see cref="SEnvir.ConDelay"/>（连接处理）和
    /// <see cref="SEnvir.SaveDelay"/>（存库），<b>既没有整帧耗时，也没有「假人段」耗时</b> ——
    /// 于是「服务器为什么卡」只能靠猜，全局熔断也无从判起。
    /// </para>
    /// <para>
    /// <b>「假人段」由两块拼成，少一块就测不准：</b>
    /// <list type="number">
    /// <item><see cref="SEnvir"/> 里 <c>BotRoster.Process()</c> 的前后 —— 调度器自身的开销（会话状态机 / 社交 / 水位）。</item>
    /// <item><c>PlayerObject.StartProcess</c> 里 <b>只有假人才会走</b>的那一小段 —— 自动寻路 + <c>BotBrain.Process()</c>。
    /// 假人的 AI <b>不在</b> <c>BotRoster.Process()</c> 里：假人就是普通 <c>PlayerObject</c>，
    /// 走的是主循环的玩家遍历，所以必须单独加一段计时（<see cref="BeginBotSlice"/> / <see cref="EndBotSlice"/>）。</item>
    /// </list>
    /// 第一版只量了 ①，实测 30 个假人在线时只有 0.01 ms/帧 —— 不是假人不干活，
    /// 是量错了地方：真正的开销（A* 规划 + 决策）在玩家遍历里。
    /// </para>
    /// <para>
    /// <b>线程模型</b>：所有 <c>Begin*</c> / <c>End*</c> / <see cref="RollSecond"/> 只在游戏主循环线程调用；
    /// 发布属性是普通字段，后台线程读到的可能是上一秒的值 —— 监控数据本来就该滞后一秒，这样反而省掉了锁。
    /// </para>
    /// </summary>
    public static class ServerPerf
    {
        /// <summary>单个样本的上限（毫秒）。线程被挂起/断点停下时会产生几秒的假样本，夹一下防止污染最大值。</summary>
        private const double MaxSampleMs = 5000;

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        private static long _frameStart;
        private static long _botStart;

        // 当前这一秒的累加（RollSecond 之后清零）
        private static double _loopSum, _botSum, _loopMax, _botMax;
        private static int _frames;

        #region 上一整秒的发布值（后台卡片 / 熔断读这些）

        /// <summary>整帧耗时平均值（毫秒）。一帧 = 一次 <c>while (Started)</c> 迭代。</summary>
        public static double LoopMs { get; private set; }

        /// <summary>
        /// 假人段耗时平均值（毫秒/帧）= ①调度器 + ②所有假人的 AI 分片。
        /// <para>
        /// <b>这个数不要拿来当熔断判据</b>：主循环是自旋的（空闲时 5000+ 帧/秒），
        /// 每帧平均值会被帧数稀释到接近 0，真实占用再高也看不出来。判据用 <see cref="BotSharePercent"/>。
        /// </para>
        /// </summary>
        public static double BotMs { get; private set; }

        /// <summary>
        /// 上一秒假人段累计耗时（毫秒/秒）。<b>这个才是绝对 CPU 开销</b>：
        /// 1000 表示吃掉一整个核。跨帧率稳定，不随主循环快慢变化。
        /// </summary>
        public static double BotMsPerSecond { get; private set; }

        /// <summary>整帧耗时的秒内峰值（毫秒）。平均值正常但峰值很高 = 偶发尖峰，真人能感觉到顿一下。</summary>
        public static double LoopMaxMs { get; private set; }

        /// <summary>假人段耗时的秒内峰值（毫秒）。</summary>
        public static double BotMaxMs { get; private set; }

        /// <summary>上一秒实际跑了多少帧（≈ FPS）。掉到个位数说明主循环被什么东西顶住了。</summary>
        public static int FramesPerSecond { get; private set; }

        /// <summary>
        /// 假人段占主循环的百分比。<b>熔断判据用这个</b> —— 与帧率无关，
        /// 直接回答「主循环烧掉的 CPU 里有多少是假人造成的」，也就直接对应
        /// 「把假人拿掉服务器会不会好」。
        /// </summary>
        public static double BotSharePercent => LoopMs > 0 ? BotMs * 100.0 / LoopMs : 0;

        #endregion

        /// <summary>一帧开始。<b>必须在 <c>while (Started)</c> 循环体最前面调用。</b></summary>
        public static void BeginFrame()
        {
            _frameStart = Stopwatch.GetTimestamp();
            _botStart = 0;
        }

        /// <summary>调度器那一段开始（<c>BotRoster.Process()</c> 之前）。</summary>
        public static void BeginBots()
        {
            _botStart = Stopwatch.GetTimestamp();
        }

        /// <summary>调度器那一段结束（<c>BotRoster.Process()</c> 之后）。</summary>
        public static void EndBots()
        {
            if (_botStart == 0) return;

            _botSum += Math.Min((Stopwatch.GetTimestamp() - _botStart) * MsPerTick, MaxSampleMs);
            _botStart = 0;
        }

        /// <summary>
        /// 单个假人的 AI 分片开始。由 <c>PlayerObject.StartProcess</c> 在
        /// <b>只有假人才走</b>的那一段（自动寻路 + <c>BotBrain.Process()</c>）之前调用。
        /// </summary>
        public static long BeginBotSlice() => Stopwatch.GetTimestamp();

        /// <summary>
        /// 单个假人的 AI 分片结束。参数是 <see cref="BeginBotSlice"/> 的返回值。
        /// <para>
        /// 每个假人每帧一次，30 个假人 × 5000 帧/秒 = 15 万次/秒 —— <c>Stopwatch.GetTimestamp()</c>
        /// 是几十纳秒的活，合计不到 1% 的核，换来的是「假人 AI 真的被量到了」，值得。
        /// </para>
        /// </summary>
        public static void EndBotSlice(long startTimestamp)
        {
            if (startTimestamp == 0) return;

            double ms = Math.Min((Stopwatch.GetTimestamp() - startTimestamp) * MsPerTick, MaxSampleMs);

            _botSum += ms;

            if (ms > _botMax) _botMax = ms;
        }

        /// <summary>
        /// 一帧结束。放在 <c>try</c> 块最后 —— 抛异常的那一帧不计入（异常帧的耗时没有参考价值，
        /// 而且 <c>catch</c> 里会 <c>break</c> 出循环，本来就到不了这里）。
        /// </summary>
        public static void EndFrame()
        {
            if (_frameStart == 0) return;

            double ms = Math.Min((Stopwatch.GetTimestamp() - _frameStart) * MsPerTick, MaxSampleMs);

            _frameStart = 0;
            _loopSum += ms;
            _frames++;

            if (ms > _loopMax) _loopMax = ms;
        }

        /// <summary>
        /// 发布上一秒的统计并清零累加。由 <see cref="SEnvir"/> 每秒那一段调用。
        /// <para>
        /// 这一秒一帧都没跑（例如刚启动、或主循环被长时间阻塞）时<b>保持上一次的发布值</b>，
        /// 不发布 0 —— 否则熔断会把「卡死」误判成「很健康」。
        /// </para>
        /// </summary>
        public static void RollSecond()
        {
            if (_frames > 0)
            {
                LoopMs = _loopSum / _frames;
                BotMs = _botSum / _frames;
                BotMsPerSecond = _botSum;
                LoopMaxMs = _loopMax;
                BotMaxMs = _botMax;
                FramesPerSecond = _frames;
            }

            _loopSum = 0;
            _botSum = 0;
            _loopMax = 0;
            _botMax = 0;
            _frames = 0;
        }

        /// <summary>调度器停止时清空（避免停服后卡片还显示旧的假人段耗时）。</summary>
        public static void Reset()
        {
            _frameStart = 0;
            _botStart = 0;
            _loopSum = 0;
            _botSum = 0;
            _loopMax = 0;
            _botMax = 0;
            _frames = 0;

            LoopMs = 0;
            BotMs = 0;
            BotMsPerSecond = 0;
            LoopMaxMs = 0;
            BotMaxMs = 0;
            FramesPerSecond = 0;
        }
    }
}

