using Library.SystemModels;
using Server.Envir;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Server.Models.AutoPath
{
    internal static class AutoPathLogger
    {
        public static void Started(MapObject actor, MapInfo sourceMap, Point source, MapInfo destinationMap, Point destination)
        {
            Log("STARTED", actor, sourceMap, source, destinationMap, destination);
        }

        public static void Ended(MapObject actor, MapInfo sourceMap, Point source, MapInfo destinationMap, Point destination)
        {
            Log("ENDED", actor, sourceMap, source, destinationMap, destination);
        }

        /// <summary>
        /// 结束时的补充说明：**停在哪、用了多久、离终点还有多远**。
        ///
        /// <para>
        /// <c>ENDED</c> 那行里的坐标是「起点」，所以光看日志分不清「走到了」和「原地卡了 30 秒被放弃」——
        /// 这两种在假人侧的表现完全不同：前者正常，后者会触发大脑的「30 秒没能靠近一格，换张图卖」，
        /// 白扔一张传送卷还把整张图拉黑 5 分钟。把落点补上就能一眼分开。
        /// </para>
        /// </summary>
        public static void Stopped(MapObject actor, MapInfo destinationMap, Point destination, Point current, MapInfo currentMap, long elapsedMs, int remaining)
        {
            string where = currentMap == null ? "未知地图" : GetMapDescription(currentMap);

            SEnvir.Log($"[AUTO PATH STOPPED] {GetActorDescription(actor)} 用时 {elapsedMs / 1000d:F1}s，" +
                       $"停在 {where} ({current.X}, {current.Y})，距终点 " +
                       $"{(currentMap == destinationMap ? Distance(current, destination).ToString() + " 格" : "跨图")}，" +
                       $"剩余路径 {remaining} 点。");
        }

        /// <summary>
        /// 长路周期追踪：一条自动寻路跑了超过 10 秒还没结束，就每 10 秒报一次「在哪、还剩多少」。
        /// 用来区分「稳步在走但路远」和「一直在原地/来回摆」—— 后者是 <c>StallCount</c> 抓不到的
        /// （每迈出一步都会清零，来回摆动也算「动了」）。
        /// </summary>
        public static void Progress(MapObject actor, MapInfo destinationMap, Point destination, Point current, MapInfo currentMap, long elapsedMs, int remaining, int stallCount)
        {
            string where = currentMap == null ? "未知地图" : GetMapDescription(currentMap);

            SEnvir.Log($"[AUTO PATH TRACE] {GetActorDescription(actor)} 已走 {elapsedMs / 1000d:F1}s，" +
                       $"当前 {where} ({current.X}, {current.Y})，距终点 " +
                       $"{(currentMap == destinationMap ? Distance(current, destination).ToString() + " 格" : "跨图")}，" +
                       $"剩余路径 {remaining} 点，停滞计数 {stallCount}。");
        }

        private static int Distance(Point a, Point b)
        {
            return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        /// <summary>同类失败在这个间隔内只打一行（热循环时否则会一秒刷几十行）。</summary>
        private const int FailureLogIntervalMs = 10000;

        /// <summary>节流表的安全上限 —— 原因是带坐标的字符串，不设上限会随地图探索无限长。</summary>
        private const int FailureKeyLimit = 2000;

        private static readonly Dictionary<string, (long Tick, int Count)> Failures =
            new Dictionary<string, (long Tick, int Count)>();

        /// <summary>
        /// 自动寻路被放弃时打一行**带原因**的日志。
        ///
        /// <para>
        /// 没有这一行的话，线上只能看到 <c>STARTED</c> 和 <c>ENDED</c> 落在同一秒，
        /// 而失败有八九种可能（角色死亡 / 起点格不存在 / 目标集合空 / 出口被规则卡住 /
        /// 避让活物的 A* 无路可走……），根本没法定位。
        /// </para>
        /// </summary>
        public static void Failed(MapObject actor, string destination, string reason)
        {
            string key = (actor?.Name ?? "?") + "|" + reason;
            long now = Environment.TickCount64;
            int count = 1;

            if (Failures.TryGetValue(key, out (long Tick, int Count) record))
            {
                if (now - record.Tick < FailureLogIntervalMs)
                {
                    Failures[key] = (record.Tick, record.Count + 1);
                    return;
                }

                count = record.Count + 1;
            }

            if (Failures.Count > FailureKeyLimit) Failures.Clear();

            Failures[key] = (now, count);

            SEnvir.Log($"[AUTO PATH FAIL] {GetActorDescription(actor)} 目的地 {destination}，原因：{reason}" +
                       (count > 1 ? $"（此前同类失败 {count - 1} 次已折叠）" : ""));
        }

        private static void Log(string status, MapObject actor, MapInfo sourceMap, Point source, MapInfo destinationMap, Point destination)
        {
            SEnvir.Log($"[AUTO PATH {status}] {GetActorDescription(actor)} from {GetMapDescription(sourceMap)} ({source.X}, {source.Y}) to {GetMapDescription(destinationMap)} ({destination.X}, {destination.Y}).");
        }

        private static string GetActorDescription(MapObject actor)
        {
            switch (actor)
            {
                case PlayerObject player:
                    return $"Player '{player.Name}'";
                case MonsterObject monster:
                    return $"Monster '{monster.MonsterInfo?.MonsterName ?? "Unknown"}'";
                default:
                    return $"{actor?.Race.ToString() ?? "Object"} '{actor?.Name ?? "Unknown"}'";
            }
        }

        private static string GetMapDescription(MapInfo map)
        {
            if (map == null) return "Unknown Map [Unknown File]";

            return $"{map.Description ?? "Unknown Map"} [{map.FileName ?? "Unknown File"}]";
        }
    }
}
