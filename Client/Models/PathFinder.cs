using Client.Scenes.Views;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Client.Models
{
    /// <summary>
    /// 客户端 A* 寻路。移植自 A 端 Client\Models\PathFinder.cs。
    ///
    /// 为什么需要它：B 端原本的自动寻路是**服务端驱动**的
    /// （AutoPathService 下发 AutoPathRoute，客户端只负责按路线走），
    /// 没有「本地给定目标点算一条路」的能力。挂机 AI 需要频繁地
    /// 朝附近的怪物/随机点移动，走服务端路线代价太大（要发包、且会
    /// 让 AutoPathRoute != null 从而跳过 MapControl.ProcessInput），
    /// 因此这里补一份轻量本地寻路。
    ///
    /// 与 A 端的差异：
    /// 1. 节点惰性创建（不预分配 Width*Height 个 Node）；
    /// 2. 每次寻路前复位上次触碰过的节点（避免 GCost 残留）；
    /// 3. 邻居遍历复用固定数组（不每节点 new List）；
    /// 4. 增加迭代上限，防止极端地图卡帧。
    /// </summary>
    public class PathFinder
    {
        private const int IterationLimit = 20000;

        private readonly Node[,] _grid;
        private readonly List<Node> _touched = new List<Node>();
        private readonly Node[] _neighbourBuffer = new Node[8];

        public MapControl Map { get; }

        public int MaxSize => Map == null ? 0 : Map.Width * Map.Height;

        public PathFinder(MapControl map)
        {
            Map = map;

            if (map != null && map.Width > 0 && map.Height > 0)
                _grid = new Node[map.Width, map.Height];
        }

        /// <summary>
        /// 计算从 start 到 target 的路径（含起点、不含终点以外的点）。
        /// 起点与终点相同 → 返回单元素列表；不可达 → 返回 null。
        /// </summary>
        public List<Node> FindPath(Point start, Point target)
        {
            if (Map == null || _grid == null || Map.Cells == null) return null;

            ResetTouched();

            Node startNode = GetNode(start);
            Node targetNode = GetNode(target);

            if (startNode == null || targetNode == null) return null;

            if (startNode == targetNode)
                return new List<Node> { startNode };

            Touch(startNode);
            startNode.GCost = 0;
            startNode.HCost = GetDistance(startNode, targetNode);
            startNode.Parent = null;

            Heap<Node> open = new Heap<Node>();
            HashSet<Node> openSet = new HashSet<Node>();
            HashSet<Node> closed = new HashSet<Node>();

            open.Add(startNode);
            openSet.Add(startNode);

            int iterations = 0;

            while (open.Count > 0 && iterations++ < IterationLimit)
            {
                Node current = open.RemoveFirst();
                openSet.Remove(current);
                closed.Add(current);

                if (current == targetNode)
                    return RetracePath(startNode, targetNode);

                int count = GetNeighbours(current, _neighbourBuffer);

                for (int i = 0; i < count; i++)
                {
                    Node neighbour = _neighbourBuffer[i];

                    if (!neighbour.Walkable || closed.Contains(neighbour)) continue;

                    int newCost = current.GCost + GetDistance(current, neighbour);

                    if (newCost >= neighbour.GCost) continue;

                    Touch(neighbour);
                    neighbour.GCost = newCost;
                    neighbour.HCost = GetDistance(neighbour, targetNode);
                    neighbour.Parent = current;

                    if (!openSet.Contains(neighbour))
                    {
                        openSet.Add(neighbour);
                        open.Add(neighbour);
                    }
                    else
                    {
                        open.UpdateItem(neighbour);
                    }
                }
            }

            return null;
        }

        private List<Node> RetracePath(Node startNode, Node endNode)
        {
            List<Node> path = new List<Node>();

            for (Node node = endNode; node != null && node != startNode; node = node.Parent)
                path.Add(node);

            path.Add(startNode);
            path.Reverse();

            return path;
        }

        private static int GetDistance(Node a, Node b)
        {
            int dx = Math.Abs(a.Location.X - b.Location.X);
            int dy = Math.Abs(a.Location.Y - b.Location.Y);

            return dx > dy
                ? 14 * dy + 10 * (dx - dy)
                : 14 * dx + 10 * (dy - dx);
        }

        private int GetNeighbours(Node node, Node[] buffer)
        {
            int count = 0;
            int x = node.Location.X;
            int y = node.Location.Y;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;

                    int nx = x + dx;
                    int ny = y + dy;

                    if (nx < 0 || ny < 0 || nx >= Map.Width || ny >= Map.Height) continue;

                    buffer[count++] = GetNodeUnchecked(nx, ny);
                }
            }

            return count;
        }

        private Node GetNode(Point location)
        {
            if (Map == null || _grid == null) return null;

            int x = location.X;
            int y = location.Y;

            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x >= Map.Width) x = Map.Width - 1;
            if (y >= Map.Height) y = Map.Height - 1;

            return GetNodeUnchecked(x, y);
        }

        private Node GetNodeUnchecked(int x, int y)
        {
            Node node = _grid[x, y];

            if (node == null)
            {
                node = new Node(Map, x, y);
                _grid[x, y] = node;
            }

            return node;
        }

        private void Touch(Node node)
        {
            if (node.GCost != Node.UnsetCost) return;

            _touched.Add(node);
        }

        private void ResetTouched()
        {
            for (int i = 0; i < _touched.Count; i++)
                _touched[i].Reset();

            _touched.Clear();
        }
    }
}
