// Views/Controls/PipeToolboxPanel.xaml.cs
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using Prism.Events;
using Prism.Ioc;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    public partial class PipeToolboxPanel : UserControl, INotifyPropertyChanged
    {
        private string _libraryTitle;
        private string _straightPipe;
        private string _elbowPipe;
        private string _zShapePipe;
        private string _tShapePipe;
        private string _foldTooltip;
        private string _expandLabel;

        private readonly IEventAggregator _eventAggregator;
        private readonly ILocalizationService _localizationService;

        public event EventHandler<PipeDragStartEventArgs> PipeDragStarted;
        public event PropertyChangedEventHandler? PropertyChanged;

        public PipeToolboxPanel()
        {
            InitializeComponent();
            this.DataContext = this;

            _eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
            _localizationService = ContainerLocator.Container.Resolve<ILocalizationService>();

            _eventAggregator.GetEvent<LanguageChangedEvent>().Subscribe(LanguageChangedHandler);

            LanguageChangedHandler("");
        }

        public string PipelineLibraryTitle
        {
            get { return _libraryTitle; }
            set
            {
                if (_libraryTitle != value)
                {
                    _libraryTitle = value;
                    RaisePropertyChanged();
                }
            }
        }

        public string StraightPipe
        {
            get { return _straightPipe; }
            set
            {
                if (_straightPipe != value)
                {
                    _straightPipe = value;
                    RaisePropertyChanged();
                }
            }
        }

        public string ElbowPipe
        {
            get { return _elbowPipe; }
            set
            {
                if (_elbowPipe != value)
                {
                    _elbowPipe = value;
                    RaisePropertyChanged();
                }
            }
        }

        public string ZShapePipe
        {
            get { return _zShapePipe; }
            set
            {
                if (_zShapePipe != value)
                {
                    _zShapePipe = value;
                    RaisePropertyChanged();
                }
            }
        }

        public string TShapePipe
        {
            get { return _tShapePipe; }
            set
            {
                if (_tShapePipe != value)
                {
                    _tShapePipe = value;
                    RaisePropertyChanged();
                }
            }
        }

        public string FoldTooltip
        {
            get { return _foldTooltip; }
            set
            {
                if (_foldTooltip != value)
                {
                    _foldTooltip = value;
                    RaisePropertyChanged();
                }
            }
        }

        public string ExpandLabel
        {
            get { return _expandLabel; }
            set
            {
                if (_expandLabel != value)
                {
                    _expandLabel = value;
                    RaisePropertyChanged();
                }
            }
        }


        private void LanguageChangedHandler(string cultureName)
        {
            PipelineLibraryTitle = _localizationService.GetString("Pipeline_Library");
            StraightPipe = _localizationService.GetString("Pipeline_StraightPipe");
            ElbowPipe = _localizationService.GetString("Pipeline_ElbowPipe");
            TShapePipe = _localizationService.GetString("Pipeline_TShapePipe");
            ZShapePipe = _localizationService.GetString("Pipeline_ZShapePipe");
            FoldTooltip = _localizationService.GetString("Pipeline_Fold_Tooltip");
            ExpandLabel = _localizationService.GetString("Pipeline_Expand");
        }

        private void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        #region 收缩/展开功能

        private void OnCollapseClick(object sender, RoutedEventArgs e)
        {
            ExpandedPanel.Visibility = Visibility.Collapsed;
            CollapsedPanel.Visibility = Visibility.Visible;
        }

        private void OnExpandClick(object sender, MouseButtonEventArgs e)
        {
            CollapsedPanel.Visibility = Visibility.Collapsed;
            ExpandedPanel.Visibility = Visibility.Visible;
        }

        #endregion

        #region 鼠标悬停效果

        private void OnToolboxItemMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(230, 240, 250));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 150, 200));
                border.BorderThickness = new Thickness(2);
            }
        }

        private void OnToolboxItemMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                border.BorderBrush = Brushes.Transparent;
                border.BorderThickness = new Thickness(0);
            }
        }

        #endregion

        #region 直管道拖拽

        private void OnStraightPipeMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var pipeData = new PipeDragData
                {
                    PipeType = PipeType.Straight,
                    Width = 96,
                    Height = 45
                };

                var dataObject = new DataObject("PipeData", pipeData);
                DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);

                PipeDragStarted?.Invoke(this, new PipeDragStartEventArgs
                {
                    PipeType = PipeType.Straight
                });
            }
        }

        #endregion

        #region 弯管道拖拽

        private void OnElbowPipeMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var pipeData = new PipeDragData
                {
                    PipeType = PipeType.Elbow,
                    Width = 80,
                    Height = 80
                };

                var dataObject = new DataObject("PipeData", pipeData);
                DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);

                PipeDragStarted?.Invoke(this, new PipeDragStartEventArgs
                {
                    PipeType = PipeType.Elbow
                });
            }
        }

        #endregion

        #region Z字形管道拖拽

        private void OnZShapePipeMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var pipeData = new PipeDragData
                {
                    PipeType = PipeType.ZShape,
                    Width = 200,
                    Height = 140
                };

                var dataObject = new DataObject("PipeData", pipeData);
                DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);

                PipeDragStarted?.Invoke(this, new PipeDragStartEventArgs
                {
                    PipeType = PipeType.ZShape
                });
            }
        }

        #endregion

        #region T型管道拖拽

        private void OnTShapePipeMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var pipeData = new PipeDragData
                {
                    PipeType = PipeType.Tee,  //  使用已定义的 Tee 类型
                    Width = 160,
                    Height = 140
                };

                var dataObject = new DataObject("PipeData", pipeData);
                DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);

                PipeDragStarted?.Invoke(this, new PipeDragStartEventArgs
                {
                    PipeType = PipeType.Tee
                });
            }
        }

        #endregion
    }

    #region 事件参数和数据类

    public class PipeDragStartEventArgs : EventArgs
    {
        public PipeType PipeType { get; set; }
    }

    public class PipeDragData
    {
        public PipeType PipeType { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public enum PipeType
    {
        Straight,      // 直管道
        Elbow,         // 弯管道（90度）
        ZShape,        // Z字形管道
        Conical,       // 锥形瓶
        PistonPump,    // 柱塞泵
        Tee,           // 三通管道
        Cross,         // 四通管道
        Valve          // 阀门
    }

    #endregion
}