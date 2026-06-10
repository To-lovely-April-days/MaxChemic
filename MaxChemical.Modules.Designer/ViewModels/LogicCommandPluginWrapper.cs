// ViewModels/LogicCommandPluginWrapper.cs
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Plugins;
using Prism.Mvvm;
using Point = System.Windows.Point;
using ValidationResult = MaxChemical.Modules.Designer.Plugins.ValidationResult;

namespace MaxChemical.Modules.Designer.ViewModels
{
    /// <summary>
    /// 逻辑命令插件包装器，用于在ViewModel中管理插件
    /// </summary>
    public class LogicCommandPluginWrapper : BindableBase
    {
        private readonly ILogicCommandPlugin _plugin;
        private readonly FlowDesignerViewModel _viewModel;
        private FrameworkElement _toolbarButton;
        private bool _isDragging = false;
        private Point _startPoint;

        public ILogicCommandPlugin Plugin => _plugin;

        public FrameworkElement ToolbarButton
        {
            get => _toolbarButton;
            private set => SetProperty(ref _toolbarButton, value);
        }

        public string Name => _plugin.Name;
        public string Description => _plugin.Description;
        public string ToolTip => _plugin.ToolTip;
        public int Order => _plugin.Order;

        public LogicCommandPluginWrapper(ILogicCommandPlugin plugin, FlowDesignerViewModel viewModel)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            CreateToolbarButton();
        }

        private void CreateToolbarButton()
        {
            try
            {
                ToolbarButton = _plugin.CreateToolbarButton();

                if (ToolbarButton != null)
                {
                    // 添加拖拽相关的事件处理
                    ToolbarButton.PreviewMouseLeftButtonDown += ToolButton_PreviewMouseLeftButtonDown;
                    ToolbarButton.PreviewMouseMove += ToolButton_PreviewMouseMove;
                    ToolbarButton.PreviewMouseLeftButtonUp += ToolButton_PreviewMouseLeftButtonUp;
                    ToolbarButton.MouseLeave += ToolButton_MouseLeave;

                    Debug.WriteLine($"为插件 {_plugin.Name} 创建了工具栏按钮");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建插件 {_plugin.Name} 的工具栏按钮时发生错误: {ex.Message}");
            }
        }

        private void ToolButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition((IInputElement)sender);
            _isDragging = false;
            Debug.WriteLine($"插件 {_plugin.Name} 按钮按下: {_startPoint}");
        }

        private void ToolButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)
            {
                Point currentPosition = e.GetPosition((IInputElement)sender);

                double deltaX = Math.Abs(currentPosition.X - _startPoint.X);
                double deltaY = Math.Abs(currentPosition.Y - _startPoint.Y);

                if (deltaX > SystemParameters.MinimumHorizontalDragDistance ||
                    deltaY > SystemParameters.MinimumVerticalDragDistance)
                {
                    Debug.WriteLine($"开始拖拽插件 {_plugin.Name}");
                    _isDragging = true;
                    StartDragOperation((FrameworkElement)sender);
                }
            }
        }

        private void ToolButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
        }

        private void ToolButton_MouseLeave(object sender, MouseEventArgs e)
        {
            _isDragging = false;
        }

        private void StartDragOperation(FrameworkElement sourceElement)
        {
            try
            {
                // 创建命令节点
                var commandNode = _plugin.CreateCommandNode();
                if (commandNode?.LogicCommand != null)
                {
                    Debug.WriteLine($"开始拖拽逻辑命令: {commandNode.LogicCommand.Name}");

                    // 创建拖拽数据
                    var dataObject = new DataObject();
                    dataObject.SetData("LogicCommand", commandNode.LogicCommand);
                    dataObject.SetData("PluginId", _plugin.Id);

                    // 执行拖拽操作
                    var result = DragDrop.DoDragDrop(sourceElement, dataObject, DragDropEffects.Copy);

                    Debug.WriteLine($"拖拽结果: {result}");
                }
                else
                {
                    Debug.WriteLine($"无法为插件 {_plugin.Name} 创建命令节点");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"拖拽操作失败: {ex.Message}");
            }
            finally
            {
                _isDragging = false;
            }
        }

        /// <summary>
        /// 创建节点控件
        /// </summary>
        public UserControl CreateNodeControl(CommandNode node)
        {
            try
            {
                return _plugin.CreateNodeControl(node);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建节点控件失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 验证节点配置
        /// </summary>
        public ValidationResult ValidateNode(CommandNode node)
        {
            try
            {
                return _plugin.ValidateNodeConfiguration(node);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"验证节点配置失败: {ex.Message}");
                return ValidationResult.Error($"验证失败: {ex.Message}");
            }
        }
    }
}