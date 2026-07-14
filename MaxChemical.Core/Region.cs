using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace MaxChemical.Core
{
    // 单元格
    public class RegionCell
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public RegionType Type { get; set; } = RegionType.None;
    }

    // 区域布局
    public class RegionLayout
    {
        public int Rows { get; set; } = 1;
        public int Cols { get; set; } = 1;

        public List<RegionCell> Cells { get; set; } = new();

        // 存储每列和每行的绝对尺寸（像素）
        public List<double> ColumnWidths { get; set; } = new List<double>();
        public List<double> RowHeights { get; set; } = new List<double>();


        // 新增：独立区域列表
        public List<IndependentRegion> IndependentRegions { get; set; } = new List<IndependentRegion>();

        // 新增：是否使用独立区域模式
        public bool UseIndependentRegions { get; set; } = true;
        /// 读取：若不存在返回默认 RegionType.None 的虚拟格
        public RegionCell Cell(int r, int c)
        {
            return Cells.FirstOrDefault(x => x.Row == r && x.Col == c)
                   ?? new RegionCell { Row = r, Col = c, Type = RegionType.None };
        }

        /// 写入：存在则更新，不存在则添加
        public void Set(int r, int c, RegionType t)
        {
            var cell = Cells.FirstOrDefault(x => x.Row == r && x.Col == c);
            if (cell == null)
            {
                Cells.Add(new RegionCell { Row = r, Col = c, Type = t });
            }
            else
            {
                cell.Type = t;
            }
        }

        public void EnsureSize(int wantRows, int wantCols)
        {
            if (wantRows > Rows) Rows = wantRows;
            if (wantCols > Cols) Cols = wantCols;

            // 为所有新行新列补齐默认单元
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (!Cells.Any(x => x.Row == r && x.Col == c))
                        Cells.Add(new RegionCell { Row = r, Col = c, Type = RegionType.None });

            // 初始化尺寸数组
            InitializeSizes();
        }

        /// <summary>
        /// 初始化尺寸数组 - 如果为空则设置默认值
        /// </summary>
        public void InitializeSizes(double defaultColumnWidth = 200, double defaultRowHeight = 200)
        {
            // 初始化列宽
            while (ColumnWidths.Count < Cols)
            {
                ColumnWidths.Add(defaultColumnWidth);
            }

            // 初始化行高
            while (RowHeights.Count < Rows)
            {
                RowHeights.Add(defaultRowHeight);
            }

            // 移除多余的尺寸
            if (ColumnWidths.Count > Cols)
            {
                ColumnWidths.RemoveRange(Cols, ColumnWidths.Count - Cols);
            }

            if (RowHeights.Count > Rows)
            {
                RowHeights.RemoveRange(Rows, RowHeights.Count - Rows);
            }
        }

        /// <summary>
        /// 从画布总尺寸初始化均等的行列尺寸
        /// </summary>
        public void InitializeFromTotalSize(double totalWidth, double totalHeight)
        {
            ColumnWidths.Clear();
            RowHeights.Clear();

            double avgColumnWidth = totalWidth / Cols;
            double avgRowHeight = totalHeight / Rows;

            for (int i = 0; i < Cols; i++)
            {
                ColumnWidths.Add(avgColumnWidth);
            }

            for (int i = 0; i < Rows; i++)
            {
                RowHeights.Add(avgRowHeight);
            }
        }

        /// <summary>
        /// 获取指定列的宽度
        /// </summary>
        public double GetColumnWidth(int col)
        {
            if (col < 0 || col >= ColumnWidths.Count) return 0;
            return ColumnWidths[col];
        }

        /// <summary>
        /// 获取指定行的高度
        /// </summary>
        public double GetRowHeight(int row)
        {
            if (row < 0 || row >= RowHeights.Count) return 0;
            return RowHeights[row];
        }

        /// <summary>
        /// 获取指定列的起始X坐标
        /// </summary>
        public double GetColumnStartX(int col)
        {
            if (col <= 0) return 0;

            double x = 0;
            for (int i = 0; i < col && i < ColumnWidths.Count; i++)
            {
                x += ColumnWidths[i];
            }
            return x;
        }

        /// <summary>
        /// 获取指定行的起始Y坐标
        /// </summary>
        public double GetRowStartY(int row)
        {
            if (row <= 0) return 0;

            double y = 0;
            for (int i = 0; i < row && i < RowHeights.Count; i++)
            {
                y += RowHeights[i];
            }
            return y;
        }

        /// <summary>
        /// 调整指定列的宽度（增量方式）
        /// </summary>
        public void AdjustColumnWidth(int col, double deltaWidth)
        {
            if (col < 0 || col >= ColumnWidths.Count) return;

            double newWidth = ColumnWidths[col] + deltaWidth;
            newWidth = System.Math.Max(50, newWidth); // 确保最小宽度
            ColumnWidths[col] = newWidth;
        }

        /// <summary>
        /// 调整指定行的高度（增量方式）
        /// </summary>
        public void AdjustRowHeight(int row, double deltaHeight)
        {
            if (row < 0 || row >= RowHeights.Count) return;

            double newHeight = RowHeights[row] + deltaHeight;
            newHeight = System.Math.Max(50, newHeight); // 确保最小高度
            RowHeights[row] = newHeight;
        }

        /// <summary>
        /// 获取画布总宽度
        /// </summary>
        public double GetTotalWidth()
        {
            return ColumnWidths.Sum();
        }

        /// <summary>
        /// 获取画布总高度
        /// </summary>
        public double GetTotalHeight()
        {
            return RowHeights.Sum();
        }

        /// <summary>
        /// 自动合并相邻的同类型区域
        /// </summary>
        public void AutoMergeRegions()
        {
            // 标记已访问的单元格
            bool[,] visited = new bool[Rows, Cols];

            // 存储合并后的区域信息
            List<MergedRegion> mergedRegions = new List<MergedRegion>();

            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    if (!visited[r, c])
                    {
                        var cell = Cell(r, c);
                        if (cell.Type != RegionType.None)
                        {
                            // 使用洪水填充算法找到连通的同类型区域
                            var region = FloodFillRegion(r, c, cell.Type, visited);
                            // 🔥 修改：包括单个单元格的区域也加入合并区域列表
                            if (region.Cells.Count >= 1) // 原来是 > 1，现在改为 >= 1
                            {
                                mergedRegions.Add(region);
                            }
                        }
                    }
                }
            }

            // 更新合并区域信息
            MergedRegions = mergedRegions;

        }


        /// <summary>
        /// 洪水填充算法找到连通区域
        /// </summary>
        private MergedRegion FloodFillRegion(int startR, int startC, RegionType regionType, bool[,] visited)
        {
            var region = new MergedRegion { Type = regionType };
            var stack = new Stack<(int r, int c)>();
            stack.Push((startR, startC));

            while (stack.Count > 0)
            {
                var (r, c) = stack.Pop();

                if (r < 0 || r >= Rows || c < 0 || c >= Cols || visited[r, c])
                    continue;

                var cell = Cell(r, c);
                if (cell.Type != regionType)
                    continue;

                visited[r, c] = true;
                region.Cells.Add(new Point(c, r));

                // 检查四个方向的邻居
                stack.Push((r - 1, c)); // 上
                stack.Push((r + 1, c)); // 下
                stack.Push((r, c - 1)); // 左
                stack.Push((r, c + 1)); // 右
            }

            // 计算区域边界
            region.CalculateBounds();
            return region;
        }
        /// <summary>
        /// 检查指定位置是否在某个合并区域内
        /// </summary>
        public MergedRegion GetMergedRegionAt(int row, int col)
        {
            return MergedRegions?.FirstOrDefault(region =>
                region.Cells.Any(cell => (int)cell.X == col && (int)cell.Y == row));
        }

        // 存储合并后的区域
        public List<MergedRegion> MergedRegions { get; set; } = new List<MergedRegion>();



        /// <summary>
        /// 局部调整指定区域的边界
        /// </summary>
        public void AdjustRegionBoundary(MergedRegion region, RegionBoundary boundary, double delta)
        {
            switch (boundary)
            {
                case RegionBoundary.Left:
                    AdjustRegionLeftBoundary(region, delta);
                    break;
                case RegionBoundary.Right:
                    AdjustRegionRightBoundary(region, delta);
                    break;
                case RegionBoundary.Top:
                    AdjustRegionTopBoundary(region, delta);
                    break;
                case RegionBoundary.Bottom:
                    AdjustRegionBottomBoundary(region, delta);
                    break;
            }
        }

        /// <summary>
        /// 调整区域左边界
        /// </summary>
        private void AdjustRegionLeftBoundary(MergedRegion region, double delta)
        {
            // 只调整该区域最左列的宽度
            int leftCol = region.MinCol;
            if (leftCol >= 0 && leftCol < ColumnWidths.Count)
            {
                double newWidth = ColumnWidths[leftCol] + delta;
                newWidth = Math.Max(50, newWidth); // 最小宽度
                ColumnWidths[leftCol] = newWidth;

               
            }
        }

        /// <summary>
        /// 调整区域右边界
        /// </summary>
        private void AdjustRegionRightBoundary(MergedRegion region, double delta)
        {
            // 只调整该区域最右列的宽度
            int rightCol = region.MaxCol;
            if (rightCol >= 0 && rightCol < ColumnWidths.Count)
            {
                double newWidth = ColumnWidths[rightCol] + delta;
                newWidth = Math.Max(50, newWidth); // 最小宽度
                ColumnWidths[rightCol] = newWidth;

               
            }
        }

        /// <summary>
        /// 调整区域上边界
        /// </summary>
        private void AdjustRegionTopBoundary(MergedRegion region, double delta)
        {
            // 只调整该区域最上行的高度
            int topRow = region.MinRow;
            if (topRow >= 0 && topRow < RowHeights.Count)
            {
                double newHeight = RowHeights[topRow] + delta;
                newHeight = Math.Max(50, newHeight); // 最小高度
                RowHeights[topRow] = newHeight;

            
            }
        }

        /// <summary>
        /// 调整区域下边界
        /// </summary>
        private void AdjustRegionBottomBoundary(MergedRegion region, double delta)
        {
            // 只调整该区域最下行的高度
            int bottomRow = region.MaxRow;
            if (bottomRow >= 0 && bottomRow < RowHeights.Count)
            {
                double newHeight = RowHeights[bottomRow] + delta;
                newHeight = Math.Max(50, newHeight); // 最小高度
                RowHeights[bottomRow] = newHeight;

             
            }
        }

        /// <summary>
        /// 从网格模式转换到独立区域模式
        /// </summary>
        public void ConvertToIndependentRegions()
        {
            IndependentRegions.Clear();

            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    var cell = Cell(r, c);
                    if (cell.Type != RegionType.None)
                    {
                        double left = GetColumnStartX(c);
                        double top = GetRowStartY(r);
                        double right = left + GetColumnWidth(c);
                        double bottom = top + GetRowHeight(r);

                        IndependentRegions.Add(new IndependentRegion
                        {
                            Type = cell.Type,
                            Left = left,
                            Top = top,
                            Right = right,
                            Bottom = bottom
                        });
                    }
                }
            }

            UseIndependentRegions = true;

           
        }

        /// <summary>
        /// 自动合并相邻的同类型独立区域
        /// </summary>
        public void AutoMergeIndependentRegions()
        {
            if (!UseIndependentRegions) return;

            bool merged = true;
            while (merged)
            {
                merged = false;

                for (int i = 0; i < IndependentRegions.Count && !merged; i++)
                {
                    for (int j = i + 1; j < IndependentRegions.Count; j++)
                    {
                        var region1 = IndependentRegions[i];
                        var region2 = IndependentRegions[j];

                        if (region1.Type == region2.Type && region1.IsAdjacentTo(region2))
                        {
                            // 合并两个区域
                            var mergedRegion = new IndependentRegion
                            {
                                Type = region1.Type,
                                Left = Math.Min(region1.Left, region2.Left),
                                Top = Math.Min(region1.Top, region2.Top),
                                Right = Math.Max(region1.Right, region2.Right),
                                Bottom = Math.Max(region1.Bottom, region2.Bottom)
                            };

                            IndependentRegions.RemoveAt(j);
                            IndependentRegions.RemoveAt(i);
                            IndependentRegions.Add(mergedRegion);

                            merged = true;
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 获取指定位置的独立区域
        /// </summary>
        public IndependentRegion GetIndependentRegionAt(Point position)
        {
            return IndependentRegions.FirstOrDefault(region => region.Contains(position));
        }

        /// <summary>
        /// 获取总画布尺寸（独立区域模式）
        /// </summary>
        public Size GetTotalSizeFromIndependentRegions()
        {
            if (IndependentRegions.Count == 0)
                return new Size(800, 600);

            double maxRight = IndependentRegions.Max(r => r.Right);
            double maxBottom = IndependentRegions.Max(r => r.Bottom);

            return new Size(maxRight, maxBottom);
        }

        /// <summary>
        /// 按比例缩放整个区域布局（画布尺寸变化时调用）
        /// </summary>
        /// <param name="ratioX">水平缩放比例</param>
        /// <param name="ratioY">垂直缩放比例</param>
        public void ScaleLayout(double ratioX, double ratioY)
        {
            // 防止无意义缩放
            if (Math.Abs(ratioX - 1.0) < 0.001 && Math.Abs(ratioY - 1.0) < 0.001) return;

            // ① 缩放独立区域模式
            if (UseIndependentRegions && IndependentRegions?.Count > 0)
            {
                foreach (var region in IndependentRegions)
                {
                    region.Left *= ratioX;
                    region.Right *= ratioX;
                    region.Top *= ratioY;
                    region.Bottom *= ratioY;

                    // 同步原始边界（如果有拖拽状态）
                    region.OriginalLeft *= ratioX;
                    region.OriginalRight *= ratioX;
                    region.OriginalTop *= ratioY;
                    region.OriginalBottom *= ratioY;
                }
            }

            // ② 缩放网格模式
            for (int i = 0; i < ColumnWidths.Count; i++)
            {
                ColumnWidths[i] *= ratioX;
            }

            for (int i = 0; i < RowHeights.Count; i++)
            {
                RowHeights[i] *= ratioY;
            }

            // ③ 重新计算合并区域边界（依赖 ColumnWidths/RowHeights，需刷新）
            if (!UseIndependentRegions && MergedRegions?.Count > 0)
            {
                foreach (var merged in MergedRegions)
                {
                    merged.CalculateBounds(); // 边界用行列索引，无需缩放
                }
            }
        }
    }

    /// <summary>
    /// 合并区域类
    /// </summary>
    public class MergedRegion
    {
        public RegionType Type { get; set; }
        public List<Point> Cells { get; set; } = new List<Point>();

        // 区域边界
        public int MinRow { get; private set; }
        public int MaxRow { get; private set; }
        public int MinCol { get; private set; }
        public int MaxCol { get; private set; }

        public void CalculateBounds()
        {
            if (Cells.Count == 0) return;

            MinCol = (int)Cells.Min(p => p.X);
            MaxCol = (int)Cells.Max(p => p.X);
            MinRow = (int)Cells.Min(p => p.Y);
            MaxRow = (int)Cells.Max(p => p.Y);
        }

        /// <summary>
        /// 获取区域的物理边界
        /// </summary>
        public Rect GetPhysicalBounds(RegionLayout layout)
        {
            double left = layout.GetColumnStartX(MinCol);
            double top = layout.GetRowStartY(MinRow);
            double right = layout.GetColumnStartX(MaxCol) + layout.GetColumnWidth(MaxCol);
            double bottom = layout.GetRowStartY(MaxRow) + layout.GetRowHeight(MaxRow);

            return new Rect(left, top, right - left, bottom - top);
        }
    }
    // <summary>
    /// 区域边界枚举
    /// </summary>
    public enum RegionBoundary
    {
        Left,
        Right,
        Top,
        Bottom
    }
    /// <summary>
    /// 独立区域类 - 每个区域有自己的边界坐标
    /// </summary>
    public class IndependentRegion
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public RegionType Type { get; set; }

        // 区域的绝对边界坐标
        public double Left { get; set; }
        public double Top { get; set; }
        public double Right { get; set; }
        public double Bottom { get; set; }
        // 🔥 添加原始边界属性，用于拖动时计算设备位置
        public double OriginalLeft { get; set; }
        public double OriginalTop { get; set; }
        public double OriginalRight { get; set; }
        public double OriginalBottom { get; set; }

        // 计算属性
        public double Width
        {
            get => Right - Left;
            set => Right = Left + value;
        }

        public double Height
        {
            get => Bottom - Top;
            set => Bottom = Top + value;
        }

        public Rect Bounds => new Rect(Left, Top, Width, Height);

        /// <summary>
        /// 检查点是否在区域内
        /// </summary>
        public bool Contains(Point point)
        {
            return point.X >= Left && point.X <= Right &&
                   point.Y >= Top && point.Y <= Bottom;
        }

        /// <summary>
        /// 检查是否与另一个区域相邻
        /// </summary>
        public bool IsAdjacentTo(IndependentRegion other, double tolerance = 1.0)
        {
            // 检查是否水平相邻
            bool horizontalAdjacent = (Math.Abs(Right - other.Left) < tolerance ||
                                      Math.Abs(Left - other.Right) < tolerance) &&
                                     !(Bottom <= other.Top || Top >= other.Bottom);

            // 检查是否垂直相邻
            bool verticalAdjacent = (Math.Abs(Bottom - other.Top) < tolerance ||
                                    Math.Abs(Top - other.Bottom) < tolerance) &&
                                   !(Right <= other.Left || Left >= other.Right);

            return horizontalAdjacent || verticalAdjacent;
        }

        /// <summary>
        /// 调整边界
        /// </summary>
        public void AdjustBoundary(RegionBoundary boundary, double delta, double minSize = 50)
        {
            switch (boundary)
            {
                case RegionBoundary.Left:
                    Left = Math.Min(Left + delta, Right - minSize);
                    break;
                case RegionBoundary.Right:
                    Right = Math.Max(Right + delta, Left + minSize);
                    break;
                case RegionBoundary.Top:
                    Top = Math.Min(Top + delta, Bottom - minSize);
                    break;
                case RegionBoundary.Bottom:
                    Bottom = Math.Max(Bottom + delta, Top + minSize);
                    break;
            }
        }

        /// <summary>
        /// 调整边界时同步相邻区域 - 修复版本
        /// </summary>
        public void AdjustBoundaryWithSync(RegionBoundary boundary, double delta,
            List<IndependentRegion> allRegions, double minSize = 50)
        {
            double tolerance = 1.0; // 相邻判断容差

            switch (boundary)
            {
                case RegionBoundary.Left:
                    {
                        double oldLeft = Left;
                        var left = Math.Max(0, Math.Min(Left + delta, Right - minSize));
                        double actualDelta = Left - oldLeft;

                        // 同步左侧相邻区域的右边界
                        foreach (var region in allRegions)
                        {
                            if (region != this && Math.Abs(region.Right - oldLeft) < tolerance &&
                                IsVerticallyAligned(region)) // 🔥 使用更严格的对齐检查
                            {
                                if (region.Left + minSize < left)
                                {
                                    region.Right = left;
                                    Left = left;
                                }
                                return;
                            }
                        }
                        Left = left;
                    }
                    break;

                case RegionBoundary.Right:
                    {
                        double oldRight = Right;
                        var right = Math.Max(Right + delta, Left + minSize);
                        double actualDelta = Right - oldRight;

                        // 同步右侧相邻区域的左边界
                        foreach (var region in allRegions)
                        {
                            if (region != this && Math.Abs(region.Left - oldRight) < tolerance &&
                                IsVerticallyAligned(region)) // 🔥 使用更严格的对齐检查
                            {
                                if (region.Right - minSize > right)
                                {
                                    region.Left = right;
                                    Right = right;
                                }

                                Debug.WriteLine("相邻区域赋值完成，立马返回");
                                return;
                            }
                        }
                        Debug.WriteLine("没有相邻区域");
                        // 没有相邻区域
                        Right = right;
                    }
                    break;

                case RegionBoundary.Top:
                    {
                        double oldTop = Top;
                        var top = Math.Max(0, Math.Min(Top + delta, Bottom - minSize));
                        double actualDelta = Top - oldTop;

                        // 🔥 关键修复：同步上方相邻区域的下边界
                        foreach (var region in allRegions)
                        {
                            if (region != this && Math.Abs(region.Bottom - oldTop) < tolerance &&
                                IsHorizontallyAligned(region)) // 🔥 使用更严格的对齐检查
                            {
                                if (region.Top + minSize < top)
                                {
                                    region.Bottom = top;
                                    Top = top;
                                }
                                return;
                            }
                        }
                        Top = top;
                    }
                    break;

                case RegionBoundary.Bottom:
                    {
                        double oldBottom = Bottom;
                        var bottom = Math.Max(Bottom + delta, Top + minSize);
                        double actualDelta = Bottom - oldBottom;

                        // 🔥 关键修复：同步下方相邻区域的上边界
                        foreach (var region in allRegions)
                        {
                            if (region != this && Math.Abs(region.Top - oldBottom) < tolerance &&
                                IsHorizontallyAligned(region)) // 🔥 使用更严格的对齐检查
                            {
                                if (region.Bottom - minSize > bottom)
                                {
                                    region.Top = bottom;
                                    Bottom = bottom;
                                }
                                return;
                            }
                        }

                        Bottom = bottom;
                    }
                    break;
            }
        }
        /// <summary>
        /// 检查两个区域是否水平完全对齐（左右边界完全重合）
        /// </summary>
        private bool IsHorizontallyAligned(IndependentRegion other)
        {
            double tolerance = 1.0;
            return Math.Abs(Left - other.Left) < tolerance &&
                   Math.Abs(Right - other.Right) < tolerance;
        }

        /// <summary>
        /// 检查两个区域是否垂直完全对齐（上下边界完全重合）
        /// </summary>
        private bool IsVerticallyAligned(IndependentRegion other)
        {
            double tolerance = 1.0;
            return Math.Abs(Top - other.Top) < tolerance &&
                   Math.Abs(Bottom - other.Bottom) < tolerance;
        }

    }
}
