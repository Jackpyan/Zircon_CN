using System;

namespace Client.Models
{
    /// <summary>
    /// 二叉堆元素接口。移植自 A 端 Client\Models\Heap.cs 的配套接口。
    /// </summary>
    public interface IHeapItem<T> : IComparable<T>
    {
        int HeapIndex { get; set; }
    }
}
