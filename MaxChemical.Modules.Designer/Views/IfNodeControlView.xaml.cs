using System;
using System.Collections.Generic;
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
    /// IfNodeControlView.xaml 的交互逻辑
    /// </summary>
    public partial class IfNodeControlView : UserControl
    {
        public event EventHandler ContentChanged;
        public event EventHandler<NestedCommandEventArgs> NestedCommandAdded;
        public event EventHandler<NestedCommandEventArgs> NestedCommandRemoved;
        public event EventHandler<LogicCommandRequestEventArgs> LogicCommandRequested;

        private Size _lastMeasuredSize;
        // 保留内部集合用于UI快速访问，但数据存储在CommandNode.Children中
        private readonly List<CommandNode> _trueBranchCommands = new List<CommandNode>();
        private readonly List<CommandNode> _falseBranchCommands = new List<CommandNode>();

        public IfNodeControlView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is CommandNode ifNode)
            {
                // 同步 Children 到内部集合和UI
                SyncChildrenToUI();
            }
        }

        private void SyncChildrenToUI()
        {
            if (DataContext is not CommandNode ifNode) return;

            try
            {
                // 清空内部集合和UI
                _trueBranchCommands.Clear();
                _falseBranchCommands.Clear();
                TrueBranchContent.Children.Clear();
                FalseBranchContent.Children.Clear();

                // 从Children集合重建UI
                foreach (var child in ifNode.Children)
                {
                    var branchType = child.GetExecutionProperty<string>("BranchType");
                    var isTrueBranch = branchType == "True";

                    var nestedControl = CreateNestedCommandControl(child);

                    if (isTrueBranch)
                    {
                        _trueBranchCommands.Add(child);
                        TrueBranchContent.Children.Add(nestedControl);
                    }
                    else
                    {
                        _falseBranchCommands.Add(child);
                        FalseBranchContent.Children.Add(nestedControl);
                    }
                }

                // 更新提示文本
                TrueHint.Visibility = TrueBranchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                FalseHint.Visibility = FalseBranchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                UpdateBranchCanvasHeight();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"同步Children到UI失败: {ex.Message}");
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateBranchCanvasHeight();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize != _lastMeasuredSize)
            {
                _lastMeasuredSize = e.NewSize;
                UpdateBranchCanvasHeight();
            }
        }

        #region 拖拽事件处理

        private void TrueBranch_DragEnter(object sender, DragEventArgs e)
        {
            if (CanAcceptDrop(e.Data))
            {
                TrueBranchDropZone.Tag = "DragOver";
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void TrueBranch_DragOver(object sender, DragEventArgs e)
        {
            if (CanAcceptDrop(e.Data))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void TrueBranch_DragLeave(object sender, DragEventArgs e)
        {
            TrueBranchDropZone.Tag = null;
            e.Handled = true;
        }

        private void TrueBranch_Drop(object sender, DragEventArgs e)
        {
            TrueBranchDropZone.Tag = null;
            HandleCommandDrop(e, true);
            e.Handled = true;
        }

        private void FalseBranch_DragEnter(object sender, DragEventArgs e)
        {
            if (CanAcceptDrop(e.Data))
            {
                FalseBranchDropZone.Tag = "DragOver";
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void FalseBranch_DragOver(object sender, DragEventArgs e)
        {
            if (CanAcceptDrop(e.Data))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void FalseBranch_DragLeave(object sender, DragEventArgs e)
        {
            FalseBranchDropZone.Tag = null;
            e.Handled = true;
        }

        private void FalseBranch_Drop(object sender, DragEventArgs e)
        {
            FalseBranchDropZone.Tag = null;
            HandleCommandDrop(e, false);
            e.Handled = true;
        }

        #endregion

        #region 拖拽处理逻辑

        private bool CanAcceptDrop(IDataObject data)
        {
            try
            {
                bool canAccept = data.GetDataPresent("LogicCommand") ||
                                data.GetDataPresent("PluginId") ||
                                data.GetDataPresent("DeviceCommand") ||
                                data.GetDataPresent(typeof(DeviceCommand)) ||
                                data.GetDataPresent(typeof(LogicCommand)) ||
                                data.GetDataPresent("CommandDragInfo") ||
                                data.GetDataPresent("CommandDragData");

                System.Diagnostics.Debug.WriteLine($"CanAcceptDrop 结果: {canAccept}");
                return canAccept;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CanAcceptDrop 异常: {ex.Message}");
                return false;
            }
        }

        private void HandleCommandDrop(DragEventArgs e, bool isTrueBranch)
        {
            try
            {
                object command = null;

                var formats = e.Data.GetFormats();
                System.Diagnostics.Debug.WriteLine($"拖拽到 {(isTrueBranch ? "TRUE" : "FALSE")} 分支，数据格式:");
                foreach (var format in formats)
                {
                    var data = e.Data.GetData(format);
                    System.Diagnostics.Debug.WriteLine($"  格式: {format}, 数据类型: {data?.GetType()?.Name ?? "null"}");
                }

                // 1. 优先处理 CommandDragInfo 格式
                if (e.Data.GetDataPresent("CommandDragInfo"))
                {
                    var commandDragInfo = e.Data.GetData("CommandDragInfo");
                    command = ExtractCommandFromDragInfo(commandDragInfo);
                    if (command != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"从 CommandDragInfo 中成功提取命令: {command.GetType().Name}");
                    }
                }
                // 2. 处理直接的 DeviceCommand 格式
                else if (e.Data.GetDataPresent("DeviceCommand"))
                {
                    var deviceCommandData = e.Data.GetData("DeviceCommand");
                    if (deviceCommandData is DeviceCommand deviceCmd)
                    {
                        command = deviceCmd;
                        System.Diagnostics.Debug.WriteLine($"直接获取到 DeviceCommand: {deviceCmd.Name}");
                    }
                }
                // 3. 处理设备命令类型格式
                else if (e.Data.GetDataPresent(typeof(DeviceCommand)))
                {
                    command = e.Data.GetData(typeof(DeviceCommand));
                    System.Diagnostics.Debug.WriteLine("通过类型获取到 DeviceCommand");
                }
                // 4. 处理逻辑命令格式
                else if (e.Data.GetDataPresent("LogicCommand"))
                {
                    var logicCommandData = e.Data.GetData("LogicCommand");
                    if (logicCommandData is LogicCommand logicCmd)
                    {
                        command = logicCmd;
                        System.Diagnostics.Debug.WriteLine($"直接获取到 LogicCommand: {logicCmd.Name}");
                    }
                    else
                    {
                        command = ExtractCommandFromDragInfo(logicCommandData);
                    }
                }
                // 5. 处理插件ID格式
                else if (e.Data.GetDataPresent("PluginId"))
                {
                    var pluginIdData = e.Data.GetData("PluginId");
                    if (pluginIdData != null)
                    {
                        var recreatedCommand = TryRecreateLogicCommand(pluginIdData.ToString());
                        if (recreatedCommand != null)
                        {
                            command = recreatedCommand;
                            System.Diagnostics.Debug.WriteLine($"通过 PluginId 重新创建了 LogicCommand");
                        }
                    }
                }

                if (command != null)
                {
                    System.Diagnostics.Debug.WriteLine($"准备添加命令: {command.GetType().Name}");
                    AddCommandToBranch(command, isTrueBranch);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("未找到有效的命令数据");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"处理命令拖拽失败: {ex.Message}");
            }
        }

        private object ExtractCommandFromDragInfo(object commandDragInfo)
        {
            try
            {
                if (commandDragInfo == null) return null;

                if (commandDragInfo is CommandDragInfo dragInfo)
                {
                    System.Diagnostics.Debug.WriteLine($"从 CommandDragInfo 中成功提取 DeviceCommand: {dragInfo.Command?.GetType().Name ?? "null"}");
                    return dragInfo.Command;
                }

                var type = commandDragInfo.GetType();
                if (type.Name == "CommandDragInfo")
                {
                    var commandProperty = type.GetProperty("Command");
                    if (commandProperty != null)
                    {
                        var command = commandProperty.GetValue(commandDragInfo);
                        if (command is DeviceCommand deviceCmd)
                        {
                            System.Diagnostics.Debug.WriteLine($"通过反射从 CommandDragInfo 中提取 DeviceCommand: {deviceCmd.Name}");
                            return deviceCmd;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"从 CommandDragInfo 提取命令失败: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"重新创建LogicCommand失败: {ex.Message}");
                return null;
            }
        }

        private void AddCommandToBranch(object command, bool isTrueBranch)
        {
            try
            {
                CommandNode nestedNode = null;

                if (command is DeviceCommand deviceCmd)
                {
                    nestedNode = new CommandNode(deviceCmd);
                }
                else if (command is LogicCommand logicCmd)
                {
                    nestedNode = new CommandNode(logicCmd);
                }

                if (nestedNode != null && DataContext is CommandNode ifNode)
                {
                    // 使用统一的Children集合
                    ifNode.AddChildToBranch(nestedNode, isTrueBranch);

                    // UI管理仍然使用内部集合来优化性能
                    var nestedControl = CreateNestedCommandControl(nestedNode);

                    if (isTrueBranch)
                    {
                        _trueBranchCommands.Add(nestedNode); // 用于UI快速访问
                        TrueBranchContent.Children.Add(nestedControl);
                        TrueHint.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        _falseBranchCommands.Add(nestedNode);
                        FalseBranchContent.Children.Add(nestedControl);
                        FalseHint.Visibility = Visibility.Collapsed;
                    }

                    // 触发嵌套命令添加事件
                    NestedCommandAdded?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = nestedNode,
                        IsTrueBranch = isTrueBranch,
                        ParentIfNode = ifNode
                    });

                    UpdateBranchCanvasHeight();
                    OnBranchContentChanged();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"添加嵌套命令失败: {ex.Message}");
            }
        }

        #endregion

        #region UI控件创建

        private UserControl CreateNestedCommandControl(CommandNode nestedNode)
        {
            UserControl result;

            if (nestedNode.IsDeviceCommand)
            {
                result = CreateDeviceCommandControl(nestedNode);
            }
            else if (IsIfLogicCommand(nestedNode))
            {
                result = CreateNestedIfNodeControl(nestedNode);
            }
            else if (IsParallelLogicCommand(nestedNode))
            {
                result = CreateNestedParallelNodeControl(nestedNode);
            }
            else if (IsLoopLogicCommand(nestedNode))
            {
                result = CreateNestedLoopNodeControl(nestedNode);
            }
            else
            {
                result = CreateLogicCommandControl(nestedNode);
            }

            return result;
        }

        private bool IsParallelLogicCommand(CommandNode node)
        {
            return node.LogicCommand != null &&
                   node.LogicCommand.CommandType.ToString().Equals("Parallel", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsIfLogicCommand(CommandNode node)
        {
            return node.LogicCommand != null &&
                   node.LogicCommand.CommandType.ToString().Equals("If", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsLoopLogicCommand(CommandNode node)
        {
            return node.LogicCommand != null &&
                   (node.LogicCommand.CommandType.ToString().Equals("Loop", StringComparison.OrdinalIgnoreCase) ||
                    node.LogicCommand.CommandType.ToString().Equals("For", StringComparison.OrdinalIgnoreCase) ||
                    node.LogicCommand.CommandType.ToString().Equals("While", StringComparison.OrdinalIgnoreCase));
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

                SubscribeToNestedParallelEvents(nestedParallelControl, nestedNode);
                System.Diagnostics.Debug.WriteLine($"创建了嵌套并行节点控件: {nestedNode.DisplayName}");
                return nestedParallelControl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"创建嵌套并行节点控件失败: {ex.Message}");
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

                SubscribeToNestedLoopEvents(nestedLoopControl, nestedNode);
                System.Diagnostics.Debug.WriteLine($"创建了嵌套循环节点控件: {nestedNode.DisplayName}");
                return nestedLoopControl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"创建嵌套循环节点控件失败: {ex.Message}");
                return CreateLogicCommandControl(nestedNode);
            }
        }

        private void SubscribeToNestedParallelEvents(ParallelNodeControlView nestedParallelControl, CommandNode nestedNode)
        {
            try
            {
                nestedParallelControl.ContentChanged += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"嵌套并行节点内容发生变化: {nestedNode.DisplayName}");
                    OnBranchContentChanged();
                };

                nestedParallelControl.NestedCommandAdded += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"嵌套并行节点中添加了命令: {e.NestedCommand.DisplayName}");
                    NestedCommandAdded?.Invoke(this, e);
                };

                nestedParallelControl.NestedCommandRemoved += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"嵌套并行节点中移除了命令: {e.NestedCommand.DisplayName}");
                    NestedCommandRemoved?.Invoke(this, e);
                };

                nestedParallelControl.LogicCommandRequested += (s, e) =>
                {
                    LogicCommandRequested?.Invoke(this, e);
                };

                System.Diagnostics.Debug.WriteLine($"为嵌套并行节点 {nestedNode.DisplayName} 订阅了所有事件");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"订阅嵌套并行节点事件失败: {ex.Message}");
            }
        }

        private void SubscribeToNestedLoopEvents(LoopNodeControlView nestedLoopControl, CommandNode nestedNode)
        {
            try
            {
                nestedLoopControl.ContentChanged += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"嵌套循环节点内容发生变化: {nestedNode.DisplayName}");
                    OnBranchContentChanged();
                };

                nestedLoopControl.NestedCommandAdded += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"嵌套循环节点中添加了命令: {e.NestedCommand.DisplayName}");
                    NestedCommandAdded?.Invoke(this, e);
                };

                nestedLoopControl.NestedCommandRemoved += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"嵌套循环节点中移除了命令: {e.NestedCommand.DisplayName}");
                    NestedCommandRemoved?.Invoke(this, e);
                };

                nestedLoopControl.LogicCommandRequested += (s, e) =>
                {
                    LogicCommandRequested?.Invoke(this, e);
                };

                System.Diagnostics.Debug.WriteLine($"为嵌套循环节点 {nestedNode.DisplayName} 订阅了所有事件");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"订阅嵌套循环节点事件失败: {ex.Message}");
            }
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

                SubscribeToNestedIfEvents(nestedIfControl, nestedNode);
                System.Diagnostics.Debug.WriteLine($"创建了嵌套IF节点控件: {nestedNode.DisplayName}");
                return nestedIfControl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"创建嵌套IF节点控件失败: {ex.Message}");
                return CreateLogicCommandControl(nestedNode);
            }
        }

        private void SubscribeToNestedIfEvents(IfNodeControlView nestedIfControl, CommandNode nestedNode)
        {
            try
            {
                nestedIfControl.ContentChanged += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"嵌套IF节点内容发生变化: {nestedNode.DisplayName}");
                    OnBranchContentChanged();
                };

                nestedIfControl.NestedCommandAdded += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"嵌套IF节点中添加了命令: {e.NestedCommand.DisplayName}");
                    NestedCommandAdded?.Invoke(this, e);
                };

                nestedIfControl.NestedCommandRemoved += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"嵌套IF节点中移除了命令: {e.NestedCommand.DisplayName}");
                    NestedCommandRemoved?.Invoke(this, e);
                };

                nestedIfControl.LogicCommandRequested += (s, e) =>
                {
                    LogicCommandRequested?.Invoke(this, e);
                };

                System.Diagnostics.Debug.WriteLine($"为嵌套IF节点 {nestedNode.DisplayName} 订阅了所有事件");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"订阅嵌套IF节点事件失败: {ex.Message}");
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
                    RemoveNestedCommand(node);
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
                    RemoveNestedCommand(node);
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

        #endregion

        #region 命令管理

        public void RemoveNestedCommand(CommandNode nestedNode)
        {
            try
            {
                if (DataContext is not CommandNode ifNode) return;

                bool isTrueBranch = _trueBranchCommands.Contains(nestedNode);
                bool removed = false;

                // 从统一的Children集合中移除
                ifNode.RemoveChild(nestedNode);

                if (isTrueBranch)
                {
                    _trueBranchCommands.Remove(nestedNode);
                    var controlToRemove = TrueBranchContent.Children
                        .OfType<UserControl>()
                        .FirstOrDefault(c => c.Tag == nestedNode);
                    if (controlToRemove != null)
                    {
                        TrueBranchContent.Children.Remove(controlToRemove);
                        removed = true;
                    }
                    TrueHint.Visibility = TrueBranchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (_falseBranchCommands.Contains(nestedNode))
                {
                    _falseBranchCommands.Remove(nestedNode);
                    var controlToRemove = FalseBranchContent.Children
                        .OfType<UserControl>()
                        .FirstOrDefault(c => c.Tag == nestedNode);
                    if (controlToRemove != null)
                    {
                        FalseBranchContent.Children.Remove(controlToRemove);
                        removed = true;
                    }
                    FalseHint.Visibility = FalseBranchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }

                if (removed)
                {
                    NestedCommandRemoved?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = nestedNode,
                        IsTrueBranch = isTrueBranch,
                        ParentIfNode = ifNode
                    });

                    UpdateBranchCanvasHeight();
                    OnBranchContentChanged();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"移除嵌套命令失败: {ex.Message}");
            }
        }

        public void ClearAllBranches()
        {
            try
            {
                if (DataContext is CommandNode ifNode)
                {
                    ifNode.ClearChildren();
                }

                _trueBranchCommands.Clear();
                _falseBranchCommands.Clear();
                TrueBranchContent.Children.Clear();
                FalseBranchContent.Children.Clear();
                TrueHint.Visibility = Visibility.Visible;
                FalseHint.Visibility = Visibility.Visible;

                UpdateBranchCanvasHeight();
                OnBranchContentChanged();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清理所有分支失败: {ex.Message}");
            }
        }

        public IReadOnlyList<CommandNode> GetTrueBranchCommands() => _trueBranchCommands.AsReadOnly();
        public IReadOnlyList<CommandNode> GetFalseBranchCommands() => _falseBranchCommands.AsReadOnly();
        public int GetTrueBranchCount() => _trueBranchCommands.Count;
        public int GetFalseBranchCount() => _falseBranchCommands.Count;

        #endregion

        #region 布局更新

        private void UpdateBranchCanvasHeight()
        {
            try
            {
                TrueBranchWrapper.UpdateLayout();
                FalseBranchWrapper.UpdateLayout();

                var trueBranchSize = CalculateBranchContentSize(TrueBranchContent);
                var falseBranchSize = CalculateBranchContentSize(FalseBranchContent);

                System.Diagnostics.Debug.WriteLine($"TRUE分支尺寸: {trueBranchSize.Width}x{trueBranchSize.Height}");
                System.Diagnostics.Debug.WriteLine($"FALSE分支尺寸: {falseBranchSize.Width}x{falseBranchSize.Height}");

                AdjustBranchContainers(trueBranchSize, falseBranchSize);
                UpdateCanvasSizeBasedOnBranches(trueBranchSize, falseBranchSize);

                System.Diagnostics.Debug.WriteLine($"Canvas尺寸更新完成: {BranchCanvas.Width}x{BranchCanvas.Height}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新分支布局失败: {ex.Message}");
                BranchCanvas.Width = 600;
                BranchCanvas.Height = 200;
            }
        }

        private Size CalculateBranchContentSize(StackPanel branchContent)
        {
            double maxWidth = 200;
            double totalHeight = 0;

            foreach (UIElement child in branchContent.Children)
            {
                try
                {
                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var childSize = child.DesiredSize;
                    maxWidth = Math.Max(maxWidth, childSize.Width);
                    totalHeight += childSize.Height;

                    if (child is IfNodeControlView)
                    {
                        maxWidth = Math.Max(maxWidth, 620);
                    }
                    else if (child is ParallelNodeControlView)
                    {
                        maxWidth = Math.Max(maxWidth, 1020);
                    }
                    else if (child is LoopNodeControlView)
                    {
                        maxWidth = Math.Max(maxWidth, 320);
                    }

                    System.Diagnostics.Debug.WriteLine($"  子元素尺寸: {childSize.Width}x{childSize.Height}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"测量子元素失败: {ex.Message}");
                    totalHeight += 40;
                }
            }

            totalHeight += Math.Max(0, branchContent.Children.Count - 1) * 8;
            totalHeight += 16;

            return new Size(maxWidth, Math.Max(60, totalHeight));
        }

        private void AdjustBranchContainers(Size trueBranchSize, Size falseBranchSize)
        {
            try
            {
                double mainCardCenterX = CalculateMainCardCenterX();

                const double FALSE_BOUNDARY_OFFSET = -50;
                const double TRUE_BOUNDARY_OFFSET = 50;

                double FALSE_FIXED_RIGHT_BOUNDARY = mainCardCenterX + FALSE_BOUNDARY_OFFSET;
                double TRUE_FIXED_LEFT_BOUNDARY = mainCardCenterX + TRUE_BOUNDARY_OFFSET;

                double trueBranchWidth = Math.Max(200, trueBranchSize.Width + 20);
                double falseBranchWidth = Math.Max(200, falseBranchSize.Width + 20);

                double falseRight = FALSE_FIXED_RIGHT_BOUNDARY;
                double falseLeft = falseRight - falseBranchWidth;

                double trueLeft = TRUE_FIXED_LEFT_BOUNDARY;

                Canvas.SetLeft(FalseBranchWrapper, falseLeft);
                Canvas.SetLeft(TrueBranchWrapper, trueLeft);

                FalseBranchWrapper.Width = falseBranchWidth;
                TrueBranchWrapper.Width = trueBranchWidth;
                FalseBranchWrapper.MinWidth = falseBranchWidth;
                TrueBranchWrapper.MinWidth = trueBranchWidth;

                double falseCenterX = falseLeft + falseBranchWidth / 2;
                double trueCenterX = trueLeft + trueBranchWidth / 2;

                UpdateConnectionLines(falseCenterX, trueCenterX);

                System.Diagnostics.Debug.WriteLine($"分支中心 - FALSE: {falseCenterX}, TRUE: {trueCenterX}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"调整分支容器失败: {ex.Message}");
            }
        }

        private void UpdateConnectionLines(double falseCenterX, double trueCenterX)
        {
            try
            {
                double mainCardCenterX = CalculateMainCardCenterX();

                const double FALSE_OFFSET = -150;
                const double TRUE_OFFSET = 150;

                double falseConnectionX = mainCardCenterX + FALSE_OFFSET;
                double trueConnectionX = mainCardCenterX + TRUE_OFFSET;

                MainVerticalLine.X1 = mainCardCenterX;
                MainVerticalLine.X2 = mainCardCenterX;

                LeftHorizontalLine.X1 = falseConnectionX;
                LeftHorizontalLine.X2 = mainCardCenterX;

                RightHorizontalLine.X1 = mainCardCenterX;
                RightHorizontalLine.X2 = trueConnectionX;

                FalseVerticalLine.X1 = falseConnectionX;
                FalseVerticalLine.X2 = falseConnectionX;

                TrueVerticalLine.X1 = trueConnectionX;
                TrueVerticalLine.X2 = trueConnectionX;

                Canvas.SetLeft(FalseArrow, falseConnectionX - 4);
                Canvas.SetLeft(TrueArrow, trueConnectionX - 4);

                System.Diagnostics.Debug.WriteLine($"连接线更新: 主卡片中心({mainCardCenterX}), FALSE连接点({falseConnectionX}), TRUE连接点({trueConnectionX})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新连接线失败: {ex.Message}");
            }
        }

        private double CalculateMainCardCenterX()
        {
            try
            {
                double canvasWidth = BranchCanvas.ActualWidth > 0 ? BranchCanvas.ActualWidth : BranchCanvas.Width;
                double mainCardCenterX = canvasWidth / 2;

                System.Diagnostics.Debug.WriteLine($"Canvas宽度: {canvasWidth}, 主卡片中心: {mainCardCenterX}");
                return mainCardCenterX;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"计算主卡片中心失败: {ex.Message}");
                double defaultWidth = BranchCanvas.Width > 0 ? BranchCanvas.Width : 600;
                return defaultWidth / 2;
            }
        }

        private void UpdateCanvasSizeBasedOnBranches(Size trueBranchSize, Size falseBranchSize)
        {
            try
            {
                double trueBranchWidth = Math.Max(200, trueBranchSize.Width + 20);
                double falseBranchWidth = Math.Max(200, falseBranchSize.Width + 20);

                double maxBranchExtension = Math.Max(
                    falseBranchWidth - 200,
                    trueBranchWidth - 200
                );

                double canvasWidth = Math.Max(600, 600 + maxBranchExtension * 2);

                double maxBranchHeight = Math.Max(trueBranchSize.Height, falseBranchSize.Height);
                double canvasHeight = Math.Max(200, maxBranchHeight + 80);

                BranchCanvas.Width = canvasWidth;
                BranchCanvas.Height = canvasHeight;

                System.Diagnostics.Debug.WriteLine($"Canvas尺寸计算:");
                System.Diagnostics.Debug.WriteLine($"  FALSE分支宽度: {falseBranchWidth}, TRUE分支宽度: {trueBranchWidth}");
                System.Diagnostics.Debug.WriteLine($"  最大扩展: {maxBranchExtension}");
                System.Diagnostics.Debug.WriteLine($"  最终Canvas宽度: {canvasWidth}x{canvasHeight}");

                BranchCanvas.UpdateLayout();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新Canvas尺寸失败: {ex.Message}");
            }
        }

        public void OnBranchContentChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateBranchCanvasHeight();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    System.Diagnostics.Debug.WriteLine($"IF节点内容变化通知完成，当前控件尺寸: {this.ActualWidth}x{this.ActualHeight}");
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnBranchContentChanged 错误: {ex.Message}");
            }
        }

        #endregion

        private void Node_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && DataContext is CommandNode node)
            {
                var routedEventArgs = new RoutedEventArgs(NodeClickEvent, node);
                RaiseEvent(routedEventArgs);
                e.Handled = true;
            }
        }

        public static readonly RoutedEvent NodeClickEvent = EventManager.RegisterRoutedEvent(
            "NodeClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(IfNodeControlView));

        public event RoutedEventHandler NodeClick
        {
            add { AddHandler(NodeClickEvent, value); }
            remove { RemoveHandler(NodeClickEvent, value); }
        }
    }

    #region 事件参数类

    public partial class NestedCommandEventArgs : EventArgs
    {
        public CommandNode NestedCommand { get; set; }
        public bool IsTrueBranch { get; set; }
        public CommandNode ParentIfNode { get; set; }
        public int BranchIndex { get; set; } = -1;
    }

    public class LogicCommandRequestEventArgs : EventArgs
    {
        public string PluginId { get; set; }
        public LogicCommand ResultCommand { get; set; }
    }

    public class CommandDragInfo
    {
        public object Command { get; set; }
    }

    #endregion
}