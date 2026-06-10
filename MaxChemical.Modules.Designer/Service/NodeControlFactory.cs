using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Events;
using MaxChemical.Modules.Designer.Views;
using MaxChemical.Logging;
using static MaxChemical.Modules.Designer.Models.CommandNode;

namespace MaxChemical.Modules.Designer.Services
{
    public class NodeControlFactory
    {
        private readonly ILogService _logger;

        public NodeControlFactory(ILogService logger)
        {
            _logger = logger?.ForContext<NodeControlFactory>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public UserControl CreateNestedCommandControl(CommandNode node, NodeClickHandler clickHandler, NodeDeleteHandler deleteHandler)
        {//  诊断
            _logger.LogDebug(" CreateNestedCommandControl: NodeName={Name}, " +
                "IsDevice={IsDev}, IsIf={IsIf}, IsLoop={IsLoop}, IsWait={IsWait}, IsSetVar={IsSetVar}, " +
                "IsComplexLogic={IsComplex}",
                node.DisplayName,
                node.IsDeviceCommand, node.IsIfNode, node.IsLoopNode,
                node.IsWaitNode, node.IsSetVariableNode,
                IsComplexLogicCommand(node));
            try
            {
                if (node.IsDeviceCommand)
                {
                    // 对于设备命令，使用 XAML 模板而不是代码创建
                    return CreateDeviceCommandControlFromTemplate(node, clickHandler, deleteHandler);
                }

                if (IsComplexLogicCommand(node))
                    return CreateComplexNodeControl(node, clickHandler, deleteHandler);

                return CreateLogicCommandControl(node, clickHandler, deleteHandler);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建嵌套命令控件失败: {NodeName}", node.DisplayName);
                return CreateLogicCommandControl(node, clickHandler, deleteHandler);
            }
        }

        private bool IsComplexLogicCommand(CommandNode node)
        {
            return node.IsIfNode || node.IsParallelNode || node.IsLoopNode ||
                   node.IsWaitNode || node.IsSetVariableNode;
        }

        // 新增：使用 XAML 模板创建设备命令控件
        private UserControl CreateDeviceCommandControlFromTemplate(CommandNode node, NodeClickHandler clickHandler, NodeDeleteHandler deleteHandler)
        {
            try
            {
                // 创建一个 ContentControl 来承载模板内容
                var contentControl = new ContentControl
                {
                    Content = node,
                    DataContext = node,
                    Tag = node
                };

                // 查找并应用设备命令模板
                var template = FindDeviceCommandTemplate();
                if (template != null)
                {
                    contentControl.ContentTemplate = template;
                }

                // 创建包装的 UserControl
                var wrapper = new UserControl
                {
                    Content = contentControl,
                    DataContext = node,
                    Tag = node,
                    Margin = new Thickness(0, 2, 0, 2) // 嵌套节点间距
                };

                // 设置点击事件处理（通过事件冒泡处理）
                wrapper.MouseLeftButtonDown += (s, e) =>
                {
                    // 检查点击是否在删除按钮上
                    // 如果点击的是删除按钮，不处理选择事件
                    if (e.OriginalSource is FrameworkElement element)
                    {
                        // 向上查找删除按钮（支持多种尺寸：16/18/20）
                        var hitDelete = FindParent<Border>(element, b =>
                            b.Name == "DeleteButton" ||
                            (b.Tag != null && b.Cursor == Cursors.Hand &&
                             b.Width >= 16 && b.Width <= 24 &&
                             b.Height >= 16 && b.Height <= 24));

                        // 也检查是否点击了 "×" 文本
                        if (hitDelete == null)
                        {
                            hitDelete = FindParent<Border>(element, b =>
                                b.Child is TextBlock tb && tb.Text == "×");
                        }

                        // 检查是否点击了含垃圾桶图标的 Path
                        if (hitDelete == null && element is System.Windows.Shapes.Path path)
                        {
                            hitDelete = FindParent<Border>(path, b =>
                                b.ToolTip?.ToString() == "删除节点");
                        }

                        if (hitDelete != null)
                        {
                            deleteHandler?.Invoke(node);
                            e.Handled = true;
                            return;
                        }
                    }

                    clickHandler?.Invoke(node);
                    e.Handled = true;
                };
                wrapper.Loaded += (sender, args) =>
                {
                    try
                    {
                        // 遍历所有 Border 子元素，找到 ToolTip="删除节点" 的那个
                        var deleteBorders = FindVisualChildren<Border>(wrapper)
                            .Where(b => b.ToolTip?.ToString() == "删除节点" ||
                                       (b.Child is TextBlock tb && tb.Text == "×"))
                            .ToList();

                        foreach (var db in deleteBorders)
                        {
                            // 移除旧的事件（模板里绑定的 DeviceNode_DeleteClick 会因为找不到处理器而无效）
                            // 添加新的事件处理
                            db.MouseLeftButtonUp += (s, me) =>
                            {
                                deleteHandler?.Invoke(node);
                                me.Handled = true;
                            };
                        }

                        _logger.LogDebug("嵌套设备节点删除按钮重绑定完成: {NodeName}, 找到{Count}个按钮",
                            node.DisplayName, deleteBorders.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "重绑定删除按钮失败: {NodeName}", node.DisplayName);
                    }
                };
                return wrapper;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "使用模板创建设备命令控件失败: {NodeName}", node.DisplayName);
                // 回退到简单控件
                return CreateSimpleDeviceControl(node, clickHandler, deleteHandler);
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    yield return typedChild;
                foreach (var desc in FindVisualChildren<T>(child))
                    yield return desc;
            }
        }
        // 辅助方法：查找父元素
        private T FindParent<T>(DependencyObject child, Func<T, bool> predicate = null) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T typedParent && (predicate == null || predicate(typedParent)))
                    return typedParent;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        // 查找设备命令模板
        private DataTemplate FindDeviceCommandTemplate()
        {
            try
            {
                // 从应用程序资源中查找模板
                if (Application.Current?.Resources["DeviceCommandNodeTemplate"] is DataTemplate appTemplate)
                {
                    return appTemplate;
                }

                // 从当前窗口资源中查找
                if (Application.Current?.MainWindow?.Resources["DeviceCommandNodeTemplate"] is DataTemplate windowTemplate)
                {
                    return windowTemplate;
                }

                // 查找 FlowDesignerView 的资源
                var flowDesignerView = FindFlowDesignerView();
                if (flowDesignerView?.Resources["DeviceCommandNodeTemplate"] is DataTemplate viewTemplate)
                {
                    return viewTemplate;
                }

                _logger.LogWarning("未找到 DeviceCommandNodeTemplate 模板");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查找设备命令模板失败");
                return null;
            }
        }

        // 查找 FlowDesignerView 实例
        private FrameworkElement FindFlowDesignerView()
        {
            try
            {
                if (Application.Current?.MainWindow == null) return null;

                return FindChildOfType<Views.FlowDesignerView>(Application.Current.MainWindow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查找 FlowDesignerView 失败");
                return null;
            }
        }

        // 递归查找指定类型的子元素
        private T FindChildOfType<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;

                var childResult = FindChildOfType<T>(child);
                if (childResult != null)
                    return childResult;
            }
            return null;
        }

        // 创建简单的设备控件作为回退
        private UserControl CreateSimpleDeviceControl(CommandNode node, NodeClickHandler clickHandler, NodeDeleteHandler deleteHandler)
        {
            var border = new Border
            {
                Width = 200,
                Height = 40,
                Background = new SolidColorBrush(Colors.White),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.LightGray),
                Margin = new Thickness(0, 2, 0, 2),
                Tag = node,
                Cursor = Cursors.Hand
            };

            var textBlock = new TextBlock
            {
                Text = node.DisplayName,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };

            border.Child = textBlock;
            border.MouseLeftButtonDown += (s, e) =>
            {
                clickHandler?.Invoke(node);
                e.Handled = true;
            };

            return new UserControl
            {
                Content = border,
                DataContext = node,
                Tag = node
            };
        }

        private UserControl CreateLogicCommandControl(CommandNode node, NodeClickHandler clickHandler, NodeDeleteHandler deleteHandler)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 2, 0, 2),
                MinHeight = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Tag = node
            };

            // 应用执行状态样式
            ApplyLogicExecutionStyle(border, node);

            border.MouseLeftButtonDown += (s, e) =>
            {
                clickHandler?.Invoke(node);
                e.Handled = true;
            };

            border.Child = CreateLogicCommandContent(node, deleteHandler);

            return new UserControl
            {
                Content = border,
                Tag = node,
                DataContext = node
            };
        }

        private UserControl CreateComplexNodeControl(CommandNode node, NodeClickHandler clickHandler, NodeDeleteHandler deleteHandler)
        {
            UserControl control = null;

            try
            {
                if (node.IsIfNode)
                    control = new IfNodeControlView();
                else if (node.IsParallelNode)
                    control = new ParallelNodeControlView();
                else if (node.IsLoopNode)
                    control = new LoopNodeControlView();
                else if (node.IsWaitNode)
                    control = new WaitNodeControlView();
                else if (node.IsSetVariableNode)
                    control = new SetVariableNodeControlView();

                if (control != null)
                {
                    control.DataContext = node;
                    control.Tag = node;
                    control.Margin = new Thickness(0, 2, 0, 2);

                    node.NodeControl = control;  // ← 你加的修复

                    //  诊断日志：确认修复生效
                    _logger.LogDebug(" CreateComplexNodeControl: 设置 node.NodeControl, " +
                        "NodeName={NodeName}, ControlType={ControlType}, NodeControl是否为null={IsNull}",
                        node.DisplayName,
                        control.GetType().Name,
                        node.NodeControl == null);

                    SubscribeToComplexNodeEvents(control, node, clickHandler, deleteHandler);
                }

                return control ?? CreateLogicCommandControl(node, clickHandler, deleteHandler);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建复杂节点控件失败: {NodeName}", node.DisplayName);
                return CreateLogicCommandControl(node, clickHandler, deleteHandler);
            }
        }

        // 为逻辑命令应用执行状态样式
        private void ApplyLogicExecutionStyle(Border border, CommandNode node)
        {
            var style = new Style(typeof(Border));

            // 默认样式
            style.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07))));
            style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));

            // 选中状态样式
            var selectedTrigger = new DataTrigger
            {
                Binding = new Binding("IsSelected") { Source = node },
                Value = true
            };
            selectedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))));
            selectedTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2)));

            // 执行状态样式
            var executingTrigger = new DataTrigger
            {
                Binding = new Binding("ExecutionStatus") { Source = node },
                Value = NodeExecutionStatus.Executing
            };
            executingTrigger.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3))));
            executingTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0xE3, 0xF2, 0xFD))));

            var completedTrigger = new DataTrigger
            {
                Binding = new Binding("ExecutionStatus") { Source = node },
                Value = NodeExecutionStatus.Completed
            };
            completedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))));
            completedTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE8))));

            var failedTrigger = new DataTrigger
            {
                Binding = new Binding("ExecutionStatus") { Source = node },
                Value = NodeExecutionStatus.Failed
            };
            failedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36))));
            failedTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0xEE))));

            style.Triggers.Add(selectedTrigger);
            style.Triggers.Add(executingTrigger);
            style.Triggers.Add(completedTrigger);
            style.Triggers.Add(failedTrigger);

            border.Style = style;
        }

        private Grid CreateLogicCommandContent(CommandNode node, NodeDeleteHandler deleteHandler)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = node.DisplayName,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameText, 0);

            var deleteButton = CreateDeleteButton(node, deleteHandler);
            Grid.SetColumn(deleteButton, 1);

            grid.Children.Add(nameText);
            grid.Children.Add(deleteButton);

            return grid;
        }

        private Border CreateDeleteButton(CommandNode node, NodeDeleteHandler deleteHandler)
        {
            var deleteButton = new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand,
                Tag = node
            };

            var visibilityBinding = new Binding("IsSelected")
            {
                Source = node,
                Converter = new BooleanToVisibilityConverter()
            };
            BindingOperations.SetBinding(deleteButton, UIElement.VisibilityProperty, visibilityBinding);

            var deleteButtonStyle = new Style(typeof(Border));
            deleteButtonStyle.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));

            var hoverTrigger = new Trigger { Property = Border.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00))));
            hoverTrigger.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(8)));

            deleteButtonStyle.Triggers.Add(hoverTrigger);
            deleteButton.Style = deleteButtonStyle;

            var deleteText = new TextBlock
            {
                Text = "×",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            deleteButton.Child = deleteText;
            deleteButton.MouseLeftButtonUp += (s, e) =>
            {
                deleteHandler?.Invoke(node);
                e.Handled = true;
            };

            return deleteButton;
        }

        private void SubscribeToComplexNodeEvents(UserControl control, CommandNode node,
            NodeClickHandler clickHandler, NodeDeleteHandler deleteHandler)
        {
            try
            {
                switch (control)
                {
                    case IfNodeControlView ifControl:
                        ifControl.NodeClicked += (s, clickedNode) => clickHandler?.Invoke(clickedNode);
                        ifControl.NodeDeleteRequested += (s, nodeToDelete) => deleteHandler?.Invoke(nodeToDelete);
                        break;
                    case ParallelNodeControlView parallelControl:
                        parallelControl.NodeClicked += (s, clickedNode) => clickHandler?.Invoke(clickedNode);
                        parallelControl.NodeDeleteRequested += (s, nodeToDelete) => deleteHandler?.Invoke(nodeToDelete);
                        break;
                    case LoopNodeControlView loopControl:
                        loopControl.NodeClicked += (s, clickedNode) => clickHandler?.Invoke(clickedNode);
                        loopControl.NodeDeleteRequested += (s, nodeToDelete) => deleteHandler?.Invoke(nodeToDelete);
                        break;
                    case WaitNodeControlView waitControl:
                        waitControl.NodeDeleteRequested += (s, nodeToDelete) => deleteHandler?.Invoke(nodeToDelete);
                        waitControl.AddHandler(WaitNodeControlView.NodeClickEvent, new RoutedEventHandler((s, e) =>
                        {
                            if (e.OriginalSource is CommandNode clickedNode)
                                clickHandler?.Invoke(clickedNode);
                        }));
                        break;
                    case SetVariableNodeControlView setVarControl:
                        setVarControl.NodeDeleteRequested += (s, nodeToDelete) => deleteHandler?.Invoke(nodeToDelete);
                        setVarControl.AddHandler(SetVariableNodeControlView.NodeClickEvent, new RoutedEventHandler((s, e) =>
                        {
                            if (e.OriginalSource is CommandNode clickedNode)
                                clickHandler?.Invoke(clickedNode);
                        }));
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "订阅复杂节点事件失败: {NodeName}", node.DisplayName);
            }
        }
    }
}