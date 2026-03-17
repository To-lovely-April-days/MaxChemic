using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class DevicePropertyDialog : UserControl
    {
        public event EventHandler CloseRequested;
        public event EventHandler<bool> DialogResult;

        private CanvasDeviceViewModel _canvasDevice;
        private DeviceCommand _selectedCommand;
        private List<CheckBox> _monitorParameterCheckBoxes = new List<CheckBox>();
        private string _deviceDisplayName;
        private bool _isInitializing = false;
        private bool _isDeviceConnected = false;
        private string _currentCommandName = "";
        private bool _originalSimulationMode = false;
        private bool _hasModifiedSimulationMode = false;
        private Dictionary<string, bool> _monitorParameterSelections = new Dictionary<string, bool>();

        public event EventHandler<MonitoringParametersChangedEventArgs> MonitoringParametersChanged;

        public DevicePropertyDialog()
        {
            InitializeComponent();
            InitializeConnectionState();
        }

        #region *** 模拟模式管理方法 ***

        /// <summary>
        /// 强制设备进入实际模式（用于设备属性对话框测试）
        /// </summary>
        private void ForceDeviceToActualMode()
        {
            try
            {
                if (_canvasDevice?.Device == null) return;

                _originalSimulationMode = _canvasDevice.Device.IsSimulationMode;

                if (_originalSimulationMode)
                {
                    Debug.WriteLine($"设备 {_canvasDevice.Device.Name} 当前为模拟模式，切换到实际模式进行测试");
                    _canvasDevice.Device.IsSimulationMode = false;
                    _hasModifiedSimulationMode = true;
                    Debug.WriteLine($"已将设备 {_canvasDevice.Device.Name} 临时切换到实际模式");
                }
                else
                {
                    Debug.WriteLine($"设备 {_canvasDevice.Device.Name} 已经是实际模式");
                    _hasModifiedSimulationMode = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"强制设备进入实际模式失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 恢复设备的原始模拟模式状态
        /// </summary>
        private void RestoreOriginalSimulationMode()
        {
            try
            {
                if (_canvasDevice?.Device == null || !_hasModifiedSimulationMode) return;

                Debug.WriteLine($"恢复设备 {_canvasDevice.Device.Name} 的原始模拟模式状态: {(_originalSimulationMode ? "模拟模式" : "实际模式")}");
                _canvasDevice.Device.IsSimulationMode = _originalSimulationMode;
                _hasModifiedSimulationMode = false;
                Debug.WriteLine($"已恢复设备 {_canvasDevice.Device.Name} 的模拟模式状态");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复设备原始模拟模式状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 确保设备处于实际模式状态
        /// </summary>
        private void EnsureDeviceInActualMode()
        {
            try
            {
                if (_canvasDevice?.Device == null) return;

                if (_canvasDevice.Device.IsSimulationMode)
                {
                    Debug.WriteLine($"检测到设备 {_canvasDevice.Device.Name} 处于模拟模式，强制切换到实际模式");
                    _canvasDevice.Device.IsSimulationMode = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"确保设备处于实际模式失败: {ex.Message}");
            }
        }

        #endregion

        #region *** 核心状态管理方法 ***

        /// <summary>
        /// 生成唯一的参数键（命令名#参数名）
        /// </summary>
        private string GenerateParameterKey(string commandName, string parameterName)
        {
            return $"{commandName}#{parameterName}";
        }

        /// <summary>
        /// 恢复监控参数选择状态 - 最终修复版本，支持完整键名和参数名两种格式
        /// </summary>
        private bool _isRestoring = false;

        public void RestoreMonitoringSelections(HashSet<string> selectedParameterKeys)
        {
            try
            {
                if (_isRestoring)
                {
                    Debug.WriteLine("正在恢复中，跳过重复调用");
                    return;
                }

                if (selectedParameterKeys == null || selectedParameterKeys.Count == 0)
                {
                    Debug.WriteLine("没有需要恢复的监控参数选择状态");
                    return;
                }

                _isRestoring = true;
                Debug.WriteLine($"开始恢复监控参数选择状态: {string.Join(", ", selectedParameterKeys)}");

                if (IsParameterKeysFormat(selectedParameterKeys))
                {
                    RestoreFromCompleteKeys(selectedParameterKeys);
                }
                else
                {
                    RestoreFromParameterNames(selectedParameterKeys);
                }

                //  关键修复：同步调用，不再使用二次 BeginInvoke
                // 原先的嵌套 BeginInvoke 导致 _isRestoring 提前置 false，
                // 用户取消选择后，内层 BeginInvoke 触发时又把复选框强制重置为 true
                RestoreCurrentCommandDisplayOnly();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复监控参数选择状态失败: {ex.Message}");
            }
            finally
            {
                // 用 finally 保证 _isRestoring 一定被重置
                _isRestoring = false;
            }
        }

        /// <summary>
        /// 判断传入的是否为完整键名格式
        /// </summary>
        private bool IsParameterKeysFormat(HashSet<string> keys)
        {
            // 如果大部分键包含 # 分隔符，则认为是完整键名格式
            int keyFormatCount = keys.Count(k => k.Contains("#"));
            return keyFormatCount > keys.Count / 2;
        }

        /// <summary>
        /// 从完整键名恢复状态 - 新方法，精确匹配
        /// </summary>
        private void RestoreFromCompleteKeys(HashSet<string> selectedParameterKeys)
        {
            try
            {
                Debug.WriteLine("使用完整键名格式恢复状态（精确匹配）");

                // *** 关键修复：直接使用完整键名设置状态 ***
                _monitorParameterSelections.Clear();

                foreach (var key in selectedParameterKeys)
                {
                    _monitorParameterSelections[key] = true;
                    Debug.WriteLine($"  设置选中状态: {key} = true");
                }

                // 确保所有未选中的参数也有明确状态
                if (_canvasDevice?.Device?.Commands != null)
                {
                    foreach (var command in _canvasDevice.Device.Commands)
                    {
                        if (command.OutputParameters?.Variables?.Any() == true)
                        {
                            foreach (var param in command.OutputParameters.Variables)
                            {
                                var key = GenerateParameterKey(command.Name, param.Name);
                                if (!_monitorParameterSelections.ContainsKey(key))
                                {
                                    _monitorParameterSelections[key] = false;
                                    Debug.WriteLine($"  设置未选中状态: {key} = false");
                                }
                            }
                        }
                    }
                }

                Debug.WriteLine("完整键名格式恢复完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"从完整键名恢复状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从参数名恢复状态 - 旧方法，向后兼容但有歧义
        /// </summary>
        private void RestoreFromParameterNames(HashSet<string> selectedParameterNames)
        {
            try
            {
                Debug.WriteLine("使用参数名格式恢复状态（向后兼容，可能有歧义）");
                Debug.WriteLine(" 警告：使用参数名格式可能导致跨命令参数误选，建议升级到完整键名格式");

                // *** 关键修复：不使用有问题的预处理方法 ***
                // 这里应该让外部调用者提供正确的完整键名格式
                Debug.WriteLine("建议外部调用者使用 GetAllSelectedParameterKeys() 获取完整键名格式");

                // 暂时清空状态，避免误选
                _monitorParameterSelections.Clear();

                Debug.WriteLine("由于参数名格式有歧义，已清空所有状态以避免误选");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"从参数名恢复状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 只恢复当前显示命令的UI状态
        /// </summary>
        private void RestoreCurrentCommandDisplayOnly()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentCommandName) || _monitorParameterCheckBoxes.Count == 0)
                {
                    Debug.WriteLine("无需恢复当前命令的显示状态");
                    return;
                }

                Debug.WriteLine($"*** 恢复当前命令 '{_currentCommandName}' 的显示状态 ***");

                _isInitializing = true; // 阻止事件处理

                int restoredCount = 0;
                int uncheckedCount = 0;

                foreach (var checkBox in _monitorParameterCheckBoxes)
                {
                    string parameterName = ExtractParameterNameFromCheckBox(checkBox);

                    if (!string.IsNullOrEmpty(parameterName))
                    {
                        var key = GenerateParameterKey(_currentCommandName, parameterName);

                        // *** 从内部状态字典获取状态 ***
                        if (_monitorParameterSelections.TryGetValue(key, out bool shouldCheck))
                        {
                            checkBox.IsChecked = shouldCheck;

                            if (shouldCheck)
                            {
                                restoredCount++;
                                Debug.WriteLine($"  显示为选中: {key} = true");
                            }
                            else
                            {
                                uncheckedCount++;
                                Debug.WriteLine($"  显示为未选中: {key} = false");
                            }
                        }
                        else
                        {
                            // 默认未选中
                            checkBox.IsChecked = false;
                            uncheckedCount++;
                            Debug.WriteLine($"  默认未选中: {key} = false (无状态)");
                        }
                    }
                }

                _isInitializing = false; // 恢复事件处理

                Debug.WriteLine($"*** 当前命令显示恢复完成: 选中 {restoredCount} 个，未选中 {uncheckedCount} 个 ***");
            }
            catch (Exception ex)
            {
                _isInitializing = false;
                Debug.WriteLine($"恢复当前命令显示状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取所有选中的参数键名 - 新方法，供外部调用
        /// </summary>
        public HashSet<string> GetAllSelectedParameterKeys()
        {
            try
            {
                // 先保存当前命令状态
                if (!string.IsNullOrEmpty(_currentCommandName) && _monitorParameterCheckBoxes.Count > 0)
                {
                    SaveCurrentCommandSelections();
                }

                // 返回所有选中的参数键
                var selectedKeys = _monitorParameterSelections
                    .Where(kvp => kvp.Value)
                    .Select(kvp => kvp.Key)
                    .ToHashSet();

                Debug.WriteLine($"获取所有选中参数键: {string.Join(", ", selectedKeys)}");
                return selectedKeys;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取选中参数键失败: {ex.Message}");
                return new HashSet<string>();
            }
        }

        #endregion

        #region *** 界面初始化和连接状态管理 ***

        /// <summary>
        /// 初始化连接状态
        /// </summary>
        private void InitializeConnectionState()
        {
            _isDeviceConnected = false;
            UpdateConnectionUI();
        }

        /// <summary>
        /// 更新连接状态相关的UI
        /// </summary>
        private void UpdateConnectionUI()
        {
            if (_isDeviceConnected)
            {
                // 连接状态
                ConnectionStatusText.Text = "已连接";
                ConnectionStatusText.Foreground = Brushes.Green;

                ConnectButton.Content = "已连接";
                ConnectButton.Style = (Style)FindResource("GreenButtonStyle");
                ConnectButton.IsEnabled = false;

                DisconnectButton.Content = "断开";
                DisconnectButton.Style = (Style)FindResource("RedButtonStyle");
                DisconnectButton.IsEnabled = true;

                ExecuteButton.IsEnabled = true;
            }
            else
            {
                // 断开状态
                ConnectionStatusText.Text = "未连接";
                ConnectionStatusText.Foreground = Brushes.Red;

                ConnectButton.Content = "连接";
                ConnectButton.Style = (Style)FindResource("BlueButtonStyle");
                ConnectButton.IsEnabled = true;

                DisconnectButton.Content = "断开";
                DisconnectButton.Style = (Style)FindResource("GrayButtonStyle");
                DisconnectButton.IsEnabled = false;

                ExecuteButton.IsEnabled = false;
            }
        }

        /// <summary>
        /// 设置设备数据
        /// </summary>
        public void SetDeviceData(CanvasDeviceViewModel canvasDevice, string displayName = null)
        {
            Debug.WriteLine($"=== 开始设置设备数据: {canvasDevice?.Device?.Name} ===");

            _isInitializing = true;
            _canvasDevice = canvasDevice;
            _deviceDisplayName = displayName;

            // *** 关键修复：完全清理所有相关状态 ***
            Debug.WriteLine("完全清理所有监控参数状态");
            _monitorParameterSelections.Clear();

            // 保存原始模拟模式状态
            if (canvasDevice?.Device != null)
            {
                _originalSimulationMode = canvasDevice.Device.IsSimulationMode;
                Debug.WriteLine($"设备 {canvasDevice.Device.Name} 原始模拟模式状态: {(_originalSimulationMode ? "模拟模式" : "实际模式")}");
            }

            if (canvasDevice?.Device != null)
            {
                LoadDeviceInformation(canvasDevice, displayName);
                LoadDeviceCommands(canvasDevice.Device);
                LoadDeviceParameters(canvasDevice.Device);

                // 只选择第一个命令，不选中任何参数
                SelectFirstCommandOnly();
            }

            _isInitializing = false;
            Debug.WriteLine("设备数据设置完成，所有状态已清理");
            Debug.WriteLine("=== 设备数据设置完成 ===\n");
        }

        #endregion

        #region *** 命令和参数管理 ***

        /// <summary>
        /// 只选择第一个命令，不选中任何参数
        /// </summary>
        private void SelectFirstCommandOnly()
        {
            if (CommandListBox.Items.Count > 0)
            {
                var firstItem = CommandListBox.Items[0] as ListBoxItem;
                if (firstItem != null && firstItem.IsEnabled)
                {
                    CommandListBox.SelectedIndex = 0;
                    HandleCommandSelectionWithoutDefaultSelection(firstItem);
                }
            }
            else
            {
                LoadEmptyState();
            }
        }

        /// <summary>
        /// 处理命令选择但不默认选中参数
        /// </summary>
        private void HandleCommandSelectionWithoutDefaultSelection(ListBoxItem selectedItem)
        {
            if (!selectedItem.IsEnabled) return;

            var commandName = selectedItem.Content.ToString();
            Debug.WriteLine($"\n*** 选择命令（无默认参数选中）: {commandName} ***");

            if (selectedItem.Tag is DeviceCommand command)
            {
                _selectedCommand = command;
                UpdateMonitorParametersForCommandWithoutDefaults(commandName, command);
                LoadCommandInputParameters(command);
                LoadCommandOutputParameters(command);
                Debug.WriteLine($"选择了命令: {commandName}, 输入参数: {command.InputParameters?.Variables?.Count ?? 0}, 输出参数: {command.OutputParameters?.Variables?.Count ?? 0}");
            }
            else
            {
                _selectedCommand = null;
                var foundCommand = FindCommandByName(commandName);
                if (foundCommand != null)
                {
                    UpdateMonitorParametersForCommandWithoutDefaults(commandName, foundCommand);
                    LoadCommandInputParameters(foundCommand);
                    LoadCommandOutputParameters(foundCommand);
                }
                else
                {
                    LoadEmptyState();
                }
            }
        }

        /// <summary>
        /// 更新监控参数但不默认选中任何参数
        /// </summary>
        private void UpdateMonitorParametersForCommandWithoutDefaults(string commandName, DeviceCommand command = null)
        {
            Debug.WriteLine($"\n=== 开始更新命令 '{commandName}' 的监控参数（不显示参数值） ===");

            // 先保存当前命令的选中状态
            if (!string.IsNullOrEmpty(_currentCommandName) &&
                _currentCommandName != commandName &&
                _monitorParameterCheckBoxes.Count > 0)
            {
                Debug.WriteLine($"保存切换前的命令状态: {_currentCommandName}");
                SaveCurrentCommandSelectionsBeforeSwitch();
            }

            // 更新当前命令名
            _currentCommandName = commandName;
            CommandText.Text = commandName;

            // 清空当前显示
            MonitorParametersPanel.Children.Clear();
            _monitorParameterCheckBoxes.Clear();

            List<ParameterBase> outputParameters = new List<ParameterBase>();

            if (command?.OutputParameters?.Variables?.Any() == true)
            {
                outputParameters = command.OutputParameters.Variables.ToList();
            }
            else
            {
                var matchedCommand = FindCommandByName(commandName);
                if (matchedCommand?.OutputParameters?.Variables?.Any() == true)
                {
                    outputParameters = matchedCommand.OutputParameters.Variables.ToList();
                }
            }

            // 创建复选框时确保完全清洁的初始状态
            _isInitializing = true; // 阻止事件处理

            foreach (var param in outputParameters)
            {
                var checkBox = new CheckBox
                {
                    Content = param.Name, // 只显示参数名称，不显示值
                    Style = (Style)FindResource("CheckBoxStyle"),
                    Margin = new Thickness(8, 2, 12, 2),
                    Tag = param,
                    IsChecked = false // 强制初始状态为未选中
                };

                // 添加事件处理
                checkBox.Checked += MonitorParameterCheckBox_CheckedChanged;
                checkBox.Unchecked += MonitorParameterCheckBox_CheckedChanged;

                MonitorParametersPanel.Children.Add(checkBox);
                _monitorParameterCheckBoxes.Add(checkBox);

                Debug.WriteLine($"  创建参数复选框: {param.Name} (仅显示名称)");
            }

            _isInitializing = false; // 恢复事件处理

            // 延迟恢复状态
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RestoreCommandSelectionsAfterSwitch(commandName);
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            Debug.WriteLine($"=== 完成更新命令 '{commandName}' 的 {outputParameters.Count} 个监控参数（仅显示名称） ===\n");
        }

        /// <summary>
        /// 在切换命令后恢复选择状态
        /// </summary>
        private void RestoreCommandSelectionsAfterSwitch(string commandName)
        {
            if (string.IsNullOrEmpty(commandName) || _monitorParameterCheckBoxes.Count == 0)
            {
                Debug.WriteLine("RestoreCommandSelectionsAfterSwitch: 没有需要恢复的状态");
                return;
            }

            Debug.WriteLine($"*** 切换后恢复命令 '{commandName}' 的状态 ***");

            _isInitializing = true; // 阻止事件处理

            int restoredCount = 0;
            int uncheckedCount = 0;

            foreach (var checkBox in _monitorParameterCheckBoxes)
            {
                try
                {
                    string parameterName = ExtractParameterNameFromCheckBox(checkBox);

                    if (!string.IsNullOrEmpty(parameterName))
                    {
                        var key = GenerateParameterKey(commandName, parameterName);

                        // *** 直接从状态字典获取状态 ***
                        bool shouldCheck = _monitorParameterSelections.TryGetValue(key, out bool savedState) && savedState;

                        checkBox.IsChecked = shouldCheck;

                        if (shouldCheck)
                        {
                            restoredCount++;
                            Debug.WriteLine($"  恢复为选中: {key} = true");
                        }
                        else
                        {
                            uncheckedCount++;
                            Debug.WriteLine($"  恢复为未选中: {key} = false");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"恢复单个参数状态失败: {ex.Message}");
                }
            }

            _isInitializing = false; // 恢复事件处理

            Debug.WriteLine($"*** 切换后恢复完成: 选中 {restoredCount} 个，未选中 {uncheckedCount} 个 ***");
        }

        /// <summary>
        /// 保存当前命令的选中状态
        /// </summary>
        private void SaveCurrentCommandSelections()
        {
            if (string.IsNullOrEmpty(_currentCommandName) || _monitorParameterCheckBoxes.Count == 0)
            {
                Debug.WriteLine("SaveCurrentCommandSelections: 没有需要保存的状态");
                return;
            }

            Debug.WriteLine($"正在保存命令 '{_currentCommandName}' 的选中状态...");

            int savedCount = 0;
            foreach (var checkBox in _monitorParameterCheckBoxes)
            {
                try
                {
                    string parameterName = ExtractParameterNameFromCheckBox(checkBox);

                    if (!string.IsNullOrEmpty(parameterName))
                    {
                        var key = GenerateParameterKey(_currentCommandName, parameterName);
                        var isChecked = checkBox.IsChecked == true;
                        _monitorParameterSelections[key] = isChecked;
                        savedCount++;

                        Debug.WriteLine($"  保存: {key} = {isChecked}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"保存单个参数失败: {ex.Message}");
                }
            }

            Debug.WriteLine($"已保存命令 '{_currentCommandName}' 的 {savedCount} 个监控参数选中状态");
        }

        /// <summary>
        /// 在切换命令前保存当前选择状态
        /// </summary>
        private void SaveCurrentCommandSelectionsBeforeSwitch()
        {
            if (string.IsNullOrEmpty(_currentCommandName) || _monitorParameterCheckBoxes.Count == 0)
            {
                Debug.WriteLine("没有需要保存的状态");
                return;
            }

            Debug.WriteLine($"*** 切换前保存命令 '{_currentCommandName}' 的状态 ***");

            int savedCount = 0;
            foreach (var checkBox in _monitorParameterCheckBoxes)
            {
                try
                {
                    string parameterName = ExtractParameterNameFromCheckBox(checkBox);

                    if (!string.IsNullOrEmpty(parameterName))
                    {
                        var key = GenerateParameterKey(_currentCommandName, parameterName);
                        var isChecked = checkBox.IsChecked == true;

                        _monitorParameterSelections[key] = isChecked;
                        savedCount++;

                        Debug.WriteLine($"  保存切换前状态: {key} = {isChecked}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"保存单个参数状态失败: {ex.Message}");
                }
            }

            Debug.WriteLine($"*** 切换前保存完成，共保存 {savedCount} 个参数状态 ***");
        }

        /// <summary>
        /// 监控参数复选框状态变化事件
        /// </summary>
        private void MonitorParameterCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _isRestoring)
            {
                Debug.WriteLine("初始化或恢复期间，忽略复选框状态变化");
                return;
            }

            try
            {
                if (sender is CheckBox checkBox && !string.IsNullOrEmpty(_currentCommandName))
                {
                    string parameterName = ExtractParameterNameFromCheckBox(checkBox);

                    if (!string.IsNullOrEmpty(parameterName))
                    {
                        var key = GenerateParameterKey(_currentCommandName, parameterName);
                        var isChecked = checkBox.IsChecked == true;

                        // 实时保存到字典
                        _monitorParameterSelections[key] = isChecked;

                        Debug.WriteLine($"用户操作状态变化: {key} = {isChecked}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"监控参数复选框状态变化处理失败: {ex.Message}");
            }
        }

        #endregion

        #region *** 工具方法 ***

        /// <summary>
        /// 从复选框提取参数名称
        /// </summary>
        private string ExtractParameterNameFromCheckBox(CheckBox checkBox)
        {
            try
            {
                if (checkBox.Tag is ParameterBase param)
                {
                    return param.Name;
                }
                else if (checkBox.Content != null)
                {
                    var content = checkBox.Content.ToString();
                    return ExtractParameterName(content);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"提取参数名称失败: {ex.Message}");
            }

            return "";
        }

        /// <summary>
        /// 从复选框内容中提取参数名称
        /// </summary>
        private string ExtractParameterName(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";

            // 假设格式是 "参数名 值"，取第一部分作为参数名
            var parts = content.Split(' ');
            return parts.Length > 0 ? parts[0] : content;
        }

        /// <summary>
        /// 根据命令名称查找对应的命令对象
        /// </summary>
        private DeviceCommand FindCommandByName(string commandName)
        {
            if (_canvasDevice?.Device?.Commands?.Any() == true)
            {
                return _canvasDevice.Device.Commands.FirstOrDefault(c => c.Name == commandName);
            }
            return null;
        }

        /// <summary>
        /// 加载空状态
        /// </summary>
        private void LoadEmptyState()
        {
            MonitorParametersPanel.Children.Clear();
            _monitorParameterCheckBoxes.Clear();
            InputParametersPanel.Children.Clear();
            OutputParametersPanel.Children.Clear();
            CommandText.Text = "无可用命令";
            _currentCommandName = "";
        }

        #endregion

        #region *** 设备信息加载 ***

        /// <summary>
        /// 加载设备基本信息
        /// </summary>
        private void LoadDeviceInformation(CanvasDeviceViewModel canvasDevice, string displayName = null)
        {
            var device = canvasDevice.Device;

            DeviceNameText.Text = displayName ?? device.Name;
            DeviceTypeText.Text = device.GetType().Name;
            ManufacturerText.Text = device.Manufacturer;

            LoadDeviceIcon(device);
        }

        /// <summary>
        /// 加载设备图标
        /// </summary>
        private void LoadDeviceIcon(IDevice device)
        {
            try
            {
                if (!string.IsNullOrEmpty(device.ImageLocation))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;

                    if (device.ImageLocation.StartsWith("pack://"))
                    {
                        bitmap.UriSource = new Uri(device.ImageLocation);
                    }
                    else
                    {
                        bitmap.UriSource = new Uri(device.ImageLocation, UriKind.RelativeOrAbsolute);
                    }

                    bitmap.EndInit();
                    DeviceImage.Source = bitmap;
                }
                else
                {
                    CreateDefaultDeviceIcon();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载设备图标失败: {ex.Message}");
                CreateDefaultDeviceIcon();
            }
        }

        /// <summary>
        /// 创建默认设备图标
        /// </summary>
        private void CreateDefaultDeviceIcon()
        {
            var drawingGroup = new DrawingGroup();

            var backgroundRect = new RectangleGeometry(new Rect(0, 0, 100, 80));
            var backgroundDrawing = new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(200, 220, 240)),
                new Pen(Brushes.DarkBlue, 1),
                backgroundRect);
            drawingGroup.Children.Add(backgroundDrawing);

            var formattedText = new FormattedText(
                "设备",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("SimSun"),
                16,
                Brushes.DarkBlue,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var textDrawing = new GeometryDrawing(
                Brushes.DarkBlue,
                null,
                formattedText.BuildGeometry(new Point(35, 30)));
            drawingGroup.Children.Add(textDrawing);

            var drawingImage = new DrawingImage(drawingGroup);
            DeviceImage.Source = drawingImage;
        }

        /// <summary>
        /// 加载设备命令
        /// </summary>
        private void LoadDeviceCommands(IDevice device)
        {
            CommandListBox.Items.Clear();

            if (device.Commands?.Any() == true)
            {
                foreach (var command in device.Commands)
                {
                    var listBoxItem = new ListBoxItem
                    {
                        Content = command.Name,
                        Tag = command,
                        Padding = new Thickness(8, 4, 8, 4),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(230, 230, 230))
                    };
                    CommandListBox.Items.Add(listBoxItem);
                }
            }
            else
            {
                var noCommandsItem = new ListBoxItem
                {
                    Content = "该设备没有可用的命令",
                    IsEnabled = false,
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    Padding = new Thickness(8, 4, 8, 4)
                };
                CommandListBox.Items.Add(noCommandsItem);
            }
        }

        /// <summary>
        /// 命令选择变化事件
        /// </summary>
        private void CommandListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            if (CommandListBox.SelectedItem is ListBoxItem selectedItem)
            {
                HandleCommandSelection(selectedItem);
            }
        }

        /// <summary>
        /// 处理命令选择
        /// </summary>
        private void HandleCommandSelection(ListBoxItem selectedItem)
        {
            if (!selectedItem.IsEnabled) return;

            var commandName = selectedItem.Content.ToString();
            Debug.WriteLine($"\n*** 切换到命令: {commandName} ***");

            if (selectedItem.Tag is DeviceCommand command)
            {
                _selectedCommand = command;
                UpdateMonitorParametersForCommand(commandName, command);
                LoadCommandInputParameters(command);
                LoadCommandOutputParameters(command);
                Debug.WriteLine($"选择了命令: {commandName}, 输入参数: {command.InputParameters?.Variables?.Count ?? 0}, 输出参数: {command.OutputParameters?.Variables?.Count ?? 0}");
            }
            else
            {
                _selectedCommand = null;
                var foundCommand = FindCommandByName(commandName);
                if (foundCommand != null)
                {
                    UpdateMonitorParametersForCommand(commandName, foundCommand);
                    LoadCommandInputParameters(foundCommand);
                    LoadCommandOutputParameters(foundCommand);
                }
                else
                {
                    if (!string.IsNullOrEmpty(_currentCommandName))
                    {
                        SaveCurrentCommandSelections();
                    }

                    MonitorParametersPanel.Children.Clear();
                    _monitorParameterCheckBoxes.Clear();
                    InputParametersPanel.Children.Clear();
                    OutputParametersPanel.Children.Clear();
                    CommandText.Text = commandName;
                    _currentCommandName = "";
                }
            }
        }

        /// <summary>
        /// 根据选中的命令更新监控参数
        /// </summary>
        private void UpdateMonitorParametersForCommand(string commandName, DeviceCommand command = null)
        {
            Debug.WriteLine($"\n=== 开始更新命令 '{commandName}' 的监控参数（不显示参数值） ===");

            // 在清空UI之前保存当前状态
            if (!string.IsNullOrEmpty(_currentCommandName) &&
                _currentCommandName != commandName &&
                _monitorParameterCheckBoxes.Count > 0)
            {
                Debug.WriteLine($"保存切换前的命令状态: {_currentCommandName}");
                SaveCurrentCommandSelectionsBeforeSwitch();
            }

            // 更新当前命令名
            _currentCommandName = commandName;
            CommandText.Text = commandName;

            // 清空当前显示
            MonitorParametersPanel.Children.Clear();
            _monitorParameterCheckBoxes.Clear();

            List<ParameterBase> outputParameters = new List<ParameterBase>();

            if (command?.OutputParameters?.Variables?.Any() == true)
            {
                outputParameters = command.OutputParameters.Variables.ToList();
            }
            else
            {
                var matchedCommand = FindCommandByName(commandName);
                if (matchedCommand?.OutputParameters?.Variables?.Any() == true)
                {
                    outputParameters = matchedCommand.OutputParameters.Variables.ToList();
                }
            }

            // 为每个输出参数创建复选框
            foreach (var param in outputParameters)
            {
                var checkBox = new CheckBox
                {
                    Content = param.Name, // 只显示参数名称，不显示值
                    Style = (Style)FindResource("CheckBoxStyle"),
                    Margin = new Thickness(8, 2, 12, 2),
                    Tag = param
                };

                // 添加选中状态变化事件处理
                checkBox.Checked += MonitorParameterCheckBox_CheckedChanged;
                checkBox.Unchecked += MonitorParameterCheckBox_CheckedChanged;

                MonitorParametersPanel.Children.Add(checkBox);
                _monitorParameterCheckBoxes.Add(checkBox);
            }

            // 延迟恢复状态，确保UI完全创建后再恢复
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RestoreCommandSelectionsAfterSwitch(commandName);
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            Debug.WriteLine($"=== 完成更新命令 '{commandName}' 的 {outputParameters.Count} 个监控参数（仅显示名称） ===\n");
        }

        #endregion

        #region *** 参数加载和显示 ***

        /// <summary>
        /// 加载命令输入参数
        /// </summary>
        private void LoadCommandInputParameters(DeviceCommand command)
        {
            InputParametersPanel.Children.Clear();

            if (command.InputParameters?.Variables?.Any() == true)
            {
                foreach (var param in command.InputParameters.Variables)
                {
                    var parameterControl = CreateParameterInputControl(param);
                    InputParametersPanel.Children.Add(parameterControl);
                }
                Debug.WriteLine($"加载了 {command.InputParameters.Variables.Count} 个输入参数");
            }
            else
            {
                var noParamsText = new TextBlock
                {
                    Text = "该命令没有输入参数",
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(10),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                InputParametersPanel.Children.Add(noParamsText);
            }
        }

        /// <summary>
        /// 加载命令输出参数
        /// </summary>
        private void LoadCommandOutputParameters(DeviceCommand command)
        {
            OutputParametersPanel.Children.Clear();

            if (command.OutputParameters?.Variables?.Any() == true)
            {
                foreach (var param in command.OutputParameters.Variables)
                {
                    var parameterControl = CreateParameterDisplayControl(param);
                    OutputParametersPanel.Children.Add(parameterControl);
                }
                Debug.WriteLine($"加载了 {command.OutputParameters.Variables.Count} 个输出参数");
            }
            else
            {
                var noParamsText = new TextBlock
                {
                    Text = "该命令没有输出参数",
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(10),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                OutputParametersPanel.Children.Add(noParamsText);
            }
        }

        /// <summary>
        /// 加载设备参数
        /// </summary>
        private void LoadDeviceParameters(IDevice device)
        {
            DeviceParametersPanel.Children.Clear();

            if (device.Parameters?.Variables?.Any() == true)
            {
                foreach (var parameter in device.Parameters.Variables)
                {
                    var parameterControl = CreateParameterInputControl(parameter);
                    DeviceParametersPanel.Children.Add(parameterControl);
                }
            }
            else
            {
                var noParamsText = new TextBlock
                {
                    Text = "该设备没有可配置的参数",
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(10),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                DeviceParametersPanel.Children.Add(noParamsText);
            }
        }

        /// <summary>
        /// 创建参数输入控件
        /// </summary>
        private UIElement CreateParameterInputControl(ParameterBase parameter)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var label = new TextBlock
            {
                Text = parameter.Name,
                Width = 80,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Style = (Style)FindResource("LabelStyle")
            };

            UIElement valueControl = CreateParameterValueControl(parameter);

            panel.Children.Add(label);
            panel.Children.Add(valueControl);

            return panel;
        }

        /// <summary>
        /// 创建参数显示控件
        /// </summary>
        private UIElement CreateParameterDisplayControl(ParameterBase parameter)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3)
            };

            var nameBlock = new TextBlock
            {
                Text = parameter.Name + ":",
                Width = 60,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Medium,
                Style = (Style)FindResource("LabelStyle")
            };

            var valueBlock = new TextBlock
            {
                Text = parameter.Value?.ToString() ?? "",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DarkBlue,
                Style = (Style)FindResource("LabelStyle"),
                Tag = parameter.Name
            };

            panel.Children.Add(nameBlock);
            panel.Children.Add(valueBlock);

            return panel;
        }

        /// <summary>
        /// 创建参数值控件
        /// </summary>
        private UIElement CreateParameterValueControl(ParameterBase parameter)
        {
            switch (parameter)
            {
                case BooleanParameter boolParam:
                    var checkBox = new CheckBox
                    {
                        IsChecked = (bool)(boolParam.Value ?? false),
                        IsEnabled = !boolParam.IsReadOnly,
                        VerticalAlignment = VerticalAlignment.Center,
                        Style = (Style)FindResource("CheckBoxStyle")
                    };
                    checkBox.Checked += (s, e) => boolParam.Value = true;
                    checkBox.Unchecked += (s, e) => boolParam.Value = false;
                    return checkBox;

                case StringParameter stringParam when stringParam.Options?.Any() == true:
                    var comboBox = new ComboBox
                    {
                        ItemsSource = stringParam.Options,
                        SelectedItem = stringParam.Value,
                        IsEnabled = !stringParam.IsReadOnly,
                        Width = 120,
                        Style = (Style)FindResource("ComboBoxStyle")
                    };
                    comboBox.SelectionChanged += (s, e) => stringParam.Value = comboBox.SelectedItem;
                    return comboBox;

                case NumberParameter numberParam:
                    var numberTextBox = new TextBox
                    {
                        Text = numberParam.Value?.ToString() ?? "0",
                        IsReadOnly = numberParam.IsReadOnly,
                        Width = 120,
                        Style = (Style)FindResource("TextBoxStyle")
                    };
                    numberTextBox.TextChanged += (s, e) =>
                    {
                        if (double.TryParse(numberTextBox.Text, out double value))
                        {
                            numberParam.Value = value;
                        }
                    };
                    return numberTextBox;

                case EnumParameter enumParam:
                    var enumComboBox = new ComboBox
                    {
                        ItemsSource = Enum.GetValues(enumParam.EnumType),
                        SelectedItem = enumParam.Value,
                        IsEnabled = !enumParam.IsReadOnly,
                        Width = 120,
                        Style = (Style)FindResource("ComboBoxStyle")
                    };
                    enumComboBox.SelectionChanged += (s, e) => enumParam.Value = enumComboBox.SelectedItem;
                    return enumComboBox;

                default:
                    var defaultTextBox = new TextBox
                    {
                        Text = parameter.Value?.ToString() ?? "",
                        IsReadOnly = parameter.IsReadOnly,
                        Width = 120,
                        Style = (Style)FindResource("TextBoxStyle")
                    };
                    defaultTextBox.TextChanged += (s, e) => parameter.Value = defaultTextBox.Text;
                    return defaultTextBox;
            }
        }

        /// <summary>
        /// 更新输出参数显示 - 执行命令后调用
        /// </summary>
        private void UpdateOutputParametersDisplay(DeviceCommandResult result = null)
        {
            try
            {
                if (result?.OutputParameters?.Variables?.Any() == true)
                {
                    UpdateOutputParametersFromResult(result.OutputParameters);
                    UpdateMonitorParametersFromResult(result.OutputParameters);
                }
                else if (_selectedCommand?.OutputParameters?.Variables?.Any() == true)
                {
                    LoadCommandOutputParameters(_selectedCommand);
                    UpdateMonitorParametersFromCommandParameters(_selectedCommand.OutputParameters);
                }
                else
                {
                    Debug.WriteLine("没有可用的输出参数进行更新");
                }

                Debug.WriteLine("输出参数显示已更新（使用实际执行结果）");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新输出参数显示失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从实际命令执行结果更新输出参数显示
        /// </summary>
        private void UpdateOutputParametersFromResult(DeviceParameters outputParameters)
        {
            OutputParametersPanel.Children.Clear();

            foreach (var param in outputParameters.Variables)
            {
                var parameterControl = CreateParameterDisplayControl(param);
                OutputParametersPanel.Children.Add(parameterControl);
            }

            Debug.WriteLine($"已更新 {outputParameters.Variables.Count} 个实际输出参数");
        }

        /// <summary>
        /// 从命令执行结果更新监控参数显示
        /// </summary>
        private void UpdateMonitorParametersFromResult(DeviceParameters outputParameters)
        {
            foreach (var checkBox in _monitorParameterCheckBoxes)
            {
                if (checkBox.Tag is ParameterBase originalParam)
                {
                    var updatedParam = outputParameters.Variables
                        .FirstOrDefault(p => p.Name == originalParam.Name);

                    if (updatedParam != null)
                    {
                        checkBox.Content = updatedParam.Name; // 只显示参数名称，不显示值
                        checkBox.Tag = updatedParam;
                    }
                }
            }

            Debug.WriteLine($"监控参数显示已从执行结果更新（仅显示名称）");
        }

        /// <summary>
        /// 从命令参数更新监控参数显示
        /// </summary>
        private void UpdateMonitorParametersFromCommandParameters(DeviceParameters commandParameters)
        {
            foreach (var checkBox in _monitorParameterCheckBoxes)
            {
                if (checkBox.Tag is ParameterBase originalParam)
                {
                    var updatedParam = commandParameters.Variables
                        .FirstOrDefault(p => p.Name == originalParam.Name);

                    if (updatedParam != null)
                    {
                        checkBox.Content = updatedParam.Name; // 只显示参数名称，不显示值
                        checkBox.Tag = updatedParam;
                    }
                }
            }
        }

        #endregion

        #region *** 获取参数方法 ***

        /// <summary>
        /// 获取当前选中的监控参数
        /// </summary>
        public List<ParameterBase> GetSelectedMonitorParameters()
        {
            var selectedParams = new List<ParameterBase>();

            try
            {
                Debug.WriteLine("获取当前命令的选中参数");

                // 先保存当前状态，确保最新的选择被包含
                if (!string.IsNullOrEmpty(_currentCommandName) && _monitorParameterCheckBoxes.Count > 0)
                {
                    SaveCurrentCommandSelections();
                }

                foreach (var checkBox in _monitorParameterCheckBoxes)
                {
                    if (checkBox.IsChecked == true && checkBox.Tag is ParameterBase param)
                    {
                        selectedParams.Add(param);
                        Debug.WriteLine($"  当前命令选中参数: {param.Name}");
                    }
                }

                Debug.WriteLine($"当前命令共 {selectedParams.Count} 个选中的监控参数");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取当前命令选中监控参数失败: {ex.Message}");
            }

            return selectedParams;
        }

        /// <summary>
        /// 获取所有命令的选中监控参数
        /// </summary>
        private List<ParameterBase> GetAllSelectedMonitorParameters()
        {
            var allSelectedParams = new List<ParameterBase>();

            try
            {
                Debug.WriteLine("\n=== 开始收集所有命令的选中参数 ===");

                // 先保存当前命令状态，确保最新的选择被包含
                if (!string.IsNullOrEmpty(_currentCommandName) && _monitorParameterCheckBoxes.Count > 0)
                {
                    SaveCurrentCommandSelections();
                }

                // 从保存的状态字典中收集所有选中的参数
                var selectedParametersByCommand = new Dictionary<string, HashSet<string>>();

                // 整理选中的参数（按命令分组）
                foreach (var kvp in _monitorParameterSelections)
                {
                    if (kvp.Value) // 只处理选中的参数
                    {
                        var parts = kvp.Key.Split('#');
                        if (parts.Length == 2)
                        {
                            var commandName = parts[0];
                            var parameterName = parts[1];

                            if (!selectedParametersByCommand.ContainsKey(commandName))
                            {
                                selectedParametersByCommand[commandName] = new HashSet<string>();
                            }
                            selectedParametersByCommand[commandName].Add(parameterName);
                        }
                    }
                }

                Debug.WriteLine($"找到 {selectedParametersByCommand.Count} 个命令有选中的参数");

                // 从设备的命令中查找对应的参数对象
                if (_canvasDevice?.Device?.Commands != null)
                {
                    foreach (var command in _canvasDevice.Device.Commands)
                    {
                        if (selectedParametersByCommand.TryGetValue(command.Name, out var selectedParamNames))
                        {
                            Debug.WriteLine($"处理命令 '{command.Name}' 的 {selectedParamNames.Count} 个选中参数");

                            if (command.OutputParameters?.Variables != null)
                            {
                                foreach (var param in command.OutputParameters.Variables)
                                {
                                    if (selectedParamNames.Contains(param.Name))
                                    {
                                        allSelectedParams.Add(param);
                                        Debug.WriteLine($"  添加参数: {command.Name}#{param.Name}");
                                    }
                                }
                            }
                        }
                    }
                }

                Debug.WriteLine($"=== 收集完成，总共 {allSelectedParams.Count} 个选中参数 ===\n");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取所有选中监控参数失败: {ex.Message}");
            }

            return allSelectedParams;
        }

        #endregion

        #region *** 验证方法 ***

        /// <summary>
        /// 验证命令输入参数
        /// </summary>
        private bool ValidateCommandInputParameters(DeviceCommand command)
        {
            if (command?.InputParameters?.Variables?.Any() != true)
            {
                return true;
            }

            return command.InputParameters.ValidateAll();
        }

        /// <summary>
        /// 获取命令输入参数的验证错误
        /// </summary>
        private List<string> GetCommandInputValidationErrors(DeviceCommand command)
        {
            if (command?.InputParameters?.Variables?.Any() != true)
            {
                return new List<string>();
            }

            return command.InputParameters.GetValidationErrors();
        }

        /// <summary>
        /// 调试方法：打印所有保存的选择状态
        /// </summary>
        private void PrintAllSavedSelections()
        {
            try
            {
                Debug.WriteLine("\n=== 所有保存的监控参数选择状态 ===");

                var groupedSelections = _monitorParameterSelections
                    .Where(kvp => kvp.Value) // 只显示选中的
                    .GroupBy(kvp => kvp.Key.Split('#')[0]) // 按命令分组
                    .ToList();

                if (groupedSelections.Count == 0)
                {
                    Debug.WriteLine("没有保存任何选中的参数");
                }
                else
                {
                    foreach (var group in groupedSelections)
                    {
                        var commandName = group.Key;
                        var parameters = group.Select(kvp => kvp.Key.Split('#')[1]).ToList();
                        Debug.WriteLine($"命令 '{commandName}': {parameters.Count} 个参数 [{string.Join(", ", parameters)}]");
                    }
                }

                Debug.WriteLine("=== 状态打印完成 ===\n");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"打印状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 调试方法：显示当前选择状态详情
        /// </summary>
        public void DebugCurrentSelectionDetails()
        {
            try
            {
                Debug.WriteLine("\n=== 当前选择状态详情 ===");
                Debug.WriteLine($"当前命令: {_currentCommandName}");

                var selectedStates = _monitorParameterSelections.Where(kvp => kvp.Value).ToList();
                var unselectedStates = _monitorParameterSelections.Where(kvp => !kvp.Value).ToList();

                Debug.WriteLine($"选中状态 ({selectedStates.Count} 个):");
                foreach (var state in selectedStates)
                {
                    Debug.WriteLine($"  {state.Key} = {state.Value}");
                }

                Debug.WriteLine($"未选中状态 ({unselectedStates.Count} 个):");
                foreach (var state in unselectedStates)
                {
                    Debug.WriteLine($"  {state.Key} = {state.Value}");
                }

                Debug.WriteLine("=== 详情显示完成 ===\n");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示选择状态详情失败: {ex.Message}");
            }
        }

        #endregion

        #region *** 事件处理 ***

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_currentCommandName) && _monitorParameterCheckBoxes.Count > 0)
                {
                    Debug.WriteLine("关闭前保存当前命令状态");
                    SaveCurrentCommandSelections();
                }

                PrintAllSavedSelections();
                RestoreOriginalSimulationMode();

                var selectedParams = GetAllSelectedMonitorParameters();

                //关键修复：无论 selectedParams 是否为空，都必须触发事件
                // 空列表 → OnMonitoringParametersChanged → RemoveMonitoringCard，卡片才会被移除
                var args = new MonitoringParametersChangedEventArgs
                {
                    DeviceId = _canvasDevice?.DeviceId,
                    DeviceName = _deviceDisplayName ?? _canvasDevice?.Device?.Name,
                    SelectedParameters = selectedParams   // 可以是空列表
                };

                Debug.WriteLine($"发布监控参数变化事件: 设备 {args.DeviceName}, 参数数量 {selectedParams.Count}");
                MonitoringParametersChanged?.Invoke(this, args);

                CloseRequested?.Invoke(this, EventArgs.Empty);
                DialogResult?.Invoke(this, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"关闭处理失败: {ex.Message}");
                CloseRequested?.Invoke(this, EventArgs.Empty);
                DialogResult?.Invoke(this, false);
            }
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isInitializing = true; // 暂时阻止事件处理

                foreach (var checkBox in _monitorParameterCheckBoxes)
                {
                    checkBox.IsChecked = true;
                }

                _isInitializing = false; // 恢复事件处理

                // 手动保存状态
                SaveCurrentCommandSelections();

                Debug.WriteLine($"已全选命令 '{_currentCommandName}' 的 {_monitorParameterCheckBoxes.Count} 个监控参数");
            }
            catch (Exception ex)
            {
                _isInitializing = false;
                Debug.WriteLine($"全选操作失败: {ex.Message}");
            }
        }

        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isInitializing = true; // 暂时阻止事件处理

                foreach (var checkBox in _monitorParameterCheckBoxes)
                {
                    checkBox.IsChecked = false;
                }

                _isInitializing = false; // 恢复事件处理

                // 手动保存状态
                SaveCurrentCommandSelections();

                Debug.WriteLine($"已清除命令 '{_currentCommandName}' 的 {_monitorParameterCheckBoxes.Count} 个监控参数的选择");
            }
            catch (Exception ex)
            {
                _isInitializing = false;
                Debug.WriteLine($"清除操作失败: {ex.Message}");
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_canvasDevice?.Device == null)
                {
                    MessageBox.Show("设备对象为空，无法连接", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Debug.WriteLine($"正在连接设备: {_canvasDevice.Device.Name} (强制使用实际模式)");

                // 强制设备进入实际模式
                ForceDeviceToActualMode();
                EnsureDeviceInActualMode();

                Debug.WriteLine($"设备 {_canvasDevice.Device.Name} 当前模拟模式状态: {(_canvasDevice.Device.IsSimulationMode ? "模拟模式" : "实际模式")}");

                if (_canvasDevice.Device.ConnectionStatus == DeviceConnectionStatus.Disconnected)
                {
                    _isDeviceConnected = await _canvasDevice.Device.ConnectAsync();
                }
                else
                {
                    _isDeviceConnected = true;
                }

                UpdateConnectionUI();

                if (_isDeviceConnected)
                {
                    Debug.WriteLine($"设备 {_canvasDevice.Device.Name} 连接成功（实际模式）");
                    MessageBox.Show($"设备 '{_canvasDevice.Device.Name}' 连接成功！\n\n注意：此连接使用实际硬件模式，所有操作都将作用于真实设备。",
                        "连接成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Debug.WriteLine($"设备 {_canvasDevice.Device.Name} 连接失败");
                    MessageBox.Show($"设备 '{_canvasDevice.Device.Name}' 连接失败！",
                        "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"连接设备失败: {ex.Message}");
                Debug.WriteLine($"异常详情: {ex}");

                _isDeviceConnected = false;
                UpdateConnectionUI();

                MessageBox.Show($"连接设备失败：{ex.Message}\n\n请检查设备是否正确连接，以及设备参数配置是否正确。",
                    "连接错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_canvasDevice?.Device == null)
                {
                    MessageBox.Show("设备对象为空，无法断开连接", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Debug.WriteLine($"正在断开设备连接: {_canvasDevice.Device.Name}");

                if (_canvasDevice.Device.ConnectionStatus == DeviceConnectionStatus.Connected)
                {
                    var result = await _canvasDevice.Device.DisconnectAsync();
                    if (result) _isDeviceConnected = false;
                }
                else
                {
                    _isDeviceConnected = false;
                }

                UpdateConnectionUI();

                Debug.WriteLine($"设备 {_canvasDevice.Device.Name} 已断开连接");
                MessageBox.Show($"设备 '{_canvasDevice.Device.Name}' 已断开连接！", "断开连接",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"断开设备连接失败: {ex.Message}");
                Debug.WriteLine($"异常详情: {ex}");

                _isDeviceConnected = false;
                UpdateConnectionUI();

                MessageBox.Show($"断开设备连接失败：{ex.Message}", "断开错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_isDeviceConnected)
                {
                    MessageBox.Show("设备未连接，请先连接设备", "设备未连接",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 执行前确保设备处于实际模式
                EnsureDeviceInActualMode();

                Debug.WriteLine($"执行命令（实际模式），设备模拟状态: {(_canvasDevice.Device.IsSimulationMode ? "模拟模式" : "实际模式")}");

                if (_selectedCommand != null)
                {
                    if (!ValidateCommandInputParameters(_selectedCommand))
                    {
                        var errors = GetCommandInputValidationErrors(_selectedCommand);
                        var errorMessage = "输入参数验证失败：\n" + string.Join("\n", errors);
                        MessageBox.Show(errorMessage, "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    Debug.WriteLine($"执行命令: {_selectedCommand.Name} (实际模式)");
                    Debug.WriteLine($"输入参数数量: {_selectedCommand.InputParameters?.Variables?.Count ?? 0}");

                    // 确认这是实际硬件操作
                    var confirmResult = MessageBox.Show(
                        $"即将执行命令 '{_selectedCommand.Name}'\n\n" +
                        " 注意：此操作将直接控制真实硬件设备，可能会对设备状态产生实际影响。\n\n" +
                        "确定要继续执行吗？",
                        "确认执行命令",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (confirmResult != MessageBoxResult.Yes)
                    {
                        Debug.WriteLine("用户取消了命令执行");
                        return;
                    }

                    var result = await  _selectedCommand.ExecuteAsync();

                    if (result.Success)
                    {
                        UpdateOutputParametersDisplay(result);

                        var selectedMonitorParams = GetSelectedMonitorParameters();

                        var message = $"命令 '{_selectedCommand.Name}' 执行成功！\n" +
                                     $" 实际硬件操作完成\n" +
                                     $"选中的监控参数: {selectedMonitorParams.Count} 个\n" +
                                     $"执行时间: {result.ExecutionTime:HH:mm:ss}";

                        if (result.OutputParameters?.Variables?.Any() == true)
                        {
                            message += $"\n输出参数: {result.OutputParameters.Variables.Count} 个";
                        }

                        MessageBox.Show(message, "执行成功", MessageBoxButton.OK, MessageBoxImage.Information);

                        Debug.WriteLine($"命令执行成功（实际模式），输出参数数量: {result.OutputParameters?.Variables?.Count ?? 0}");
                    }
                    else
                    {
                        var errorDetails = !string.IsNullOrEmpty(result.ErrorMessage)
                            ? result.ErrorMessage
                            : "未知错误";

                        if (result.ValidationErrors?.Any() == true)
                        {
                            errorDetails += "\n验证错误: " + string.Join(", ", result.ValidationErrors);
                        }

                        if (result.Exception != null)
                        {
                            errorDetails += $"\n异常: {result.Exception.Message}";
                            Debug.WriteLine($"命令执行异常（实际模式）: {result.Exception}");
                        }

                        MessageBox.Show($"命令执行失败：{errorDetails}\n\n💡 提示：请检查设备状态和参数配置", "执行错误",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    MessageBox.Show("请先选择一个命令", "未选择命令",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"执行命令失败（实际模式）: {ex}");
                MessageBox.Show($"执行命令时发生异常：{ex.Message}\n\n请检查设备连接状态和命令参数。", "执行异常",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }

    public class MonitoringParametersChangedEventArgs : EventArgs
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public List<ParameterBase> SelectedParameters { get; set; }
    }
}