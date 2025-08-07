// NodeControlFactory.cs
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Events;
using MaxChemical.Modules.Designer.Views;
using MaxChemical.Logging;
using System.Windows.Media.Animation;

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
        {
            try
            {
                if (node.IsDeviceCommand)
                    return CreateDeviceCommandControl(node, clickHandler, deleteHandler);

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

        private UserControl CreateDeviceCommandControl(CommandNode node, NodeClickHandler clickHandler, NodeDeleteHandler deleteHandler)
        {
            var border = new Border
            {
                Width = 231,
                Height = 64,
                Background = new SolidColorBrush(Colors.White),
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 4),
                Tag = node
            };

            border.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 4,
                Direction = 315,
                BlurRadius = 8,
                Color = Colors.Black,
                Opacity = 0.25
            };

            // 统一的选中状态样式
            ApplySelectionStyle(border, node);

            // 统一的点击处理
            border.MouseLeftButtonDown += (s, e) =>
            {
                clickHandler?.Invoke(node);
                e.Handled = true;
            };

            border.Child = CreateDeviceCommandContent(node, deleteHandler);

            return new UserControl
            {
                Content = border,
                Tag = node,
                DataContext = node
            };
        }

        private UserControl CreateLogicCommandControl(CommandNode node, NodeClickHandler clickHandler, NodeDeleteHandler deleteHandler)
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
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Tag = node
            };

            ApplySelectionStyle(border, node);

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
                    control.Margin = new Thickness(0, 4, 0, 4);

                    // 订阅事件
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

        private void ApplySelectionStyle(Border border, CommandNode node)
        {
            var style = new Style(typeof(Border));

            // 默认边框样式
            style.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0))));
            style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));

            // 默认阴影效果
            style.Setters.Add(new Setter(Border.EffectProperty,
                new System.Windows.Media.Effects.DropShadowEffect
                {
                    ShadowDepth = 0,
                    Direction = 315,
                    BlurRadius = 8,
                    Color = Colors.Black,
                    Opacity = 0.25
                }));

            style.Setters.Add(new Setter(Border.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));

            // 选中状态样式
            var selectedTrigger = new DataTrigger
            {
                Binding = new Binding("IsSelected") { Source = node },
                Value = true
            };
            selectedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))));
            selectedTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2)));

            // 插入高亮状态样式
            var insertHighlightTrigger = new DataTrigger
            {
                Binding = new Binding("IsInsertHighlight") { Source = node },
                Value = true
            };

            insertHighlightTrigger.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0x18, 0x6D, 0xF5))));
            insertHighlightTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2)));
            insertHighlightTrigger.Setters.Add(new Setter(Border.EffectProperty,
                new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(0x18, 0x6D, 0xF5),
                    ShadowDepth = 0,
                    Direction = 0,
                    BlurRadius = 20,
                    Opacity = 0.8  // 稍微增加透明度使效果更明显
                }));

            style.Triggers.Add(selectedTrigger);
            style.Triggers.Add(insertHighlightTrigger);
            border.Style = style;
        }

        private Grid CreateDeviceCommandContent(CommandNode node, NodeDeleteHandler deleteHandler)
        {
            var grid = new Grid { Margin = new Thickness(16, 0, 16, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 图标
            var iconGrid = CreateIconGrid(node);
            Grid.SetColumn(iconGrid, 0);

            // 文本内容
            var textStack = CreateTextContent(node);
            Grid.SetColumn(textStack, 1);

            // 删除按钮
            var deleteButton = CreateDeleteButton(node, deleteHandler);
            Grid.SetColumn(deleteButton, 2);

            grid.Children.Add(iconGrid);
            grid.Children.Add(textStack);
            grid.Children.Add(deleteButton);

            return grid;
        }

        private Grid CreateLogicCommandContent(CommandNode node, NodeDeleteHandler deleteHandler)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = node.DisplayName,
                FontSize = 12,
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

        private Grid CreateIconGrid(CommandNode node)
        {
            var iconGrid = new Grid
            {
                Width = 32,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };

            if (node.HasImage && !string.IsNullOrEmpty(node.DisplayImageLocation))
            {
                try
                {
                    var image = new Image
                    {
                        Source = new BitmapImage(new Uri(node.DisplayImageLocation, UriKind.RelativeOrAbsolute)),
                        Stretch = Stretch.Uniform,
                        Width = 26,
                        Height = 26
                    };
                    iconGrid.Children.Add(image);
                }
                catch
                {
                    AddDefaultIcon(iconGrid);
                }
            }
            else
            {
                AddDefaultIcon(iconGrid);
            }

            return iconGrid;
        }

        private StackPanel CreateTextContent(CommandNode node)
        {
            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var nameText = new TextBlock
            {
                Text = node.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Margin = new Thickness(0, 0, 0, 2),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var helpText = new TextBlock
            {
                Text = node.DisplayHelpText,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            textStack.Children.Add(nameText);
            textStack.Children.Add(helpText);

            return textStack;
        }

        private Border CreateDeleteButton(CommandNode node, NodeDeleteHandler deleteHandler)
        {
            var deleteButton = new Border
            {
                Width = 20,
                Height = 20,
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
            hoverTrigger.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(10)));

            deleteButtonStyle.Triggers.Add(hoverTrigger);
            deleteButton.Style = deleteButtonStyle;

            var deleteText = new TextBlock
            {
                Text = "×",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var textHoverStyle = new Style(typeof(TextBlock));
            textHoverStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66))));

            var textHoverTrigger = new DataTrigger
            {
                Binding = new Binding("IsMouseOver") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Border), 1) },
                Value = true
            };
            textHoverTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))));

            textHoverStyle.Triggers.Add(textHoverTrigger);
            deleteText.Style = textHoverStyle;

            deleteButton.Child = deleteText;
            deleteButton.MouseLeftButtonUp += (s, e) =>
            {
                deleteHandler?.Invoke(node);
                e.Handled = true;
            };

            return deleteButton;
        }

        private void AddDefaultIcon(Grid iconGrid)
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