// Models/NodeConnection.cs - 修复版本
using Prism.Mvvm;

namespace MaxChemical.Modules.Designer.Models
{
    public class NodeConnection : BindableBase
    {
        private double _x;
        private double _y;
        private double _lineLength;
        private double _arrowTop;
        private double _totalHeight;
        private bool _isBranchConnection; // 新增：标识是否为分支内连接线
        private bool _isTrueBranch;       // 新增：标识是TRUE还是FALSE分支
        private string _parentIfNodeId;   // 新增：父IF节点ID

        public string FromNodeId { get; set; }
        public string ToNodeId { get; set; }

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

        public double LineLength
        {
            get => _lineLength;
            set => SetProperty(ref _lineLength, value);
        }

        public double ArrowTop
        {
            get => _arrowTop;
            set => SetProperty(ref _arrowTop, value);
        }

        public double TotalHeight
        {
            get => _totalHeight;
            set => SetProperty(ref _totalHeight, value);
        }

        /// <summary>
        /// 是否为分支内连接线
        /// </summary>
        public bool IsBranchConnection
        {
            get => _isBranchConnection;
            set => SetProperty(ref _isBranchConnection, value);
        }

        /// <summary>
        /// 是否为TRUE分支（仅当IsBranchConnection=true时有效）
        /// </summary>
        public bool IsTrueBranch
        {
            get => _isTrueBranch;
            set => SetProperty(ref _isTrueBranch, value);
        }

        /// <summary>
        /// 父IF节点ID（仅当IsBranchConnection=true时有效）
        /// </summary>
        public string ParentIfNodeId
        {
            get => _parentIfNodeId;
            set => SetProperty(ref _parentIfNodeId, value);
        }
    }
}