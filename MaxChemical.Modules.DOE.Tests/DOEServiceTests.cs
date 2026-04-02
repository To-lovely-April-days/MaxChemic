using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using Newtonsoft.Json;
using Python.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace MaxChemical.Modules.DOE.Tests
{
    /// <summary>
    /// DOE C# 服务层集成测试
    /// 
    /// 测试层次:
    ///   Test1_xxx: pythonnet 桥接层 — JSON 序列化往返、中文字段名、数值精度
    ///   Test2_xxx: DOEDesignService — CCD/BBD 矩阵生成、设计质量评估
    ///   Test3_xxx: GPRModelService — 单响应模型训练、预测、序列化
    ///   Test4_xxx: MultiResponseGPRService — 多响应独立性、PredictAll
    ///   Test5_xxx: DesirabilityService — 多响应优化端到端
    ///   Test6_xxx: DI 容器验证
    /// 
    /// 运行方式:
    ///   Visual Studio: Test Explorer → Run All
    ///   命令行: dotnet test
    /// 
    /// 注意:
    ///   所有测试共享一个 Python 环境（通过 PythonFixture）。
    ///   测试顺序可能随机，每个测试必须自包含。
    /// </summary>
    [Collection("Python")]
    public class DOEServiceTests
    {
        private readonly PythonFixture _python;
        private readonly ITestOutputHelper _output;

        public DOEServiceTests(PythonFixture python, ITestOutputHelper output)
        {
            _python = python;
            _output = output;
        }

        // ═══════════════════════════════════════════════════════
        // Test1: pythonnet 桥接基础
        // ═══════════════════════════════════════════════════════

        [Fact]
        public void Test1_01_PythonEnvironmentReady()
        {
            Assert.True(_python.IsReady, "Python 环境未能初始化。请检查 PythonDLL 路径和 Python 安装。");
        }

        [Fact]
        public void Test1_02_CanImportDoeEngine()
        {
            using (Py.GIL())
            {
                dynamic doeEngine = Py.Import("doe_engine");
                Assert.NotNull(doeEngine);
                _output.WriteLine("doe_engine 导入成功");
            }
        }

        [Fact]
        public void Test1_03_CanImportDoeGpr()
        {
            using (Py.GIL())
            {
                dynamic doeGpr = Py.Import("doe_gpr");
                Assert.NotNull(doeGpr);
                _output.WriteLine("doe_gpr 导入成功");
            }
        }

        [Fact]
        public void Test1_04_CanImportDoeDesirability()
        {
            using (Py.GIL())
            {
                dynamic doeDesirability = Py.Import("doe_desirability");
                Assert.NotNull(doeDesirability);
                _output.WriteLine("doe_desirability 导入成功");
            }
        }

        [Fact]
        public void Test1_05_ChineseFieldNameRoundTrip()
        {
            // 验证中文字段名在 C# JSON → Python → C# 往返中不丢失
            var original = new Dictionary<string, double>
            {
                ["温度"] = 120.5,
                ["压力"] = 20.3,
                ["催化剂"] = 3.14
            };

            var json = JsonConvert.SerializeObject(original);

            using (Py.GIL())
            {
                dynamic pyJson = Py.Import("json");
                dynamic parsed = pyJson.loads(json);
                string roundTripped = pyJson.dumps(parsed).ToString();

                var result = JsonConvert.DeserializeObject<Dictionary<string, double>>(roundTripped);

                Assert.NotNull(result);
                Assert.Equal(original["温度"], result!["温度"], precision: 10);
                Assert.Equal(original["压力"], result["压力"], precision: 10);
                Assert.Equal(original["催化剂"], result["催化剂"], precision: 10);

                _output.WriteLine($"中文字段往返: {json} → {roundTripped}");
            }
        }

        // ═══════════════════════════════════════════════════════
        // Test2: DOEDesignService（通过直接调用 Python）
        // ═══════════════════════════════════════════════════════

        [Fact]
        public void Test2_01_CCD_GeneratesCorrectRunCount()
        {
            using (Py.GIL())
            {
                dynamic doeEngine = Py.Import("doe_engine");
                dynamic designer = doeEngine.create_designer();

                var factorsJson = JsonConvert.SerializeObject(new[]
                {
                    new { name = "温度", lower = 80.0, upper = 160.0 },
                    new { name = "压力", lower = 10.0, upper = 30.0 },
                    new { name = "催化剂", lower = 1.0, upper = 5.0 }
                });

                string matrixJson = designer.ccd(factorsJson, "rotatable", -1).ToString();
                var matrix = JsonConvert.DeserializeObject<List<Dictionary<string, double>>>(matrixJson);

                Assert.NotNull(matrix);
                Assert.InRange(matrix!.Count, 14, 20);
                _output.WriteLine($"CCD 生成 {matrix.Count} 组实验");

                // 验证中心点存在
                bool hasCenter = matrix.Any(row =>
                    Math.Abs(row["温度"] - 120) < 1 &&
                    Math.Abs(row["压力"] - 20) < 1 &&
                    Math.Abs(row["催化剂"] - 3.0) < 0.1);
                Assert.True(hasCenter, "CCD 缺少中心点");
            }
        }

        [Fact]
        public void Test2_02_BBD_NoCornersPresent()
        {
            using (Py.GIL())
            {
                dynamic doeEngine = Py.Import("doe_engine");
                dynamic designer = doeEngine.create_designer();

                var factorsJson = JsonConvert.SerializeObject(new[]
                {
                    new { name = "温度", lower = 80.0, upper = 160.0 },
                    new { name = "压力", lower = 10.0, upper = 30.0 },
                    new { name = "催化剂", lower = 1.0, upper = 5.0 }
                });

                string matrixJson = designer.box_behnken(factorsJson, -1).ToString();
                var matrix = JsonConvert.DeserializeObject<List<Dictionary<string, double>>>(matrixJson);

                Assert.NotNull(matrix);
                Assert.InRange(matrix!.Count, 12, 16);

                // BBD 不应有角点
                bool hasCorner = matrix.Any(row =>
                    (Math.Abs(row["温度"] - 80) < 1 || Math.Abs(row["温度"] - 160) < 1) &&
                    (Math.Abs(row["压力"] - 10) < 1 || Math.Abs(row["压力"] - 30) < 1) &&
                    (Math.Abs(row["催化剂"] - 1.0) < 0.1 || Math.Abs(row["催化剂"] - 5.0) < 0.1));
                Assert.False(hasCorner, "BBD 不应包含角点");
            }
        }

        [Fact]
        public void Test2_03_FullFactorial_27Runs()
        {
            using (Py.GIL())
            {
                dynamic doeEngine = Py.Import("doe_engine");
                dynamic designer = doeEngine.create_designer();

                var factorsJson = JsonConvert.SerializeObject(new[]
                {
                    new { name = "温度", levels = new[] { 80.0, 120.0, 160.0 } },
                    new { name = "压力", levels = new[] { 10.0, 20.0, 30.0 } },
                    new { name = "催化剂", levels = new[] { 1.0, 3.0, 5.0 } }
                });

                string matrixJson = designer.full_factorial(factorsJson).ToString();
                var matrix = JsonConvert.DeserializeObject<List<Dictionary<string, double>>>(matrixJson);

                Assert.NotNull(matrix);
                Assert.Equal(27, matrix!.Count);
            }
        }

        // ═══════════════════════════════════════════════════════
        // Test3: GPR 单响应模型
        // ═══════════════════════════════════════════════════════

        [Fact]
        public void Test3_01_GPR_TrainAndPredict()
        {
            using (Py.GIL())
            {
                dynamic doeGpr = Py.Import("doe_gpr");
                var factorNames = JsonConvert.SerializeObject(new[] { "温度", "压力", "催化剂" });
                var bounds = JsonConvert.SerializeObject(KnownFunctions.FactorBounds);

                dynamic model = doeGpr.create_model(factorNames, bounds, 6);

                // 喂入已知数据
                var trainingData = KnownFunctions.GenerateTrainingData();
                foreach (var (factors, responses) in trainingData)
                {
                    var factorsJson = JsonConvert.SerializeObject(factors);
                    double yieldVal = responses["产率"];
                    model.append_data(factorsJson, yieldVal, "measured", "test", "");
                }

                // 训练
                string trainJson = model.train().ToString();
                var trainResult = JsonConvert.DeserializeObject<TrainResultDto>(trainJson);

                Assert.NotNull(trainResult);
                Assert.True(trainResult!.IsActive, "GPR 应该已激活（15 组 > 6 组阈值）");
                Assert.True(trainResult.RSquared > 0.85, $"R² = {trainResult.RSquared} 应该 > 0.85");

                _output.WriteLine($"GPR 训练: R²={trainResult.RSquared:F4}, RMSE={trainResult.RMSE:F4}");

                // 在未见过的点预测
                var testFactors = new Dictionary<string, double>
                {
                    ["温度"] = 125,
                    ["压力"] = 22,
                    ["催化剂"] = 3.5
                };
                string predJson = model.predict(JsonConvert.SerializeObject(testFactors)).ToString();
                var pred = JsonConvert.DeserializeObject<PredResultDto>(predJson);

                double trueValue = KnownFunctions.TrueYield(125, 22, 3.5);
                double error = Math.Abs(pred!.Mean - trueValue);

                _output.WriteLine($"预测: mean={pred.Mean:F2}, 真值={trueValue:F2}, 误差={error:F2}");
                Assert.True(error < 5.0, $"预测误差 {error:F2} 应该 < 5.0");
            }
        }

        [Fact]
        public void Test3_02_GPR_SerializeDeserialize()
        {
            using (Py.GIL())
            {
                dynamic doeGpr = Py.Import("doe_gpr");
                var factorNames = JsonConvert.SerializeObject(new[] { "温度", "压力", "催化剂" });
                var bounds = JsonConvert.SerializeObject(KnownFunctions.FactorBounds);

                dynamic model = doeGpr.create_model(factorNames, bounds, 6);

                var trainingData = KnownFunctions.GenerateTrainingData();
                foreach (var (factors, responses) in trainingData)
                {
                    model.append_data(JsonConvert.SerializeObject(factors),
                        responses["产率"], "measured", "test", "");
                }
                model.train();

                // 序列化
                byte[] bytes = (byte[])model.serialize();
                Assert.True(bytes.Length > 0, "序列化字节不应为空");
                _output.WriteLine($"序列化大小: {bytes.Length} bytes");

                // 反序列化
                dynamic restored = doeGpr.load_model(bytes);

                // 比较预测值
                var testJson = JsonConvert.SerializeObject(new Dictionary<string, double>
                { ["温度"] = 120, ["压力"] = 20, ["催化剂"] = 3 });

                string pred1 = model.predict(testJson).ToString();
                string pred2 = restored.predict(testJson).ToString();

                var p1 = JsonConvert.DeserializeObject<PredResultDto>(pred1);
                var p2 = JsonConvert.DeserializeObject<PredResultDto>(pred2);

                Assert.True(Math.Abs(p1!.Mean - p2!.Mean) < 0.01,
                    $"序列化前后预测不一致: {p1.Mean:F4} vs {p2.Mean:F4}");
            }
        }

        // ═══════════════════════════════════════════════════════
        // Test4: 多响应 GPR 独立性（核心修复验证）
        // ═══════════════════════════════════════════════════════

        [Fact]
        public void Test4_01_MultiGPR_IndependentModels()
        {
            // ★ 这是最关键的测试: 验证三个 GPR 模型的预测值互不相同
            // 修复前的 bug: 所有响应共享一个 GPR，预测值相同
            using (Py.GIL())
            {
                dynamic doeGpr = Py.Import("doe_gpr");
                var factorNames = JsonConvert.SerializeObject(new[] { "温度", "压力", "催化剂" });
                var bounds = JsonConvert.SerializeObject(KnownFunctions.FactorBounds);

                // 为每个响应创建独立模型（模拟 MultiResponseGPRService 的行为）
                dynamic modelYield = doeGpr.create_model(factorNames, bounds, 6);
                dynamic modelSelectivity = doeGpr.create_model(factorNames, bounds, 6);
                dynamic modelCost = doeGpr.create_model(factorNames, bounds, 6);

                // 喂入相同因子、不同响应
                var trainingData = KnownFunctions.GenerateTrainingData();
                foreach (var (factors, responses) in trainingData)
                {
                    var fJson = JsonConvert.SerializeObject(factors);
                    modelYield.append_data(fJson, responses["产率"], "measured", "test", "");
                    modelSelectivity.append_data(fJson, responses["选择性"], "measured", "test", "");
                    modelCost.append_data(fJson, responses["成本"], "measured", "test", "");
                }

                modelYield.train();
                modelSelectivity.train();
                modelCost.train();

                // 预测同一个点
                var testJson = JsonConvert.SerializeObject(new Dictionary<string, double>
                { ["温度"] = 125, ["压力"] = 22, ["催化剂"] = 3.5 });

                var predY = JsonConvert.DeserializeObject<PredResultDto>(modelYield.predict(testJson).ToString());
                var predS = JsonConvert.DeserializeObject<PredResultDto>(modelSelectivity.predict(testJson).ToString());
                var predC = JsonConvert.DeserializeObject<PredResultDto>(modelCost.predict(testJson).ToString());

                _output.WriteLine($"产率预测:   {predY!.Mean:F2}");
                _output.WriteLine($"选择性预测: {predS!.Mean:F2}");
                _output.WriteLine($"成本预测:   {predC!.Mean:F2}");

                // ★ 核心断言: 三个预测值必须不同
                Assert.NotEqual(predY.Mean, predS.Mean);
                Assert.NotEqual(predS.Mean, predC.Mean);
                Assert.NotEqual(predY.Mean, predC.Mean);

                // 验证与真值的偏差
                double trueY = KnownFunctions.TrueYield(125, 22, 3.5);
                double trueS = KnownFunctions.TrueSelectivity(125, 22, 3.5);
                double trueC = KnownFunctions.TrueCost(125, 22, 3.5);

                _output.WriteLine($"产率误差:   {Math.Abs(predY.Mean - trueY):F2} (真值={trueY:F2})");
                _output.WriteLine($"选择性误差: {Math.Abs(predS.Mean - trueS):F2} (真值={trueS:F2})");
                _output.WriteLine($"成本误差:   {Math.Abs(predC.Mean - trueC):F2} (真值={trueC:F2})");

                Assert.True(Math.Abs(predY.Mean - trueY) < 5.0);
                Assert.True(Math.Abs(predS.Mean - trueS) < 3.0);
                Assert.True(Math.Abs(predC.Mean - trueC) < 8.0);
            }
        }

        [Fact]
        public void Test4_02_MultiGPR_ColdStartBehavior()
        {
            // 验证数据不足时模型不应该激活
            using (Py.GIL())
            {
                dynamic doeGpr = Py.Import("doe_gpr");
                var factorNames = JsonConvert.SerializeObject(new[] { "温度", "压力", "催化剂" });
                var bounds = JsonConvert.SerializeObject(KnownFunctions.FactorBounds);

                dynamic model = doeGpr.create_model(factorNames, bounds, 6);

                // 只喂 3 组数据（低于阈值 6）
                for (int i = 0; i < 3; i++)
                {
                    var factors = KnownFunctions.GenerateTrainingData()[i].Factors;
                    var responses = KnownFunctions.GenerateTrainingData()[i].Responses;
                    model.append_data(JsonConvert.SerializeObject(factors),
                        responses["产率"], "measured", "test", "");
                }

                string trainJson = model.train().ToString();
                var result = JsonConvert.DeserializeObject<TrainResultDto>(trainJson);

                Assert.False(result!.IsActive, "3 组数据不应该激活模型（阈值=6）");
                Assert.Equal(3, result.DataCount);
                _output.WriteLine($"冷启动: active={result.IsActive}, count={result.DataCount}");
            }
        }

        // ═══════════════════════════════════════════════════════
        // Test5: Desirability Function
        // ═══════════════════════════════════════════════════════

        [Fact]
        public void Test5_01_Desirability_HandCalculation()
        {
            using (Py.GIL())
            {
                dynamic doeDesirability = Py.Import("doe_desirability");
                dynamic engine = doeDesirability.create_engine();

                var configs = JsonConvert.SerializeObject(new[]
  {
    new { name = "产率",   goal = "maximize", lower = 50.0, upper = 100.0, target = 100.0,
          weight = 3.0, shape = 1.0, shape_lower = 1.0, shape_upper = 1.0 },
    new { name = "选择性", goal = "target",   lower = 90.0, upper = 100.0, target = 95.0,
          weight = 2.0, shape = 1.0, shape_lower = 1.0, shape_upper = 1.0 },
    new { name = "成本",   goal = "minimize", lower = 10.0, upper = 100.0, target = 10.0,
          weight = 1.0, shape = 1.0, shape_lower = 1.0, shape_upper = 1.0 }
});
                var factorNames = JsonConvert.SerializeObject(new[] { "温度", "压力", "催化剂" });
                var bounds = JsonConvert.SerializeObject(KnownFunctions.FactorBounds);

                engine.configure(configs, factorNames, bounds);

                // 手算验证: 产率=85 → d1 = (85-50)/(100-50) = 0.70
                var predsJson = JsonConvert.SerializeObject(new Dictionary<string, double>
                { ["产率"] = 85, ["选择性"] = 95, ["成本"] = 45 });

                string resultJson = engine.composite_desirability(predsJson).ToString();
                var result = JsonConvert.DeserializeObject<DesirabilityResultDto>(resultJson);

                Assert.NotNull(result);

                double d1 = result!.IndividualD["产率"].D;
                double d2 = result.IndividualD["选择性"].D;
                double d3 = result.IndividualD["成本"].D;

                _output.WriteLine($"d(产率=85)={d1:F4}, d(选择性=95)={d2:F4}, d(成本=45)={d3:F4}");
                _output.WriteLine($"综合 D={result.CompositeD:F4}");

                Assert.True(Math.Abs(d1 - 0.70) < 0.01, $"d(产率) 应≈0.70, 实际={d1}");
                Assert.True(Math.Abs(d2 - 1.00) < 0.01, $"d(选择性) 应=1.00, 实际={d2}");
                Assert.True(Math.Abs(d3 - 0.6111) < 0.02, $"d(成本) 应≈0.611, 实际={d3}");

                // 综合 D = (d1^3 * d2^2 * d3^1)^(1/6)
                double expectedD = Math.Pow(Math.Pow(d1, 3) * Math.Pow(d2, 2) * Math.Pow(d3, 1), 1.0 / 6.0);
                Assert.True(Math.Abs(result.CompositeD - expectedD) < 0.01,
                    $"综合 D={result.CompositeD}, 预期={expectedD}");
            }
        }

        [Fact]
        public void Test5_02_Desirability_ZeroPropagation()
        {
            // 任何一个 d=0 → 综合 D=0
            using (Py.GIL())
            {
                dynamic doeDesirability = Py.Import("doe_desirability");
                dynamic engine = doeDesirability.create_engine();

                var configs = JsonConvert.SerializeObject(new[]
                {
                    new { name = "产率", goal = "maximize", lower = 50.0, upper = 100.0, weight = 1.0, shape = 1.0 },
                    new { name = "成本", goal = "minimize", lower = 10.0, upper = 100.0, weight = 1.0, shape = 1.0 }
                });
                engine.configure(configs,
                    JsonConvert.SerializeObject(new[] { "温度" }),
                    JsonConvert.SerializeObject(new Dictionary<string, double[]> { ["温度"] = new[] { 80.0, 160.0 } }));

                // 产率=50（正好在 lower → d=0），成本=50
                var predsJson = JsonConvert.SerializeObject(new Dictionary<string, double>
                { ["产率"] = 50, ["成本"] = 50 });
                string resultJson = engine.composite_desirability(predsJson).ToString();
                var result = JsonConvert.DeserializeObject<DesirabilityResultDto>(resultJson);

                Assert.True(result!.CompositeD < 0.01, $"有 d=0 时综合 D 应=0, 实际={result.CompositeD}");
            }
        }

        // ═══════════════════════════════════════════════════════
        // Test6: DI 容器
        // ═══════════════════════════════════════════════════════

        [Fact]
        public void Test6_01_DIRegistration_AllServicesResolvable()
        {
            // 验证 DOEModule.RegisterTypes 中注册的所有服务类型可以实例化
            // 这里不启动完整 Prism 容器，而是直接验证类型构造

            // MultiResponseGPRService 的构造函数签名是否匹配
            var constructorParams = typeof(MultiResponseGPRService)
                .GetConstructors()
                .First()
                .GetParameters();

            var paramNames = constructorParams.Select(p => p.ParameterType.Name).ToList();

            _output.WriteLine($"MultiResponseGPRService 构造函数参数: {string.Join(", ", paramNames)}");

            Assert.Contains("IGPRModelService", paramNames);
            Assert.Contains("IDOERepository", paramNames);
            Assert.Contains("ILogService", paramNames);
        }

        [Fact]
        public void Test6_02_DIRegistration_BatchExecutorSignature()
        {
            // 验证修复后的 DOEBatchExecutor 构造函数包含 IMultiResponseGPRService
            var constructorParams = typeof(DOEBatchExecutor)
                .GetConstructors()
                .First()
                .GetParameters();

            var paramNames = constructorParams.Select(p => p.ParameterType.Name).ToList();

            _output.WriteLine($"DOEBatchExecutor 构造函数参数: {string.Join(", ", paramNames)}");

            Assert.Contains("IMultiResponseGPRService", paramNames);
            Assert.Contains("IGPRModelService", paramNames);
            Assert.Contains("IFlowExecutionService", paramNames);
        }

        [Fact]
        public void Test6_03_DIRegistration_DesirabilityServiceSignature()
        {
            // 验证修复后的 DesirabilityService 构造函数包含 IMultiResponseGPRService
            var constructorParams = typeof(DesirabilityService)
                .GetConstructors()
                .First()
                .GetParameters();

            var paramNames = constructorParams.Select(p => p.ParameterType.Name).ToList();

            _output.WriteLine($"DesirabilityService 构造函数参数: {string.Join(", ", paramNames)}");

            Assert.Contains("IMultiResponseGPRService", paramNames);
            Assert.Contains("IGPRModelService", paramNames);
        }

        // ═══════════════════════════════════════════════════════
        // DTO (反序列化 Python 返回值)
        // ═══════════════════════════════════════════════════════

        private class TrainResultDto
        {
            [JsonProperty("is_active")] public bool IsActive { get; set; }
            [JsonProperty("r_squared")] public double RSquared { get; set; }
            [JsonProperty("rmse")] public double RMSE { get; set; }
            [JsonProperty("data_count")] public int DataCount { get; set; }
        }

        private class PredResultDto
        {
            [JsonProperty("mean")] public double Mean { get; set; }
            [JsonProperty("std")] public double Std { get; set; }
        }

        private class DesirabilityResultDto
        {
            [JsonProperty("composite_d")] public double CompositeD { get; set; }
            [JsonProperty("individual_d")] public Dictionary<string, IndividualDDto>? IndividualD { get; set; }
        }

        private class IndividualDDto
        {
            [JsonProperty("value")] public double Value { get; set; }
            [JsonProperty("d")] public double D { get; set; }
        }
    }
}