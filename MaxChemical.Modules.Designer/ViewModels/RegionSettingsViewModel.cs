using MaxChemical.Core;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Models;
using Prism.Events;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
namespace MaxChemical.Modules.Designer.ViewModels
{
    namespace MaxChemical.Modules.Designer.ViewModels
    {
        public class RegionSettingsViewModel : INotifyPropertyChanged
        {
            private readonly IEventAggregator _eventAggregator;
            private readonly ILocalizationService _localizationService;

            #region Properties

            private int _rows = 2;
            public int Rows
            {
                get => _rows;
                set
                {
                    if (SetProperty(ref _rows, value))
                    {
                        if (value > 0 && value <= 10)
                        {
                            GenerateGridCommand?.Execute(null);
                        }
                    }
                }
            }

            private int _cols = 2;
            public int Cols
            {
                get => _cols;
                set
                {
                    if (SetProperty(ref _cols, value))
                    {
                        if (value > 0 && value <= 10)
                        {
                            GenerateGridCommand?.Execute(null);
                        }
                    }
                }
            }

            private ObservableCollection<RegionCellViewModel> _gridCells;
            public ObservableCollection<RegionCellViewModel> GridCells
            {
                get => _gridCells;
                set => SetProperty(ref _gridCells, value);
            }

            public RegionLayout Result { get; private set; }

            // 获取画布尺寸的委托
            public Func<(double Width, double Height)> GetCanvasSize { get; set; }

            #endregion

            #region Commands

            public ICommand GenerateGridCommand { get; }
            public ICommand ConfirmCommand { get; }
            public ICommand CancelCommand { get; }

            #endregion

            #region Events

            public event Action<bool> DialogResult;
            public event PropertyChangedEventHandler PropertyChanged;

            #endregion

            #region Constructor

            public RegionSettingsViewModel(IEventAggregator eventAggregator, ILocalizationService localization, RegionLayout originalLayout = null)
            {
                // 新增：本地化
                _eventAggregator = eventAggregator;
                _localizationService = localization;

                _eventAggregator.GetEvent<LanguageChangedEvent>().Subscribe(LanguageChangedHandler);

                // 初始化命令
                GenerateGridCommand = new RelayCommand(GenerateGrid);
                ConfirmCommand = new RelayCommand(Confirm);
                CancelCommand = new RelayCommand(Cancel);

                // 如果有原始布局，使用其值并恢复已设置的区域
                if (originalLayout != null)
                {
                    _rows = originalLayout.Rows;
                    _cols = originalLayout.Cols;
                    GenerateGrid();
                    RestoreExistingLayout(originalLayout);
                }
                else
                {
                    // 新建时使用默认值
                    GenerateGrid();
                }


                LanguageChangedHandler("");
            }
            /// <summary>
            /// 恢复已存在的区域布局
            /// </summary>
            private void RestoreExistingLayout(RegionLayout layout)
            {
                if (layout.UseIndependentRegions && layout.IndependentRegions?.Any() == true)
                {
                    // 处理独立区域布局
                    RestoreFromIndependentRegions(layout);
                }
                else if (layout.Cells?.Any() == true)
                {
                    // 处理传统网格布局
                    RestoreFromGridCells(layout);
                }
            }

            /// <summary>
            /// 从独立区域恢复
            /// </summary>
            private void RestoreFromIndependentRegions(RegionLayout layout)
            {
                // 获取画布尺寸
                var (canvasWidth, canvasHeight) = GetCanvasSize?.Invoke() ?? (1140.0, 650.0);

                double cellWidth = canvasWidth / Cols;
                double cellHeight = canvasHeight / Rows;

                foreach (var region in layout.IndependentRegions)
                {
                    // 计算该独立区域覆盖的网格单元格
                    int startCol = (int)(region.Left / cellWidth);
                    int endCol = (int)(region.Right / cellWidth);
                    int startRow = (int)(region.Top / cellHeight);
                    int endRow = (int)(region.Bottom / cellHeight);

                    // 为覆盖的每个单元格设置区域类型
                    for (int row = startRow; row <= endRow && row < Rows; row++)
                    {
                        for (int col = startCol; col <= endCol && col < Cols; col++)
                        {
                            var cell = GridCells.FirstOrDefault(c => c.Row == row && c.Col == col);
                            if (cell != null)
                            {
                                SetCellRegionType(cell, region.Type);
                            }
                        }
                    }
                }
            }

            /// <summary>
            /// 从网格单元格恢复
            /// </summary>
            private void RestoreFromGridCells(RegionLayout layout)
            {
                foreach (var layoutCell in layout.Cells)
                {
                    var cell = GridCells.FirstOrDefault(c => c.Row == layoutCell.Row && c.Col == layoutCell.Col);
                    if (cell != null)
                    {
                        SetCellRegionType(cell, layoutCell.Type);
                    }
                }
            }

            /// <summary>
            /// 设置单元格区域类型
            /// </summary>
            private void SetCellRegionType(RegionCellViewModel cell, RegionType regionType)
            {
                var config = GetRegionTypeConfig(regionType);
                cell.RegionType = regionType;
                cell.DisplayName = config.DisplayName;
                cell.Background = config.Brush;
            }

            /// <summary>
            /// 获取区域类型配置
            /// </summary>
            public static RegionTypeConfig GetRegionTypeConfig(RegionType regionType)
            {
                return regionType switch
                {
                    RegionType.Feed => new RegionTypeConfig
                    {
                        DisplayName = "进料",
                        Color = Color.FromRgb(255, 182, 193),
                        Description = "原料输入和准备区域"
                    },
                    RegionType.PreHeat => new RegionTypeConfig
                    {
                        DisplayName = "预热",
                        Color = Color.FromRgb(255, 218, 185),
                        Description = "反应前的预加热区域"
                    },
                    RegionType.Reaction => new RegionTypeConfig
                    {
                        DisplayName = "反应",
                        Color = Color.FromRgb(255, 228, 196),
                        Description = "主要化学反应区域"
                    },
                    RegionType.Quench => new RegionTypeConfig
                    {
                        DisplayName = "淬火",
                        Color = Color.FromRgb(173, 216, 230),
                        Description = "快速冷却处理区域"
                    },
                    RegionType.PostProcess => new RegionTypeConfig
                    {
                        DisplayName = "后处理",
                        Color = Color.FromRgb(240, 255, 240),
                        Description = "产物分离和纯化区域"
                    },
                    RegionType.None => new RegionTypeConfig
                    {
                        DisplayName = "空",
                        Color = Colors.White,
                        Description = "未分配区域"
                    },
                    _ => new RegionTypeConfig
                    {
                        DisplayName = "未知",
                        Color = Colors.Gray,
                        Description = "未定义的区域类型"
                    }
                };
            }
            #endregion

            #region Private Methods

            private void GenerateGrid()
            {
                // 保存当前已设置的区域信息
                Dictionary<(int, int), CellState> existingCells = null;

                if (GridCells != null)
                {
                    existingCells = GridCells.ToDictionary(
                        c => (c.Row, c.Col),
                        c => new CellState
                        {
                            RegionType = c.RegionType,
                            DisplayName = c.DisplayName,
                            Background = c.Background
                        });
                }

                var cells = new ObservableCollection<RegionCellViewModel>();

                for (int row = 0; row < Rows; row++)
                {
                    for (int col = 0; col < Cols; col++)
                    {
                        var cellViewModel = new RegionCellViewModel(this)
                        {
                            Row = row,
                            Col = col
                        };

                        // 如果之前有设置，恢复设置
                        if (existingCells != null && existingCells.TryGetValue((row, col), out var existingCell))
                        {
                            cellViewModel.RegionType = existingCell.RegionType;
                            cellViewModel.DisplayName = existingCell.DisplayName;
                            cellViewModel.Background = existingCell.Background;
                        }
                        else
                        {
                            // 新单元格使用默认值
                            cellViewModel.RegionType = RegionType.None;
                            cellViewModel.DisplayName = "空";
                            cellViewModel.Background = Brushes.White;
                        }

                        cells.Add(cellViewModel);
                    }
                }

                GridCells = cells;
                OnPropertyChanged(nameof(GridCells));
            }

            /// <summary>
            /// 单元格状态类，用于保存和恢复单元格状态
            /// </summary>
            private class CellState
            {
                public RegionType RegionType { get; set; }
                public string DisplayName { get; set; }
                public Brush Background { get; set; }
            }

            private void Confirm()
            {
                try
                {
                    // 获取画布尺寸
                    var (canvasWidth, canvasHeight) = GetCanvasSize?.Invoke() ?? (1200.0, 800.0);

                    // 创建独立区域布局
                    var layout = CreateIndependentRegionLayout(canvasWidth, canvasHeight);

                    Result = layout;
                    DialogResult?.Invoke(true);
                }
                catch (Exception ex)
                {
                    // 这里可以添加错误处理逻辑
                    System.Windows.MessageBox.Show($"创建区域布局失败: {ex.Message}", "错误",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }

            private void Cancel()
            {
                DialogResult?.Invoke(false);
            }

            private RegionLayout CreateIndependentRegionLayout(double canvasWidth, double canvasHeight)
            {
                var layout = new RegionLayout
                {
                    Rows = Rows,
                    Cols = Cols,
                    UseIndependentRegions = true,
                    IndependentRegions = new List<IndependentRegion>()
                };

                // 计算每个单元格的尺寸
                double cellWidth = canvasWidth / Cols;
                double cellHeight = canvasHeight / Rows;

                // 创建独立区域
                foreach (var cell in GridCells)
                {
                    if (cell.RegionType != RegionType.None)
                    {
                        double left = cell.Col * cellWidth;
                        double top = cell.Row * cellHeight;
                        double right = left + cellWidth;
                        double bottom = top + cellHeight;

                        var region = new IndependentRegion
                        {
                            Type = cell.RegionType,
                            Left = left,
                            Top = top,
                            Right = right,
                            Bottom = bottom
                        };

                        layout.IndependentRegions.Add(region);
                    }

                    // 保留兼容性：同时创建传统的Cell数据
                    layout.Cells.Add(new RegionCell
                    {
                        Row = cell.Row,
                        Col = cell.Col,
                        Type = cell.RegionType
                    });
                }

                // 自动合并相邻的同类型区域
                layout.AutoMergeIndependentRegions();

                return layout;
            }

            #endregion

            #region INotifyPropertyChanged Implementation

            protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
            {
                if (Equals(storage, value)) return false;
                storage = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

            #endregion

            #region Localization

            private string _regionSettingTitle;
            private string _rowLabel;
            private string _colLabel;
            private string _generateLayout;
            private string _tip;
            private string _okLabel;
            private string _cancelLabel;

            public string RegionSettingTitle
            {
                get { return _regionSettingTitle; }
                set { SetProperty(ref _regionSettingTitle, value); }
            }

            public string RowLabel
            {
                get { return _rowLabel; }
                set { SetProperty(ref _rowLabel, value); }
            }

            public string ColLabel
            {
                get { return _colLabel; }
                set { SetProperty(ref _colLabel, value); }
            }

            public string GenerateLayout
            {
                get { return _generateLayout; }
                set { SetProperty(ref _generateLayout, value); }
            }

            public string TipMessage
            {
                get { return _tip; }
                set { SetProperty(ref _tip, value); }
            }

            public string OkLabel
            {
                get { return _okLabel; }
                set { SetProperty(ref _okLabel, value); }
            }

            public string CancelLabel
            {
                get { return _cancelLabel; }
                set { SetProperty(ref _cancelLabel, value); }
            }


            private void LanguageChangedHandler(string cultureName)
            {
                RegionSettingTitle = _localizationService.GetString("RegionSetting_Title", "工艺区划分");
                RowLabel = _localizationService.GetString("RegionSetting_Row", "行");
                ColLabel = _localizationService.GetString("RegionSetting_Col", "列");
                GenerateLayout = _localizationService.GetString("RegionSetting_Generate", "生成布局");
                TipMessage = _localizationService.GetString("RegionSetting_Tip", "右键点击网格单元格选择区域类型");
                OkLabel = _localizationService.GetString("RegionSetting_OK", "确定");
                CancelLabel = _localizationService.GetString("RegionSetting_Cancel", "取消");

                //
                foreach (var cell in GridCells)
                {
                    if (cell == null) continue;

                    cell.DisplayName = _localizationService.GetString($"RegionSetting_RegionType_{cell.RegionType}");

                    // 右键菜单
                    var items = cell.ContextMenuItems.ToList();
                    if (items?.Any() ?? false)
                    {
                        foreach (var item in items)
                        {
                            item.Header = _localizationService.GetString($"RegionSetting_RegionType_{item.RegionTypeValue}");
                        }
                        cell.ContextMenuItems.Clear();
                        cell.ContextMenuItems = new ObservableCollection<RegionTypeMenuItem>(items);
                    }
                }
            }


            public string SetRegionTypeLocalizationText(RegionType type,string txt)
            {
                return _localizationService.GetString($"RegionSetting_RegionType_{type}", txt);
            }
            #endregion
        }

        #region Helper Classes

        public class RegionCellViewModel : INotifyPropertyChanged
        {
            private readonly RegionSettingsViewModel _parentViewModel;
            private RegionType _regionType;
            private string _displayName;
            private Brush _background;
            private ObservableCollection<RegionTypeMenuItem> _contextMenuItems;

            public RegionCellViewModel(RegionSettingsViewModel parentViewModel)
            {
                _parentViewModel = parentViewModel;
                SetRegionTypeCommand = new RelayCommand<string>(OnSetRegionType);
                InitializeContextMenu();
            }

            public int Row { get; set; }
            public int Col { get; set; }

            public RegionType RegionType
            {
                get => _regionType;
                set => SetProperty(ref _regionType, value);
            }

            public string DisplayName
            {
                get => _displayName;
                set => SetProperty(ref _displayName, value);
            }

            public Brush Background
            {
                get => _background;
                set => SetProperty(ref _background, value);
            }

            public ObservableCollection<RegionTypeMenuItem> ContextMenuItems
            {
                get => _contextMenuItems;
                set => SetProperty(ref _contextMenuItems, value);
            }

            public ICommand SetRegionTypeCommand { get; }

            private void InitializeContextMenu()
            {
                ContextMenuItems = new ObservableCollection<RegionTypeMenuItem>
                {
                    new RegionTypeMenuItem
        {
            Header = "进料区域",
            RegionTypeValue = "Feed",
            ColorBrush = new SolidColorBrush(Color.FromRgb(255, 182, 193)), // 浅粉色
            Command = SetRegionTypeCommand
        },
        new RegionTypeMenuItem
        {
            Header = "预热区域",
            RegionTypeValue = "PreHeat",
            ColorBrush = new SolidColorBrush(Color.FromRgb(255, 218, 185)), // 桃色
            Command = SetRegionTypeCommand
        },
        new RegionTypeMenuItem
        {
            Header = "反应区域",
            RegionTypeValue = "Reaction",
            ColorBrush = new SolidColorBrush(Color.FromRgb(255, 228, 196)), // 浅橙色
            Command = SetRegionTypeCommand
        },
        new RegionTypeMenuItem
        {
            Header = "淬火区域",
            RegionTypeValue = "Quench",
            ColorBrush = new SolidColorBrush(Color.FromRgb(173, 216, 230)), // 浅蓝色
            Command = SetRegionTypeCommand
        },
        new RegionTypeMenuItem
        {
            Header = "后处理区域",
            RegionTypeValue = "PostProcess",
            ColorBrush = new SolidColorBrush(Color.FromRgb(240, 255, 240)), // 浅绿色
            Command = SetRegionTypeCommand
        },
        new RegionTypeMenuItem
        {
            IsSeparator = true
        },
        new RegionTypeMenuItem
        {
            Header = "清空区域",
            RegionTypeValue = "None",
            ColorBrush = Brushes.White,
            Command = SetRegionTypeCommand
        }
                };
            }

            private void OnSetRegionType(string regionTypeString)
            {
                if (Enum.TryParse<RegionType>(regionTypeString, out var regionType))
                {
                    switch (regionType)
                    {
                        case RegionType.Feed:
                            RegionType = RegionType.Feed;
                            DisplayName = _parentViewModel.SetRegionTypeLocalizationText(RegionType.Feed, "进料");
                            Background = new SolidColorBrush(Color.FromRgb(255, 182, 193)); // 浅粉色
                            break;
                        case RegionType.PreHeat:
                            RegionType = RegionType.PreHeat;
                            DisplayName = _parentViewModel.SetRegionTypeLocalizationText(RegionType.PreHeat, "预热");
                            Background = new SolidColorBrush(Color.FromRgb(255, 218, 185)); // 桃色
                            break;
                        case RegionType.Reaction:
                            RegionType = RegionType.Reaction;
                            DisplayName = _parentViewModel.SetRegionTypeLocalizationText(RegionType.Reaction, "反应");
                            Background = new SolidColorBrush(Color.FromRgb(255, 228, 196)); // 浅橙色
                            break;
                        case RegionType.Quench:
                            RegionType = RegionType.Quench;
                            DisplayName = _parentViewModel.SetRegionTypeLocalizationText(RegionType.Quench, "淬火");
                            Background = new SolidColorBrush(Color.FromRgb(173, 216, 230)); // 浅蓝色
                            break;
                        case RegionType.PostProcess:
                            RegionType = RegionType.PostProcess;
                            DisplayName = _parentViewModel.SetRegionTypeLocalizationText(RegionType.PostProcess, "后处理");
                            Background = new SolidColorBrush(Color.FromRgb(240, 255, 240)); // 浅绿色
                            break;
                        case RegionType.None:
                        default:
                            RegionType = RegionType.None;
                            DisplayName = _parentViewModel.SetRegionTypeLocalizationText(RegionType.None, "空");
                            Background = Brushes.White;
                            break;
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
            {
                if (Equals(storage, value)) return false;
                storage = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public class RelayCommand : ICommand
        {
            private readonly Action _execute;
            private readonly Func<bool> _canExecute;

            public RelayCommand(Action execute, Func<bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public event EventHandler CanExecuteChanged
            {
                add { CommandManager.RequerySuggested += value; }
                remove { CommandManager.RequerySuggested -= value; }
            }

            public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
            public void Execute(object parameter) => _execute();
        }

        public class RelayCommand<T> : ICommand
        {
            private readonly Action<T> _execute;
            private readonly Func<T, bool> _canExecute;

            public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public event EventHandler CanExecuteChanged
            {
                add { CommandManager.RequerySuggested += value; }
                remove { CommandManager.RequerySuggested -= value; }
            }

            public bool CanExecute(object parameter) => _canExecute?.Invoke((T)parameter) ?? true;
            public void Execute(object parameter) => _execute((T)parameter);
        }
        /// <summary>
        /// 区域类型配置辅助类
        /// </summary>
        public class RegionTypeConfig
        {
            public string DisplayName { get; set; }
            public Color Color { get; set; }
            public string Description { get; set; }
            public Brush Brush => new SolidColorBrush(Color);
        }
        #endregion
    }

}
