using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MaxChemical.Modules.Designer.Models;
using DevicePlugins.Devices;
using MaxChemical.DataModule.Model;
using System.Windows.Media.Imaging;
using System.Diagnostics;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// ParallelNodeControlView.xaml 的交互逻辑
    /// </summary>
    public partial class ParallelNodeControlView : UserControl, IDisposable
    {
        public event EventHandler ContentChanged;
        public event EventHandler<NestedCommandEventArgs> NestedCommandAdded;
        public event EventHandler<NestedCommandEventArgs> NestedCommandRemoved;
        public event EventHandler<LogicCommandRequestEventArgs> LogicCommandRequested;

        private Size _lastMeasuredSize;
        // 保留内部集合用于UI快速访问
        private readonly List<ParallelBranchInfo> _parallelBranches = new List<ParallelBranchInfo>();
        private int _currentMaxParallelCount = 0;

        public ParallelNodeControlView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializeParallelBranches();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize != _lastMeasuredSize)
            {
                _lastMeasuredSize = e.NewSize;
                UpdateBranchCanvasLayout();
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is CommandNode node && node.LogicCommand != null)
            {
                var maxParallelCount = GetMaxParallelCount(node);
                if (maxParallelCount != _currentMaxParallelCount)
                {
                    _currentMaxParallelCount = maxParallelCount;
                    InitializeParallelBranches();
                }

                // 同步Children到UI
                SyncChildrenToUI();
            }
        }

        private int GetMaxParallelCount(CommandNode node)
        {
            try
            {
                if (node.LogicCommand?.Properties != null &&
                    node.LogicCommand.Properties.TryGetValue("MaxParallelCount", out var value))
                {
                    if (value is int intValue)
                        return intValue;
                    if (int.TryParse(value.ToString(), out var parsedValue))
                        return parsedValue;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取MaxParallelCount失败: {ex.Message}");
            }
            return 4; // 默认值
        }

        private void SyncChildrenToUI()
        {
            if (DataContext is not CommandNode parallelNode) return;

            try
            {
                // 清空所有分支的UI
                foreach (var branchInfo in _parallelBranches)
                {
                    var branchContent = FindBranchContentContainer(branchInfo.Index);
                    if (branchContent != null)
                    {
                        branchContent.Children.Clear();
                        branchInfo.Commands.Clear();
                    }
                }

                // 从Children集合重建UI
                foreach (var child in parallelNode.Children)
                {
                    var branchIndex = child.GetExecutionProperty<int>("BranchIndex");
                    if (branchIndex >= 0 && branchIndex < _parallelBranches.Count)
                    {
                        var branchInfo = _parallelBranches[branchIndex];
                        branchInfo.Commands.Add(child);

                        var nestedControl = CreateNestedCommandControl(child, branchIndex);
                        var branchContent = FindBranchContentContainer(branchIndex);
                        if (branchContent != null)
                        {
                            branchContent.Children.Add(nestedControl);

                            var hint = FindBranchHint(branchIndex);
                            if (hint != null)
                            {
                                hint.Visibility = Visibility.Collapsed;
                            }
                        }
                    }
                }

                UpdateBranchCanvasLayout();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"同步并行节点Children到UI失败: {ex.Message}");
            }
        }

        #region 并行分支管理

        private void InitializeParallelBranches()
        {
            try
            {
                if (DataContext is not CommandNode node || node.LogicCommand == null)
                    return;

                var maxParallelCount = GetMaxParallelCount(node);

                ClearAllBranches();

                for (int i = 0; i < maxParallelCount; i++)
                {
                    CreateParallelBranch(i);
                }

                UpdateBranchCanvasLayout();
                System.Diagnostics.Debug.WriteLine($"创建了 {maxParallelCount} 个并行分支");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化并行分支失败: {ex.Message}");
            }
        }

        private void CreateParallelBranch(int branchIndex)
        {
            var branchInfo = new ParallelBranchInfo
            {
                Index = branchIndex,
                Commands = new List<CommandNode>()
            };

            var branchContainer = CreateBranchContainer(branchIndex);
            branchInfo.Container = branchContainer;

            ParallelBranchesContainer.Children.Add(branchContainer);
            _parallelBranches.Add(branchInfo);

            CreateBranchConnectionElements(branchIndex, branchContainer);
        }

        private Border CreateBranchContainer(int branchIndex)
        {
            Debug.WriteLine($"=== 创建分支容器 {branchIndex} ===");

            var branchWrapper = new Border
            {
                MinWidth = 250,
                Margin = new Thickness(10, 0, 10, 0)
            };

            var stackPanel = new StackPanel
            {
                MinWidth = 250
            };

            var branchLabel = new TextBlock
            {
                Text = $"分支 {branchIndex + 1}",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var dropZone = new Border
            {
                Name = $"BranchDropZone_{branchIndex}",
                Background = new SolidColorBrush(Color.FromArgb(0x08, 0x60, 0x7D, 0x8B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0x60, 0x7D, 0x8B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                MinHeight = 60,
                MinWidth = 250,
                AllowDrop = true,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                Margin = new Thickness(0, 4, 0, 0),
                Tag = branchIndex
            };

            Debug.WriteLine($"创建拖放区域，AllowDrop: {dropZone.AllowDrop}, Tag: {dropZone.Tag}");

            dropZone.DragEnter += Branch_DragEnter;
            dropZone.DragOver += Branch_DragOver;
            dropZone.DragLeave += Branch_DragLeave;
            dropZone.Drop += Branch_Drop;

            Debug.WriteLine($"已订阅拖放事件");

            var dropZoneGrid = new Grid
            {
                UseLayoutRounding = true
            };

            var hint = new TextBlock
            {
                Name = $"BranchHint_{branchIndex}",
                Text = "拖拽步骤到此处",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0x60, 0x7D, 0x8B)),
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                FontFamily = new FontFamily("Segoe UI"),
                UseLayoutRounding = true
            };

            var branchContent = new StackPanel
            {
                Name = $"BranchContent_{branchIndex}",
                UseLayoutRounding = true
            };

            dropZoneGrid.Children.Add(hint);
            dropZoneGrid.Children.Add(branchContent);
            dropZone.Child = dropZoneGrid;

            stackPanel.Children.Add(branchLabel);
            stackPanel.Children.Add(dropZone);
            branchWrapper.Child = stackPanel;

            Debug.WriteLine($"=== 分支容器 {branchIndex} 创建完成 ===");
            return branchWrapper;
        }

        private void CreateBranchConnectionElements(int branchIndex, Border branchContainer)
        {
            double branchCenterX = 150 + (branchIndex * 200);

            var verticalLine = new Line
            {
                Name = $"BranchVerticalLine_{branchIndex}",
                X1 = branchCenterX,
                Y1 = 24,
                X2 = branchCenterX,
                Y2 = 36,
                Stroke = new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B)),
                StrokeThickness = 2
            };

            var arrow = new Polygon
            {
                Name = $"BranchArrow_{branchIndex}",
                Fill = new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B)),
                Points = new PointCollection(new[]
                {
                    new Point(0, 0),
                    new Point(8, 0),
                    new Point(4, 8)
                })
            };

            Canvas.SetLeft(arrow, branchCenterX - 4);
            Canvas.SetTop(arrow, 32);

            BranchCanvas.Children.Add(verticalLine);
            BranchCanvas.Children.Add(arrow);
        }

        #endregion

        #region 拖拽事件处理

        private void Branch_DragEnter(object sender, DragEventArgs e)
        {
            Debug.WriteLine("=== Branch_DragEnter 被调用 ===");
            Debug.WriteLine($"Sender: {sender?.GetType().Name}");

            if (sender is Border border)
            {
                Debug.WriteLine($"Border Tag: {border.Tag}");
            }

            bool canAccept = CanAcceptDrop(e.Data);
            Debug.WriteLine($"CanAcceptDrop 结果: {canAccept}");

            if (canAccept)
            {
                if (sender is Border borderSender)
                {
                    borderSender.Uid = "DragOver";
                    Debug.WriteLine("设置拖放悬停状态");

                    borderSender.Background = new SolidColorBrush(Color.FromArgb(0x20, 0x60, 0x7D, 0x8B));
                    borderSender.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x60, 0x7D, 0x8B));
                }
                e.Effects = DragDropEffects.Copy;
                Debug.WriteLine("设置拖放效果为 Copy");
            }
            else
            {
                e.Effects = DragDropEffects.None;
                Debug.WriteLine("拒绝拖放");
            }
            e.Handled = true;
            Debug.WriteLine("=== Branch_DragEnter 处理完成 ===");
        }

        private void Branch_DragOver(object sender, DragEventArgs e)
        {
            bool canAccept = CanAcceptDrop(e.Data);
            e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Branch_DragLeave(object sender, DragEventArgs e)
        {
            Debug.WriteLine("Branch_DragLeave 被调用");
            if (sender is Border border)
            {
                border.Uid = null;
                Debug.WriteLine("清除拖放悬停状态");

                border.Background = new SolidColorBrush(Color.FromArgb(0x08, 0x60, 0x7D, 0x8B));
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0x60, 0x7D, 0x8B));
            }
            e.Handled = true;
        }

        private void Branch_Drop(object sender, DragEventArgs e)
        {
            Debug.WriteLine("=== Branch_Drop 被调用 ===");
            Debug.WriteLine($"Sender: {sender?.GetType().Name}");

            if (sender is Border border)
            {
                border.Uid = null;
                Debug.WriteLine("清除拖放状态");

                border.Background = new SolidColorBrush(Color.FromArgb(0x08, 0x60, 0x7D, 0x8B));
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0x60, 0x7D, 0x8B));

                Debug.WriteLine($"Border Tag 类型: {border.Tag?.GetType().Name}");
                Debug.WriteLine($"Border Tag 值: {border.Tag}");

                if (border.Tag is int branchIndex)
                {
                    Debug.WriteLine($"分支索引: {branchIndex}");
                    HandleCommandDrop(e, branchIndex);
                }
                else
                {
                    Debug.WriteLine($"Border.Tag 不是 int 类型: {border.Tag?.GetType().Name ?? "null"}，值: {border.Tag}");
                }
            }
            else
            {
                Debug.WriteLine("Sender 不是 Border 类型");
            }
            e.Handled = true;
            Debug.WriteLine("=== Branch_Drop 处理完成 ===");
        }

        private bool CanAcceptDrop(IDataObject data)
        {
            try
            {
                Debug.WriteLine("=== CanAcceptDrop 检查开始 ===");

                var formats = data.GetFormats();
                Debug.WriteLine($"数据格式数量: {formats.Length}");

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
                Debug.WriteLine($"CanAcceptDrop 异常: {ex.Message}");
                return false;
            }
        }

        private void HandleCommandDrop(DragEventArgs e, int branchIndex)
        {
            try
            {
                Debug.WriteLine($"=== 处理分支 {branchIndex} 的命令拖放 ===");

                var command = ExtractCommandFromDragData(e.Data);
                if (command != null)
                {
                    Debug.WriteLine($"提取到命令: {command.GetType().Name}");
                    AddCommandToBranch(command, branchIndex);
                }
                else
                {
                    Debug.WriteLine("未能提取到有效命令");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理命令拖拽失败: {ex.Message}");
            }
        }

        private object ExtractCommandFromDragData(IDataObject data)
        {
            try
            {
                if (data.GetDataPresent("CommandDragInfo"))
                {
                    var commandDragInfo = data.GetData("CommandDragInfo");
                    var command = ExtractCommandFromDragInfo(commandDragInfo);
                    if (command != null) return command;
                }

                if (data.GetDataPresent("DeviceCommand"))
                {
                    var deviceCommandData = data.GetData("DeviceCommand");
                    if (deviceCommandData is DeviceCommand deviceCmd)
                        return deviceCmd;
                }

                if (data.GetDataPresent(typeof(DeviceCommand)))
                {
                    var typeCommand = data.GetData(typeof(DeviceCommand));
                    if (typeCommand is DeviceCommand typeDeviceCmd)
                        return typeDeviceCmd;
                }

                if (data.GetDataPresent("LogicCommand"))
                {
                    var logicCommandData = data.GetData("LogicCommand");
                    if (logicCommandData is LogicCommand logicCmd)
                        return logicCmd;
                    else
                        return ExtractCommandFromDragInfo(logicCommandData);
                }

                if (data.GetDataPresent("PluginId"))
                {
                    var pluginIdData = data.GetData("PluginId");
                    if (pluginIdData != null)
                    {
                        return TryRecreateLogicCommand(pluginIdData.ToString());
                    }
                }

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

        #endregion

        #region 命令管理

        private void AddCommandToBranch(object command, int branchIndex)
        {
            try
            {
                if (branchIndex < 0 || branchIndex >= _parallelBranches.Count)
                    return;

                CommandNode nestedNode = null;

                if (command is DeviceCommand deviceCmd)
                {
                    nestedNode = new CommandNode(deviceCmd);
                }
                else if (command is LogicCommand logicCmd)
                {
                    nestedNode = new CommandNode(logicCmd);
                }

                if (nestedNode != null && DataContext is CommandNode parallelNode)
                {
                    // 使用统一的Children集合
                    parallelNode.AddChildToParallelBranch(nestedNode, branchIndex);

                    // UI管理仍然使用内部集合
                    var branchInfo = _parallelBranches[branchIndex];
                    branchInfo.Commands.Add(nestedNode);

                    var nestedControl = CreateNestedCommandControl(nestedNode, branchIndex);
                    var branchContent = FindBranchContentContainer(branchIndex);
                    if (branchContent != null)
                    {
                        branchContent.Children.Add(nestedControl);

                        var hint = FindBranchHint(branchIndex);
                        if (hint != null)
                        {
                            hint.Visibility = Visibility.Collapsed;
                        }
                    }

                    NestedCommandAdded?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = nestedNode,
                        IsTrueBranch = false,
                        ParentIfNode = parallelNode,
                        BranchIndex = branchIndex
                    });

                    UpdateBranchCanvasLayout();
                    OnBranchContentChanged();

                    System.Diagnostics.Debug.WriteLine($"添加命令到并行分支 {branchIndex}: {nestedNode.DisplayName}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"添加嵌套命令失败: {ex.Message}");
            }
        }

        private UserControl CreateNestedCommandControl(CommandNode nestedNode, int branchIndex)
        {
            if (nestedNode.IsDeviceCommand)
            {
                return CreateDeviceCommandControl(nestedNode, branchIndex);
            }
            else if (IsIfLogicCommand(nestedNode))
            {
                return CreateNestedIfNodeControl(nestedNode, branchIndex);
            }
            else if (IsParallelLogicCommand(nestedNode))
            {
                return CreateNestedParallelNodeControl(nestedNode, branchIndex);
            }
            else if (IsLoopLogicCommand(nestedNode))
            {
                return CreateNestedLoopNodeControl(nestedNode, branchIndex);
            }
            else
            {
                return CreateLogicCommandControl(nestedNode, branchIndex);
            }
        }

        private bool IsIfLogicCommand(CommandNode node)
        {
            return node.LogicCommand != null &&
                   node.LogicCommand.CommandType.ToString().Equals("If", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsParallelLogicCommand(CommandNode node)
        {
            return node.LogicCommand != null &&
                   node.LogicCommand.CommandType.ToString().Equals("Parallel", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsLoopLogicCommand(CommandNode node)
        {
            return node.LogicCommand != null &&
                   (node.LogicCommand.CommandType.ToString().Equals("Loop", StringComparison.OrdinalIgnoreCase) ||
                    node.LogicCommand.CommandType.ToString().Equals("For", StringComparison.OrdinalIgnoreCase) ||
                    node.LogicCommand.CommandType.ToString().Equals("While", StringComparison.OrdinalIgnoreCase));
        }

        private UserControl CreateNestedIfNodeControl(CommandNode nestedNode, int branchIndex)
        {
            try
            {
                var nestedIfControl = new IfNodeControlView
                {
                    DataContext = nestedNode,
                    Tag = new NestedNodeInfo { Node = nestedNode, BranchIndex = branchIndex },
                    Margin = new Thickness(0, 4, 0, 4)
                };

                SubscribeToNestedNodeEvents(nestedIfControl, nestedNode, branchIndex);
                return nestedIfControl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"创建嵌套IF节点控件失败: {ex.Message}");
                return CreateLogicCommandControl(nestedNode, branchIndex);
            }
        }

        private UserControl CreateNestedParallelNodeControl(CommandNode nestedNode, int branchIndex)
        {
            try
            {
                var nestedParallelControl = new ParallelNodeControlView
                {
                    DataContext = nestedNode,
                    Tag = new NestedNodeInfo { Node = nestedNode, BranchIndex = branchIndex },
                    Margin = new Thickness(0, 4, 0, 4)
                };

                SubscribeToNestedNodeEvents(nestedParallelControl, nestedNode, branchIndex);
                return nestedParallelControl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"创建嵌套并行节点控件失败: {ex.Message}");
                return CreateLogicCommandControl(nestedNode, branchIndex);
            }
        }

        private UserControl CreateNestedLoopNodeControl(CommandNode nestedNode, int branchIndex)
        {
            try
            {
                var nestedLoopControl = new LoopNodeControlView
                {
                    DataContext = nestedNode,
                    Tag = new NestedNodeInfo { Node = nestedNode, BranchIndex = branchIndex },
                    Margin = new Thickness(0, 4, 0, 4)
                };

                SubscribeToNestedNodeEvents(nestedLoopControl, nestedNode, branchIndex);
                return nestedLoopControl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"创建嵌套循环节点控件失败: {ex.Message}");
                return CreateLogicCommandControl(nestedNode, branchIndex);
            }
        }

        private void SubscribeToNestedNodeEvents(UserControl nestedControl, CommandNode nestedNode, int branchIndex)
        {
            try
            {
                if (nestedControl is IfNodeControlView ifControl)
                {
                    ifControl.ContentChanged += (s, e) => OnBranchContentChanged();
                    ifControl.NestedCommandAdded += (s, e) => NestedCommandAdded?.Invoke(this, e);
                    ifControl.NestedCommandRemoved += (s, e) => NestedCommandRemoved?.Invoke(this, e);
                    ifControl.LogicCommandRequested += (s, e) => LogicCommandRequested?.Invoke(this, e);
                }
                else if (nestedControl is ParallelNodeControlView parallelControl)
                {
                    parallelControl.ContentChanged += (s, e) => OnBranchContentChanged();
                    parallelControl.NestedCommandAdded += (s, e) => NestedCommandAdded?.Invoke(this, e);
                    parallelControl.NestedCommandRemoved += (s, e) => NestedCommandRemoved?.Invoke(this, e);
                    parallelControl.LogicCommandRequested += (s, e) => LogicCommandRequested?.Invoke(this, e);
                }
                else if (nestedControl is LoopNodeControlView loopControl)
                {
                    loopControl.ContentChanged += (s, e) => OnBranchContentChanged();
                    loopControl.NestedCommandAdded += (s, e) => NestedCommandAdded?.Invoke(this, e);
                    loopControl.NestedCommandRemoved += (s, e) => NestedCommandRemoved?.Invoke(this, e);
                    loopControl.LogicCommandRequested += (s, e) => LogicCommandRequested?.Invoke(this, e);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"订阅嵌套节点事件失败: {ex.Message}");
            }
        }

        private UserControl CreateDeviceCommandControl(CommandNode nestedNode, int branchIndex)
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
                    AddDefaultIconToGrid(iconGrid, 26);
                }
            }
            else
            {
                AddDefaultIconToGrid(iconGrid, 26);
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
                Tag = new NestedNodeInfo { Node = nestedNode, BranchIndex = branchIndex }
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
                if (s is Border borderSender && borderSender.Tag is NestedNodeInfo info)
                {
                    RemoveNestedCommand(info.Node, info.BranchIndex);
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
                Tag = new NestedNodeInfo { Node = nestedNode, BranchIndex = branchIndex }
            };
        }

        private UserControl CreateLogicCommandControl(CommandNode nestedNode, int branchIndex)
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
                Tag = new NestedNodeInfo { Node = nestedNode, BranchIndex = branchIndex }
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
                if (s is Border borderSender && borderSender.Tag is NestedNodeInfo info)
                {
                    RemoveNestedCommand(info.Node, info.BranchIndex);
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
                Tag = new NestedNodeInfo { Node = nestedNode, BranchIndex = branchIndex }
            };
        }

        private void AddDefaultIconToGrid(Grid iconGrid, double size)
        {
            var iconBorder = new Border
            {
                Width = size,
                Height = size,
                Background = new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF)),
                CornerRadius = new CornerRadius(size / 2)
            };

            var iconText = new TextBlock
            {
                Text = "🔧",
                FontSize = size * 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White)
            };

            iconBorder.Child = iconText;
            iconGrid.Children.Add(iconBorder);
        }

        public void RemoveNestedCommand(CommandNode nestedNode, int branchIndex)
        {
            try
            {
                if (branchIndex < 0 || branchIndex >= _parallelBranches.Count)
                    return;

                if (DataContext is not CommandNode parallelNode) return;

                var branchInfo = _parallelBranches[branchIndex];
                if (!branchInfo.Commands.Contains(nestedNode))
                    return;

                // 从统一的Children集合中移除
                parallelNode.RemoveChild(nestedNode);

                branchInfo.Commands.Remove(nestedNode);

                var branchContent = FindBranchContentContainer(branchIndex);
                if (branchContent != null)
                {
                    var controlToRemove = branchContent.Children
                        .OfType<UserControl>()
                        .FirstOrDefault(c => c.Tag is NestedNodeInfo info && info.Node == nestedNode);

                    if (controlToRemove != null)
                    {
                        branchContent.Children.Remove(controlToRemove);
                    }

                    var hint = FindBranchHint(branchIndex);
                    if (hint != null)
                    {
                        hint.Visibility = branchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    }
                }

                NestedCommandRemoved?.Invoke(this, new NestedCommandEventArgs
                {
                    NestedCommand = nestedNode,
                    IsTrueBranch = false,
                    ParentIfNode = parallelNode,
                    BranchIndex = branchIndex
                });

                UpdateBranchCanvasLayout();
                OnBranchContentChanged();

                System.Diagnostics.Debug.WriteLine($"从并行分支 {branchIndex} 移除命令: {nestedNode.DisplayName}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"移除嵌套命令失败: {ex.Message}");
            }
        }

        #endregion

        #region 辅助方法

        private StackPanel FindBranchContentContainer(int branchIndex)
        {
            try
            {
                if (branchIndex < 0 || branchIndex >= _parallelBranches.Count)
                    return null;

                var branchContainer = _parallelBranches[branchIndex].Container;
                return FindElementByName<StackPanel>(branchContainer, $"BranchContent_{branchIndex}");
            }
            catch
            {
                return null;
            }
        }

        private TextBlock FindBranchHint(int branchIndex)
        {
            try
            {
                if (branchIndex < 0 || branchIndex >= _parallelBranches.Count)
                    return null;

                var branchContainer = _parallelBranches[branchIndex].Container;
                return FindElementByName<TextBlock>(branchContainer, $"BranchHint_{branchIndex}");
            }
            catch
            {
                return null;
            }
        }

        private T FindElementByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent == null) return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T element && element.Name == name)
                {
                    return element;
                }

                var result = FindElementByName<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        #endregion

        #region 布局更新

        private void UpdateBranchCanvasLayout()
        {
            try
            {
                if (_parallelBranches.Count == 0) return;

                ParallelBranchesContainer.UpdateLayout();
                CalculateBranchPositions();
                UpdateCanvasSize();
                UpdateConnectionLines();

                System.Diagnostics.Debug.WriteLine($"并行分支布局更新完成，共 {_parallelBranches.Count} 个分支");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新并行分支布局失败: {ex.Message}");
            }
        }

        private void CalculateBranchPositions()
        {
            try
            {
                var totalWidth = 0.0;
                var maxHeight = 0.0;

                foreach (var branchInfo in _parallelBranches)
                {
                    branchInfo.Container.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var size = branchInfo.Container.DesiredSize;

                    totalWidth += size.Width + 20;
                    maxHeight = Math.Max(maxHeight, size.Height);
                }

                var canvasWidth = Math.Max(800, totalWidth + 100);
                var canvasHeight = Math.Max(200, maxHeight + 80);

                BranchCanvas.Width = canvasWidth;
                BranchCanvas.Height = canvasHeight;

                var containerLeft = (canvasWidth - totalWidth) / 2;
                Canvas.SetLeft(ParallelBranchesContainer, containerLeft);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"计算分支位置失败: {ex.Message}");
            }
        }

        private void UpdateCanvasSize()
        {
            try
            {
                ParallelBranchesContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var containerSize = ParallelBranchesContainer.DesiredSize;

                var canvasWidth = Math.Max(800, containerSize.Width + 200);
                var canvasHeight = Math.Max(200, containerSize.Height + 80);

                BranchCanvas.Width = canvasWidth;
                BranchCanvas.Height = canvasHeight;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新Canvas尺寸失败: {ex.Message}");
            }
        }

        private void UpdateConnectionLines()
        {
            try
            {
                var canvasCenterX = BranchCanvas.Width / 2;

                MainVerticalLine.X1 = canvasCenterX;
                MainVerticalLine.X2 = canvasCenterX;

                if (_parallelBranches.Count > 0)
                {
                    var containerLeft = Canvas.GetLeft(ParallelBranchesContainer);
                    ParallelBranchesContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var containerWidth = ParallelBranchesContainer.DesiredSize.Width;

                    var lineLeft = containerLeft;
                    var lineRight = containerLeft + containerWidth;

                    HorizontalDistributionLine.X1 = lineLeft;
                    HorizontalDistributionLine.X2 = lineRight;
                }

                UpdateBranchConnectionLines();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新连接线失败: {ex.Message}");
            }
        }

        private void UpdateBranchConnectionLines()
        {
            try
            {
                var containerLeft = Canvas.GetLeft(ParallelBranchesContainer);
                var currentX = containerLeft;

                for (int i = 0; i < _parallelBranches.Count; i++)
                {
                    var branchContainer = _parallelBranches[i].Container;
                    branchContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var branchWidth = branchContainer.DesiredSize.Width;
                    var branchCenterX = currentX + branchWidth / 2;

                    var verticalLine = BranchCanvas.Children
                        .OfType<Line>()
                        .FirstOrDefault(l => l.Name == $"BranchVerticalLine_{i}");

                    if (verticalLine != null)
                    {
                        verticalLine.X1 = branchCenterX;
                        verticalLine.X2 = branchCenterX;
                    }

                    var arrow = BranchCanvas.Children
                        .OfType<Polygon>()
                        .FirstOrDefault(p => p.Name == $"BranchArrow_{i}");

                    if (arrow != null)
                    {
                        Canvas.SetLeft(arrow, branchCenterX - 4);
                    }

                    currentX += branchWidth + 20;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新分支连接线失败: {ex.Message}");
            }
        }

        private void OnBranchContentChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateBranchCanvasLayout();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    System.Diagnostics.Debug.WriteLine("并行节点内容变化通知完成");
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnBranchContentChanged 错误: {ex.Message}");
            }
        }

        #endregion

        #region 清理和公共方法

        public void ClearAllBranches()
        {
            try
            {
                if (DataContext is CommandNode parallelNode)
                {
                    parallelNode.ClearChildren();
                }

                foreach (var branchInfo in _parallelBranches)
                {
                    var branchContent = FindBranchContentContainer(branchInfo.Index);
                    if (branchContent != null)
                    {
                        branchContent.Children.Clear();
                    }

                    branchInfo.Commands.Clear();

                    var hint = FindBranchHint(branchInfo.Index);
                    if (hint != null)
                    {
                        hint.Visibility = Visibility.Visible;
                    }
                }

                var elementsToRemove = BranchCanvas.Children
                    .OfType<FrameworkElement>()
                    .Where(e => e.Name != null &&
                               (e.Name.StartsWith("BranchVerticalLine_") ||
                                e.Name.StartsWith("BranchArrow_")))
                    .ToList();

                foreach (var element in elementsToRemove)
                {
                    BranchCanvas.Children.Remove(element);
                }

                ParallelBranchesContainer.Children.Clear();
                _parallelBranches.Clear();

                UpdateBranchCanvasLayout();
                OnBranchContentChanged();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清理所有分支失败: {ex.Message}");
            }
        }

        public IReadOnlyList<CommandNode> GetBranchCommands(int branchIndex)
        {
            if (branchIndex >= 0 && branchIndex < _parallelBranches.Count)
            {
                return _parallelBranches[branchIndex].Commands.AsReadOnly();
            }
            return new List<CommandNode>().AsReadOnly();
        }

        public int GetBranchCount() => _parallelBranches.Count;

        public int GetTotalCommandCount()
        {
            return _parallelBranches.Sum(b => b.Commands.Count);
        }

        public void Dispose()
        {
            try
            {
                ClearAllBranches();
                ContentChanged = null;
                NestedCommandAdded = null;
                NestedCommandRemoved = null;
                LogicCommandRequested = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Dispose 失败: {ex.Message}");
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
            "NodeClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ParallelNodeControlView));

        public event RoutedEventHandler NodeClick
        {
            add { AddHandler(NodeClickEvent, value); }
            remove { RemoveHandler(NodeClickEvent, value); }
        }
    }

    #region 辅助类

    public class ParallelBranchInfo
    {
        public int Index { get; set; }
        public List<CommandNode> Commands { get; set; } = new List<CommandNode>();
        public Border Container { get; set; }
    }

    public class NestedNodeInfo
    {
        public CommandNode Node { get; set; }
        public int BranchIndex { get; set; }
    }

    #endregion
}