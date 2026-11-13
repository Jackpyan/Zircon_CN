using Server.Bots;
using Server.Envir;

// P4-5 纯逻辑测试：全局熔断 + 帧耗时埋点。
//
// 熔断是「自动降载」的最后一环，一旦逻辑错了会出现两种都很隐蔽的故障：
//   ① 该触发时不触发 → 服务器被假人拖死，且没有任何报错；
//   ② 不该触发时触发 → 假人再也不涨了，运营以为「水位坏了」。
// 两种都不会抛异常，所以必须在这里钉死。
//
// 帧耗时用 Thread.Sleep 造：真实时间，不 mock。为了跑得快，阈值和窗口都调小。
//
// 判据是「假人段占主循环的百分比」，所以每帧要分别控制「假人段」和「假人段之外的耗时」：
// 只睡假人段 → 占比接近 100%；只在假人段之外睡 → 占比接近 0。

int checks = 0;
int failures = 0;

void Check(bool condition, string name)
{
    checks++;

    if (condition)
    {
        Console.WriteLine($"  ok   {name}");
        return;
    }

    failures++;
    Console.WriteLine($"  FAIL {name}");
}

/// <summary>跑一帧并发布：假人段睡 botMs，假人段之外再睡 loopMs（用来压低占比）。</summary>
void PublishFrame(int botMs, int loopMs = 0)
{
    ServerPerf.BeginFrame();
    ServerPerf.BeginBots();

    if (botMs > 0) Thread.Sleep(botMs);

    ServerPerf.EndBots();

    if (loopMs > 0) Thread.Sleep(loopMs);

    ServerPerf.EndFrame();
    ServerPerf.RollSecond();
}

void Setup(bool enabled, int sharePercent = 50, int tripSeconds = 2, int recoverSeconds = 2)
{
    Config.BotBreakerEnabled = enabled;
    Config.BotBreakerSharePercent = sharePercent;
    Config.BotBreakerTripSeconds = tripSeconds;
    Config.BotBreakerRecoverSeconds = recoverSeconds;

    BotBreaker.Clear();
    ServerPerf.Reset();
}

Console.WriteLine("帧耗时埋点（P4-5）：");

Setup(false);

PublishFrame(20);

Check(ServerPerf.BotMs >= 10, $"假人段单帧被量到（实测 {ServerPerf.BotMs:0.0} ms）");
Check(ServerPerf.BotMsPerSecond >= 10, $"假人段每秒累计被量到（实测 {ServerPerf.BotMsPerSecond:0.0} ms/秒）");
Check(ServerPerf.LoopMs >= 10, $"整帧耗时被量到（实测 {ServerPerf.LoopMs:0.0} ms）");
Check(ServerPerf.FramesPerSecond == 1, $"一秒一帧 → 帧数 1（实测 {ServerPerf.FramesPerSecond}）");
Check(ServerPerf.BotSharePercent is > 50 and <= 100, $"只睡假人段 → 占比接近 100%（实测 {ServerPerf.BotSharePercent:0.0}%）");

ServerPerf.Reset();

// 只在假人段之外睡：占比应该接近 0，而不是「只要跑了就 50%」。
PublishFrame(0, 20);

Check(ServerPerf.BotSharePercent < 5, $"假人段空转 → 占比接近 0（实测 {ServerPerf.BotSharePercent:0.00}%）");
Check(ServerPerf.FramesPerSecond == 1, "仍然只算 1 帧");

Console.WriteLine();
Console.WriteLine("未启用时不动作：");

Setup(false);

PublishFrame(20);
BotBreaker.Evaluate(30);
PublishFrame(20);
BotBreaker.Evaluate(30);
PublishFrame(20);
BotBreaker.Evaluate(30);

Check(!BotBreaker.Open, "开关关闭 → 连续超标也不熔断");
Check(BotBreaker.ClampTarget(30, 30) == 30, "开关关闭 → 目标不被夹");

Console.WriteLine();
Console.WriteLine("触发：");

Setup(true, sharePercent: 50, tripSeconds: 2);

PublishFrame(20);
BotBreaker.Evaluate(30);

Check(!BotBreaker.Open, "第 1 秒只计数，不触发");
Check(BotBreaker.OverloadSeconds == 1, $"连续超标计数 = 1（实测 {BotBreaker.OverloadSeconds}）");

PublishFrame(20);
BotBreaker.Evaluate(30);

Check(BotBreaker.Open, "第 2 秒达到窗口 → 熔断");
Check(BotBreaker.Ceiling == 30, $"上限 = 触发时的在线数 30（实测 {BotBreaker.Ceiling}）");
Check(BotBreaker.TripCount == 1, $"累计触发 1 次（实测 {BotBreaker.TripCount}）");
Check(BotBreaker.OpenedAt != DateTime.MinValue, "记录了触发时刻");

Console.WriteLine();
Console.WriteLine("只停止新增，不踢在线：");

Check(BotBreaker.ClampTarget(30, 30) == 30, "在线 30、目标 30 → 原样（不触发下线分支）");
Check(BotBreaker.ClampTarget(40, 30) == 30, "目标被夹到 30（水位涨不上去）");
Check(BotBreaker.ClampTarget(30, 28) == 28, "假人自然掉到 28 → 目标跟着降到 28");
Check(BotBreaker.Ceiling == 28, $"上限同步降到 28，否则会把人补回来（实测 {BotBreaker.Ceiling}）");
Check(BotBreaker.ClampTarget(30, 28) == 28, "同一帧再夹一次仍是 28（幂等）");

// 熔断期间继续超标：上限继续跟着在线数往下带。
PublishFrame(20);
BotBreaker.Evaluate(25);

Check(BotBreaker.Ceiling == 25, $"熔断期间在线掉到 25 → 上限跟着降（实测 {BotBreaker.Ceiling}）");
Check(BotBreaker.ClampTarget(30, 25) == 25, "目标仍被夹住");

Console.WriteLine();
Console.WriteLine("恢复：");

PublishFrame(0, 20);
BotBreaker.Evaluate(25);

Check(BotBreaker.Open, "占比回落到 0% 1 秒还不够（恢复窗口 2 秒）");

PublishFrame(0, 20);
BotBreaker.Evaluate(25);

Check(!BotBreaker.Open, "持续 2 秒回落 → 解除");
Check(BotBreaker.Ceiling == -1, $"上限复位为 -1（实测 {BotBreaker.Ceiling}）");
Check(BotBreaker.ClampTarget(30, 25) == 30, "解除后目标不再被夹");

Console.WriteLine();
Console.WriteLine("滞回（阈值附近不能反复开合）：");

Setup(true, sharePercent: 50, tripSeconds: 2, recoverSeconds: 2);

PublishFrame(20);
BotBreaker.Evaluate(30);
PublishFrame(20);
BotBreaker.Evaluate(30);

Check(BotBreaker.Open, "先让它熔断");

// 占比 ~33%（10ms 假人段 / 30ms 整帧）：低于阈值 50，但高于阈值一半 25 ——
// 属于「好了但没完全好」，不能解除。
PublishFrame(10, 20);
BotBreaker.Evaluate(30);
PublishFrame(10, 20);
BotBreaker.Evaluate(30);
PublishFrame(10, 20);
BotBreaker.Evaluate(30);

Check(BotBreaker.Open, $"占比 ~33%（阈值 50 / 一半 25）→ 落在滞回区，不解除（实测 {ServerPerf.BotSharePercent:0}%）");

Console.WriteLine();
Console.WriteLine("单次尖峰不触发：");

Setup(true, sharePercent: 50, tripSeconds: 3);

PublishFrame(20);
BotBreaker.Evaluate(30);
PublishFrame(0, 20);
BotBreaker.Evaluate(30);
PublishFrame(20);
BotBreaker.Evaluate(30);

Check(!BotBreaker.Open, "超标 → 正常 → 超标：不满足「连续」3 秒，不触发");
Check(BotBreaker.OverloadSeconds == 1, $"中间那一秒把计数清零了（实测 {BotBreaker.OverloadSeconds}）");

Console.WriteLine();
Console.WriteLine("手动解除与停服清理：");

Setup(true, sharePercent: 50, tripSeconds: 2);

PublishFrame(20);
BotBreaker.Evaluate(30);
PublishFrame(20);
BotBreaker.Evaluate(30);

Check(BotBreaker.Open, "先触发");

BotBreaker.Reset("测试手动解除");

Check(!BotBreaker.Open, "Reset 立刻解除");
Check(BotBreaker.Ceiling == -1, "Reset 后上限复位");

PublishFrame(20);
BotBreaker.Evaluate(30);
PublishFrame(20);
BotBreaker.Evaluate(30);

Check(BotBreaker.Open, "解除后仍超标 → 窗口满了会重新触发（设计如此）");

BotBreaker.Clear();

Check(!BotBreaker.Open, "Clear 清掉熔断状态");
Check(BotBreaker.TripCount == 0, "Clear 清掉触发计数");

Console.WriteLine();
Console.WriteLine("运行期计数（P4-5 卡片）：");

BotMetrics.Reset();

Check(BotMetrics.MapChanges == 0 && BotMetrics.Chats == 0, "Reset 后归零");

BotMetrics.MapChange();
BotMetrics.MapChange();
BotMetrics.Chat();

Check(BotMetrics.MapChanges == 2, $"跨图计数（实测 {BotMetrics.MapChanges}）");
Check(BotMetrics.Chats == 1, $"发言计数（实测 {BotMetrics.Chats}）");

Console.WriteLine();
Console.WriteLine(new string('-', 60));
Console.WriteLine($"共 {checks} 项，失败 {failures} 项。");

return failures == 0 ? 0 : 1;
