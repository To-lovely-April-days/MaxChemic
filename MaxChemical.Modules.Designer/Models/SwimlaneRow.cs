// Models/SwimlaneRow.cs — 改造版：支持设备颜色 + 真实时间轴
using System;
using System.Windows.Media;
using Prism.Mvvm;

namespace MaxChemical.Modules.Designer.Models
{
    public class SwimlaneRow : BindableBase
    {
        public string NodeId { get; set; }
        public string NodeName { get; set; }
        public string NodeType { get; set; } = "设备命令";

        // ════ 新增：设备信息（用于按设备着色） ════
        private string _deviceName = "";
        public string DeviceName
        {
            get => _deviceName;
            set => SetProperty(ref _deviceName, value);
        }

        private Brush _deviceColor;
        /// <summary>条块颜色 — 由设备类型决定，而非执行状态</summary>
        public Brush DeviceColor
        {
            get => _deviceColor;
            set => SetProperty(ref _deviceColor, value);
        }

        private Brush _deviceDotColor;
        /// <summary>图例圆点颜色（部分设备有红点标记）</summary>
        public Brush DeviceDotColor
        {
            get => _deviceDotColor;
            set => SetProperty(ref _deviceDotColor, value);
        }

        // ════ 行号 ════
        private int _rowIndex;
        public int RowIndex
        {
            get => _rowIndex;
            set { SetProperty(ref _rowIndex, value); RaisePropertyChanged(nameof(RowIndexDisplay)); }
        }
        public string RowIndexDisplay => (RowIndex + 1).ToString();

        // ════ 执行状态 ════
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

        // ════ 时间文字 ════
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

        private string _timeRangeText = "";
        public string TimeRangeText
        {
            get => _timeRangeText;
            set => SetProperty(ref _timeRangeText, value);
        }

        // ════ 条块位置 ════
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

        public double BarRight => BarLeft + BarWidth;
        public double EndTimeLabelLeft => BarLeft + BarWidth + 4;
        public bool IsNarrowBar => BarWidth < 120;
        public bool IsWideBar => BarWidth >= 120;

        public const double PixelsPerSecond = 52.0;
    }

    // ════════════════════════════════════════════
    // 时间轴刻度模型
    // ════════════════════════════════════════════
    public class TimelineTick : BindableBase
    {
        private double _x;
        public double X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        public string AbsoluteTimeText { get; set; }
        public string RelativeTimeText { get; set; }
    }

    // ════════════════════════════════════════════
    // 设备图例项
    // ════════════════════════════════════════════
    public class DeviceLegendItem : BindableBase
    {
        public string DeviceName { get; set; }
        public Brush BarColor { get; set; }
    }
}