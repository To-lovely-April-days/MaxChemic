using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxChemical.Modules.Designer.Models;
using DevicePlugins.Devices;
using MaxChemical.DataModule.Model;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// LoopNodeControlView.xaml 的交互逻辑
    /// </summary>
    public partial class LoopNodeControlView : UserControl
    {
        public event EventHandler ContentChanged;
        public event EventHandler<NestedCommandEventArgs> NestedCommandAdded;
        public event EventHandler<NestedCommandEventArgs> NestedCommandRemoved;
        public event EventHandler<LogicCommandRequestEventArgs> LogicCommandRequested;

        private bool _isExpanded = true;

        public LoopNodeControlView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is CommandNode loopNode)
            {
                // 同步Children到UI
                UpdateChildrenDisplay();
            }
        }

        private void Node_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && DataContext is CommandNode node)
            {
                var routedEventArgs = new RoutedEventArgs(NodeClickEvent, node);
                RaiseEvent(routedEventArgs);
                e.Handled = true;
            }
        }

        private void ExpandButton_Click(object sender, RoutedEventArgs e)
        {
            _isExpanded = !_isExpanded;

            //if (_isExpanded)
            //{
            //    ChildrenContainer.Visibility = Visibility.Visible;
            //    ExpandIcon.Data = Geometry.Parse("M7.41,8.58L12,13.17L16.59,8.58L18,10L12,16L6,10L7.41,8.58Z");
            //    ExpandButton.ToolTip = "折叠循环";
            //}
            //else
            //{
            //    ChildrenContainer.Visibility = Visibility.Collapsed;
            //    ExpandIcon.Data = Geometry.Parse("M8.59,16.58L13.17,12L8.59,7.41L10,6L16,12L10,18L8.59,16.58Z");
            //    ExpandButton.ToolTip = "展开循环";
            //}
        }

        #region 拖拽事件处理

        private void DropArea_DragEnter(object sender, DragEventArgs e)
        {
            Debug.WriteLine("=== LoopNode DropArea_DragEnter 被调用 ===");

            bool canAccept = CanAcceptDrop(e.Data);
            if (canAccept && sender is Border border)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE8));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DropArea_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            }
            e.Handled = true;
        }

        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            bool canAccept = CanAcceptDrop(e.Data);
            e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropArea_Drop(object sender, DragEventArgs e)
        {
            Debug.WriteLine("=== LoopNode DropArea_Drop 被调用 ===");

            try
            {
                if (DataContext is CommandNode loopNode)
                {
                    var command = ExtractCommandFromDragData(e.Data);
                    if (command != null)
                    {
                        Debug.WriteLine($"提取到命令: {command.GetType().Name}");
                        AddCommandToLoop(command, loopNode);
                    }
                    else
                    {
                        Debug.WriteLine("未能提取到有效命令");
                    }
                }

                // 恢复拖放区域样式
                if (sender is Border border)
                {
                    border.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoopNode 处理拖放失败: {ex.Message}");
            }

            e.Handled = true;
        }

        private void DropArea_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is CommandNode loopNode)
            {
                var args = new RoutedEventArgs(ShowCommandSelectorEvent, loopNode);
                RaiseEvent(args);
            }
        }

        #endregion

        #region 拖拽数据处理

        private bool CanAcceptDrop(IDataObject data)
        {
            try
            {
                Debug.WriteLine("=== LoopNode CanAcceptDrop 检查开始 ===");

                var formats = data.GetFormats();
                Debug.WriteLine($"数据格式数量: {formats.Length}");

                foreach (string format in formats)
                {
                    Debug.WriteLine($"检查格式: {format}");
                }

                bool hasLogicCommand = data.GetDataPresent("LogicCommand");
                bool hasDeviceCommand = data.GetDataPresent("DeviceCommand");
                bool hasPluginId = data.GetDataPresent("PluginId");
                bool hasCommandDragInfo = data.GetDataPresent("CommandDragInfo");
                bool hasCommandDragData = data.GetDataPresent("CommandDragData");
                bool hasDeviceCommandType = data.GetDataPresent(typeof(DeviceCommand));
                bool hasLogicCommandType = data.GetDataPresent(typeof(LogicCommand));

                bool canAccept = hasLogicCommand || hasDeviceCommand || hasPluginId ||
                                hasCommandDragInfo || hasCommandDragData ||
                                hasDeviceCommandType || hasLogicCommandType;

                Debug.WriteLine($"最终结果: {canAccept}");
                return canAccept;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoopNode CanAcceptDrop 异常: {ex.Message}");
                return false;
            }
        }

        private object ExtractCommandFromDragData(IDataObject data)
        {
            Debug.WriteLine("=== LoopNode 开始提取拖拽数据中的命令 ===");

            try
            {
                // 1. 优先处理 CommandDragInfo 格式
                if (data.GetDataPresent("CommandDragInfo"))
                {
                    Debug.WriteLine("尝试提取 CommandDragInfo");
                    var commandDragInfo = data.GetData("CommandDragInfo");
                    Debug.WriteLine($"CommandDragInfo 类型: {commandDragInfo?.GetType().Name}");
                    var command = ExtractCommandFromDragInfo(commandDragInfo);
                    if (command != null)
                    {
                        Debug.WriteLine($"从 CommandDragInfo 提取成功: {command.GetType().Name}");
                        return command;
                    }
                }

                // 2. 处理直接的 DeviceCommand 格式
                if (data.GetDataPresent("DeviceCommand"))
                {
                    Debug.WriteLine("尝试提取 DeviceCommand");
                    var deviceCommandData = data.GetData("DeviceCommand");
                    Debug.WriteLine($"DeviceCommand 数据类型: {deviceCommandData?.GetType().Name}");
                    if (deviceCommandData is DeviceCommand deviceCmd)
                    {
                        Debug.WriteLine($"DeviceCommand 提取成功: {deviceCmd.Name}");
                        return deviceCmd;
                    }
                }

                // 3. 处理设备命令类型格式
                if (data.GetDataPresent(typeof(DeviceCommand)))
                {
                    Debug.WriteLine("尝试提取 DeviceCommand 类型");
                    var typeCommand = data.GetData(typeof(DeviceCommand));
                    Debug.WriteLine($"类型命令: {typeCommand?.GetType().Name}");
                    if (typeCommand is DeviceCommand typeDeviceCmd)
                    {
                        Debug.WriteLine($"类型 DeviceCommand 提取成功: {typeDeviceCmd.Name}");
                        return typeDeviceCmd;
                    }
                }

                // 4. 处理逻辑命令格式
                if (data.GetDataPresent("LogicCommand"))
                {
                    Debug.WriteLine("尝试提取 LogicCommand");
                    var logicCommandData = data.GetData("LogicCommand");
                    Debug.WriteLine($"LogicCommand 数据类型: {logicCommandData?.GetType().Name}");
                    if (logicCommandData is LogicCommand logicCmd)
                    {
                        Debug.WriteLine($"LogicCommand 提取成功: {logicCmd.Name}");
                        return logicCmd;
                    }
                    else
                    {
                        var command = ExtractCommandFromDragInfo(logicCommandData);
                        if (command != null)
                        {
                            Debug.WriteLine($"从 LogicCommand 数据提取成功: {command.GetType().Name}");
                            return command;
                        }
                    }
                }

                // 5. 处理插件ID格式
                if (data.GetDataPresent("PluginId"))
                {
                    Debug.WriteLine("尝试提取 PluginId");
                    var pluginIdData = data.GetData("PluginId");
                    Debug.WriteLine($"PluginId: {pluginIdData}");
                    if (pluginIdData != null)
                    {
                        var recreatedCommand = TryRecreateLogicCommand(pluginIdData.ToString());
                        if (recreatedCommand != null)
                        {
                            Debug.WriteLine($"从 PluginId 重建命令成功: {recreatedCommand.Name}");
                            return recreatedCommand;
                        }
                    }
                }

                Debug.WriteLine("所有提取方法都失败了");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"提取命令时发生异常: {ex.Message}");
                return null;
            }
        }

        private object ExtractCommandFromDragInfo(object commandDragInfo)
        {
            try
            {
                if (commandDragInfo == null) return null;

                if (commandDragInfo is CommandDragInfo dragInfo)
                {
                    return dragInfo.Command;
                }

                // 使用反射处理
                var type = commandDragInfo.GetType();
                if (type.Name == "CommandDragInfo")
                {
                    var commandProperty = type.GetProperty("Command");
                    if (commandProperty != null)
                    {
                        var command = commandProperty.GetValue(commandDragInfo);
                        if (command is DeviceCommand deviceCmd)
                        {
                            return deviceCmd;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"从 CommandDragInfo 提取命令失败: {ex.Message}");
                return null;
            }
        }

        private LogicCommand TryRecreateLogicCommand(string pluginId)
        {
            try
            {
                if (string.IsNullOrEmpty(pluginId))
                    return null;

                var args = new LogicCommandRequestEventArgs { PluginId = pluginId };
                LogicCommandRequested?.Invoke(this, args);
                return args.ResultCommand;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"重新创建LogicCommand失败: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region 命令管理

        private void AddCommandToLoop(object command, CommandNode loopNode)
        {
            try
            {
                CommandNode childNode = null;

                if (command is DeviceCommand deviceCmd)
                {
                    childNode = new CommandNode(deviceCmd);
                }
                else if (command is LogicCommand logicCmd)
                {
                    childNode = new CommandNode(logicCmd);
                }

                if (childNode != null)
                {
                    // 使用统一的Children集合
                    loopNode.AddChildToLoop(childNode);

                    Debug.WriteLine($"添加命令到循环: {childNode.DisplayName}");

                    // 更新UI显示
                    UpdateChildrenDisplay();

                    // 触发添加子命令事件
                    NestedCommandAdded?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = childNode,
                        IsTrueBranch = false, // 循环没有分支概念
                        ParentIfNode = loopNode
                    });

                    // 触发内容变化事件
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"添加命令到循环失败: {ex.Message}");
            }
        }

        private void UpdateChildrenDisplay()
        {
            try
            {
                if (DataContext is not CommandNode loopNode)
                    return;

                // 清空现有UI
                LoopChildrenContent.Children.Clear();

                // 为每个子命令创建UI
                foreach (var child in loopNode.Children)
                {
                    var childControl = CreateLoopChildControl(child);
                    LoopChildrenContent.Children.Add(childControl);
                }

                Debug.WriteLine($"循环节点UI更新完成，子项数: {loopNode.Children.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新子命令显示失败: {ex.Message}");
            }
        }

        private UserControl CreateLoopChildControl(CommandNode childNode)
        {
            if (childNode.IsDeviceCommand)
            {
                return CreateDeviceCommandControl(childNode);
            }
            else if (childNode.IsIfNode)
            {
                return CreateNestedIfNodeControl(childNode);
            }
            else if (childNode.IsParallelNode)
            {
                return CreateNestedParallelNodeControl(childNode);
            }
            else if (childNode.IsLoopNode)
            {
                return CreateNestedLoopNodeControl(childNode);
            }
            else
            {
                return CreateLogicCommandControl(childNode);
            }
        }

        private UserControl CreateDeviceCommandControl(CommandNode nestedNode)
        {
            var border = new Border
            {
                Width = 231,
                Height = 64,
                Background = new SolidColorBrush(Colors.White),
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 4)
            };

            border.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 4,
                Direction = 315,
                BlurRadius = 8,
                Color = Colors.Black,
                Opacity = 0.25
            };

            var mainGrid = new Grid
            {
                Margin = new Thickness(16)
            };

            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧图标
            var iconGrid = new Grid
            {
                Width = 32,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetColumn(iconGrid, 0);

            if (nestedNode.HasImage && !string.IsNullOrEmpty(nestedNode.DisplayImageLocation))
            {
                try
                {
                    var image = new Image
                    {
                        Source = new BitmapImage(new Uri(nestedNode.DisplayImageLocation, UriKind.RelativeOrAbsolute)),
                        Stretch = Stretch.Uniform,
                        Width = 26,
                        Height = 26
                    };
                    iconGrid.Children.Add(image);
                }
                catch
                {
                    AddDefaultIconToGrid(iconGrid);
                }
            }
            else
            {
                AddDefaultIconToGrid(iconGrid);
            }

            // 中间文字内容
            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(textStack, 1);

            var nameText = new TextBlock
            {
                Text = nestedNode.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Margin = new Thickness(0, 0, 0, 2),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var helpText = new TextBlock
            {
                Text = nestedNode.DisplayHelpText,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            textStack.Children.Add(nameText);
            textStack.Children.Add(helpText);

            // 删除按钮
            var deleteButtonBorder = new Border
            {
                Width = 16,
                Height = 16,
                Background = new SolidColorBrush(Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand,
                Tag = nestedNode
            };

            var deleteText = new TextBlock
            {
                Text = "✕",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            deleteButtonBorder.Child = deleteText;
            deleteButtonBorder.MouseLeftButtonUp += (s, e) =>
            {
                if (s is Border borderSender && borderSender.Tag is CommandNode node)
                {
                    RemoveLoopCommand(node);
                }
            };

            deleteButtonBorder.MouseEnter += (s, e) =>
            {
                deleteText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
            };
            deleteButtonBorder.MouseLeave += (s, e) =>
            {
                deleteText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            };

            Grid.SetColumn(deleteButtonBorder, 2);

            mainGrid.Children.Add(iconGrid);
            mainGrid.Children.Add(textStack);
            mainGrid.Children.Add(deleteButtonBorder);

            border.Child = mainGrid;

            return new UserControl
            {
                Content = border,
                Tag = nestedNode
            };
        }

        private UserControl CreateLogicCommandControl(CommandNode nestedNode)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 4),
                MinHeight = 36,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = nestedNode.DisplayName,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameText, 0);

            var deleteButtonBorder = new Border
            {
                Width = 20,
                Height = 20,
                Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)),
                CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                Tag = nestedNode
            };

            var deleteText = new TextBlock
            {
                Text = "×",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            deleteButtonBorder.Child = deleteText;
            deleteButtonBorder.MouseLeftButtonUp += (s, e) =>
            {
                if (s is Border borderSender && borderSender.Tag is CommandNode node)
                {
                    RemoveLoopCommand(node);
                }
            };

            deleteButtonBorder.MouseEnter += (s, e) =>
            {
                deleteButtonBorder.Background = new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));
            };
            deleteButtonBorder.MouseLeave += (s, e) =>
            {
                deleteButtonBorder.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
            };

            Grid.SetColumn(deleteButtonBorder, 1);

            grid.Children.Add(nameText);
            grid.Children.Add(deleteButtonBorder);
            border.Child = grid;

            return new UserControl
            {
                Content = border,
                Tag = nestedNode
            };
        }

        private UserControl CreateNestedIfNodeControl(CommandNode nestedNode)
        {
            try
            {
                var nestedIfControl = new IfNodeControlView
                {
                    DataContext = nestedNode,
                    Tag = nestedNode,
                    Margin = new Thickness(0, 4, 0, 4)
                };

                SubscribeToNestedNodeEvents(nestedIfControl, nestedNode);
                return nestedIfControl;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建嵌套IF节点控件失败: {ex.Message}");
                return CreateLogicCommandControl(nestedNode);
            }
        }

        private UserControl CreateNestedParallelNodeControl(CommandNode nestedNode)
        {
            try
            {
                var nestedParallelControl = new ParallelNodeControlView
                {
                    DataContext = nestedNode,
                    Tag = nestedNode,
                    Margin = new Thickness(0, 4, 0, 4)
                };

                SubscribeToNestedNodeEvents(nestedParallelControl, nestedNode);
                return nestedParallelControl;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建嵌套并行节点控件失败: {ex.Message}");
                return CreateLogicCommandControl(nestedNode);
            }
        }

        private UserControl CreateNestedLoopNodeControl(CommandNode nestedNode)
        {
            try
            {
                var nestedLoopControl = new LoopNodeControlView
                {
                    DataContext = nestedNode,
                    Tag = nestedNode,
                    Margin = new Thickness(0, 4, 0, 4)
                };

                SubscribeToNestedNodeEvents(nestedLoopControl, nestedNode);
                return nestedLoopControl;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建嵌套循环节点控件失败: {ex.Message}");
                return CreateLogicCommandControl(nestedNode);
            }
        }

        private void SubscribeToNestedNodeEvents(UserControl nestedControl, CommandNode nestedNode)
        {
            try
            {
                if (nestedControl is IfNodeControlView ifControl)
                {
                    ifControl.ContentChanged += (s, e) => ContentChanged?.Invoke(this, EventArgs.Empty);
                    ifControl.NestedCommandAdded += (s, e) => NestedCommandAdded?.Invoke(this, e);
                    ifControl.NestedCommandRemoved += (s, e) => NestedCommandRemoved?.Invoke(this, e);
                    ifControl.LogicCommandRequested += (s, e) => LogicCommandRequested?.Invoke(this, e);
                }
                else if (nestedControl is ParallelNodeControlView parallelControl)
                {
                    parallelControl.ContentChanged += (s, e) => ContentChanged?.Invoke(this, EventArgs.Empty);
                    parallelControl.NestedCommandAdded += (s, e) => NestedCommandAdded?.Invoke(this, e);
                    parallelControl.NestedCommandRemoved += (s, e) => NestedCommandRemoved?.Invoke(this, e);
                    parallelControl.LogicCommandRequested += (s, e) => LogicCommandRequested?.Invoke(this, e);
                }
                else if (nestedControl is LoopNodeControlView loopControl)
                {
                    loopControl.ContentChanged += (s, e) => ContentChanged?.Invoke(this, EventArgs.Empty);
                    loopControl.NestedCommandAdded += (s, e) => NestedCommandAdded?.Invoke(this, e);
                    loopControl.NestedCommandRemoved += (s, e) => NestedCommandRemoved?.Invoke(this, e);
                    loopControl.LogicCommandRequested += (s, e) => LogicCommandRequested?.Invoke(this, e);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅嵌套节点事件失败: {ex.Message}");
            }
        }

        private void AddDefaultIconToGrid(Grid iconGrid)
        {
            var iconBorder = new Border
            {
                Width = 26,
                Height = 26,
                Background = new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF)),
                CornerRadius = new CornerRadius(13)
            };

            var iconText = new TextBlock
            {
                Text = "🔧",
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White)
            };

            iconBorder.Child = iconText;
            iconGrid.Children.Add(iconBorder);
        }

        public void RemoveLoopCommand(CommandNode childNode)
        {
            try
            {
                if (DataContext is CommandNode loopNode && loopNode.RemoveChild(childNode))
                {
                    Debug.WriteLine($"从循环移除命令: {childNode.DisplayName}");

                    // 更新UI显示
                    UpdateChildrenDisplay();

                    // 触发移除事件
                    NestedCommandRemoved?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = childNode,
                        IsTrueBranch = false,
                        ParentIfNode = loopNode
                    });

                    // 触发内容变化事件
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"移除循环命令失败: {ex.Message}");
            }
        }

        public void ClearLoopCommands()
        {
            try
            {
                if (DataContext is CommandNode loopNode)
                {
                    loopNode.ClearChildren();
                    UpdateChildrenDisplay();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清空循环命令失败: {ex.Message}");
            }
        }

        public IReadOnlyList<CommandNode> GetLoopCommands()
        {
            if (DataContext is CommandNode loopNode)
            {
                return loopNode.Children.ToList().AsReadOnly();
            }
            return new List<CommandNode>().AsReadOnly();
        }

        #endregion

        // 节点点击事件
        public static readonly RoutedEvent NodeClickEvent = EventManager.RegisterRoutedEvent(
            "NodeClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(LoopNodeControlView));

        public event RoutedEventHandler NodeClick
        {
            add { AddHandler(NodeClickEvent, value); }
            remove { RemoveHandler(NodeClickEvent, value); }
        }

        // 显示命令选择器事件
        public static readonly RoutedEvent ShowCommandSelectorEvent = EventManager.RegisterRoutedEvent(
            "ShowCommandSelector", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(LoopNodeControlView));

        public event RoutedEventHandler ShowCommandSelector
        {
            add { AddHandler(ShowCommandSelectorEvent, value); }
            remove { RemoveHandler(ShowCommandSelectorEvent, value); }
        }
    }
}