using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Prism.Mvvm;

namespace MaxChemical.Modules.Designer.Models
{
    public class LogicIfNode : BindableBase
    {
        private double _x;
        private double _y;
        private bool _isSelected;
        private bool _isExecuting;
        private string _nodeId;
        private string _condition = "条件判断逻辑";
        private int _nodeNumber;

        public string NodeId
        {
            get => _nodeId;
            set => SetProperty(ref _nodeId, value);
        }

        public double X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        public double Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsExecuting
        {
            get => _isExecuting;
            set => SetProperty(ref _isExecuting, value);
        }

        public string Condition
        {
            get => _condition;
            set => SetProperty(ref _condition, value);
        }

        public int NodeNumber
        {
            get => _nodeNumber;
            set => SetProperty(ref _nodeNumber, value);
        }

        public ObservableCollection<CommandNode> TrueNodes { get; set; }
        public ObservableCollection<CommandNode> FalseNodes { get; set; }

        public LogicIfNode()
        {
            NodeId = Guid.NewGuid().ToString();
            TrueNodes = new ObservableCollection<CommandNode>();
            FalseNodes = new ObservableCollection<CommandNode>();
        }
    }
}
