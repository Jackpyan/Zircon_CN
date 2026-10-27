using Client.Scenes.Views;
using System;
using System.Drawing;

namespace Client.Models
{
    /// <summary>
    /// A* 寻路节点。移植自 A 端 Client\Models\Node.cs。
    ///
    /// 与 A 端的差异：
    /// 1. 节点改为**惰性创建**（A 端在构造时一次性 new 满 Width*Height 个对象，
    ///    大地图下内存占用可达数十 MB）；
    /// 2. 每次寻路前由 PathFinder 统一复位本次搜索触碰过的节点，
    ///    避免 A 端「GCost 跨次残留」导致路径质量劣化。
    /// </summary>
    public class Node : IHeapItem<Node>
    {
        /// <summary>未访问节点的代价哨兵值。取 int.MaxValue/2 避免 FCost 相加溢出。</summary>
        public const int UnsetCost = int.MaxValue >> 1;

        public readonly MapControl Map;
        public readonly Point Location;

        public Node Parent;
        public int GCost = UnsetCost;
        public int HCost;

        public int HeapIndex { get; set; }

        public Node(MapControl map, int x, int y)
        {
            Map = map;
            Location = new Point(x, y);
        }

        public bool Walkable => Map != null && Map.EmptyCell(Location);

        public int FCost => GCost + HCost;

        public int CompareTo(Node other)
        {
            if (other == null) return 1;

            int num = FCost.CompareTo(other.FCost);
            if (num == 0)
                num = HCost.CompareTo(other.HCost);

            return -num; // 取反 → 最小堆
        }

        public void Reset()
        {
            Parent = null;
            GCost = UnsetCost;
            HCost = 0;
            HeapIndex = 0;
        }
    }
}
