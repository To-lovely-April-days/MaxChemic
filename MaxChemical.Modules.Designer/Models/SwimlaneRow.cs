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

        // 开始/结束时间字符串（格式 HH:mm:ss）
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

        // 耗时文字
        private string _durationText = "";
        public string DurationText
        {
            get => _durationText;
            set => SetProperty(ref _durationText, value);
        }

        // 条块位置（像素），由外部根据时间计算后设置
        private double _barLeft;
        public double BarLeft
        {
            get => _barLeft;
            set { SetProperty(ref _barLeft, value); RaisePropertyChanged(nameof(DurLeft)); }
        }

        private double _barWidth = 6;
        public double BarWidth
        {
            get => _barWidth;
            set { SetProperty(ref _barWidth, value); RaisePropertyChanged(nameof(DurLeft)); }
        }

        // 耗时标签位置 = 条块右侧 + 5px
        public double DurLeft => BarLeft + BarWidth + 5;

        public const double PixelsPerSecond = 52.0;
    }
}