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
        public string Action { get; set; }           // 动作: "SetParameter", "CreateDevice", "ControlState"
        public string DeviceType { get; set; }       // 设备类型: "柱塞泵", "流量计"
        public string DeviceName { get; set; }       // 设备名称（如果指定）
        public string ParameterName { get; set; }    // 参数名称
        public double? ParameterValue { get; set; }  // 参数值
        public bool? TargetState { get; set; }       // 目标状态（开/关）
        public string RawCommand { get; set; }       // 原始命令
        public float Confidence { get; set; }        // 置信度
    }

    public class OllamaLlmService
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
        /// 🔥 使用大模型理解命令意图
        /// </summary>
        public async Task<CommandIntent> UnderstandCommand(string userCommand)
        {
            try
            {
                _logger.LogInformation($"🤖 使用大模型理解命令: {userCommand}");

                // 构建 Prompt
                string prompt = BuildPrompt(userCommand);

                // 调用 Ollama API
                var requestBody = new
                {
                    model = ModelName,
                    prompt = prompt,
                    stream = false,
                    options = new
                    {
                        temperature = 0.1,  // 低温度，更确定性的输出
                        top_p = 0.9
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(OllamaApiUrl, content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<OllamaResponse>(responseJson);

                // 解析大模型的回复
                var intent = ParseLlmResponse(result.response, userCommand);

                _logger.LogInformation($"✅ 意图识别成功:");
                _logger.LogInformation($"   动作: {intent.Action}");
                _logger.LogInformation($"   设备: {intent.DeviceType}");
                _logger.LogInformation($"   参数: {intent.ParameterName} = {intent.ParameterValue}");

                return intent;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 大模型理解失败: {ex.Message}");
                return FallbackParse(userCommand); // 降级到关键词匹配
            }
        }

        /// <summary>
        /// 🔥 构建给大模型的 Prompt
        /// </summary>
        private string BuildPrompt(string userCommand)
        {
            return $@"你是一个工业设备控制系统的命令解析助手。

支持的设备类型: 柱塞泵, 流量计, 电磁阀, 换热器, 压力表
支持的动作: 设置参数, 创建设备, 控制状态

请将用户的自然语言命令解析为结构化的JSON格式。

输出格式:
{{
  ""action"": ""SetParameter"" | ""CreateDevice"" | ""ControlState"",
  ""deviceType"": ""设备类型"",
  ""deviceName"": ""具体设备名（如果有）"",
  ""parameterName"": ""参数名（如果是设置参数）"",
  ""parameterValue"": 数值（如果是设置参数）,
  ""targetState"": true|false（如果是状态控制）
}}

示例:
输入: ""设置柱塞泵流量为20""
输出: {{""action"":""SetParameter"",""deviceType"":""柱塞泵"",""parameterName"":""流量"",""parameterValue"":20}}

输入: ""创建一个流量计""
输出: {{""action"":""CreateDevice"",""deviceType"":""流量计""}}

输入: ""打开电磁阀""
输出: {{""action"":""ControlState"",""deviceType"":""电磁阀"",""targetState"":true}}

现在请解析以下命令:
用户命令: {userCommand}

只输出JSON，不要任何其他文字:";
        }

        /// <summary>
        /// 解析大模型的 JSON 回复
        /// </summary>
        private CommandIntent ParseLlmResponse(string llmResponse, string rawCommand)
        {
            try
            {
                // 提取 JSON 部分
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
                    intent.RawCommand = rawCommand;
                    intent.Confidence = 0.9f;

                    return intent;
                }

                throw new Exception("未找到有效的 JSON");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 解析大模型回复失败: {ex.Message}");
                return FallbackParse(rawCommand);
            }
        }

        /// <summary>
        /// 降级方案：关键词匹配
        /// </summary>
        private CommandIntent FallbackParse(string command)
        {
            _logger.LogWarning("⚠ 降级到关键词匹配");

            var intent = new CommandIntent
            {
                RawCommand = command,
                Confidence = 0.5f
            };

            // 简单的关键词匹配
            if (command.Contains("创建"))
            {
                intent.Action = "CreateDevice";
                if (command.Contains("柱塞泵")) intent.DeviceType = "柱塞泵";
                else if (command.Contains("流量计")) intent.DeviceType = "流量计";
            }
            else if (command.Contains("设置"))
            {
                intent.Action = "SetParameter";
                if (command.Contains("柱塞泵"))
                {
                    intent.DeviceType = "柱塞泵";
                    if (command.Contains("流量")) intent.ParameterName = "流量";
                }

                // 提取数值
                var match = System.Text.RegularExpressions.Regex.Match(command, @"\d+");
                if (match.Success)
                {
                    intent.ParameterValue = double.Parse(match.Value);
                }
            }

            return intent;
        }

        private class OllamaResponse
        {
            public string model { get; set; }
            public string response { get; set; }
            public bool done { get; set; }
        }
    }
}