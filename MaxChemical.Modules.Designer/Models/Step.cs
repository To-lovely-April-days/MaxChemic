using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Models
{
    public class ProcessStep : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _name;
        private StepType _type;
        private double _x;
        private double _y;
        private bool _isSelected;
        public string ImageLocation { get; set; } // 新增
        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public StepType Type
        {
            get => _type;
            set { _type = value; OnPropertyChanged(); }
        }

        public List<string> NextStepIds { get; set; } = new List<string>();

        public double X
        {
            get => _x;
            set { _x = value; OnPropertyChanged(); }
        }

        public double Y
        {
            get => _y;
            set { _y = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    public class ConditionStep : ProcessStep
    {
        public string Condition { get; set; }

        public ConditionStep()
        {
            Type = StepType.IfCondition;
        }
    }

    public class LoopStep : ProcessStep
    {
        public bool UseCondition { get; set; }
        public int Count { get; set; } = 1;
        public string LoopCondition { get; set; }
        public bool BreakLoop { get; set; }
        public string BreakCondition { get; set; }

        public LoopStep()
        {
            Type = StepType.Loop;
        }
    }

    public class ParallelControlStep : ProcessStep
    {
        public int? BranchCount { get; set; } = 3;
        public bool? WaitForAll { get; set; } = true;
        public bool? HasTimeout { get; set; } = false;
        public int? TimeoutSeconds { get; set; } = 30;

        public ParallelControlStep()
        {
            Type = StepType.ParallelStart;
        }
    }

    // 添加步骤类型枚举
    public enum StepType
    {
        DeviceCommand,
        Delay,
        IfCondition,
        Loop,
        ParallelStart,
        SetVariable,
        WaitCondition,
        Comment
    }
}
