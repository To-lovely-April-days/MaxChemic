using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace MaxChemical.Modules.Designer.Service
{
    /// <summary>
    /// 四叉树节点（用于空间分区）
    /// </summary>
    public class QuadTreeNode<T> where T : class
    {
        private const int MaxObjectsPerNode = 10; // 每个节点最多存储的对象数
        private const int MaxDepth = 8; // 最大深度

        private readonly Rect _bounds;
        private readonly int _depth;
        private readonly List<QuadTreeObject<T>> _objects;
        private QuadTreeNode<T>[] _children;

        public QuadTreeNode(Rect bounds, int depth = 0)
        {
            _bounds = bounds;
            _depth = depth;
            _objects = new List<QuadTreeObject<T>>();
            _children = null;
        }

        /// <summary>
        /// 插入对象
        /// </summary>
        public void Insert(QuadTreeObject<T> obj)
        {
            // 如果已经分割，插入到子节点
            if (_children != null)
            {
                int index = GetQuadrant(obj.Bounds);
                if (index != -1)
                {
                    _children[index].Insert(obj);
                    return;
                }
            }

            // 添加到当前节点
            _objects.Add(obj);

            // 检查是否需要分割
            if (_objects.Count > MaxObjectsPerNode && _depth < MaxDepth)
            {
                if (_children == null)
                {
                    Split();
                }

                // 重新分配对象到子节点
                var remainingObjects = new List<QuadTreeObject<T>>();
                foreach (var o in _objects)
                {
                    int index = GetQuadrant(o.Bounds);
                    if (index != -1)
                    {
                        _children[index].Insert(o);
                    }
                    else
                    {
                        remainingObjects.Add(o);
                    }
                }
                _objects.Clear();
                _objects.AddRange(remainingObjects);
            }
        }

        /// <summary>
        /// 查询区域内的对象
        /// </summary>
        public List<QuadTreeObject<T>> Query(Rect searchArea)
        {
            var result = new List<QuadTreeObject<T>>();

            // 如果搜索区域不相交，直接返回
            if (!_bounds.IntersectsWith(searchArea))
            {
                return result;
            }

            // 添加当前节点的对象
            foreach (var obj in _objects)
            {
                if (obj.Bounds.IntersectsWith(searchArea))
                {
                    result.Add(obj);
                }
            }

            // 查询子节点
            if (_children != null)
            {
                foreach (var child in _children)
                {
                    result.AddRange(child.Query(searchArea));
                }
            }

            return result;
        }

        /// <summary>
        /// 查询点附近的对象
        /// </summary>
        public List<QuadTreeObject<T>> QueryRadius(Point center, double radius)
        {
            var searchArea = new Rect(
                center.X - radius,
                center.Y - radius,
                radius * 2,
                radius * 2
            );
            return Query(searchArea);
        }

        /// <summary>
        /// 清空四叉树
        /// </summary>
        public void Clear()
        {
            _objects.Clear();
            if (_children != null)
            {
                foreach (var child in _children)
                {
                    child.Clear();
                }
                _children = null;
            }
        }

        /// <summary>
        /// 分割节点为四个子节点
        /// </summary>
        private void Split()
        {
            double x = _bounds.X;
            double y = _bounds.Y;
            double halfWidth = _bounds.Width / 2;
            double halfHeight = _bounds.Height / 2;

            _children = new QuadTreeNode<T>[4];
            _children[0] = new QuadTreeNode<T>(new Rect(x, y, halfWidth, halfHeight), _depth + 1); // 左上
            _children[1] = new QuadTreeNode<T>(new Rect(x + halfWidth, y, halfWidth, halfHeight), _depth + 1); // 右上
            _children[2] = new QuadTreeNode<T>(new Rect(x, y + halfHeight, halfWidth, halfHeight), _depth + 1); // 左下
            _children[3] = new QuadTreeNode<T>(new Rect(x + halfWidth, y + halfHeight, halfWidth, halfHeight), _depth + 1); // 右下
        }

        /// <summary>
        /// 获取对象所属的象限（-1表示跨象限）
        /// </summary>
        private int GetQuadrant(Rect objBounds)
        {
            double midX = _bounds.X + _bounds.Width / 2;
            double midY = _bounds.Y + _bounds.Height / 2;

            bool inTop = objBounds.Bottom <= midY;
            bool inBottom = objBounds.Top >= midY;
            bool inLeft = objBounds.Right <= midX;
            bool inRight = objBounds.Left >= midX;

            if (inTop && inLeft) return 0;
            if (inTop && inRight) return 1;
            if (inBottom && inLeft) return 2;
            if (inBottom && inRight) return 3;

            return -1; // 跨象限
        }
    }

    /// <summary>
    /// 四叉树对象包装
    /// </summary>
    public class QuadTreeObject<T> where T : class
    {
        public Rect Bounds { get; set; }
        public T Data { get; set; }

        public QuadTreeObject(Rect bounds, T data)
        {
            Bounds = bounds;
            Data = data;
        }
    }

    /// <summary>
    /// 四叉树容器
    /// </summary>
    public class QuadTree<T> where T : class
    {
        private QuadTreeNode<T> _root;
        private readonly Rect _worldBounds;

        public QuadTree(Rect worldBounds)
        {
            _worldBounds = worldBounds;
            _root = new QuadTreeNode<T>(worldBounds);
        }

        public void Insert(Rect bounds, T data)
        {
            _root.Insert(new QuadTreeObject<T>(bounds, data));
        }

        public List<T> Query(Rect searchArea)
        {
            return _root.Query(searchArea).Select(obj => obj.Data).ToList();
        }

        public List<T> QueryRadius(Point center, double radius)
        {
            return _root.QueryRadius(center, radius).Select(obj => obj.Data).ToList();
        }

        public void Clear()
        {
            _root.Clear();
        }

        public void Rebuild()
        {
            _root = new QuadTreeNode<T>(_worldBounds);
        }
    }
}