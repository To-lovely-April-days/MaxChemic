using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// 命令意图识别结果
    /// </summary>
    public class CommandIntent
    {
        public string Action { get; set; }
        public string DeviceType { get; set; }
        public string DeviceName { get; set; }
        public string ParameterName { get; set; }
        public double? ParameterValue { get; set; }
        public bool? TargetState { get; set; }
        public string SystemCommand { get; set; }
        public string QueryType { get; set; }
        public string Response { get; set; }
        public string RawCommand { get; set; }
        public float Confidence { get; set; }
    }

    /// <summary>
    /// Ollama 大语言模型服务
    /// </summary>
    public class OllamaLlmService : IDisposable
    {
        private readonly ILogService _logger;
        private readonly HttpClient _httpClient;
        private const string OllamaApiUrl = "http://localhost:11434/api/generate";
        private const string ModelName = "qwen2.5:1.5b";

        public OllamaLlmService(ILogService logger)
        {
            _logger = logger;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        /// <summary>
        /// 使用大模型理解用户命令意图
        /// </summary>
        public async Task<CommandIntent> UnderstandCommand(string userCommand)
        {
            try
            {
                _logger.LogInformation($"调用大模型分析命令: {userCommand}");

                string prompt = BuildPrompt(userCommand);

                var requestBody = new
                {
                    model = ModelName,
                    prompt = prompt,
                    stream = false,
                    options = new
                    {
                        temperature = 0.1,
                        top_p = 0.9
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(OllamaApiUrl, content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<OllamaResponse>(responseJson);

                var intent = ParseLlmResponse(result.response, userCommand);

                _logger.LogInformation("意图识别完成:");
                _logger.LogInformation($"  动作: {intent.Action}");
                _logger.LogInformation($"  设备: {intent.DeviceType}");
                _logger.LogInformation($"  参数: {intent.ParameterName} = {intent.ParameterValue}");
                _logger.LogInformation($"  系统命令: {intent.SystemCommand}");
                _logger.LogInformation($"  查询类型: {intent.QueryType}");

                return intent;
            }
            catch (Exception ex)
            {
                _logger.LogError($"大模型处理失败: {ex.Message}");
                return FallbackParse(userCommand);
            }
        }

        private string BuildPrompt(string userCommand)
        {
            return $@"你是小桐，一个工业设备控制系统的智能助手。

**支持的设备类型**
- 柱塞泵、流量计、电磁阀、换热器、压力表

**支持的操作**
1. 创建设备: CreateDevice
2. 设置参数: SetParameter
3. 控制状态: ControlState
4. 系统命令: SystemCommand (maximize/minimize/restore/close/settings/home)
5. 查询信息: Query (time/date/weekday)
6. 休眠: Sleep
7. 闲聊: Chat

**输出JSON格式**
{{
  ""action"": ""操作类型"",
  ""deviceType"": ""设备类型（如果有）"",
  ""parameterName"": ""参数名（如果有）"",
  ""parameterValue"": 数值（如果有）,
  ""targetState"": true/false（如果有）,
  ""systemCommand"": ""系统命令（如果有）"",
  ""queryType"": ""查询类型（如果有）"",
  ""response"": ""回复内容（如果需要）""
}}

**示例**
输入: ""帮我创建一个泵""
输出: {{""action"":""CreateDevice"",""deviceType"":""柱塞泵""}}

输入: ""把流量调到二十""
输出: {{""action"":""SetParameter"",""deviceType"":""柱塞泵"",""parameterName"":""流量"",""parameterValue"":20}}

输入: ""打开那个阀门""
输出: {{""action"":""ControlState"",""deviceType"":""电磁阀"",""targetState"":true}}

输入: ""最大化窗口""
输出: {{""action"":""SystemCommand"",""systemCommand"":""maximize""}}

输入: ""现在几点了""
输出: {{""action"":""Query"",""queryType"":""time""}}

输入: ""你好吗""
输出: {{""action"":""Chat"",""response"":""我很好，谢谢关心！""}}

输入: ""休息吧""
输出: {{""action"":""Sleep""}}

**用户输入**: {userCommand}

只输出JSON，不要其他文字:";
        }

        private CommandIntent ParseLlmResponse(string llmResponse, string rawCommand)
        {
            try
            {
                var jsonStart = llmResponse.IndexOf('{');
                var jsonEnd = llmResponse.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonStr = llmResponse.Substring(jsonStart, jsonEnd - jsonStart + 1);

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    var intent = JsonSerializer.Deserialize<CommandIntent>(jsonStr, options);
                    if (intent != null)
                    {
                        intent.RawCommand = rawCommand;
                        intent.Confidence = 0.9f;
                        return intent;
                    }
                }

                throw new Exception("未找到有效的 JSON 响应");
            }
            catch (Exception ex)
            {
                _logger.LogError($"解析大模型响应失败: {ex.Message}");
                _logger.LogError($"原始响应: {llmResponse}");
                return FallbackParse(rawCommand);
            }
        }

        private CommandIntent FallbackParse(string command)
        {
            _logger.LogWarning("使用关键词匹配降级方案");

            var intent = new CommandIntent
            {
                RawCommand = command,
                Confidence = 0.5f
            };

            if (command.Contains("最大化") || command.Contains("放大窗口") || command.Contains("全屏"))
            {
                intent.Action = "SystemCommand";
                intent.SystemCommand = "maximize";
                return intent;
            }

            if (command.Contains("最小化") || command.Contains("缩小窗口"))
            {
                intent.Action = "SystemCommand";
                intent.SystemCommand = "minimize";
                return intent;
            }

            if (command.Contains("关闭") || command.Contains("退出"))
            {
                intent.Action = "SystemCommand";
                intent.SystemCommand = "close";
                return intent;
            }

            if (command.Contains("几点") || command.Contains("时间"))
            {
                intent.Action = "Query";
                intent.QueryType = "time";
                return intent;
            }

            if (command.Contains("日期") || command.Contains("今天"))
            {
                intent.Action = "Query";
                intent.QueryType = "date";
                return intent;
            }

            if (command.Contains("星期"))
            {
                intent.Action = "Query";
                intent.QueryType = "weekday";
                return intent;
            }

            if (command.Contains("休息") || command.Contains("睡觉") || command.Contains("再见"))
            {
                intent.Action = "Sleep";
                return intent;
            }

            if (command.Contains("创建"))
            {
                intent.Action = "CreateDevice";
                if (command.Contains("柱塞泵") || command.Contains("泵")) intent.DeviceType = "柱塞泵";
                else if (command.Contains("流量计")) intent.DeviceType = "流量计";
                else if (command.Contains("电磁阀") || command.Contains("阀门")) intent.DeviceType = "电磁阀";
                else if (command.Contains("换热器")) intent.DeviceType = "换热器";
                return intent;
            }

            if (command.Contains("设置") || command.Contains("调"))
            {
                intent.Action = "SetParameter";

                if (command.Contains("柱塞泵") || command.Contains("泵"))
                {
                    intent.DeviceType = "柱塞泵";
                    if (command.Contains("流量") || command.Contains("速度"))
                        intent.ParameterName = "流量";
                }
                else if (command.Contains("流量计"))
                {
                    intent.DeviceType = "流量计";
                    intent.ParameterName = "流量";
                }
                else if (command.Contains("换热器"))
                {
                    intent.DeviceType = "换热器";
                    intent.ParameterName = "温度";
                }

                var match = System.Text.RegularExpressions.Regex.Match(command, @"\d+");
                if (match.Success)
                {
                    intent.ParameterValue = double.Parse(match.Value);
                }

                return intent;
            }

            if (command.Contains("启动") || command.Contains("开启") || command.Contains("打开"))
            {
                intent.Action = "ControlState";
                intent.TargetState = true;

                if (command.Contains("柱塞泵") || command.Contains("泵")) intent.DeviceType = "柱塞泵";
                else if (command.Contains("电磁阀") || command.Contains("阀门")) intent.DeviceType = "电磁阀";

                return intent;
            }

            if (command.Contains("停止") || command.Contains("关闭"))
            {
                intent.Action = "ControlState";
                intent.TargetState = false;

                if (command.Contains("柱塞泵") || command.Contains("泵")) intent.DeviceType = "柱塞泵";
                else if (command.Contains("电磁阀") || command.Contains("阀门")) intent.DeviceType = "电磁阀";

                return intent;
            }

            intent.Action = "Chat";
            intent.Response = "不好意思，我没有完全理解您的意思";

            return intent;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        private class OllamaResponse
        {
            public string model { get; set; }
            public string response { get; set; }
            public bool done { get; set; }
        }
    }
}