using MaxChemical.Infrastructure.DOE;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Events;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using MaxChemical.Modules.DOE.Services.Designs;
using Newtonsoft.Json;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MaxChemical.Modules.DOE.ViewModels
{
    /// <summary>
    /// DOE 设计向导 ViewModel — 多步骤表单：
    /// Step1: 因子定义  Step2: 设计方法  Step3: 参数矩阵预览
    /// Step4: 响应变量 + 停止条件  Step5: 历史数据导入（可选）  Step6: 确认保存
    /// </summary>
    public class DOEDesignWizardViewModel : BindableBase
    {
        private readonly IDOEDesignService _designService;
        private readonly IDOERepository _repository;
        private readonly IGPRModelService _gprService;           //  新增: GPR 模型服务
        private readonly IMultiResponseGPRService _multiGprService;
        private readonly IFlowParameterProvider _paramProvider;
        private readonly IDialogService _dialogService;
        private readonly IEventAggregator _eventAggregator;      // ★ 新增: 事件聚合器
        private readonly ILogService _logger;
        private readonly ILocalizationService _localizationService; // 本地化

        // ── 向导状态 ──
        private int _currentStep = 1;
        private const int TOTAL_STEPS = 5;
        private bool _isLoading;
        private string _statusMessage = "";
        private string _batchName = "";

        // ── 因子数据 (Step1) ──
        private ObservableCollection<FactorViewModel> _factors = new();
        private ObservableCollection<FactorCandidate> _availableCandidates = new();

        // ── 设计方法 (Step2) ──
        private DOEDesignMethod _selectedMethod = DOEDesignMethod.FullFactorial;
        private string _taguchiTableType = "auto";
        private int _fractionalResolution = 3;
        //  新增: CCD 配置
        private string _ccdAlphaType = "rotatable";
        private int _ccdCenterCount = -1;
        //  新增: BBD 配置
        private int _bbdCenterCount = -1;
        //  新增: D-Optimal 配置
        private int _dOptimalRunCount = -1;
        private string _dOptimalModelType = "quadratic";
        private int _fullFactorialLevels = 2;
        private int _fullFactorialCenterCount = 0;
        //  新增: 设计质量
        private DOEDesignQuality? _designQuality;
        // ── 参数矩阵 (Step3) ──
        private DOEDesignMatrix? _designMatrix;
        private DataTable? _matrixTable;
        private int _centerPointCount = 0;
        private bool _isRandomized;
        // ── ★ 新增: Plackett-Burman 参数 ──
        private int _pbRunCount = 12;
        private int _pbCenterCount = 0;
        private int _pbReplicates = 1;

        // ── ★ 新增: Fractional Factorial 参数 ──
        private int _ffRunCount = -1;          // -1 = 按分辨度自动
        private int _ffCenterCount = 0;
        private int _ffReplicates = 1;

        // ── ★ 新增: Full Factorial 补全 ──
        private int _fullFactorialReplicates = 1;

        // ── ★ 新增: CCD 补全 ──
        private int _ccdCubeCenterCount = -1;
        private int _ccdAxialCenterCount = 0;
        private int _ccdReplicates = 1;
        private bool _ccdUseFractional = false;

        // ── ★ 新增: BBD 补全 ──
        private int _bbdReplicates = 1;

        // ── ★ 新增: D-Optimal 补全 ──
        private int _dOptRestarts = 20;
        private int _dOptMaxIterations = 100;
        private int _dOptRandomSeed = 0;
        private int _dOptCandidateGridSize = 3;

        // ── ★ 新增: Taguchi 补全 ──
        private string _taguchiSnRatio = "larger";

        // ── ★ 新增: 通用 ──
        private bool _randomizeRunOrder = false;
        // ── 响应变量 + 停止条件 (Step4) ──
        private ObservableCollection<ResponseViewModel> _responses = new();
        private ObservableCollection<StopConditionViewModel> _stopConditions = new();

        // ── 历史数据导入 (Step5) ──
        private string? _importFilePath;
        private DataValidationResult? _validationResult;
        private int _importedDataCount;
        private string? _customImportPath;  // ★ v6: 自定义导入 Excel 路径

        // ★ 新增: 项目模式字段
        private string? _currentProjectId;
        private int? _currentRoundNumber;
        private DOEProjectPhase? _currentProjectPhase;
        private bool _isProjectMode;

        public DOEDesignWizardViewModel(
            IDOEDesignService designService,
            IDOERepository repository,
            IGPRModelService gprService,                         //  新增
            IMultiResponseGPRService multiGprService,
            IFlowParameterProvider paramProvider,
            IDialogService dialogService,
            IEventAggregator eventAggregator,                    // ★ 新增
            ILogService logger,
            ILocalizationService localizationService)
        {
            _designService = designService;
            _repository = repository;
            _gprService = gprService;                            //  新增
            _paramProvider = paramProvider;
            _dialogService = dialogService;
            _eventAggregator = eventAggregator;                  // ★ 新增
            _multiGprService = multiGprService ?? throw new ArgumentNullException(nameof(multiGprService));
            _logger = logger?.ForContext<DOEDesignWizardViewModel>()
                      ?? throw new ArgumentNullException(nameof(logger));
            // 本地化
            _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
            InitLocalizationText();

            InitializeCommands();
            LoadAvailableCandidates();

            // ★ 新增: 订阅下一轮请求事件
            _eventAggregator.GetEvent<RequestNextRoundEvent>().Subscribe(payload =>
            {
                InitializeFromProject(payload);
            }, ThreadOption.UIThread);
        }

        #region 本地化

        private string _titleTxt;
        public string TitleTxt { get { return _titleTxt; } set { SetProperty(ref _titleTxt, value); } }

        private string _bottomTipCancelTxt;
        public string BottomTipCancelTxt { get { return _bottomTipCancelTxt; } set { SetProperty(ref _bottomTipCancelTxt, value); } }

        private string _bottomTipTxt;
        public string BottomTipTxt { get { return _bottomTipTxt; } set { SetProperty(ref _bottomTipTxt, value); } }

        private string _factorDefineTxt;
        public string FactorDefineTxt { get { return _factorDefineTxt; } set { SetProperty(ref _factorDefineTxt, value); } }

        private string _designMethodTxt;
        public string DesignMethodTxt { get { return _designMethodTxt; } set { SetProperty(ref _designMethodTxt, value); } }

        private string _matrixPreviewTxt;
        public string MatrixPreviewTxt { get { return _matrixPreviewTxt; } set { SetProperty(ref _matrixPreviewTxt, value); } }

        private string _respAndCondiTxt;
        public string RespAndCondiTxt { get { return _respAndCondiTxt; } set { SetProperty(ref _respAndCondiTxt, value); } }

        private string _confirmSaveTxt;
        public string ConfirmSaveTxt { get { return _confirmSaveTxt; } set { SetProperty(ref _confirmSaveTxt, value); } }

        private string _previousStepTxt;
        public string PreviousStepTxt { get { return _previousStepTxt; } set { SetProperty(ref _previousStepTxt, value); } }

        private string _nextStepTxt;
        public string NextStepTxt { get { return _nextStepTxt; } set { SetProperty(ref _nextStepTxt, value); } }

        private string _saveExecTxt;
        public string SaveExecTxt { get { return _saveExecTxt; } set { SetProperty(ref _saveExecTxt, value); } }

        private string _s1Title;
        public string S1Title { get { return _s1Title; } set { SetProperty(ref _s1Title, value); } }

        private string _s1Subtitle;
        public string S1Subtitle { get { return _s1Subtitle; } set { SetProperty(ref _s1Subtitle, value); } }

        private string _s1AddFactorTxt;
        public string S1AddFactorTxt { get { return _s1AddFactorTxt; } set { SetProperty(ref _s1AddFactorTxt, value); } }

        private string _s1InstructionTxt;
        public string S1InstructionTxt { get { return _s1InstructionTxt; } set { SetProperty(ref _s1InstructionTxt, value); } }

        private string _factorNameHeader;
        public string FactorNameHeader { get { return _factorNameHeader; } set { SetProperty(ref _factorNameHeader, value); } }

        private string _factorTypeHeader;
        public string FactorTypeHeader { get { return _factorTypeHeader; } set { SetProperty(ref _factorTypeHeader, value); } }

        private string _lowLevelHeader;
        public string LowLevelHeader { get { return _lowLevelHeader; } set { SetProperty(ref _lowLevelHeader, value); } }

        private string _highLevelHeader;
        public string HighLevelHeader { get { return _highLevelHeader; } set { SetProperty(ref _highLevelHeader, value); } }

        private string _lowerLimitHeader;
        public string LowerLimitHeader { get { return _lowerLimitHeader; } set { SetProperty(ref _lowerLimitHeader, value); } }

        private string _upperLimitHeader;
        public string UpperLimitHeader { get { return _upperLimitHeader; } set { SetProperty(ref _upperLimitHeader, value); } }

        private string _bindParamHeader;
        public string BindParamHeader { get { return _bindParamHeader; } set { SetProperty(ref _bindParamHeader, value); } }

        private string _s2Title;
        public string S2Title { get { return _s2Title; } set { SetProperty(ref _s2Title, value); } }

        private string _s2Sutitle;
        public string S2Sutitle { get { return _s2Sutitle; } set { SetProperty(ref _s2Sutitle, value); } }

        private string _configParamTxt;
        public string ConfigParamTxt { get { return _configParamTxt; } set { SetProperty(ref _configParamTxt, value); } }

        private string _screeningTitleTxt;
        public string ScreeningTitleTxt { get { return _screeningTitleTxt; } set { SetProperty(ref _screeningTitleTxt, value); } }

        private string _screeningDesc1;
        public string ScreeningDesc1 { get { return _screeningDesc1; } set { SetProperty(ref _screeningDesc1, value); } }

        private string _screeningDesc2;
        public string ScreeningDesc2 { get { return _screeningDesc2; } set { SetProperty(ref _screeningDesc2, value); } }

        private string _screeningLabel;
        public string ScreeningLabel { get { return _screeningLabel; } set { SetProperty(ref _screeningLabel, value); } }

        private string _factorTitle;
        public string FactorTitle { get { return _factorTitle; } set { SetProperty(ref _factorTitle, value); } }

        private string _factorDesc1;
        public string FactorDesc1 { get { return _factorDesc1; } set { SetProperty(ref _factorDesc1, value); } }

        private string _factorDesc2;
        public string FactorDesc2 { get { return _factorDesc2; } set { SetProperty(ref _factorDesc2, value); } }

        private string _factorialTitleTxt;
        public string FactorialTitleTxt { get { return _factorialTitleTxt; } set { SetProperty(ref _factorialTitleTxt, value); } }

        private string _fullTitle;
        public string FullTitle { get { return _fullTitle; } set { SetProperty(ref _fullTitle, value); } }

        private string _fullDesc1;
        public string FullDesc1 { get { return _fullDesc1; } set { SetProperty(ref _fullDesc1, value); } }

        private string _fullDesc2;
        public string FullDesc2 { get { return _fullDesc2; } set { SetProperty(ref _fullDesc2, value); } }

        private string _factorialLabel;
        public string FactorialLabel { get { return _factorialLabel; } set { SetProperty(ref _factorialLabel, value); } }

        private string _rsmTitleTxt;
        public string RsmTitleTxt { get { return _rsmTitleTxt; } set { SetProperty(ref _rsmTitleTxt, value); } }

        private string _ccdTitle;
        public string CcdTitle { get { return _ccdTitle; } set { SetProperty(ref _ccdTitle, value); } }

        private string _ccdDesc1;
        public string CcdDesc1 { get { return _ccdDesc1; } set { SetProperty(ref _ccdDesc1, value); } }

        private string _ccdDesc2;
        public string CcdDesc2 { get { return _ccdDesc2; } set { SetProperty(ref _ccdDesc2, value); } }

        private string _recommandLabel;
        public string RecommandLabel { get { return _recommandLabel; } set { SetProperty(ref _recommandLabel, value); } }

        private string _boxBehTitle;
        public string BoxBehTitle { get { return _boxBehTitle; } set { SetProperty(ref _boxBehTitle, value); } }

        private string _boxBehDesc1;
        public string BoxBehDesc1 { get { return _boxBehDesc1; } set { SetProperty(ref _boxBehDesc1, value); } }

        private string _boxBehDesc2;
        public string BoxBehDesc2 { get { return _boxBehDesc2; } set { SetProperty(ref _boxBehDesc2, value); } }

        private string _rsmLabel;
        public string RsmLabel { get { return _rsmLabel; } set { SetProperty(ref _rsmLabel, value); } }

        private string _dOptTitle;
        public string DOptTitle { get { return _dOptTitle; } set { SetProperty(ref _dOptTitle, value); } }

        private string _dOptDesc1;
        public string DOptDesc1 { get { return _dOptDesc1; } set { SetProperty(ref _dOptDesc1, value); } }

        private string _dOptDesc2;
        public string DOptDesc2 { get { return _dOptDesc2; } set { SetProperty(ref _dOptDesc2, value); } }

        private string _tagTitle;
        public string TagTitle { get { return _tagTitle; } set { SetProperty(ref _tagTitle, value); } }

        private string _tagDesc1;
        public string TagDesc1 { get { return _tagDesc1; } set { SetProperty(ref _tagDesc1, value); } }

        private string _tagDesc2;
        public string TagDesc2 { get { return _tagDesc2; } set { SetProperty(ref _tagDesc2, value); } }

        private string _customTitleTxt;
        public string CustomTitleTxt { get { return _customTitleTxt; } set { SetProperty(ref _customTitleTxt, value); } }

        private string _customTitle;
        public string CustomTitle { get { return _customTitle; } set { SetProperty(ref _customTitle, value); } }

        private string _customDesc1;
        public string CustomDesc1 { get { return _customDesc1; } set { SetProperty(ref _customDesc1, value); } }

        private string _customDesc2;
        public string CustomDesc2 { get { return _customDesc2; } set { SetProperty(ref _customDesc2, value); } }

        private string _customLabel;
        public string CustomLabel { get { return _customLabel; } set { SetProperty(ref _customLabel, value); } }

        private string _randomOrderTxt;
        public string RandomOrderTxt { get { return _randomOrderTxt; } set { SetProperty(ref _randomOrderTxt, value); } }

        private string _s3Title;
        public string S3Title { get { return _s3Title; } set { SetProperty(ref _s3Title, value); } }

        private string _s3Sutitle;
        public string S3Sutitle { get { return _s3Sutitle; } set { SetProperty(ref _s3Sutitle, value); } }

        private string _randomTxt;
        public string RandomTxt { get { return _randomTxt; } set { SetProperty(ref _randomTxt, value); } }

        private string _centerPointTxt;
        public string CenterPointTxt { get { return _centerPointTxt; } set { SetProperty(ref _centerPointTxt, value); } }

        private string _totalTxt;
        public string TotalTxt { get { return _totalTxt; } set { SetProperty(ref _totalTxt, value); } }

        private string _groupTxt;
        public string GroupTxt { get { return _groupTxt; } set { SetProperty(ref _groupTxt, value); } }

        private string _s4Title;
        public string S4Title { get { return _s4Title; } set { SetProperty(ref _s4Title, value); } }

        private string _s4Subtitle;
        public string S4Subtitle { get { return _s4Subtitle; } set { SetProperty(ref _s4Subtitle, value); } }

        private string _respVarTxt;
        public string RespVarTxt { get { return _respVarTxt; } set { SetProperty(ref _respVarTxt, value); } }

        private string _nameHeader;
        public string NameHeader { get { return _nameHeader; } set { SetProperty(ref _nameHeader, value); } }

        private string _targetHeader;
        public string TargetHeader { get { return _targetHeader; } set { SetProperty(ref _targetHeader, value); } }

        private string _weightHeader;
        public string WeightHeader { get { return _weightHeader; } set { SetProperty(ref _weightHeader, value); } }

        private string _addRespHeader;
        public string AddRespHeader { get { return _addRespHeader; } set { SetProperty(ref _addRespHeader, value); } }

        private string _stopCondiTxt;
        public string StopCondiTxt { get { return _stopCondiTxt; } set { SetProperty(ref _stopCondiTxt, value); } }

        private string _reaponseHeader;
        public string ReaponseHeader { get { return _reaponseHeader; } set { SetProperty(ref _reaponseHeader, value); } }

        private string _conditionHeader;
        public string ConditionHeader { get { return _conditionHeader; } set { SetProperty(ref _conditionHeader, value); } }

        private string _targetValueHeader;
        public string TargetValueHeader { get { return _targetValueHeader; } set { SetProperty(ref _targetValueHeader, value); } }

        private string _addCondiHeader;
        public string AddCondiHeader { get { return _addCondiHeader; } set { SetProperty(ref _addCondiHeader, value); } }

        private string _s5Title;
        public string S5Title { get { return _s5Title; } set { SetProperty(ref _s5Title, value); } }

        private string _s5Subtitle;
        public string S5Subtitle { get { return _s5Subtitle; } set { SetProperty(ref _s5Subtitle, value); } }

        private string _batchNameLabel;
        public string BatchNameLabel { get { return _batchNameLabel; } set { SetProperty(ref _batchNameLabel, value); } }

        private string _factorNumberTxt;
        public string FactorNumberTxt { get { return _factorNumberTxt; } set { SetProperty(ref _factorNumberTxt, value); } }

        private string _expriGroupTxt;
        public string ExpriGroupTxt { get { return _expriGroupTxt; } set { SetProperty(ref _expriGroupTxt, value); } }

        private string _stopCondiLabel;
        public string StopCondiLabel { get { return _stopCondiLabel; } set { SetProperty(ref _stopCondiLabel, value); } }

        private string _historyDataTxt;
        public string HistoryDataTxt { get { return _historyDataTxt; } set { SetProperty(ref _historyDataTxt, value); } }

        private string _designWeightTxt;
        public string DesignWeightTxt { get { return _designWeightTxt; } set { SetProperty(ref _designWeightTxt, value); } }

        private string _aEfficiency;
        public string AEfficiency { get { return _aEfficiency; } set { SetProperty(ref _aEfficiency, value); } }

        private string _dEfficiency;
        public string DEfficiency { get { return _dEfficiency; } set { SetProperty(ref _dEfficiency, value); } }

        private string _gEfficiency;
        public string GEfficiency { get { return _gEfficiency; } set { SetProperty(ref _gEfficiency, value); } }

        private string _freedom;
        public string Freedom { get { return _freedom; } set { SetProperty(ref _freedom, value); } }

        private string _paramConfigTitle;
        public string ParamConfigTitle { get { return _paramConfigTitle; } set { SetProperty(ref _paramConfigTitle, value); } }

        private string _confirm;
        public string Confirm { get { return _confirm; } set { SetProperty(ref _confirm, value); } }

        private string _expriNumberTxt;
        public string ExpriNumberTxt { get { return _expriNumberTxt; } set { SetProperty(ref _expriNumberTxt, value); } }

        private string _centerPointLabel;
        public string CenterPointLabel { get { return _centerPointLabel; } set { SetProperty(ref _centerPointLabel, value); } }

        private string _linesLabel;
        public string LinesLabel { get { return _linesLabel; } set { SetProperty(ref _linesLabel, value); } }

        private string _resolutionLabel;
        public string ResolutionLabel { get { return _resolutionLabel; } set { SetProperty(ref _resolutionLabel, value); } }

        private string _runLinesLabel;
        public string RunLinesLabel { get { return _runLinesLabel; } set { SetProperty(ref _runLinesLabel, value); } }

        private string _levelNumberLabel;
        public string LevelNumberLabel { get { return _levelNumberLabel; } set { SetProperty(ref _levelNumberLabel, value); } }

        private string _alphaTypeLabel;
        public string AlphaTypeLabel { get { return _alphaTypeLabel; } set { SetProperty(ref _alphaTypeLabel, value); } }

        private string _cubePointLabel;
        public string CubePointLabel { get { return _cubePointLabel; } set { SetProperty(ref _cubePointLabel, value); } }

        private string _axisPointLabel;
        public string AxisPointLabel { get { return _axisPointLabel; } set { SetProperty(ref _axisPointLabel, value); } }

        private string _useHalfFactorLabel;
        public string UseHalfFactorLabel { get { return _useHalfFactorLabel; } set { SetProperty(ref _useHalfFactorLabel, value); } }

        private string _modelLabel;
        public string ModelLabel { get { return _modelLabel; } set { SetProperty(ref _modelLabel, value); } }

        private string _restratsLabel;
        public string RestratsLabel { get { return _restratsLabel; } set { SetProperty(ref _restratsLabel, value); } }

        private string _iterationLabel;
        public string IterationLabel { get { return _iterationLabel; } set { SetProperty(ref _iterationLabel, value); } }

        private string _condidateGridLabel;
        public string CondidateGridLabel { get { return _condidateGridLabel; } set { SetProperty(ref _condidateGridLabel, value); } }

        private string _randomSeedLabel;
        public string RandomSeedLabel { get { return _randomSeedLabel; } set { SetProperty(ref _randomSeedLabel, value); } }

        private string _lTableLabel;
        public string LTableLabel { get { return _lTableLabel; } set { SetProperty(ref _lTableLabel, value); } }

        private string _snrLabel;
        public string SnrLabel { get { return _snrLabel; } set { SetProperty(ref _snrLabel, value); } }

        private string _analysisTypeLabel;
        public string AnalysisTypeLabel { get { return _analysisTypeLabel; } set { SetProperty(ref _analysisTypeLabel, value); } }

        private string _rotatableItem;
        public string RotatableItem { get { return _rotatableItem; } set { SetProperty(ref _rotatableItem, value); } }

        private string _orthogonalItem;
        public string OrthogonalItem { get { return _orthogonalItem; } set { SetProperty(ref _orthogonalItem, value); } }

        private string _faceHeartItem;
        public string FaceHeartItem { get { return _faceHeartItem; } set { SetProperty(ref _faceHeartItem, value); } }

        private string _practicalItem;
        public string PracticalItem { get { return _practicalItem; } set { SetProperty(ref _practicalItem, value); } }

        private string _doptimalTip;
        public string DoptimalTip { get { return _doptimalTip; } set { SetProperty(ref _doptimalTip, value); } }

        private string _modelLinear;
        public string ModelLinear { get { return _modelLinear; } set { SetProperty(ref _modelLinear, value); } }

        private string _modelInteraction;
        public string ModelInteraction { get { return _modelInteraction; } set { SetProperty(ref _modelInteraction, value); } }

        private string _modelQuadratic;
        public string ModelQuadratic { get { return _modelQuadratic; } set { SetProperty(ref _modelQuadratic, value); } }

        private string _seedTip;
        public string SeedTip { get { return _seedTip; } set { SetProperty(ref _seedTip, value); } }

        private string _autoItem;
        public string AutoItem { get { return _autoItem; } set { SetProperty(ref _autoItem, value); } }

        private string _largerItem;
        public string LargerItem { get { return _largerItem; } set { SetProperty(ref _largerItem, value); } }

        private string _smallerItem;
        public string SmallerItem { get { return _smallerItem; } set { SetProperty(ref _smallerItem, value); } }

        private string _nominalItem;
        public string NominalItem { get { return _nominalItem; } set { SetProperty(ref _nominalItem, value); } }

        private string _analysisLinear;
        public string AnalysisLinear { get { return _analysisLinear; } set { SetProperty(ref _analysisLinear, value); } }

        private string _analysisInteraction;
        public string AnalysisInteraction { get { return _analysisInteraction; } set { SetProperty(ref _analysisInteraction, value); } }

        private string _analysisQuadratic;
        public string AnalysisQuadratic { get { return _analysisQuadratic; } set { SetProperty(ref _analysisQuadratic, value); } }

        private string _cubeTip;
        private string CubeTip { get { return _cubeTip; } set { SetProperty(ref _cubeTip, value); } }


        private void InitLocalizationText()
        {
            TitleTxt = _localizationService.GetString("Doe_Guide_Title", "DOE 向导");
            BottomTipCancelTxt = _localizationService.GetString("Doe_Guide_PressEsc", "按 ESC 取消");
            BottomTipTxt = _localizationService.GetString("Doe_Guide_AutoSave", "进度自动保存");
            FactorDefineTxt = _localizationService.GetString("Doe_Gudie_FactorDefine", "因子定义");
            DesignMethodTxt = _localizationService.GetString("Doe_Guide_DesignMethod", "设计方法");
            MatrixPreviewTxt = _localizationService.GetString("Doe_Guide_MatrixPreview", "矩阵预览");
            RespAndCondiTxt = _localizationService.GetString("Doe_Guide_ResponseCondition", "响应条件");
            ConfirmSaveTxt = _localizationService.GetString("Doe_Guide_ConfirmSave", "确认保存");
            PreviousStepTxt = _localizationService.GetString("Doe_Guide_PreviousStep", "← 上一步");
            NextStepTxt = _localizationService.GetString("Doe_Guide_NextStep", "下一步 →");
            SaveExecTxt = _localizationService.GetString("Doe_Guide_SaveStartExec", "保存并开始执行");

            S1Title = _localizationService.GetString("Doe_Guide_Step1Title", "定义实验因子");
            S1Subtitle = _localizationService.GetString("Doe_Guide_Step1SubTitle", "添加因子，设置类型和水平范围");
            S1AddFactorTxt = _localizationService.GetString("Doe_Guide_AddFactor", "+ 添加因子");
            S1InstructionTxt = _localizationService.GetString("Doe_Guide_Instruction", "连续因子默认绑定流程参数;勾选「手动」则不绑定,由操作员按矩阵值手动调节。类别因子始终为手动操作。");
            FactorNameHeader = _localizationService.GetString("Doe_Guide_FactorName", "因子名称");
            FactorTypeHeader = _localizationService.GetString("Doe_Guide_Type", "类型");
            LowLevelHeader = _localizationService.GetString("Doe_Guide_LowLevel", "低水平");
            HighLevelHeader = _localizationService.GetString("Doe_Guide_HighLevel", "高水平");
            LowerLimitHeader = _localizationService.GetString("Doe_Guide_Lower", "物理下限");
            UpperLimitHeader = _localizationService.GetString("Doe_Guide_Upper", "物理上限");
            BindParamHeader = _localizationService.GetString("Doe_Guide_BindParam", "绑定参数");

            S2Title = _localizationService.GetString("Doe_Guide_Step2Title", "选择设计方法");
            S2Sutitle = _localizationService.GetString("Doe_Guide_Step2SubTitle", "点击卡片选择方法，点「配置参数」调整详细设置");
            ConfigParamTxt = _localizationService.GetString("Doe_Guide_ConfigParam", "配置参数 →");
            ScreeningTitleTxt = _localizationService.GetString("Doe_Guide_ScreeningTitle", "筛选 Screening");
            ScreeningDesc1 = _localizationService.GetString("Doe_Guide_PB_Desc1", "经济筛选设计");
            ScreeningDesc2 = _localizationService.GetString("Doe_Guide_PB_Desc2", "实验次数为4的倍数，最多23因子");
            ScreeningLabel = _localizationService.GetString("Doe_Guide_Screen", "刷选");
            FactorTitle = _localizationService.GetString("Doe_Guide_Part_Title", "部分因子（Fractional）");
            FactorDesc1 = _localizationService.GetString("Doe_Guide_Part_Desc1", "2^(k-p) 分辨度可控");
            FactorDesc2 = _localizationService.GetString("Doe_Guide_Part_Desc2", "用更少实验量捕获主效应");
            FactorialTitleTxt = _localizationService.GetString("Doe_Guide_FactorialTitle", "因子 Factorial");
            FullTitle = _localizationService.GetString("Doe_Guide_Full_Title", "全因子（Full Factorial）");
            FullDesc1 = _localizationService.GetString("Doe_Guide_Full_Desc1", "完全组合无混杂");
            FullDesc2 = _localizationService.GetString("Doe_Guide_Full_Desc2", "所有因子所有水平完全交叉");
            FactorialLabel = _localizationService.GetString("Doe_Guide_Factor", "因子");
            RsmTitleTxt = _localizationService.GetString("Doe_Guide_RSMTitle", "响应曲面 RSM");
            CcdTitle = _localizationService.GetString("Doe_Guide_CCD_Title", "CCD 中心复合设计");
            CcdDesc1 = _localizationService.GetString("Doe_Guide_CCD_Desc1", "RSM 最常用");
            CcdDesc2 = _localizationService.GetString("Doe_Guide_CCD_Desc2", "角点+轴点+中心点");
            RecommandLabel = _localizationService.GetString("Doe_Guide_Recommend", "推荐");
            //BoxBehTitle = _localizationService.GetString("", "");
            BoxBehDesc1 = _localizationService.GetString("Doe_Guide_BB_Desc1", "无极端角点");
            BoxBehDesc2 = _localizationService.GetString("Doe_Guide_BB_Desc2", "化工安全性更好，需 3+ 连续因子");
            //RsmLabel = _localizationService.GetString("Doe_Guide_RSM", "RSM");
            //DOptTitle = _localizationService.GetString("", "");
            DOptDesc1 = _localizationService.GetString("Doe_Guide_DO_Desc1", "不规则因子空间");
            DOptDesc2 = _localizationService.GetString("Doe_Guide_DO_Desc2", "coordinate-exchange 算法");
            TagTitle = _localizationService.GetString("Doe_Guide_Tag_Title", "Taguchi 正交设计");
            TagDesc1 = _localizationService.GetString("Doe_Guide_Tag_Desc1", "标准 L 表");
            TagDesc2 = _localizationService.GetString("Doe_Guide_Tag_Desc2", "最经济实验数");
            CustomTitleTxt = _localizationService.GetString("Doe_Guide_CustTitle", "自定义 Custom");
            CustomTitle = _localizationService.GetString("Doe_Guide_Cust_Title", "自定义导入");
            CustomDesc1 = _localizationService.GetString("Doe_Guide_Cust_Desc1", "从 Excel 导入矩阵");
            CustomDesc2 = _localizationService.GetString("Doe_Guide_Cust_Desc2", "列名需要匹配因子名称");
            CustomLabel = _localizationService.GetString("Doe_Guide_Custom", "自定义");
            RandomOrderTxt = _localizationService.GetString("Doe_Guide_Randomization", "随机化运行顺序");

            S3Title = _localizationService.GetString("Doe_Guide_Step3Title", "实验矩阵预览");
            S3Sutitle = _localizationService.GetString("Doe_Guide_Step3SubTitle", "检查生成的实验方案");
            RandomTxt = _localizationService.GetString("Doe_Guide_Random", "随机化");
            CenterPointTxt = _localizationService.GetString("Doe_Guide_Drawer_PB_Center", "中心点");
            TotalTxt = _localizationService.GetString("Doe_Guide_Totle", "共 ");
            GroupTxt = _localizationService.GetString("Doe_Guide_Group", " 组");

            S4Title = _localizationService.GetString("Doe_Guide_Step4Title", "响应变量与停止条件");
            S4Subtitle = _localizationService.GetString("Doe_Guide_Step4SubTitle", "定义测量指标和自动终止规则");
            RespVarTxt = _localizationService.GetString("Doe_Guide_ResponseVar", "响应变量");
            NameHeader = _localizationService.GetString("Doe_Guide_Header_Name", "名称");
            TargetHeader = _localizationService.GetString("Doe_Guide_Header_Target", "目标");
            WeightHeader = _localizationService.GetString("Doe_Guide_Header_Weight", "权重");
            AddRespHeader = _localizationService.GetString("Doe_Guide_AddResponse", "+ 添加响应");
            StopCondiTxt = _localizationService.GetString("Doe_Guide_StopCondi", "停止条件（可选）");
            ReaponseHeader = _localizationService.GetString("Doe_Guide_Header_Response", "响应");
            ConditionHeader = _localizationService.GetString("Doe_Guide_Header_Condition", "条件");
            TargetValueHeader = _localizationService.GetString("Doe_Guide_Header_TargetValue", "目标值");
            AddCondiHeader = _localizationService.GetString("Doe_Guide_AddCondition", "+ 添加条件");

            S5Title = _localizationService.GetString("Doe_Guide_Step5Title", "确认并保存");
            S5Subtitle = _localizationService.GetString("Doe_Guide_Step5SubTitle", "核对以下配置，确认无误后保存");
            BatchNameLabel = _localizationService.GetString("Doe_Guide_BatchName", "批次名称");
            FactorNumberTxt = _localizationService.GetString("Doe_Guide_FactorNumber", "因子数");
            ExpriGroupTxt = _localizationService.GetString("Doe_Guide_ExperiGroups", "实验组数");
            StopCondiLabel = _localizationService.GetString("Doe_Guide_StopCondition", "停止条件");
            HistoryDataTxt = _localizationService.GetString("Doe_Guide_HistoryData", "历史数据");
            DesignWeightTxt = _localizationService.GetString("Doe_Guide_DesignWeight", "设计质量");
            AEfficiency = _localizationService.GetString("Doe_Guide_AEfficiency", "A-效率");
            DEfficiency = _localizationService.GetString("Doe_Guide_DEfficiency", "D-效率");
            GEfficiency = _localizationService.GetString("Doe_Guide_GEfficiency", "G-效率");
            Freedom = _localizationService.GetString("Doe_Guide_Freedom", "自由度");

            ParamConfigTitle = _localizationService.GetString("Doe_Guide_Drawer_SubTitle", "参数配置");
            Confirm = _localizationService.GetString("Doe_Guide_Drawer_Confirm", "确认");
            ExpriNumberTxt = _localizationService.GetString("Doe_Guide_Drawer_PB_Number", "实验次数");
            CenterPointLabel = _localizationService.GetString("Doe_Guide_Drawer_PB_Center", "中心点");
            LinesLabel = _localizationService.GetString("Doe_Guide_Drawer_PB_Rows", "仿行数");
            ResolutionLabel = _localizationService.GetString("Doe_Guide_Drawer_FF_Resolution", "分辨度");
            RunLinesLabel = _localizationService.GetString("Doe_Guide_Drawer_FF_RunRows", "运行数");
            LevelNumberLabel = _localizationService.GetString("Doe_Guide_Drawer_Full_Hori", "水平数");
            AlphaTypeLabel = _localizationService.GetString("Doe_Guide_Drawer_CCD_Alpha", "Alpha 类型");
            CubePointLabel = _localizationService.GetString("Doe_Guide_Drawer_CCD_Cube", "立方中心点");
            AxisPointLabel = _localizationService.GetString("Doe_Guide_Drawer_CCD_Axis", "轴点中心点");
            UseHalfFactorLabel = _localizationService.GetString("Doe_Guide_Drawer_CCD_UseHalf", "使用 1/2 部分因子");
            ModelLabel = _localizationService.GetString("Doe_Guide_Drawer_DOpt_Model", "模型");
            RestratsLabel = _localizationService.GetString("Doe_Guide_Drawer_DOpt_Restore", "重启次数");
            IterationLabel = _localizationService.GetString("Doe_Guide_Drawer_DOpt_Iteration", "迭代上限");
            CondidateGridLabel = _localizationService.GetString("Doe_Guide_Drawer_DOpt_Grid", "候选网格");
            RandomSeedLabel = _localizationService.GetString("Doe_Guide_Drawer_DOpt_Seed", "随机种子");
            LTableLabel = _localizationService.GetString("Doe_Guide_Drawer_DOpt_L", "L表");
            SnrLabel = _localizationService.GetString("Doe_Guide_Drawer_DOpt_Noise", "信噪比");
            AnalysisTypeLabel = _localizationService.GetString("Doe_Guide_Drawer_Custom_Type", "分析类型");

            RotatableItem = _localizationService.GetString("Doe_Guide_Alpha_Rotatable", "可旋转（推荐）");
            OrthogonalItem = _localizationService.GetString("Doe_Guide_Alpha_Orthogonal", "正交");
            FaceHeartItem = _localizationService.GetString("Doe_Guide_Alpha_Face", "面心（α=1）");
            PracticalItem = _localizationService.GetString("Doe_Guide_Alpha_Practical", "实用");
            DoptimalTip = _localizationService.GetString("Doe_Guide_Drawer_DOpt_Instruc", "-1 = 自动");
            ModelLinear = _localizationService.GetString("Doe_Guide_Model_Linear", "线性");
            ModelInteraction = _localizationService.GetString("Doe_Guide_Model_Interaction", "交互");
            ModelQuadratic = _localizationService.GetString("Doe_Guide_Model_Quadratic", "二阶（推荐）");
            SeedTip = _localizationService.GetString("Doe_Guide_Drawer_DOpt_SeedTip", "0 = 时间种子");
            AutoItem = _localizationService.GetString("Doe_Guide_L_Auto", "自动");
            LargerItem = _localizationService.GetString("Doe_Guide_Noise_Larger", "越大越好");
            SmallerItem = _localizationService.GetString("Doe_Guide_Noise_Smaller", "越小越好");
            NominalItem = _localizationService.GetString("Doe_Guide_Noise_Nominal", "望目");
            AnalysisLinear = _localizationService.GetString("Doe_Guide_AT_Linear", "筛选（主效应）");
            AnalysisInteraction = _localizationService.GetString("Doe_Guide_AT_Interaction", "因子（主效应+交互）");
            AnalysisQuadratic = _localizationService.GetString("Doe_Guide_AT_Quadratic", "响应曲面（二阶）");
            CubeTip = _localizationService.GetString("Doe_Guide_Drawer_CCD_CubeTip", "-1 = Minitab 默认");
        }

        public string SetDrawerTitleLocalizationTxt(string key,string defTxt)
        {
            return _localizationService.GetString(key, defTxt);
        }

        #endregion

        // ══════════════ Properties ══════════════

        public int CurrentStep { get => _currentStep; set { SetProperty(ref _currentStep, value); UpdateStepVisibility(); } }
        public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
        public string BatchName { get => _batchName; set => SetProperty(ref _batchName, value); }
        //  新增: CCD 属性
        public string CcdAlphaType { get => _ccdAlphaType; set => SetProperty(ref _ccdAlphaType, value); }
        public int CcdCenterCount { get => _ccdCenterCount; set => SetProperty(ref _ccdCenterCount, value); }

        //  新增: BBD 属性
        public int BbdCenterCount { get => _bbdCenterCount; set => SetProperty(ref _bbdCenterCount, value); }
        //
        //  新增: D-Optimal 属性
        public int DOptimalRunCount { get => _dOptimalRunCount; set => SetProperty(ref _dOptimalRunCount, value); }
        public string DOptimalModelType { get => _dOptimalModelType; set => SetProperty(ref _dOptimalModelType, value); }
        private string _customAnalysisType = "linear";
        /// <summary>自定义导入的分析类型（linear/interaction/quadratic）</summary>
        public string CustomAnalysisType
        {
            get => _customAnalysisType;
            set => SetProperty(ref _customAnalysisType, value);
        }
        //  新增: 设计质量评估结果
        public DOEDesignQuality? DesignQuality
        {
            get => _designQuality; set => SetProperty(ref _designQuality, value);
        }
        //  新增: 设计质量显示属性（供 XAML 绑定）
        public string QualityDEffText => DesignQuality != null ? $"{DesignQuality.DEfficiency:P1}" : "—";
        public string QualityAEffText => DesignQuality != null ? $"{DesignQuality.AEfficiency:P1}" : "—";
        public string QualityGEffText => DesignQuality != null ? $"{DesignQuality.GEfficiency:P1}" : "—";
        public string QualityDofText => DesignQuality != null ? $"{DesignQuality.DegreesOfFreedom}" : "—";
        public bool HasDesignQuality => DesignQuality != null;

        // ★ 新增: 项目模式属性
        /// <summary>是否在项目模式下创建批次</summary>
        public bool IsProjectMode
        {
            get => _isProjectMode;
            set => SetProperty(ref _isProjectMode, value);
        }

        private string _projectName = "";
        /// <summary>项目名称（UI 显示用）</summary>
        public string ProjectName
        {
            get => _projectName;
            set => SetProperty(ref _projectName, value);
        }

        // Step1
        public ObservableCollection<FactorViewModel> Factors { get => _factors; set => SetProperty(ref _factors, value); }
        public ObservableCollection<FactorCandidate> AvailableCandidates { get => _availableCandidates; set => SetProperty(ref _availableCandidates, value); }

        // Step2
        public DOEDesignMethod SelectedMethod { get => _selectedMethod; set => SetProperty(ref _selectedMethod, value); }
        public string TaguchiTableType { get => _taguchiTableType; set => SetProperty(ref _taguchiTableType, value); }
        public int FractionalResolution { get => _fractionalResolution; set => SetProperty(ref _fractionalResolution, value); }

        // Step3
        public DataTable? MatrixTable { get => _matrixTable; set => SetProperty(ref _matrixTable, value); }
        public int MatrixRunCount => _designMatrix?.RunCount ?? 0;
        public int CenterPointCount { get => _centerPointCount; set => SetProperty(ref _centerPointCount, value); }
        public bool IsRandomized { get => _isRandomized; set => SetProperty(ref _isRandomized, value); }

        // Step4
        public ObservableCollection<ResponseViewModel> Responses { get => _responses; set => SetProperty(ref _responses, value); }
        public ObservableCollection<StopConditionViewModel> StopConditions { get => _stopConditions; set => SetProperty(ref _stopConditions, value); }

        // Step5
        public string? ImportFilePath
        {
            get => _importFilePath;
            set
            {
                if (SetProperty(ref _importFilePath, value))
                {
                    ValidateImportCommand.RaiseCanExecuteChanged();
                }
            }
        }
        public DataValidationResult? ValidationResult { get => _validationResult; set => SetProperty(ref _validationResult, value); }
        public int ImportedDataCount { get => _importedDataCount; set => SetProperty(ref _importedDataCount, value); }

        // Step visibility
        public bool IsStep1Visible => CurrentStep == 1;
        public bool IsStep2Visible => CurrentStep == 2;
        public bool IsStep3Visible => CurrentStep == 3;
        public bool IsStep4Visible => CurrentStep == 4;
        public bool IsStep5Visible => CurrentStep == 5;
        /// <summary>Full Factorial 水平数（2 或 3）</summary>
        public int FullFactorialLevels
        {
            get => _fullFactorialLevels;
            set => SetProperty(ref _fullFactorialLevels, value);
        }

        /// <summary>Full Factorial 中心点数</summary>
        public int FullFactorialCenterCount
        {
            get => _fullFactorialCenterCount;
            set => SetProperty(ref _fullFactorialCenterCount, value);
        }
        // Plackett-Burman
        public int PbRunCount { get => _pbRunCount; set => SetProperty(ref _pbRunCount, value); }
        public int PbCenterCount { get => _pbCenterCount; set => SetProperty(ref _pbCenterCount, value); }
        public int PbReplicates { get => _pbReplicates; set => SetProperty(ref _pbReplicates, value); }

        // Fractional Factorial
        public int FfRunCount { get => _ffRunCount; set => SetProperty(ref _ffRunCount, value); }
        public int FfCenterCount { get => _ffCenterCount; set => SetProperty(ref _ffCenterCount, value); }
        public int FfReplicates { get => _ffReplicates; set => SetProperty(ref _ffReplicates, value); }

        // Full Factorial (仿行)
        public int FullFactorialReplicates { get => _fullFactorialReplicates; set => SetProperty(ref _fullFactorialReplicates, value); }

        // CCD
        public int CcdCubeCenterCount { get => _ccdCubeCenterCount; set => SetProperty(ref _ccdCubeCenterCount, value); }
        public int CcdAxialCenterCount { get => _ccdAxialCenterCount; set => SetProperty(ref _ccdAxialCenterCount, value); }
        public int CcdReplicates { get => _ccdReplicates; set => SetProperty(ref _ccdReplicates, value); }
        public bool CcdUseFractional { get => _ccdUseFractional; set => SetProperty(ref _ccdUseFractional, value); }

        // BBD
        public int BbdReplicates { get => _bbdReplicates; set => SetProperty(ref _bbdReplicates, value); }

        // D-Optimal
        public int DOptRestarts { get => _dOptRestarts; set => SetProperty(ref _dOptRestarts, value); }
        public int DOptMaxIterations { get => _dOptMaxIterations; set => SetProperty(ref _dOptMaxIterations, value); }
        public int DOptRandomSeed { get => _dOptRandomSeed; set => SetProperty(ref _dOptRandomSeed, value); }
        public int DOptCandidateGridSize { get => _dOptCandidateGridSize; set => SetProperty(ref _dOptCandidateGridSize, value); }

        // Taguchi
        public string TaguchiSnRatio { get => _taguchiSnRatio; set => SetProperty(ref _taguchiSnRatio, value); }

        // 通用
        public bool RandomizeRunOrder { get => _randomizeRunOrder; set => SetProperty(ref _randomizeRunOrder, value); }


        // ══════════════ Commands ══════════════

        public DelegateCommand NextStepCommand { get; private set; } = null!;
        public DelegateCommand PreviousStepCommand { get; private set; } = null!;
        public DelegateCommand AddFactorCommand { get; private set; } = null!;
        public DelegateCommand<FactorViewModel> RemoveFactorCommand { get; private set; } = null!;
        public DelegateCommand AddResponseCommand { get; private set; } = null!;
        public DelegateCommand<ResponseViewModel> RemoveResponseCommand { get; private set; } = null!;
        public DelegateCommand AddStopConditionCommand { get; private set; } = null!;
        public DelegateCommand<StopConditionViewModel> RemoveStopConditionCommand { get; private set; } = null!;
        public DelegateCommand GenerateMatrixCommand { get; private set; } = null!;
        public DelegateCommand RandomizeMatrixCommand { get; private set; } = null!;
        public DelegateCommand AddCenterPointsCommand { get; private set; } = null!;
        public DelegateCommand BrowseImportFileCommand { get; private set; } = null!;
        public DelegateCommand ValidateImportCommand { get; private set; } = null!;
        public DelegateCommand SaveBatchCommand { get; private set; } = null!;

        /// <summary>
        /// 保存完成事件 — View 层订阅此事件关闭窗口
        /// </summary>
        public event EventHandler<string>? BatchSaved;

        private void InitializeCommands()
        {
            NextStepCommand = new DelegateCommand(async () => await GoNextStepAsync(), () => CurrentStep < TOTAL_STEPS);
            PreviousStepCommand = new DelegateCommand(() => { CurrentStep--; }, () => CurrentStep > 1);
            AddFactorCommand = new DelegateCommand(AddFactor);
            RemoveFactorCommand = new DelegateCommand<FactorViewModel>(f => { if (f != null) Factors.Remove(f); });
            AddResponseCommand = new DelegateCommand(AddResponse);
            RemoveResponseCommand = new DelegateCommand<ResponseViewModel>(r => { if (r != null) Responses.Remove(r); });
            AddStopConditionCommand = new DelegateCommand(AddStopCondition);
            RemoveStopConditionCommand = new DelegateCommand<StopConditionViewModel>(c => { if (c != null) StopConditions.Remove(c); });
            GenerateMatrixCommand = new DelegateCommand(async () => await GenerateMatrixAsync());
            RandomizeMatrixCommand = new DelegateCommand(RandomizeMatrix, () => _designMatrix != null);
            AddCenterPointsCommand = new DelegateCommand(async () => await AddCenterPointsAsync(), () => _designMatrix != null);
            BrowseImportFileCommand = new DelegateCommand(BrowseImportFile);
            ValidateImportCommand = new DelegateCommand(async () => await ValidateImportAsync(), () => !string.IsNullOrEmpty(ImportFilePath));
            SaveBatchCommand = new DelegateCommand(async () => await SaveBatchAsync());
        }

        // ══════════════ ★ 新增: 项目模式初始化 ══════════════

        /// <summary>
        /// ★ 新增: 从项目模式初始化设计向导
        ///
        /// 由 RequestNextRoundEvent 触发调用。
        /// 自动预填活跃因子、推荐设计方法、关联项目 ID。
        /// </summary>
        public void InitializeFromProject(NextRoundPayload payload)
        {
            _currentProjectId = payload.ProjectId;
            _currentRoundNumber = payload.RoundNumber;
            _currentProjectPhase = payload.RecommendedPhase;
            IsProjectMode = true;

            // 预填因子
            Factors.Clear();
            foreach (var f in payload.PrefilledFactors)
            {
                var fvm = new FactorViewModel
                {
                    FactorName = f.FactorName,
                    FactorType = f.FactorType,
                    LowerBound = f.LowerBound,
                    UpperBound = f.UpperBound,
                    LevelCount = f.LevelCount,
                    CategoryLevels = f.CategoryLevels ?? "",
                    AvailableCandidates = new ObservableCollection<FactorCandidate>(AvailableCandidates)
                };
                // 尝试绑定参数候选
                if (!string.IsNullOrEmpty(f.SourceNodeId) && !string.IsNullOrEmpty(f.SourceParamName))
                {
                    var candidate = fvm.AvailableCandidates
                        .FirstOrDefault(c => c.NodeId == f.SourceNodeId && c.ParameterName == f.SourceParamName);
                    if (candidate != null)
                        fvm.SelectedCandidate = candidate;
                }
                Factors.Add(fvm);
            }

            // 预选设计方法
            SelectedMethod = payload.RecommendedMethod;

            // ★ 优化2: 预填响应变量（从上一轮继承）
            if (payload.PrefilledResponses.Count > 0)
            {
                Responses.Clear();
                foreach (var r in payload.PrefilledResponses)
                {
                    Responses.Add(new ResponseViewModel
                    {
                        ResponseName = r.ResponseName,
                        Unit = r.Unit
                    });
                }
            }

            // 提示信息
            StatusMessage = string.Format(_localizationService.GetString("Doe_Guide_StatusMsg_ProjMode", 
                "项目模式: 第 {0} 轮 ({1})"),payload.RoundNumber,payload.RecommendedPhase);
        }

        /// <summary>
        /// 将 DOEDesignMatrix 转为 DataTable（WPF DataGrid 原生支持）
        /// ★ 修复 (v3): 类别因子列用 string 类型显示标签
        /// </summary>
        private DataTable ConvertMatrixToDataTable(DOEDesignMatrix matrix)
        {
            var dt = new DataTable();

            // 添加序号列
            dt.Columns.Add("组号", typeof(int));

            // ★ 修复 (v3): 类别因子列用 string 类型
            var categoricalFactorNames = new HashSet<string>(
                Factors.Where(f => f.IsCategorical).Select(f => f.FactorName));

            foreach (var name in matrix.FactorNames)
            {
                if (categoricalFactorNames.Contains(name))
                    dt.Columns.Add(name, typeof(string));
                else
                    dt.Columns.Add(name, typeof(double));
            }

            // 填充数据
            for (int i = 0; i < matrix.Rows.Count; i++)
            {
                var dr = dt.NewRow();
                dr["组号"] = i + 1;
                foreach (var name in matrix.FactorNames)
                {
                    if (matrix.Rows[i].TryGetValue(name, out var val))
                    {
                        if (categoricalFactorNames.Contains(name))
                            dr[name] = val?.ToString() ?? "";
                        else
                            dr[name] = val is double d ? d : Convert.ToDouble(val);
                    }
                    else
                    {
                        dr[name] = categoricalFactorNames.Contains(name) ? (object)"" : (object)0.0;
                    }
                }
                dt.Rows.Add(dr);
            }

            return dt;
        }
        // ══════════════ Step Navigation ══════════════

        private async Task GoNextStepAsync()
        {
            // 每步切换前做校验
            switch (CurrentStep)
            {
                case 1:
                    if (Factors.Count == 0)
                    {
                        _dialogService.ShowError("请至少添加一个因子", "验证");
                        return;
                    }
                    foreach (var f in Factors)
                    {
                        // ★ 修改: 仅"非手动的连续因子"必须绑定流程参数
                        //   手动操作因子(包括类别因子和勾选了"手动"的连续因子)跳过此校验,
                        //   运行时由操作员手动调节,不参与流程参数注入。
                        if (f.NeedsBinding && f.SelectedCandidate == null)
                        {
                            _dialogService.ShowError(
                                $"连续因子 '{f.FactorName}' 未绑定流程参数。如需手动调节,请勾选「手动操作」",
                                "验证");
                            return;
                        }
                        // ★ 修改: 仅连续因子检查上下界
                        if (f.IsContinuous && f.LowerBound >= f.UpperBound)
                        {
                            _dialogService.ShowError($"因子 '{f.FactorName}' 的下界必须小于上界", "验证");
                            return;
                        }
                        // ★ 新增: 类别因子必须输入至少 2 个水平
                        if (f.IsCategorical)
                        {
                            var levels = (f.CategoryLevels ?? "").Split(',')
                                .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                            if (levels.Count < 2)
                            {
                                _dialogService.ShowError($"类别因子 '{f.FactorName}' 至少需要 2 个水平标签（逗号分隔）", "验证");
                                return;
                            }
                        }
                    }
                    break;

                case 2: // 设计方法 → 生成矩阵
                    //  修改: 连续因子数校验（类别因子不计入 RSM 最低要求）
                    var continuousCount = Factors.Count(f => f.IsContinuous);
                    if (SelectedMethod == DOEDesignMethod.BoxBehnken && continuousCount < 3)
                    {
                        _dialogService.ShowError("Box-Behnken 设计至少需要 3 个连续因子", "验证");
                        return;
                    }
                    if (SelectedMethod == DOEDesignMethod.CCD && continuousCount < 2)
                    {
                        _dialogService.ShowError("CCD 设计至少需要 2 个连续因子", "验证");
                        return;
                    }
                    // ★ 修复 v6: 自定义导入先选文件再导入矩阵
                    if (SelectedMethod == DOEDesignMethod.Custom)
                    {
                        var path = _dialogService.ShowOpenFileDialog("Excel 文件|*.xlsx");
                        if (string.IsNullOrEmpty(path))
                            return;
                        _customImportPath = path;
                    }
                    await GenerateMatrixAsync();
                    if (_designMatrix == null || _designMatrix.IsEmpty)
                    {
                        _dialogService.ShowError("矩阵生成失败，请检查因子配置或导入文件", "验证");
                        return;
                    }
                    break;

                case 4: // 响应变量 → 确认保存
                    if (Responses.Count == 0)
                    {
                        _dialogService.ShowError("请至少添加一个响应变量", "验证");
                        return;
                    }
                    break;
            }

            CurrentStep++;
        }

        private void UpdateStepVisibility()
        {
            RaisePropertyChanged(nameof(IsStep1Visible));
            RaisePropertyChanged(nameof(IsStep2Visible));
            RaisePropertyChanged(nameof(IsStep3Visible));
            RaisePropertyChanged(nameof(IsStep4Visible));
            RaisePropertyChanged(nameof(IsStep5Visible));
            NextStepCommand.RaiseCanExecuteChanged();
            PreviousStepCommand.RaiseCanExecuteChanged();
        }

        // ══════════════ Step1: 因子管理 ══════════════

        private void LoadAvailableCandidates()
        {
            try
            {
                var candidates = _paramProvider.GetFactorCandidates();
                AvailableCandidates = new ObservableCollection<FactorCandidate>(candidates);
                _logger.LogInformation("加载到 {Count} 个因子候选项", candidates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载因子候选列表失败");
            }
        }

        private void AddFactor()
        {
            var factor = new FactorViewModel
            {
                FactorName = $"因子{Factors.Count + 1}",
                LowerBound = 0,
                UpperBound = 100,
                LevelCount = 3,
                AvailableCandidates = new ObservableCollection<FactorCandidate>(AvailableCandidates)
            };
            Factors.Add(factor);
        }



        // ══════════════ Step2+3: 矩阵生成 ══════════════
        private async Task GenerateMatrixAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("Doe_Guide_StatusMsg_CreateMatrix", "正在生成参数矩阵...");
                var factors = Factors.Select(f => f.ToModel()).ToList();

                DesignOptions? options = SelectedMethod switch
                {
                    DOEDesignMethod.FullFactorial => new FullFactorialOptions
                    {
                        LevelCount = FullFactorialLevels,
                        CenterPointCount = FullFactorialCenterCount,
                        Replicates = FullFactorialReplicates,
                        Randomize = RandomizeRunOrder
                    },
                    DOEDesignMethod.FractionalFactorial => new FractionalFactorialOptions
                    {
                        RunCount = FfRunCount > 0 ? FfRunCount : InferFractionalRunCount(factors.Count, FractionalResolution),
                        CenterPointCount = FfCenterCount,
                        Replicates = FfReplicates,
                        Randomize = RandomizeRunOrder
                    },
                    DOEDesignMethod.PlackettBurman => new PlackettBurmanOptions
                    {
                        RunCount = PbRunCount,
                        CenterPointCount = PbCenterCount,
                        Replicates = PbReplicates,
                        Randomize = RandomizeRunOrder
                    },
                    DOEDesignMethod.CCD => new CCDOptions
                    {
                        AlphaType = CcdAlphaType,
                        CubeCenterCount = CcdCubeCenterCount >= 0 ? CcdCubeCenterCount : CcdCenterCount,
                        AxialCenterCount = CcdAxialCenterCount,
                        UseFractional = CcdUseFractional,
                        Replicates = CcdReplicates,
                        Randomize = RandomizeRunOrder
                    },
                    DOEDesignMethod.BoxBehnken => new BoxBehnkenOptions
                    {
                        BbdCenterCount = BbdCenterCount,
                        Replicates = BbdReplicates,
                        Randomize = RandomizeRunOrder
                    },
                    DOEDesignMethod.DOptimal => new DOptimalOptions
                    {
                        RunCount = DOptimalRunCount > 0 ? DOptimalRunCount : InferDOptimalRunCount(factors.Count, DOptimalModelType),
                        ModelType = DOptimalModelType,
                        Restarts = DOptRestarts,
                        MaxIterations = DOptMaxIterations,
                        CandidateGridSize = DOptCandidateGridSize,
                        RandomSeed = DOptRandomSeed == 0 ? (int?)null : DOptRandomSeed,
                        Randomize = RandomizeRunOrder
                    },
                    DOEDesignMethod.Taguchi => new TaguchiOptions
                    {
                        LArray = TaguchiTableType,
                        SnRatio = TaguchiSnRatio,
                        Randomize = RandomizeRunOrder
                    },
                    _ => null
                };

                if (SelectedMethod == DOEDesignMethod.Custom)
                {
                    _designMatrix = !string.IsNullOrEmpty(_customImportPath)
                        ? await _designService.ImportCustomMatrixAsync(_customImportPath, factors)
                        : null;
                }
                else if (options != null)
                {
                    _designMatrix = await _designService.GenerateAsync(factors, options);
                }

                if (_designMatrix != null)
                {
                    MatrixTable = ConvertMatrixToDataTable(_designMatrix);
                    RaisePropertyChanged(nameof(MatrixRunCount));
                    StatusMessage = string.Format(_localizationService.GetString("Doe_Guide_StatusMsg_AlreadyGroup", "已生成 {0} 组实验"),_designMatrix.RunCount);
                    await EvaluateDesignQualityAsync(factors);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成参数矩阵失败");
                _dialogService.ShowError($"生成失败: {ex.Message}", "错误");
            }
            finally
            {
                IsLoading = false;
                RandomizeMatrixCommand.RaiseCanExecuteChanged();
                AddCenterPointsCommand.RaiseCanExecuteChanged();
            }
        }

        // ★ 新增辅助方法 (放在 GenerateMatrixAsync 下面):

        private int InferFractionalRunCount(int k, int desiredResolution)
        {
            // 查 Minitab 标准表:选最小且满足分辨度要求的 runs
            var entries = MaxChemical.Modules.DOE.Services.Designs.FractionalFactorialTables
                .GetFeasibleRuns(k);

            // resolution == 99 表示全因子(Minitab 在表里把全因子标为"全分辨度"),
            // 应作为兜底:只有当不存在任何部分因子方案满足分辨度时,才退回全因子。
            var fractionalOnly = entries
                .Where(e => e.resolution >= desiredResolution && e.resolution < 99)
                .OrderBy(e => e.runs)
                .ToList();

            if (fractionalOnly.Count > 0)
                return fractionalOnly.First().runs;

            // 表里没有满足该分辨度的部分因子方案 → 退回全因子并提示
            var full = entries.FirstOrDefault(e => e.resolution == 99);
            if (full.runs > 0)
            {
                _logger.LogWarning(
                    "k={K} 没有 Res ≥ {Res} 的部分因子方案,自动退回全因子 ({Runs} 组)。" +
                    "建议提示用户调低分辨度或减少因子数。",
                    k, desiredResolution, full.runs);
                return full.runs;
            }

            throw new ArgumentException($"k={k} 无可用部分因子设计");
        }

        private int InferDOptimalRunCount(int k, string modelType)
        {
            int p = 1 + k;
            if (modelType == "interaction" || modelType == "quadratic")
                p += k * (k - 1) / 2;
            if (modelType == "quadratic")
                p += k;
            return p + 4;
        }


        /// <summary>
        ///  新增: 评估设计质量
        /// 在生成矩阵后自动调用，结果展示在 Step3 的设计质量面板
        /// </summary>
        private async Task EvaluateDesignQualityAsync(List<DOEFactor> factors)
        {
            if (_designMatrix == null || _designMatrix.IsEmpty)
            {
                DesignQuality = null;
                return;
            }

            try
            {
                StatusMessage = _localizationService.GetString("Doe_Guide_StatusMsg_DesignQuality", "正在评估设计质量...");

                // 根据设计方法选择合适的模型类型
                string modelType = SelectedMethod switch
                {
                    DOEDesignMethod.CCD or DOEDesignMethod.BoxBehnken or DOEDesignMethod.DOptimal => "quadratic",
                    DOEDesignMethod.FractionalFactorial => "linear",
                    _ => factors.Count <= 4 ? "interaction" : "linear"
                };

                DesignQuality = await _designService.GetDesignQualityAsync(factors, _designMatrix, modelType);

                // 通知 UI 更新
                RaisePropertyChanged(nameof(QualityDEffText));
                RaisePropertyChanged(nameof(QualityAEffText));
                RaisePropertyChanged(nameof(QualityGEffText));
                RaisePropertyChanged(nameof(QualityDofText));
                RaisePropertyChanged(nameof(HasDesignQuality));

                _logger.LogInformation(" 设计质量评估完成: D-eff={DEff}, DF={DF}",
                    DesignQuality.DEfficiency, DesignQuality.DegreesOfFreedom);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设计质量评估失败");
                DesignQuality = null;
            }
            finally
            {
                StatusMessage = _localizationService.GetString("Doe_Guide_StatusMsg_AssessComplete", "设计质量评估完成");
            }
        }
        private void RandomizeMatrix()
        {
            if (_designMatrix == null) return;
            _designMatrix.Randomize();
            MatrixTable = ConvertMatrixToDataTable(_designMatrix);
            IsRandomized = true;
        }

        private async Task AddCenterPointsAsync()
        {
            if (_designMatrix == null || CenterPointCount <= 0) return;

            var factors = Factors.Select(f => f.ToModel()).ToList();
            // ★ 修复 (v3): 类别因子中心值用第一个水平标签，连续因子用 CenterPoint
            var centerValues = new Dictionary<string, object>();
            foreach (var f in factors)
            {
                if (f.IsCategorical)
                {
                    var levels = f.GetCategoryLevelList();
                    centerValues[f.FactorName] = levels.Count > 0 ? levels[0] : "";
                }
                else
                {
                    centerValues[f.FactorName] = f.CenterPoint;
                }
            }

            for (int i = 0; i < CenterPointCount; i++)
                _designMatrix.AddCenterPoint(centerValues);

            MatrixTable = ConvertMatrixToDataTable(_designMatrix);
            RaisePropertyChanged(nameof(MatrixRunCount));
        }

        // ══════════════ Step4: 响应 + 停止条件 ══════════════

        private void AddResponse()
        {
            Responses.Add(new ResponseViewModel { ResponseName = $"响应{Responses.Count + 1}", Unit = "%" });
        }

        private void AddStopCondition()
        {
            StopConditions.Add(new StopConditionViewModel
            {
                ConditionType = DOEStopConditionType.Threshold,
                ResponseName = Responses.FirstOrDefault()?.ResponseName ?? "",
                Operator = "GreaterThanOrEqual",
                TargetValue = 95
            });
        }

        // ══════════════ Step5: 历史数据导入 ══════════════

        private void BrowseImportFile()
        {
            var path = _dialogService.ShowOpenFileDialog("Excel 文件|*.xlsx");
            if (!string.IsNullOrEmpty(path))
            {
                ImportFilePath = path;
                ValidateImportCommand.RaiseCanExecuteChanged();
            }
        }

        private async Task ValidateImportAsync()
        {
            if (string.IsNullOrEmpty(ImportFilePath)) return;

            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("Doe_Guide_StatusMsg_ValidFile", "正在校验导入文件...");

                var factors = Factors.Select(f => f.ToModel()).ToList();
                var responses = Responses.Select(r => r.ToModel()).ToList();

                ValidationResult = await _designService.ValidateImportedDataAsync(ImportFilePath, factors, responses);

                if (ValidationResult.IsValid)
                    StatusMessage = string.Format(_localizationService.GetString("Doe_Guide_StatusMsg_ValidEnter",
                        "校验通过，{0} 行有效数据"),ValidationResult.ValidRowCount);
                else
                    StatusMessage = string.Format(_localizationService.GetString("Doe_Guide_StatusMsg_VaildFail",
                        "校验失败: {0}"),string.Join("; ", ValidationResult.Errors));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "校验导入文件失败");
                _dialogService.ShowError($"校验失败: {ex.Message}", "错误");
            }
            finally
            {
                IsLoading = false;
            }
        }

        // ══════════════ Step6: 保存批次 ══════════════

        private async Task SaveBatchAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("Doe_Guide_StatusMsg_SavePlan", "正在保存 DOE 方案...");

                if (string.IsNullOrWhiteSpace(BatchName))
                    BatchName = $"DOE_{DateTime.Now:yyyyMMdd_HHmmss}";

                // 创建批次
                var batch = new DOEBatch
                {
                    FlowId = _paramProvider.GetCurrentFlowId() ?? "unknown",
                    FlowName = _paramProvider.GetCurrentFlowName() ?? "未命名流程",
                    BatchName = BatchName,
                    DesignMethod = SelectedMethod,
                    Status = DOEBatchStatus.Ready,
                    DesignConfigJson = JsonConvert.SerializeObject(new
                    {
                        method = SelectedMethod.ToString(),
                        taguchiTable = TaguchiTableType,
                        fractionalResolution = FractionalResolution,
                        isRandomized = IsRandomized,
                        centerPointCount = CenterPointCount,
                        //  新增: RSM 参数
                        ccdAlphaType = CcdAlphaType,
                        ccdCenterCount = CcdCenterCount,
                        bbdCenterCount = BbdCenterCount,
                        dOptimalRunCount = DOptimalRunCount,
                        dOptimalModelType = DOptimalModelType,
                        //  新增: 设计质量快照
                        designQuality = DesignQuality != null ? new
                        {
                            dEfficiency = DesignQuality.DEfficiency,
                            aEfficiency = DesignQuality.AEfficiency,
                            gEfficiency = DesignQuality.GEfficiency,
                            degreesOfFreedom = DesignQuality.DegreesOfFreedom
                        } : null,
                        // ★ 新增: 持久化 OLS 模型类型（分析页读取用）
                        olsModelType = GetOlsModelTypeForBatch(),
                        fullFactorialLevels = FullFactorialLevels,
                        fullFactorialCenterCount = FullFactorialCenterCount,
                        pbRunCount = PbRunCount,
                        pbCenterCount = PbCenterCount,
                        pbReplicates = PbReplicates,
                        ffRunCount = FfRunCount,
                        ffCenterCount = FfCenterCount,
                        ffReplicates = FfReplicates,
                        fullFactorialReplicates = FullFactorialReplicates,
                        ccdCubeCenterCount = CcdCubeCenterCount,
                        ccdAxialCenterCount = CcdAxialCenterCount,
                        ccdReplicates = CcdReplicates,
                        ccdUseFractional = CcdUseFractional,
                        bbdReplicates = BbdReplicates,
                        dOptRestarts = DOptRestarts,
                        dOptMaxIterations = DOptMaxIterations,
                        dOptRandomSeed = DOptRandomSeed,
                        dOptCandidateGridSize = DOptCandidateGridSize,
                        taguchiSnRatio = TaguchiSnRatio,
                        randomizeRunOrder = RandomizeRunOrder,
                    }),
                    // ★ 新增: 项目关联（_currentProjectId 为 null 时行为与旧版一致）
                    ProjectId = _currentProjectId,
                    RoundNumber = _currentRoundNumber,
                    ProjectPhase = _currentProjectPhase
                };

                var batchId = await _repository.CreateBatchAsync(batch);

                // 保存因子
                var factors = Factors.Select(f => f.ToModel()).ToList();
                await _repository.SaveFactorsAsync(batchId, factors);

                // 保存响应变量
                var responses = Responses.Select(r => r.ToModel()).ToList();
                await _repository.SaveResponsesAsync(batchId, responses);

                // 保存停止条件
                var stopConditions = StopConditions.Select(c => c.ToModel()).ToList();
                await _repository.SaveStopConditionsAsync(batchId, stopConditions);
                //  新增: 保存 Desirability 配置
                var desirabilityConfigs = Responses.Select(r => r.ToDesirabilityConfig()).ToList();
                var configJson = JsonConvert.SerializeObject(desirabilityConfigs);
                await _repository.SaveDesirabilityConfigAsync(batchId, configJson);
                _logger.LogInformation("Desirability 配置已保存: {Count} 个响应", desirabilityConfigs.Count);
                // 保存参数矩阵为 DOE runs (Pending 状态)
                if (_designMatrix != null)
                {
                    var runs = new List<DOERunRecord>();
                    for (int i = 0; i < _designMatrix.RunCount; i++)
                    {
                        runs.Add(new DOERunRecord
                        {
                            BatchId = batchId,
                            RunIndex = i,
                            FactorValuesJson = JsonConvert.SerializeObject(_designMatrix.Rows[i]),
                            DataSource = DOEDataSource.Measured,
                            Status = DOERunStatus.Pending
                        });
                    }
                    await _repository.SaveRunsAsync(runs);
                }

                // 导入历史数据（如果有）
                if (ValidationResult?.IsValid == true && !string.IsNullOrEmpty(ImportFilePath))
                {
                    var importedRuns = await _designService.ImportHistoricalDataAsync(
                        ImportFilePath, batchId, factors, responses);
                    await _repository.SaveRunsAsync(importedRuns);
                    ImportedDataCount = importedRuns.Count;
                }

                // ════════════════════════════════════════════════════════
                //  新增: 创建并持久化 GPR 模型（错误不阻断方案保存）
                // ════════════════════════════════════════════════════════
                await InitializeGPRModelAsync(batch.FlowId, batchId, factors, responses);

                // ★ 新增: 项目模式下更新项目信息 + 同步因子池
                if (!string.IsNullOrEmpty(_currentProjectId))
                {
                    try
                    {
                        var project = await _repository.GetProjectAsync(_currentProjectId);
                        if (project != null)
                        {
                            if (_currentProjectPhase.HasValue)
                                project.CurrentPhase = _currentProjectPhase.Value;

                            // ★ 首轮时关联 FlowId
                            if (string.IsNullOrEmpty(project.FlowId))
                            {
                                project.FlowId = batch.FlowId;
                                project.FlowName = batch.FlowName;
                            }

                            await _repository.UpdateProjectAsync(project);

                            // ★ 首轮时同步因子到项目因子池
                            var existingFactors = await _repository.GetProjectFactorsAsync(_currentProjectId);
                            if (existingFactors.Count == 0)
                            {
                                var projectFactors = factors.Select((f, idx) => new DOEProjectFactor
                                {
                                    ProjectId = _currentProjectId,
                                    FactorName = f.FactorName,
                                    FactorType = f.FactorType,
                                    FactorStatus = ProjectFactorStatus.Active,
                                    CurrentLowerBound = f.LowerBound,
                                    CurrentUpperBound = f.UpperBound,
                                    CategoryLevels = f.CategoryLevels,
                                    SourceNodeId = f.SourceNodeId,
                                    SourceParamName = f.SourceParamName,
                                    BoundsHistoryJson = JsonConvert.SerializeObject(new[]
                                    {
                                        new { batch_id = batchId, lower = f.LowerBound, upper = f.UpperBound,
                                              reason = "初始范围", timestamp = DateTime.Now }
                                    }),
                                    SortOrder = idx
                                }).ToList();

                                await _repository.SaveProjectFactorsAsync(_currentProjectId, projectFactors);
                                _logger.LogInformation("项目因子池已初始化: {Count} 个因子", projectFactors.Count);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "更新项目信息失败（不影响方案保存）");
                    }
                }

                StatusMessage = string.Format(_localizationService.GetString("Doe_Guide_StatusMsg_AlreadySave", "DOE 方案已保存: {0}"),batchId);
                _logger.LogInformation("DOE 方案保存成功: {BatchId} - {BatchName}", batchId, BatchName);

                BatchSaved?.Invoke(this, batchId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存 DOE 方案失败");
                _dialogService.ShowError($"保存失败: {ex.Message}", "错误");
            }
            finally
            {
                IsLoading = false;
            }
        }
        /// <summary>
        /// 根据设计方法自动推断 OLS 模型类型
        /// 持久化到 DesignConfigJson.olsModelType，分析时直接读取
        /// </summary>
        private string GetOlsModelTypeForBatch()
        {
            return SelectedMethod switch
            {
                // 筛选类 → 一阶主效应
                DOEDesignMethod.PlackettBurman => "linear",
                DOEDesignMethod.FractionalFactorial => FractionalResolution >= 4 ? "interaction" : "linear",

                // 因子类 → 按因子数
                DOEDesignMethod.FullFactorial => FullFactorialLevels == 2
                      ? (Factors.Count <= 4 ? "interaction" : "linear")
                      : "quadratic",  // 3水平可拟合二次项

                // 响应曲面类 → 二阶
                DOEDesignMethod.CCD => "quadratic",
                DOEDesignMethod.BoxBehnken => "quadratic",

                // D-Optimal → 用户选择
                DOEDesignMethod.DOptimal => DOptimalModelType,

                // 自定义 → 用户在 Step2 选的分析类型
                DOEDesignMethod.Custom => CustomAnalysisType,

                // 兜底
                _ => "quadratic"
            };
        }

        /// <summary>
        ///  新增: 在创建 DOE 方案时初始化 GPR 模型
        /// 
        /// 执行流程：
        /// 1. InitializeModelAsync — 按 FlowId + FactorSignature 查已有模型或新建
        /// 2. 如果有导入的历史数据 → AppendData 逐条喂给模型
        /// 3. SaveInitialStateAsync — 持久化（有数据时自动训练）
        /// 
        /// 错误隔离: GPR 任何步骤失败都不影响方案保存
        /// </summary>
        private async Task InitializeGPRModelAsync(
              string flowId,
              string batchId,
              List<DOEFactor> factors,
              List<DOEResponse> responses)
        {
            try
            {
                StatusMessage = _localizationService.GetString("Doe_Guide_StatusMsg_ForecastModel", "正在初始化 GPR 预测模型...");
                // ★ 新增：传入项目 ID
                _gprService.SetProjectId(_currentProjectId);
                // Step 1: 初始化主响应模型（新建或恢复已有同签名模型）
                await _gprService.InitializeModelAsync(flowId, factors);

                //  新增 Step 1.5: 初始化多响应模型（为每个响应创建独立的 GPR）
                try
                {
                    await _multiGprService.InitializeAsync(flowId, factors, responses, _currentProjectId);
                    _logger.LogInformation("多响应 GPR 初始化成功: {Count} 个响应", responses.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "多响应 GPR 初始化失败（不影响主模型）");
                }

                // Step 2: 如果有已导入的历史数据，喂给所有模型
                if (ImportedDataCount > 0)
                {
                    var primaryResponseName = responses.FirstOrDefault()?.ResponseName;
                    if (!string.IsNullOrEmpty(primaryResponseName))
                    {
                        var completedRuns = await _repository.GetCompletedRunsAsync(batchId);
                        int fedCount = 0;

                        foreach (var run in completedRuns)
                        {
                            try
                            {
                                // ★ 修复 (v3): 用 Dict<string, object> 反序列化，保留类别因子标签
                                var factorValues = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                                    run.FactorValuesJson);
                                if (factorValues == null) continue;

                                var responseValues = !string.IsNullOrEmpty(run.ResponseValuesJson)
                                    ? JsonConvert.DeserializeObject<Dictionary<string, double>>(run.ResponseValuesJson)
                                    : null;
                                if (responseValues == null) continue;

                                //  修改: 喂给所有响应模型（原来只喂主响应）
                                _multiGprService.AppendAllResponses(
                                    factorValues, responseValues,
                                    "imported", "", "");

                                fedCount++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "GPR 导入数据第 {RunIndex} 组失败，跳过", run.RunIndex);
                            }
                        }

                        _logger.LogInformation("GPR 历史数据导入: {FedCount}/{ImportedCount} 组",
                            fedCount, ImportedDataCount);
                    }
                }

                // Step 3: 持久化主模型
                await _gprService.SaveInitialStateAsync(flowId);

                //  新增 Step 3.5: 持久化次要模型
                try
                {
                    await _multiGprService.SaveAllAsync(flowId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "次要 GPR 模型保存失败（不影响方案保存）");
                }

                _logger.LogInformation("GPR 模型已随方案创建: FlowId={FlowId}, DataCount={Count}",
                    flowId, _gprService.DataCount);
            }
            catch (Exception ex)
            {
                //  关键: GPR 失败不阻断方案保存
                _logger.LogError(ex, "GPR 模型初始化失败（不影响 DOE 方案保存）");
            }
        }
    }

    // ══════════════ 子 ViewModel ══════════════

    public class FactorViewModel : BindableBase
    {
        private string _factorName = "";
        private FactorCandidate? _selectedCandidate;
        private double _lowerBound;
        private double _upperBound;
        private int _levelCount = 3;
        private DOEFactorType _factorType = DOEFactorType.Continuous;
        private string _categoryLevels = "";
        private bool _isManual;   // ★ 新增: 手动操作开关
        /// <summary>
        /// 用户自定义的因子名称（如 "反应温度"）
        /// </summary>
        public string FactorName { get => _factorName; set => SetProperty(ref _factorName, value); }

        /// <summary>
        /// ★ 新增: 因子类型（连续/类别）
        /// </summary>
        public DOEFactorType FactorType
        {
            get => _factorType;
            set
            {
                if (SetProperty(ref _factorType, value))
                {
                    RaisePropertyChanged(nameof(IsContinuous));
                    RaisePropertyChanged(nameof(IsCategorical));
                    RaisePropertyChanged(nameof(StepSize));
                }
            }
        }

        /// <summary>★ 新增: 是否连续因子（UI 绑定用）</summary>
        public bool IsContinuous => FactorType == DOEFactorType.Continuous;

        /// <summary>★ 新增: 是否类别因子（UI 绑定用）</summary>
        public bool IsCategorical => FactorType == DOEFactorType.Categorical;
        /// <summary>
        /// ★ 新增: 是否为"手动操作"因子(不绑定流程参数,运行时由操作员手动调节)
        /// 类别因子默认就是手动操作,无法切换;连续因子可由用户勾选。
        /// </summary>
        public bool IsManual
        {
            get => IsCategorical || _isManual;   // 类别因子始终视为手动
            set
            {
                // 类别因子不允许切换
                if (IsCategorical) return;
                if (SetProperty(ref _isManual, value))
                {
                    RaisePropertyChanged(nameof(NeedsBinding));
                    RaisePropertyChanged(nameof(IsBound));
                    // 切到手动时清空已选的候选项,避免下一步把脏数据写进 ToModel
                    if (value && _selectedCandidate != null)
                    {
                        _selectedCandidate = null;
                        RaisePropertyChanged(nameof(SelectedCandidate));
                        RaisePropertyChanged(nameof(BindingDisplayText));
                    }
                }
            }
        }

        /// <summary>
        /// ★ 新增: 是否需要绑定流程参数(连续 + 非手动)
        /// UI 用此属性控制 ComboBox 可见性,Step1 校验也用此属性。
        /// </summary>
        public bool NeedsBinding => IsContinuous && !_isManual;
        /// <summary>
        /// ★ 新增: 类别因子的水平标签（逗号分隔，如 "催化剂A,催化剂B,催化剂C"）
        /// </summary>
        public string CategoryLevels
        {
            get => _categoryLevels;
            set
            {
                if (SetProperty(ref _categoryLevels, value))
                {
                    // 自动更新 LevelCount
                    var levels = (value ?? "").Split(',')
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                    LevelCount = Math.Max(levels.Count, 1);
                }
            }
        }

        /// <summary>★ 新增: 因子类型选项（供 ComboBox 绑定）</summary>
        public static List<DOEFactorType> FactorTypeOptions => new()
        {
            DOEFactorType.Continuous,
            DOEFactorType.Categorical
        };

        /// <summary>
        /// 绑定的流程参数候选项（下拉选择）
        /// </summary>
        public FactorCandidate? SelectedCandidate
        {
            get => _selectedCandidate;
            set
            {
                if (SetProperty(ref _selectedCandidate, value) && value != null)
                {
                    // 自动填充默认值
                    if (string.IsNullOrEmpty(FactorName) || FactorName.StartsWith("因子"))
                    {
                        // 取参数名作为因子名
                        FactorName = value.ParameterName ?? value.DisplayName;
                    }

                    // 根据当前值自动设置上下界（仅连续因子）
                    if (IsContinuous && value.CurrentValue != null && double.TryParse(value.CurrentValue.ToString(), out var v) && v != 0)
                    {
                        if (LowerBound == 0 && UpperBound == 100) // 只在默认值时自动填充
                        {
                            LowerBound = Math.Round(v * 0.5, 2);
                            UpperBound = Math.Round(v * 1.5, 2);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 绑定参数的显示文本
        /// </summary>
        public string BindingDisplayText => SelectedCandidate != null
            ? SelectedCandidate.DisplayName
            : "请选择绑定参数...";

  
        /// <summary>
        /// 是否已绑定(手动操作的因子永远不绑定)
        /// </summary>
        public bool IsBound => !IsManual && SelectedCandidate != null;

        public double LowerBound { get => _lowerBound; set { SetProperty(ref _lowerBound, value); RaisePropertyChanged(nameof(StepSize)); } }
        public double UpperBound { get => _upperBound; set { SetProperty(ref _upperBound, value); RaisePropertyChanged(nameof(StepSize)); } }
        public int LevelCount { get => _levelCount; set { SetProperty(ref _levelCount, value); RaisePropertyChanged(nameof(StepSize)); } }
        public double StepSize => IsContinuous && LevelCount > 1 ? Math.Round((UpperBound - LowerBound) / (LevelCount - 1), 4) : 0;

        /// <summary>
        /// 可选的候选参数列表（绑定到 ComboBox 的 ItemsSource）
        /// </summary>
        public ObservableCollection<FactorCandidate> AvailableCandidates { get; set; } = new();

        public DOEFactor ToModel() => new()
        {
            FactorName = FactorName,
            FactorSource = SelectedCandidate?.SourceType ?? FactorSourceType.ParameterOverride,
            SourceNodeId = SelectedCandidate?.NodeId,
            SourceParamName = SelectedCandidate?.ParameterName,
            LowerBound = LowerBound,
            UpperBound = UpperBound,
            LevelCount = LevelCount,
            FactorType = FactorType,
            CategoryLevels = IsCategorical ? CategoryLevels : null
        };
    }

    public class ResponseViewModel : BindableBase
    {
        private string _responseName = "";
        private string _unit = "";
        //  新增: Desirability 配置（Step4 中设置）
        private DesirabilityGoal _goal = DesirabilityGoal.Maximize;
        private double _desirabilityLower = 0;
        private double _desirabilityUpper = 100;
        private double _desirabilityTarget = 100;
        private double _desirabilityWeight = 1.0;
        private int _importance = 3;
        public string ResponseName { get => _responseName; set => SetProperty(ref _responseName, value); }
        public string Unit { get => _unit; set => SetProperty(ref _unit, value); }
        //  新增: Desirability 属性
        public DesirabilityGoal Goal { get => _goal; set => SetProperty(ref _goal, value); }
        public double DesirabilityLower { get => _desirabilityLower; set => SetProperty(ref _desirabilityLower, value); }
        public double DesirabilityUpper { get => _desirabilityUpper; set => SetProperty(ref _desirabilityUpper, value); }
        public double DesirabilityTarget { get => _desirabilityTarget; set => SetProperty(ref _desirabilityTarget, value); }
        public double DesirabilityWeight { get => _desirabilityWeight; set => SetProperty(ref _desirabilityWeight, value); }
        public int Importance { get => _importance; set => SetProperty(ref _importance, value); }
        /// <summary>
        /// 目标类型选项（供 ComboBox 绑定）
        /// </summary>
        public static List<DesirabilityGoal> GoalOptions => new()
        {
            DesirabilityGoal.Maximize,
            DesirabilityGoal.Minimize,
            DesirabilityGoal.Target
        };
        public DOEResponse ToModel() => new()
        {
            ResponseName = ResponseName,
            Unit = Unit,
            CollectionMethod = DOECollectionMethod.Manual
        };
        /// <summary>
        ///  新增: 转为 Desirability 配置
        /// </summary>
        public DesirabilityResponseConfig ToDesirabilityConfig() => new()
        {
            ResponseName = ResponseName,
            Goal = Goal,
            Lower = DesirabilityLower,
            Upper = DesirabilityUpper,
            Target = DesirabilityTarget,
            Weight = DesirabilityWeight,
            Importance = Importance
        };
    }

    public class StopConditionViewModel : BindableBase
    {
        private DOEStopConditionType _conditionType = DOEStopConditionType.Threshold;
        private string _responseName = "";
        private string _operator = "GreaterThanOrEqual";
        private double _targetValue = 95;

        public DOEStopConditionType ConditionType { get => _conditionType; set => SetProperty(ref _conditionType, value); }
        public string ResponseName { get => _responseName; set => SetProperty(ref _responseName, value); }
        public string Operator { get => _operator; set => SetProperty(ref _operator, value); }
        public double TargetValue { get => _targetValue; set => SetProperty(ref _targetValue, value); }

        public static List<string> OperatorOptions => new()
        {
            "Equals", "NotEquals", "GreaterThan", "GreaterThanOrEqual", "LessThan", "LessThanOrEqual"
        };

        public DOEStopCondition ToModel() => new()
        {
            ConditionType = ConditionType,
            ResponseName = ResponseName,
            Operator = Operator,
            TargetValue = TargetValue
        };
    }
}