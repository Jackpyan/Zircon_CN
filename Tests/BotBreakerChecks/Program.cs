using Library;
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
Console.WriteLine("新号与运行参数默认值（P5-8）：");

// 用户实测踩过的坑：BotNewbieGold 默认 5000 → 六合石跨图一次要几千金，
// 假人判「没钱」只能退化成 A* 逐格走，洞穴图一绕就规划失败、卡死在半路。
Check(Config.BotNewbieGold >= 100000,
    $"启动金币默认值够跨图传送（实测 {Config.BotNewbieGold:N0}，5000 连一趟都不够）");
Check(Config.BotNewbieGold / 10 == 50000,
    $"补发阈值 = 启动金币的 10%（实测 {Config.BotNewbieGold / 10:N0}，掉到它以下才补）");
Check(Config.BotNewbiePotionCount is > 0 and <= 20,
    $"启动药水不会一出生就超重（实测 {Config.BotNewbiePotionCount} 瓶，红蓝各这么多）");
Check(Config.BotNewbieTownScrollCount is > 0 and <= 10,
    $"启动回城卷够跑一次闭环（实测 {Config.BotNewbieTownScrollCount} 张）");
Check(Config.BotStartupRampSeconds >= 1 && Config.BotRosterIntervalSeconds >= 1,
    $"铺量/重算间隔都 ≥ 1 秒（实测 {Config.BotStartupRampSeconds} s / {Config.BotRosterIntervalSeconds} s）");
Check(Config.BotStepDelayMaxMs >= Config.BotStepDelayMinMs,
    $"上线分步间隔上限不小于下限（实测 {Config.BotStepDelayMinMs}~{Config.BotStepDelayMaxMs} ms）");
Check(Config.BotLogLevel is >= 0 and <= 3,
    $"日志级别落在 0~3（实测 {Config.BotLogLevel}；调 2 会把正常事件也藏起来）");
Check(Config.BotNightMinHour != Config.BotNightMaxHour,
    $"夜间时段起止不相同（实测 {Config.BotNightMinHour}~{Config.BotNightMaxHour} 点）");
Check(Config.BotNightScalePercent is > 0 and <= 100,
    $"夜间比例在 1~100（实测 {Config.BotNightScalePercent}%）");

// ── 群攻技能表自洽（2026-11-13）────────────────────────────────
// BotSkills.GroupWanted 里的技能必须同时出现在 Wanted 里，否则假人**永远不会学它**
// ——「群攻」就成了一段永远走不到的死代码。这个检查专门防这种手写表漏配。
foreach (MirClass cls in new[] { MirClass.Wizard, MirClass.Taoist })
{
    var learned = BotSkills.Wanted(cls).ToHashSet();
    var groups = BotSkills.GroupWanted(cls).ToList();

    Check(groups.Count > 0, $"{cls} 配了群攻技能（实测 {groups.Count} 个）");

    foreach (MagicType group in groups)
    {
        Check(learned.Contains(group),
            $"{cls} 的群攻 {group} 也在补技能名单里（不在的话永远学不会）");
    }
}

Check(!BotSkills.GroupWanted(MirClass.Warrior).Any(), "战士没有群攻（平砍就是输出）");

// ── 弹药表自洽（2026-11-13）────────────────────────────────────
// 三条不变量，都是「手写表漏配就会静默失效」的类型：
// ① 施毒术是纯 debuff（只 ApplyPoison、零直接伤害），**绝不能**当攻击技能选出来 ——
//    否则 12 级道士会站在 10 格外一直放毒、永不贴脸砍（用户 2026-11-13 报的就是这个）。
// ② 要弹药的技能必须都在补技能名单里，否则买回来的护身符/毒药粉永远用不上。
// ③ 反过来：攻击/群攻表里的技能如果漏登记弹药，就会「没弹药也照选」→ 空放（有动画、扣蓝、零伤害）。
var taoistLearned = BotSkills.Wanted(MirClass.Taoist).ToHashSet();

Check(!BotSkills.AttackSkillsOf(MirClass.Taoist).Contains(MagicType.PoisonDust),
    "施毒术不在道士攻击技能表里（纯 debuff，只能当开场）");

Check(taoistLearned.Contains(MagicType.PoisonDust),
    "施毒术仍在补技能名单里（不在的话 TryOpenWithPoison 永远放不出来）");

foreach (MagicType ammo in BotSkills.AmmoSkillTypes)
{
    bool learned = BotSkills.Wanted(MirClass.Taoist).Contains(ammo)
                   || BotSkills.Wanted(MirClass.Wizard).Contains(ammo)
                   || BotSkills.GroupWanted(MirClass.Taoist).Contains(ammo)
                   || BotSkills.GroupWanted(MirClass.Wizard).Contains(ammo);

    Check(learned, $"要弹药的 {ammo} 也在某个补技能名单里（不在的话弹药白买）");

    EquipmentSlot slot = BotSkills.AmmoSlotOf(ammo);

    Check(slot == EquipmentSlot.Amulet || slot == EquipmentSlot.Poison,
        $"要弹药的 {ammo} 登记在 护身符/毒药 槽（实测 {slot}）");
}

// ── 按元素抗性选技能（2026-11-13）──────────────────────────────
// 用户报：「怪物是有元素抗性的，别用怪物抗性高的技能，半天打不掉还浪费蓝药」。
// 本库 309 只怪里 204 只有非零抗性（`tools/monq ... stat` 实测，最高 10 = 完全免疫），
// 所以选技能不能只看等级门槛。下面把「抗性 → 系数 → 排序」三层都钉死。
Console.WriteLine();
Console.WriteLine("── 元素抗性 → 技能选择 ──");

// ① 系数口径必须与引擎 PlayerObject.MagicAttack 一致（**魔法路径负抗也是 /10，不是 /5**）：
//    res 0 → 1.0；+5 → 0.5；+10 → 0（免疫）；-3 → 1.3（弱点，多受 30%）
Check(Math.Abs(BotSkills.ResistanceMultiplier(0) - 1.0) < 1e-9, "抗性 0 → 系数 1.0（无影响）");
Check(Math.Abs(BotSkills.ResistanceMultiplier(5) - 0.5) < 1e-9, "抗性 +5 → 系数 0.5（减半）");
Check(Math.Abs(BotSkills.ResistanceMultiplier(10) - 0.0) < 1e-9, "抗性 +10 → 系数 0（完全免疫）");
Check(Math.Abs(BotSkills.ResistanceMultiplier(7) - 0.3) < 1e-9, "抗性 +7 → 系数 0.3（只剩三成伤害）");
Check(Math.Abs(BotSkills.ResistanceMultiplier(-3) - 1.3) < 1e-9,
    "抗性 -3 → 系数 1.3（魔法路径是 /10；只有平砍才 /5，抄错不会崩但会排错）");

// ② 排序：免疫的绝不选；被抗的高阶 vs 吃满的低阶，看「系数 × (门槛+10)」
Check(BotSkills.ChooseBest(new[]
{
    (MagicType.FireBall, 7, 1.0),
    (MagicType.MeteorShower, 45, 0.0),
}) == MagicType.FireBall, "高阶技能被完全免疫 → 退回低阶（免疫的永远不选）");

Check(BotSkills.ChooseBest(new[]
{
    (MagicType.FireBall, 7, 1.0),
    (MagicType.ThunderBolt, 26, 0.5),
}) == MagicType.ThunderBolt, "高阶减伤 50% 仍胜过低阶吃满（0.5×36 > 1.0×17）");

Check(BotSkills.ChooseBest(new[]
{
    (MagicType.FireBall, 7, 1.0),
    (MagicType.IceBolt, 7, 0.3),
}) == MagicType.FireBall, "同门槛下挑不被抗的那个");

Check(BotSkills.ChooseBest(new[]
{
    (MagicType.FireBall, 7, 0.0),
    (MagicType.IceBolt, 7, 0.0),
}) == MagicType.None, "全部免疫 → None（退回平砍，别再空放扣蓝）");

Check(BotSkills.ChooseBest(Array.Empty<(MagicType, int, double)>()) == MagicType.None,
    "没有候选 → None");

// ③ 候选源：攻击候选只能是纯伤害表（不能含治疗 / 施毒术），
//    否则 12 级道士（还没灵魂火符）会把施毒术当攻击技能，站远处一直放毒。
foreach (MirClass cls in new[] { MirClass.Wizard, MirClass.Taoist, MirClass.Warrior })
{
    var attacks = BotSkills.AttackSkillsOf(cls).ToList();

    Check(!attacks.Contains(MagicType.Heal) && !attacks.Contains(MagicType.MassHeal),
        $"{cls} 的攻击候选里没有治疗技能");

    Check(!attacks.Contains(MagicType.PoisonDust),
        $"{cls} 的攻击候选里没有施毒术（纯 debuff）");

    Check(attacks.All(x => BotSkills.Wanted(cls).Contains(x)),
        $"{cls} 的攻击候选都在补技能名单里（否则永远学不会）");
}

Check(BotSkills.ElementName(Element.Fire) == "火" && BotSkills.ElementName(Element.None) == "物理",
    "元素中文名（日志/后台要能看懂）");

Console.WriteLine();
Console.WriteLine(new string('-', 60));
Console.WriteLine($"共 {checks} 项，失败 {failures} 项。");

return failures == 0 ? 0 : 1;
