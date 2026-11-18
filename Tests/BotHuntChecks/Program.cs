using System.Drawing;
using Server.Bots;

// 步骤 2-2 负载负权重分散（D5）纯函数测试。
//
// HuntLoadWeight 是选点评分的负载分（点级 + 区域级双层），算错会导致
// 假人扎堆或分散失效，且没有报错可查 —— 所以把边界与去重逻辑钉死。
// 纯函数，不碰数据库、不起服务器。距离口径与引擎一致（切比雪夫，含对角）。

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

Point P(int x, int y) => new Point(x, y);

// ---------- 空列表 / null ----------

Check(BotPerception.HuntLoadWeight(P(0, 0), null, 5, null) == 0, "假人位置 null → 负载 0");
Check(BotPerception.HuntLoadWeight(P(0, 0), new List<Point>(), 5, null) == 0, "空假人列表 → 负载 0");

// ---------- 点级 ----------

// 候选点 (0,0)，半径 5。切比雪夫距离 <=5 的假人计入点级。
var pointOnly = new List<Point> { P(3, 4), P(5, 0), P(-5, 0), P(0, 5), P(6, 0) };

// (3,4) 距离 4、(5,0) 距离 5、(-5,0) 距离 5、(0,5) 距离 5 → 4 个；(6,0) 距离 6 不计入。
Check(BotPerception.HuntLoadWeight(P(0, 0), pointOnly, 5, null) == 4,
    "点级：切比雪夫距离 <=5 的 4 个假人计入，距离 6 的不计入");

// 边界：距离恰好 5 计入（<= 而非 <）。
Check(BotPerception.HuntLoadWeight(P(0, 0), new List<Point> { P(5, 5) }, 5, null) == 1,
    "点级边界：距离恰好 5（对角）计入");

// 半径 0：只有同格假人计入。
Check(BotPerception.HuntLoadWeight(P(0, 0), new List<Point> { P(0, 0), P(1, 0) }, 0, null) == 1,
    "点级半径 0：只有同格假人计入");

// ---------- 区域级 ----------

// 候选点 (0,0)，半径 5。假人 (20, 20) 点级够不到，但在包围盒 (10..30, 10..30) 内 → 区域级计入。
var regionBox = new BotPerception.HuntRegionBox(10, 30, 10, 30);
Check(BotPerception.HuntLoadWeight(P(0, 0), new List<Point> { P(20, 20) }, 5, regionBox) == 1,
    "区域级：点级够不到但落在刷怪区包围盒内的假人计入");

// 假人 (40, 40) 点级够不到、区域外 → 不计入。
Check(BotPerception.HuntLoadWeight(P(0, 0), new List<Point> { P(40, 40) }, 5, regionBox) == 0,
    "区域级：点级够不到且区域外的假人不计入");

// regionBox 为 null → 只算点级，不做区域级。
Check(BotPerception.HuntLoadWeight(P(0, 0), new List<Point> { P(20, 20) }, 5, null) == 0,
    "regionBox null → 只算点级，区域外假人不计入");

// ---------- 去重：点级已计的不重复计入区域级 ----------

// 假人 (3,3) 点级距离 3 <=5 已计入；它也在包围盒 (0..10, 0..10) 内，但不应重复计。
Check(BotPerception.HuntLoadWeight(P(0, 0), new List<Point> { P(3, 3) }, 5,
    new BotPerception.HuntRegionBox(0, 10, 0, 10)) == 1,
    "去重：点级已计入的假人不重复计入区域级（只算 1）");

// 两个假人：(3,3) 点级计入，(20,20) 区域级计入 → 合计 2。
Check(BotPerception.HuntLoadWeight(P(0, 0), new List<Point> { P(3, 3), P(20, 20) }, 5,
    new BotPerception.HuntRegionBox(0, 30, 0, 30)) == 2,
    "点级 + 区域级合计：点级 1 + 区域级 1 = 2");

// ---------- 多假人累加 ----------

var many = new List<Point>
{
    P(1, 1), P(2, 2), P(-1, -1),   // 点级 3 个
    P(15, 15), P(25, 25),           // 区域级 2 个（包围盒 10..30）
    P(50, 50),                      // 都不沾
};
Check(BotPerception.HuntLoadWeight(P(0, 0), many, 5, regionBox) == 5,
    "多假人累加：点级 3 + 区域级 2 = 5，区域外不计入");

// ---------- 步骤 2-3 跨假人共享不可达点黑名单（D6，BotBadHuntPoints） ----------
//
// 不可达是地形属性，标记跨假人共享：一个假人拉黑某点，同图其它假人也避开。
// 钉死：标记/查询、跨图隔离（键含地图索引）、TTL 过期、Prune、重标记续期。
// 用独立坐标+地图索引，避免与其它用例互相干扰；测试结束后清场。

int badMapA = 9001, badMapB = 9002;
Point badP1 = P(100, 100);

// 未标记 → 不是坏点。
Check(!BotBadHuntPoints.IsBad(badMapA, badP1), "未标记 → IsBad 返回 false");

// 标记后 → 查询命中。
BotBadHuntPoints.Mark(badMapA, badP1);
Check(BotBadHuntPoints.IsBad(badMapA, badP1), "标记后 → IsBad 返回 true（共享给同图所有假人）");

// 跨图隔离：同坐标不同地图是不同点，B 图不受影响。
Check(!BotBadHuntPoints.IsBad(badMapB, badP1), "跨图隔离：A 图标记不影响 B 图同坐标点");

// 其它坐标不受影响。
Check(!BotBadHuntPoints.IsBad(badMapA, P(101, 100)), "其它坐标不受影响");

// TTL 过期：短 TTL 标记，等待过期后查询返回 false 且条目被清掉。
Point badP2 = P(200, 200);
BotBadHuntPoints.Mark(badMapA, badP2, 60);
Check(BotBadHuntPoints.IsBad(badMapA, badP2), "短 TTL：过期前 IsBad 命中");
System.Threading.Thread.Sleep(120);
Check(!BotBadHuntPoints.IsBad(badMapA, badP2), "短 TTL：过期后 IsBad 返回 false");

// Prune 清过期：标记短 TTL 后等待过期，Prune 前 Count 不变（条目还在），Prune 后减少。
Point badP3 = P(300, 300);
int countBefore = BotBadHuntPoints.Count;
BotBadHuntPoints.Mark(badMapA, badP3, 60);
Check(BotBadHuntPoints.Count == countBefore + 1, "标记后 Count +1");
System.Threading.Thread.Sleep(120);
BotBadHuntPoints.Prune();
Check(BotBadHuntPoints.Count == countBefore, "Prune：清掉过期条目，Count 回落");

// 重标记续期：过期前重标记，TTL 重新起算仍命中。
Point badP4 = P(400, 400);
BotBadHuntPoints.Mark(badMapA, badP4, 200);
System.Threading.Thread.Sleep(100);
BotBadHuntPoints.Mark(badMapA, badP4, 200);
System.Threading.Thread.Sleep(100);
Check(BotBadHuntPoints.IsBad(badMapA, badP4), "重标记续期：第二次标记后仍在有效期内");

// 清场：把测试期间的剩余标记用极短 TTL 放干净（避免静态表残留影响后续重跑）。
BotBadHuntPoints.Mark(badMapA, badP1, 1);
BotBadHuntPoints.Mark(badMapA, badP4, 1);
System.Threading.Thread.Sleep(30);
BotBadHuntPoints.Prune();
Check(BotBadHuntPoints.Count == 0, "清场：Prune 后共享表归零");

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"全部通过：{checks} 项"
    : $"失败 {failures} / {checks} 项");

return failures == 0 ? 0 : 1;
