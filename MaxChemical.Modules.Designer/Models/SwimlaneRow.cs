// Models/SwimlaneRow.cs
using System;
using Prism.Mvvm;

namespace MaxChemical.Modules.Designer.Models
{
    public class SwimlaneRow : BindableBase
    {
        public string NodeId { get; set; }
        public string NodeName { get; set; }
        public string NodeType { get; set; } = "设备命令";

        private int _rowIndex;
        public int RowIndex
        {
            get => _rowIndex;
            set { SetProperty(ref _rowIndex, value); RaisePropertyChanged(nameof(RowIndexDisplay)); }
        }
        public string RowIndexDisplay => (RowIndex + 1).ToString();

        private bool _isExecuting;
        public bool IsExecuting
        {
            get => _isExecuting;
            set => SetProperty(ref _isExecuting, value);
        }

        private bool _isCompleted;
        public bool IsCompleted
        {
            get => _isCompleted;
            set => SetProperty(ref _isCompleted, value);
        }

        private bool _isFailed;
        public bool IsFailed
        {
            get => _isFailed;
            set => SetProperty(ref _isFailed, value);
        }

        // ════════════════════════════════════════════
        // 时间文字（精确到毫秒 HH:mm:ss.fff）
        // ════════════════════════════════════════════

        private string _startTimeText = "";
        public string StartTimeText
        {
            get => _startTimeText;
            set => SetProperty(ref _startTimeText, value);
        }

        private string _endTimeText = "";
        public string EndTimeText
        {
            get => _endTimeText;
            set => SetProperty(ref _endTimeText, value);
        }

        private string _durationText = "";
        public string DurationText
        {
            get => _durationText;
            set => SetProperty(ref _durationText, value);
        }

        /// <summary>
        /// 合并的时间范围文字，显示在条块上方。
        /// 格式: "13:41:07.699 → 13:41:07.865 · 166ms"
        /// 由 ViewModel 在设置时间时一并计算。
        /// </summary>
        private string _timeRangeText = "";
        public string TimeRangeText
        {
            get => _timeRangeText;
            set => SetProperty(ref _timeRangeText, value);
        }

        // ════════════════════════════════════════════
        // 条块位置
        // ════════════════════════════════════════════

        private double _barLeft;
        public double BarLeft
        {
            get => _barLeft;
            set
            {
                SetProperty(ref _barLeft, value);
                RaisePropertyChanged(nameof(BarRight));
                RaisePropertyChanged(nameof(EndTimeLabelLeft));
            }
        }

        private double _barWidth = 6;
        public double BarWidth
        {
            get => _barWidth;
            set
            {
                SetProperty(ref _barWidth, value);
                RaisePropertyChanged(nameof(BarRight));
                RaisePropertyChanged(nameof(IsNarrowBar));
                RaisePropertyChanged(nameof(IsWideBar));
                RaisePropertyChanged(nameof(EndTimeLabelLeft));
            }
        }

        /// <summary>条块右边缘 X</summary>
        public double BarRight => BarLeft + BarWidth;

        /// <summary>结束时间标签位置 = 右端 + 4px</summary>
        public double EndTimeLabelLeft => BarLeft + BarWidth + 4;

        /// <summary>条块宽度 < 120px 时为窄条块，合并显示时间</summary>
        public bool IsNarrowBar => BarWidth < 120;

        /// <summary>条块宽度 >= 120px 时为宽条块，左右分开显示时间</summary>
        public bool IsWideBar => BarWidth >= 120;

        public const double PixelsPerSecond = 52.0;
    }
}
