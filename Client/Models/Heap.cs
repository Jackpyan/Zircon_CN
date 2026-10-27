using System;

namespace Client.Models
{
    /// <summary>
    /// 最小堆（按 CompareTo 语义），供 A* 寻路使用。
    /// 移植自 A 端 Client\Models\Heap.cs，逻辑一致，但改为**按需扩容**：
    /// A 端按 Map.Width * Map.Height 预分配数组，大地图下每次寻路都要分配数 MB，
    /// 挂机每秒多次寻路会造成明显 GC 压力，这里改为从 64 起翻倍扩容。
    /// </summary>
    public class Heap<T> where T : IHeapItem<T>
    {
        private T[] items;
        private int currentItemCount;

        public Heap(int capacity = 64)
        {
            items = new T[Math.Max(4, capacity)];
        }

        public int Count => currentItemCount;

        public void Add(T item)
        {
            if (currentItemCount == items.Length)
                Array.Resize(ref items, items.Length * 2);

            item.HeapIndex = currentItemCount;
            items[currentItemCount] = item;
            SortUp(item);
            ++currentItemCount;
        }

        public T RemoveFirst()
        {
            T obj = items[0];
            --currentItemCount;
            items[0] = items[currentItemCount];
            items[0].HeapIndex = 0;
            SortDown(items[0]);
            return obj;
        }

        public void UpdateItem(T item)
        {
            SortUp(item);
        }

        public bool Contains(T item)
        {
            return Equals(items[item.HeapIndex], item);
        }

        private void SortDown(T item)
        {
            while (true)
            {
                int index1 = item.HeapIndex * 2 + 1;
                int index2 = item.HeapIndex * 2 + 2;

                if (index1 >= currentItemCount) return;

                int index3 = index1;

                if (index2 < currentItemCount && items[index1].CompareTo(items[index2]) < 0)
                    index3 = index2;

                if (item.CompareTo(items[index3]) < 0)
                    Swap(item, items[index3]);
                else
                    return;
            }
        }

        private void SortUp(T item)
        {
            int index = (item.HeapIndex - 1) / 2;

            while (true)
            {
                T obj = items[index];

                if (item.CompareTo(obj) > 0)
                {
                    Swap(item, obj);
                    index = (item.HeapIndex - 1) / 2;
                }
                else
                {
                    return;
                }
            }
        }

        private void Swap(T itemA, T itemB)
        {
            items[itemA.HeapIndex] = itemB;
            items[itemB.HeapIndex] = itemA;

            int heapIndex = itemA.HeapIndex;
            itemA.HeapIndex = itemB.HeapIndex;
            itemB.HeapIndex = heapIndex;
        }
    }
}
