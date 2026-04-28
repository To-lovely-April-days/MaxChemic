using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using Newtonsoft.Json;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;

namespace MaxChemical.Modules.DOE.ViewModels
{
    public partial class  DOEProjectDetailViewModel : BindableBase
    {
        private readonly IDOERepository _repository;
        private readonly ILocalizationService _localizationService;

        // ── 基础信息 ──
        public string ProjectName { get; set; } = "";
        public string PhaseText { get; set; } = "";
        public string PhaseColor { get; set; } = "#7F8C8D";
        public string StatusText { get; set; } = "";
        public string BestValueText { get; set; } = "—";
        public string ObjectiveDescription { get; set; } = "";
        public int TotalExperiments { get; set; }
        public int CompletedRounds { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime UpdatedTime { get; set; }
        public string PhaseProgressText { get; set; } = "";

        // ── 进度条 ──
        public ObservableCollection<PhaseSegmentItem> PhaseSegments { get; set; } = new();

        // ── 最优因子组合 ──
        public ObservableCollection<BestFactorItem> BestFactorItems { get; set; } = new();

        // ── 因子池表格 ──
        public ObservableCollection<FactorTableItem> FactorTableItems { get; set; } = new();

        // ── 模型列表 ──
        public ObservableCollection<ModelListItem> ModelItems { get; set; } = new();

        // ── 批次列表 ──
        public ObservableCollection<BatchListItem> BatchItems { get; set; } = new();

        public DOEProjectDetailViewModel(IDOERepository repository,ILocalizationService localizationService)
        {
            _repository = repository;
            _localizationService = localizationService;

            UpdateLocalizationTxt();
        }

        public async Task LoadAsync(string projectId)
        {
            var project = await _repository.GetProjectWithDetailsAsync(projectId);
            if (project == null) return;

            // 基础信息
            ProjectName = project.ProjectName;
            ObjectiveDescription = string.IsNullOrEmpty(project.ObjectiveDescription) ? "—" : project.ObjectiveDescription;
            TotalExperiments = project.TotalExperiments;
            CompletedRounds = project.CompletedRounds;
            CreatedTime = project.CreatedTime;
            UpdatedTime = project.UpdatedTime;
            BestValueText = project.BestResponseValue.HasValue ? $"{project.BestResponseValue:F2}" : "—";

            PhaseText = project.CurrentPhase switch
            {
                DOEProjectPhase.Screening => _localizationService.GetString("Doe_ProjectPhase_Screening", "筛选"),
                DOEProjectPhase.PathSearch => _localizationService.GetString("Doe_ProjectPhase_PathSearch", "爬坡"),
                DOEProjectPhase.RSM => _localizationService.GetString("Doe_ProjectPhase_RSM", "RSM"),
                DOEProjectPhase.Augmenting => _localizationService.GetString("Doe_ProjectPhase_Augmenting", "补点"),
                DOEProjectPhase.Confirmation => _localizationService.GetString("Doe_ProjectPhase_Confirmation", "验证"),
                DOEProjectPhase.Completed => _localizationService.GetString("Doe_ProjectPhase_Completed", "已完成"),
                _ => project.CurrentPhase.ToString() ?? ""
            };

            PhaseColor = project.CurrentPhase switch
            {
                DOEProjectPhase.Screening => "#E67E22",
                DOEProjectPhase.PathSearch => "#3498DB",
                DOEProjectPhase.RSM => "#9B59B6",
                DOEProjectPhase.Augmenting => "#1ABC9C",
                DOEProjectPhase.Confirmation => "#27AE60",
                DOEProjectPhase.Completed => "#95A5A6",
                _ => "#7F8C8D"
            };

            StatusText = project.Status switch
            {
                DOEProjectStatus.Active => _localizationService.GetString("Doe_ProjectStatus_Active", "活跃"),
                DOEProjectStatus.Paused => _localizationService.GetString("Doe_ProjectStatus_Paused", "暂停"),
                DOEProjectStatus.Completed => _localizationService.GetString("Doe_ProjectStatus_Completed", "已完成"),
                DOEProjectStatus.Archived => _localizationService.GetString("Doe_ProjectStatus_Archived", "已归档"),
                _ => ""
            };

            // 进度条
            PhaseSegments = BuildPhaseSegments(project.CurrentPhase);
            PhaseProgressText = project.CurrentPhase == DOEProjectPhase.Completed
                ? _localizationService.GetString("Doe_ProjDetail_AlreadyAll", "已完成全部优化流程")
                : _localizationService.GetString("Doe_ProjDetail_PhaseProgress", "筛选 → 爬坡 → RSM → 验证 → 完成");

            // 最优因子组合
            BestFactorItems = ParseBestFactors(project.BestFactorsJson);

            // 因子池
            FactorTableItems = BuildFactorTable(project.ProjectFactors);

            // 模型列表
            await LoadModelsAsync(project);

            // 批次列表
            BatchItems = await BuildBatchListAsync(project.Batches, project.RoundSummaries);

            RaisePropertyChanged(string.Empty); // 刷新所有属性
        }

        private ObservableCollection<BestFactorItem> ParseBestFactors(string? json)
        {
            var items = new ObservableCollection<BestFactorItem>();
            if (string.IsNullOrEmpty(json)) return items;

            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (dict != null)
                {
                    foreach (var kv in dict)
                    {
                        items.Add(new BestFactorItem { Name = kv.Key, Value = kv.Value?.ToString() ?? "—" });
                    }
                }
            }
            catch { }
            return items;
        }

        private ObservableCollection<FactorTableItem> BuildFactorTable(List<DOEProjectFactor> factors)
        {
            var items = new ObservableCollection<FactorTableItem>();
            if (factors == null) return items;

            // 活跃因子排前面
            foreach (var f in factors.OrderBy(f => f.FactorStatus).ThenBy(f => f.SortOrder))
            {
                string range;
                if (f.IsCategorical)
                    range = "{" + (f.CategoryLevels ?? "—") + "}";
                else
                    range = $"[{f.CurrentLowerBound}, {f.CurrentUpperBound}]";

                string status;
                bool isActive;
                switch (f.FactorStatus)
                {
                    case ProjectFactorStatus.Active:
                        status = _localizationService.GetString("Doe_ProjDetail_FactorState_Active", "活跃");
                        isActive = true;
                        break;
                    case ProjectFactorStatus.ScreenedOut:
                        status = f.StatusReason ?? _localizationService.GetString("Doe_ProjDetail_FactorState_Out", "已淘汰");
                        isActive = false;
                        break;
                    case ProjectFactorStatus.Fixed:
                        var fixVal = f.FixedCategoryLevel ?? f.FixedValue?.ToString("F1") ?? "";
                        status = string.Format(_localizationService.GetString("Doe_ProjDetail_FacotState_Fixed", "固定={0}"),fixVal);
                        isActive = false;
                        break;
                    default:
                        status = f.FactorStatus.ToString();
                        isActive = true;
                        break;
                }

                items.Add(new FactorTableItem
                {
                    Name = f.FactorName,
                    Range = isActive ? range : "—",
                    Status = status,
                    IsActive = isActive
                });
            }
            return items;
        }

        private async Task LoadModelsAsync(DOEProject project)
        {
            var items = new ObservableCollection<ModelListItem>();
            try
            {
                // ★ 按项目 ID 查所有 GPR 模型
                var models = await _repository.GetGPRModelsByProjectAsync(project.ProjectId);

                foreach (var m in models)
                {
                    var name = m.ModelName;
                    if (string.IsNullOrEmpty(name))
                    {
                        var sigParts = m.FactorSignature?.Split("::") ?? Array.Empty<string>();
                        name = sigParts.Length > 1 ? sigParts[1] : _localizationService.GetString("Doe_ProjDetail_MainModel", "主响应模型");
                    }

                    var factorSig = m.FactorSignature ?? "";
                    if (factorSig.Contains("::"))
                        factorSig = factorSig.Substring(0, factorSig.IndexOf("::"));

                    items.Add(new ModelListItem
                    {
                        Name = name,
                        Description = string.Format(_localizationService.GetString("Doe_ProjDetail_GroupData", "{0} · {1} 组数据"), factorSig, m.DataCount),
                        RSquaredText = m.RSquared.HasValue ? $"{m.RSquared:F3}" : "—"
                    });
                }

                // 如果也有 FlowId，补充查 FlowId 下没有 ProjectId 的旧模型
                if (!string.IsNullOrEmpty(project.FlowId))
                {
                    var flowModels = await _repository.GetGPRModelsByFlowAsync(project.FlowId);
                    foreach (var m in flowModels)
                    {
                        // 跳过已经按 ProjectId 查到的（避免重复）
                        if (!string.IsNullOrEmpty(m.ProjectId)) continue;

                        var name = m.ModelName;
                        if (string.IsNullOrEmpty(name))
                        {
                            var sigParts = m.FactorSignature?.Split("::") ?? Array.Empty<string>();
                            name = sigParts.Length > 1 ? sigParts[1] : _localizationService.GetString("Doe_ProjDetail_MainModel", "主响应模型");
                        }

                        var factorSig = m.FactorSignature ?? "";
                        if (factorSig.Contains("::"))
                            factorSig = factorSig.Substring(0, factorSig.IndexOf("::"));

                        items.Add(new ModelListItem
                        {
                            Name = name,
                            Description = string.Format(_localizationService.GetString("Doe_ProjDetail_GroupData", "{0} · {1} 组数据"),factorSig,m.DataCount),
                            RSquaredText = m.RSquared.HasValue ? $"{m.RSquared:F3}" : "—"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载 GPR 模型列表失败: {ex.Message}");
            }

            if (items.Count == 0)
            {
                items.Add(new ModelListItem
                {
                    Name = _localizationService.GetString("Doe_ProjDetail_NoneModel", "暂无模型"),
                    Description = _localizationService.GetString("Doe_ProjDetail_Desc", "执行实验后将自动创建 GPR 模型"),
                    RSquaredText = "—"
                });
            }

            ModelItems = items;
        }


        private async Task<ObservableCollection<BatchListItem>> BuildBatchListAsync(
      List<DOEBatch> batches, List<DOERoundSummary> summaries)
        {
            var items = new ObservableCollection<BatchListItem>();
            if (batches == null) return items;

            foreach (var b in batches.OrderByDescending(b => b.RoundNumber ?? 0))
            {
                var summary = summaries?.FirstOrDefault(s => s.BatchId == b.BatchId);

                // ★ 查询实际 Runs 数量
                int runCount = 0;
                try
                {
                    var runs = await _repository.GetRunsAsync(b.BatchId);
                    runCount = runs?.Count ?? 0;
                }
                catch { }

                var phase = b.ProjectPhase ?? DOEProjectPhase.Screening;
                var phaseText = phase switch
                {
                    DOEProjectPhase.Screening => _localizationService.GetString("Doe_ProjectPhase_Screening", "筛选"),
                    DOEProjectPhase.PathSearch => _localizationService.GetString("Doe_ProjectPhase_PathSearch", "爬坡"),
                    DOEProjectPhase.RSM => _localizationService.GetString("Doe_ProjectPhase_RSM", "RSM"),
                    DOEProjectPhase.Augmenting => _localizationService.GetString("Doe_ProjectPhase_Augmenting", "补点"),
                    DOEProjectPhase.Confirmation => _localizationService.GetString("Doe_ProjectPhase_Confirmation", "验证"),
                    DOEProjectPhase.Completed => _localizationService.GetString("Doe_ProjectPhase_Completed", "已完成"),
                    _ => phase.ToString() ?? ""
                };
                var phaseColor = phase switch
                {
                    DOEProjectPhase.Screening => "#E67E22",
                    DOEProjectPhase.PathSearch => "#3498DB",
                    DOEProjectPhase.RSM => "#9B59B6",
                    DOEProjectPhase.Augmenting => "#1ABC9C",
                    DOEProjectPhase.Confirmation => "#27AE60",
                    _ => "#7F8C8D"
                };

                var methodText = b.DesignMethod switch
                {
                    DOEDesignMethod.CCD => _localizationService.GetString("Doe_DesignMethod_CCD", "CCD 中心复合"),
                    DOEDesignMethod.BoxBehnken => _localizationService.GetString("Doe_DesignMethod_BoxBehnken", "Box-Behnken"),
                    DOEDesignMethod.DOptimal => _localizationService.GetString("Doe_DesignMethod_DOptimal", "D-Optimal"),
                    DOEDesignMethod.FullFactorial => _localizationService.GetString("Doe_DesignMethod_FullFactorial", "全因子"),
                    DOEDesignMethod.FractionalFactorial => _localizationService.GetString("Doe_DesignMethod_FractionalFactorial", "部分因子"),
                    DOEDesignMethod.Taguchi => _localizationService.GetString("Doe_DesignMethod_Taguchi", "Taguchi"),
                    DOEDesignMethod.SteepestAscent => _localizationService.GetString("Doe_DesignMethod_SteepestAscent", "最速上升"),
                    DOEDesignMethod.AugmentedDesign => _localizationService.GetString("Doe_DesignMethod_AugmentedDesign", "增强设计"),
                    DOEDesignMethod.ConfirmationRuns => _localizationService.GetString("Doe_DesignMethod_ConfirmationRuns", "验证实验"),
                    DOEDesignMethod.PlackettBurman => _localizationService.GetString("Doe_DesignMethod_PlackettBurman", "Plackett-Burman"),
                    _ => b.DesignMethod.ToString()
                };

                items.Add(new BatchListItem
                {
                    RoundLabel = $"R{b.RoundNumber ?? 0}",
                    PhaseText = phaseText,
                    PhaseColor = phaseColor,
                    MethodAndRuns = string.Format(_localizationService.GetString("Doe_ProjDetail_MethodRuns","{0} · {1} 组"),methodText,runCount),
                    RSquaredText = summary?.RSquared.HasValue == true ? $"R²={summary.RSquared:F3}" : "—",
                    DateText = b.CreatedTime.ToString("MM-dd")
                });
            }
            return items;
        }

        private static ObservableCollection<PhaseSegmentItem> BuildPhaseSegments(DOEProjectPhase currentPhase)
        {
            int currentIndex = currentPhase switch
            {
                DOEProjectPhase.Screening => 0,
                DOEProjectPhase.PathSearch => 1,
                DOEProjectPhase.RSM => 2,
                DOEProjectPhase.Augmenting => 2,
                DOEProjectPhase.Confirmation => 3,
                DOEProjectPhase.Completed => 5,
                _ => 0
            };
            var active = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078D4"));
            var current = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078D4")) { Opacity = 0.35 };
            var inactive = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8EAED"));
            var segments = new ObservableCollection<PhaseSegmentItem>();
            for (int i = 0; i < 5; i++)
            {
                if (i < currentIndex) segments.Add(new PhaseSegmentItem { Color = active });
                else if (i == currentIndex) segments.Add(new PhaseSegmentItem { Color = current });
                else segments.Add(new PhaseSegmentItem { Color = inactive });
            }
            return segments;
        }

        #region 本地化标签文本

        private string _titleTxt;
        private string _optimalTxt;
        private string _optimizeTargetTxt;
        private string _totalExperimentTxt;
        private string _groupTxt;
        private string _roundTxt;
        private string _createTimeTxt;
        private string _lastUpdateTxt;
        private string _factoriPoolTxt;
        private string _factoriTxt;
        private string _rangeTxt;
        private string _statusTxt;
        private string _grpModelTxt;
        private string _batchHistoryTxt;

        public string TitleTxt { get { return _titleTxt; } set { SetProperty(ref _titleTxt, value); } }
        public string OptimalTxt { get { return _optimalTxt; } set { SetProperty(ref _optimalTxt, value); } }
        public string OptimizeTargetTxt { get { return _optimizeTargetTxt; } set { SetProperty(ref _optimizeTargetTxt, value); } }
        public string TotalExperimentTxt { get { return _totalExperimentTxt; } set { SetProperty(ref _totalExperimentTxt, value); } }
        public string GroupTxt { get { return _groupTxt; } set { SetProperty(ref _groupTxt, value); } }
        public string RoundTxt { get { return _roundTxt; } set { SetProperty(ref _roundTxt, value); } }
        public string CreateTimeTxt { get { return _createTimeTxt; } set { SetProperty(ref _createTimeTxt, value); } }
        public string LastUpdateTxt { get { return _lastUpdateTxt; } set { SetProperty(ref _lastUpdateTxt, value); } }
        public string FactoriPoolTxt { get { return _factoriPoolTxt; } set { SetProperty(ref _factoriPoolTxt, value); } }
        public string FactoriTxt { get { return _factoriTxt; } set { SetProperty(ref _factoriTxt, value); } }
        public string RangeTxt { get { return _rangeTxt; } set { SetProperty(ref _rangeTxt, value); } }
        public string StatusTxt { get { return _statusTxt; } set { SetProperty(ref _statusTxt, value); } }
        public string GrpModeTxt { get { return _grpModelTxt; } set { SetProperty(ref _grpModelTxt, value); } }
        public string BatchHistoryTxt { get { return _batchHistoryTxt; } set { SetProperty(ref _batchHistoryTxt, value); } }

        private void UpdateLocalizationTxt()
        {
            TitleTxt = _localizationService.GetString("Doe_ProjDetail_Title", "项目详情");
            OptimalTxt = _localizationService.GetString("Doe_ProjDetail_Optimal", "当前最优");
            OptimizeTargetTxt = _localizationService.GetString("Doe_ProjDetail_OptimizeTarget", "优化目标");
            TotalExperimentTxt = _localizationService.GetString("Doe_ProjDetail_TotalExperiment", "总实验");
            GroupTxt = _localizationService.GetString("Doe_ProjDetail_Group", " 组 / ");
            RoundTxt = _localizationService.GetString("Doe_ProjDetail_Round", " 轮");
            CreateTimeTxt = _localizationService.GetString("Doe_ProjDetail_CreateTime", "创建时间");
            LastUpdateTxt = _localizationService.GetString("Doe_ProjDetail_LastUpdate", "最后更新");
            FactoriPoolTxt = _localizationService.GetString("Doe_ProjDetail_FactorPool", "因子池");
            FactoriTxt = _localizationService.GetString("Doe_ProjDetail_Factor", "因子");
            RangeTxt = _localizationService.GetString("Doe_ProjDetail_Range", "范围");
            StatusTxt = _localizationService.GetString("Doe_ProjDetail_Status", "状态");
            GrpModeTxt = _localizationService.GetString("Doe_ProjDetail_GPRMode", "GPR 模型");
            BatchHistoryTxt = _localizationService.GetString("Doe_ProjDetail_BatchHistory", "批次历史");
        }

        #endregion
    }

    // ── 数据项类 ──

    public class BestFactorItem
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public class FactorTableItem
    {
        public string Name { get; set; } = "";
        public string Range { get; set; } = "";
        public string Status { get; set; } = "";
        public bool IsActive { get; set; } = true;
    }

    public class ModelListItem
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string RSquaredText { get; set; } = "—";
    }

    public class BatchListItem
    {
        public string RoundLabel { get; set; } = "";
        public string PhaseText { get; set; } = "";
        public string PhaseColor { get; set; } = "#7F8C8D";
        public string MethodAndRuns { get; set; } = "";
        public string RSquaredText { get; set; } = "—";
        public string DateText { get; set; } = "";
    }
}