using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Service;
using DesignerFlowDocument = MaxChemical.Modules.Designer.Models.FlowDocument;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 工程专员的领域工具:新建工程文档 / 新建空白流程 / 保存工程 / 打开工艺与流程设计器。
    /// 新建类操作复用主界面「新建项目」按钮的同一套服务与事件序列(见 MainToolBarViewModel
    /// CreateNewProjectWithName / SaveProjectToFile),保证 AI 操作与手工点击走同一条业务路径。
    /// </summary>

    /// <summary>新建工程文档(含新画布),自动保存为 .mcp 到默认目录并加入最近文件。</summary>
    public sealed class CreateNewDocumentTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public CreateNewDocumentTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "create_new_document";
        public string DisplayName => "新建工程文档";
        public string Description => @"新建工程文档(项目):创建新的空工程——含新画布与空流程——并自动保存为 .mcp 文件到 文档\MaxChemical\Projects\ 目录、加入最近文件。当前工程内容会被替换(有未保存修改将丢弃)。名称必须来自用户原话或经用户确认,任务里没给名称就先向用户提问,严禁自己编。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""name"":{""type"":""string"",""description"":""新工程文档名称,必须来自用户原话,不能自己编;长度不超过 60 字""}
  },
  ""required"":[""name""]
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "name")?.Trim() ?? "";
            string warn = "";
            try { warn = _ctx.Resolve<ICanvasStateService>().HasUnsavedChanges ? "(当前有未保存的修改,将被丢弃)" : ""; } catch { }
            return $"将新建工程文档「{name}」并自动保存到默认目录{warn}。";
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return AgentToolResult.Fail("name 必填:新工程文档名称必须来自用户原话,不能自己编。");
            if (name.Length > 60)
                return AgentToolResult.Fail("名称过长(不超过 60 字),请向用户确认一个简短名称。");

            // ① 当前画布尺寸(与工具栏一致:UI 线程请求,1 秒超时回退默认 1200×800)
            double width = 1200, height = 800;
            try
            {
                using var sizeSet = new ManualResetEventSlim(false);
                AgentToolContext.RunOnUi(() =>
                {
                    try
                    {
                        _ctx.Events.GetEvent<GetCanvasSizeRequestEvent>()?.Publish((w, h) =>
                        {
                            width = w;
                            height = h;
                            sizeSet.Set();
                        });
                    }
                    catch { }
                });
                sizeSet.Wait(TimeSpan.FromSeconds(1));
            }
            catch { /* 超时/无订阅方,用默认尺寸 */ }

            // ② 创建新画布与工程文档(与 MainToolBarViewModel.CreateNewProjectWithCustomName 相同结构)
            var canvasService = _ctx.Resolve<ICanvasDataService>();
            var newCanvas = canvasService.CreateNewCanvas(width, height);
            var project = new ProjectDocument
            {
                Name = name,
                Description = $"由小桐创建于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                CanvasDesign = new CanvasDesignData
                {
                    Id = newCanvas.Id,
                    Name = newCanvas.Name,
                    CreatedTime = newCanvas.CreatedTime,
                    LastModifiedTime = newCanvas.LastModifiedTime,
                    RegionLayout = newCanvas.RegionLayout,
                    Devices = newCanvas.Devices,
                    Connections = newCanvas.Connections,
                    CanvasWidth = width,
                    CanvasHeight = height
                },
                FlowDesign = new FlowDesignData
                {
                    Name = "流程设计",
                    CanvasWidth = Math.Max(width, 1200),
                    CanvasHeight = Math.Max(height, 800),
                    StartNodeX = (Math.Max(width, 1200) - 231) / 2,
                    StartNodeY = 20
                }
            };

            // ③ 重置状态 + 界面事件序列(与工具栏 CreateNewProjectWithName 完全一致)
            var canvasState = _ctx.Resolve<ICanvasStateService>();
            canvasState.Reset();
            try
            {
                AgentToolContext.RunOnUi(() =>
                {
                    _ctx.Events.GetEvent<ClearProjectRequestEvent>()?.Publish();
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _ctx.Events.GetEvent<ProjectNameUpdatedEvent>()?.Publish(name);
                        _ctx.Events.GetEvent<NewProjectCreatedEvent>()?.Publish(project);
                        _ctx.Events.GetEvent<RegionLayoutUpdatedEvent>()?.Publish(newCanvas.RegionLayout);
                        _ctx.Events.GetEvent<NewCanvasCreatedEvent>()?.Publish(newCanvas);
                        _ctx.Events.GetEvent<StatusMessageChangedEvent>()?.Publish($"小桐已新建工程文档 '{name}'");
                    }), System.Windows.Threading.DispatcherPriority.Background);
                });
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail("工程文档已在界面创建,但界面刷新失败:" + ex.Message);
            }

            // ④ 自动保存到默认目录并加入最近文件(与工具栏 SaveProjectToFile 相同序列)
            string safeName = SanitizeFileName(name);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MaxChemical", "Projects");
            string path = Path.Combine(dir, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.mcp");
            try
            {
                Directory.CreateDirectory(dir);
                if (_ctx.Resolve<IProjectDataService>().SaveProject(project, path))
                {
                    _ctx.Resolve<IRecentFileService>().Add(path);
                    canvasState.SetCurrentFile(path);
                    canvasState.MarkAsSaved();
                    AgentToolContext.RunOnUi(() =>
                    {
                        _ctx.Events.GetEvent<MaxChemical.Modules.Designer.Event.ProjectSavedEvent>()?.Publish(path);
                        _ctx.Events.GetEvent<FileSavedEvent>()?.Publish(path);
                        _ctx.Events.GetEvent<StatusMessageChangedEvent>()?.Publish($"工程文档已保存: {Path.GetFileName(path)}");
                    });
                    return AgentToolResult.Ok(
                        $"已新建工程文档「{name}」并自动保存到:\n{path}\n" +
                        "工艺设计器已是一块空白新画布。提醒用户:可以在画布上摆设备;要搭流程时说「新建流程」或「打开流程设计器」,我可以继续。");
                }

                return AgentToolResult.Ok(
                    $"工程文档「{name}」已在界面创建(新画布),但自动保存失败。如实告知用户,并提醒用户点工具栏「保存」手动保存。");
            }
            catch (Exception ex)
            {
                return AgentToolResult.Ok(
                    $"工程文档「{name}」已在界面创建(新画布),但自动保存失败:{ex.Message}。如实告知用户,并提醒用户点工具栏「保存」手动保存。");
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "未命名工程" : name;
        }
    }

    /// <summary>新建空白工艺流程:清空流程设计器当前流程,铺一张空白流程。</summary>
    public sealed class CreateNewFlowTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public CreateNewFlowTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "create_new_flow";
        public string DisplayName => "新建空白流程";
        public string Description => @"新建空白工艺流程:清空流程设计器里当前流程,铺一张空白流程(0 个节点)。流程数据只进设计器内存,随「保存工程」落盘。当前流程会被替换,执行前系统会向用户弹确认卡。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""name"":{""type"":""string"",""description"":""新流程名称,可选;不填默认「新建流程」;名称来自用户原话,不自己编""}
  }
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "新建流程";
            return $"将清空流程设计器当前流程,新建空白流程「{name}」。";
        }

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "新建流程";
            if (name.Length > 60)
                return Task.FromResult(AgentToolResult.Fail("流程名称过长(不超过 60 字)。"));

            // 空流程:加载事件是"追加"语义,先清空旧流程再载入(与 BuildFlowTool 同一序列)
            var flow = new DesignerFlowDocument
            {
                Name = name,
                Description = "由小桐新建的空白流程",
                CommandNodes = new List<FlowCommandNodeData>(),
                Connections = new List<FlowConnectionData>()
            };

            try
            {
                AgentToolContext.RunOnUi(() =>
                {
                    _ctx.Events.GetEvent<ClearFlowRequestEvent>()?.Publish();
                    _ctx.Events.GetEvent<LoadFlowRequestEvent>()?.Publish(flow);
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("流程铺到画布失败:" + ex.Message));
            }

            return Task.FromResult(AgentToolResult.Ok(
                $"已新建空白流程「{name}」并载入流程设计器,当前 0 个节点。提醒用户:流程内容要等「保存工程」才会写入文件;" +
                "如需口述搭流程,可以让总控派设计专员(build_flow)。"));
        }
    }

    /// <summary>保存当前工程:已保存过则覆盖原路径,未保存过存到默认目录。只读式操作,免确认。</summary>
    public sealed class SaveCurrentDocumentTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public SaveCurrentDocumentTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "save_current_document";
        public string DisplayName => "保存当前工程";
        public string Description => @"保存当前工程文档:已保存过则覆盖原路径,从未保存过则存到 文档\MaxChemical\Projects\{名称}_{时间戳}.mcp。把画布与流程当前内容写入 .mcp 文件。返回保存路径。";
        public string ParametersSchema => @"{ ""type"":""object"", ""properties"":{} }";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "保存当前工程";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            // 收集当前工程数据走请求-回调事件,与手工保存同一链路,需在 UI 线程发布
            ProjectDocument doc = null;
            Exception collectError = null;
            AgentToolContext.RunOnUi(() =>
            {
                try { doc = _ctx.Resolve<IProjectDataService>().GetCurrentProjectData(); }
                catch (Exception ex) { collectError = ex; }
            });

            if (collectError != null)
                return Task.FromResult(AgentToolResult.Fail("获取当前工程数据失败:" + collectError.Message));
            if (doc == null)
                return Task.FromResult(AgentToolResult.Fail("当前没有打开的工程文档,无法保存。"));

            var canvasState = _ctx.Resolve<ICanvasStateService>();
            string path = canvasState.CurrentFilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                string safeName = Sanitize(doc.Name);
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "MaxChemical", "Projects", $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.mcp");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
            }

            try
            {
                if (!_ctx.Resolve<IProjectDataService>().SaveProject(doc, path))
                    return Task.FromResult(AgentToolResult.Fail("保存失败(文件写入未成功),请查看日志。"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("保存失败:" + ex.Message));
            }

            _ctx.Resolve<IRecentFileService>().Add(path);
            canvasState.SetCurrentFile(path);
            canvasState.MarkAsSaved();
            AgentToolContext.RunOnUi(() =>
            {
                _ctx.Events.GetEvent<MaxChemical.Modules.Designer.Event.ProjectSavedEvent>()?.Publish(path);
                _ctx.Events.GetEvent<FileSavedEvent>()?.Publish(path);
                _ctx.Events.GetEvent<StatusMessageChangedEvent>()?.Publish($"工程文档已保存: {Path.GetFileName(path)}");
            });
            return Task.FromResult(AgentToolResult.Ok($"已保存当前工程到:\n{path}"));
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "未命名工程";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }

    /// <summary>打开流程设计器 Tab。免确认。</summary>
    public sealed class OpenFlowDesignerTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public OpenFlowDesignerTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "open_flow_designer";
        public string DisplayName => "打开流程设计器";
        public string Description => "把主界面切换到「流程设计器」页(查看/编辑工艺流程的那个页面)。只切界面,不动任何数据。";
        public string ParametersSchema => @"{ ""type"":""object"", ""properties"":{} }";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "打开流程设计器";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            try
            {
                AgentToolContext.RunOnUi(() => _ctx.Events.GetEvent<Events.TabSwitchRequestEvent>()?.Publish("flow"));
                return Task.FromResult(AgentToolResult.Ok("已切换到流程设计器。"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("切换失败:" + ex.Message));
            }
        }
    }

    /// <summary>打开工艺设计器 Tab。免确认。</summary>
    public sealed class OpenProcessDesignerTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public OpenProcessDesignerTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "open_process_designer";
        public string DisplayName => "打开工艺设计器";
        public string Description => "把主界面切换到「工艺设计器」页(摆放设备/画布的那个页面)。只切界面,不动任何数据。";
        public string ParametersSchema => @"{ ""type"":""object"", ""properties"":{} }";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "打开工艺设计器";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            try
            {
                AgentToolContext.RunOnUi(() => _ctx.Events.GetEvent<Events.TabSwitchRequestEvent>()?.Publish("process"));
                return Task.FromResult(AgentToolResult.Ok("已切换到工艺设计器。"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("切换失败:" + ex.Message));
            }
        }
    }

    /// <summary>列出左侧设备库的全部设备(按分类),供放置设备前核对准确名称。只读。</summary>
    public sealed class ListLibraryDevicesTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public ListLibraryDevicesTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "list_library_devices";
        public string DisplayName => "列出设备库";
        public string Description => "列出左侧设备库里的全部设备(按分类)。用户要把设备放到画布上之前,先用本工具核对设备库中的准确名称,严禁凭印象写设备名;放置用 add_device_to_canvas。只读。";
        public string ParametersSchema => @"{ ""type"":""object"", ""properties"":{} }";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "列出设备库";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            try
            {
                var manager = _ctx.Resolve<IDeviceManager>();
                var categories = manager.GetCategories() ?? new List<string>();
                var sb = new System.Text.StringBuilder();
                if (!categories.Any())
                {
                    return Task.FromResult(AgentToolResult.Ok("设备库当前为空(没有加载到任何设备插件)。"));
                }

                foreach (var category in categories)
                {
                    var devices = manager.GetDevicesByCategory(category);
                    sb.AppendLine($"**{category}**({devices?.Count() ?? 0} 台):");
                    if (devices != null)
                        foreach (var d in devices)
                            sb.AppendLine($"- {d.Name}");
                    sb.AppendLine();
                }
                sb.Append("把这张清单转述给用户。用户指定要放的设备后,用 add_device_to_canvas 放置(名称必须与清单一致)。");
                return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("读取设备库失败:" + ex.Message));
            }
        }
    }

    /// <summary>
    /// 添加设备到画布(等效拖拽):把设备库里的设备放到工艺设计器画布,位置自动排列。
    /// 走 AgentCreateDeviceEvent(请求-回调),由 ProcessDesignerView 复用语音链路同一创建路径。
    /// </summary>
    public sealed class AddDeviceToCanvasTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public AddDeviceToCanvasTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "add_device_to_canvas";
        public string DisplayName => "添加设备到画布";
        public string Description => @"把设备库中的设备添加到工艺设计器画布(等效用户从设备库拖拽)。位置自动排列、不与已有设备重叠。只是放置,不会自动连接设备;要连接请让用户用 open_device_dialog 打开连接界面。设备名称必须与设备库一致——先用 list_library_devices 核对,严禁凭印象写。支持一次放多台。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""devices"":{""type"":""array"",""items"":{""type"":""string""},""description"":""要放到画布的设备名称列表(设备库中的准确名称),1~10 台""}
  },
  ""required"":[""devices""]
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
        {
            var names = new List<string>();
            if (args.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
                foreach (var d in devs.EnumerateArray())
                    if (d.ValueKind == JsonValueKind.String)
                        names.Add(d.GetString());
            return $"将在工艺设计器画布添加设备:{string.Join("、", names)}(自动排列位置,不自动连接)。";
        }

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var names = new List<string>();
            if (args.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in devs.EnumerateArray())
                    if (d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString()))
                        names.Add(d.GetString().Trim());
            }

            if (names.Count == 0)
                return Task.FromResult(AgentToolResult.Fail("devices 必填:给出要放到画布的设备名称列表(先用 list_library_devices 核对名称)。"));
            if (names.Count > 10)
                return Task.FromResult(AgentToolResult.Fail("一次最多放 10 台设备,请分批放置。"));

            var lines = new List<string>();
            foreach (var name in names)
            {
                try
                {
                    using var done = new ManualResetEventSlim(false);
                    AgentCreateDeviceResult result = null;
                    bool published = false;

                    AgentToolContext.RunOnUi(() =>
                    {
                        try
                        {
                            _ctx.Events.GetEvent<AgentCreateDeviceEvent>()?.Publish(
                                new AgentCreateDeviceEventArgs
                                {
                                    DeviceName = name,
                                    Callback = r => { result = r; done.Set(); }
                                });
                            published = true;
                        }
                        catch { }
                    });

                    // 回调在 UI 线程同步触发;等待放在工具线程,无死锁
                    if (!published || !done.Wait(TimeSpan.FromSeconds(10)))
                    {
                        lines.Add($"× {name}:画布视图无响应(工艺设计器可能尚未加载),未放置");
                        continue;
                    }

                    if (result == null || !result.Success)
                        lines.Add($"× {name}:{result?.Error ?? "未知错误"}");
                    else
                        lines.Add($"√ {name} → 已放置,画布显示名「{result.DisplayName}」");
                }
                catch (Exception ex)
                {
                    lines.Add($"× {name}:{ex.Message}");
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"已处理 {names.Count} 台设备的放置请求:");
            foreach (var l in lines) sb.AppendLine(l);
            sb.Append("提醒用户:放置不等于连接。要连接设备时,让我 open_device_dialog 打开连接界面(真实连接由用户在界面里点),或转派执行专员 connect_devices。");
            return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
        }
    }

    /// <summary>打开画布上某台设备的连接界面(等效双击设备)。免确认。</summary>
    public sealed class OpenDeviceDialogTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public OpenDeviceDialogTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "open_device_dialog";
        public string DisplayName => "打开设备连接界面";
        public string Description => @"打开画布上某台设备的连接界面(等效用户双击画布设备):弹出设备属性窗口,可配置串口/参数并点「连接」。device_name 用画布上的显示名(如 自研冷水机#1,来自 list_devices);只弹界面,不执行连接动作。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""device_name"":{""type"":""string"",""description"":""画布上设备的显示名,如 自研冷水机#1""}
  },
  ""required"":[""device_name""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "打开设备连接界面";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "device_name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Task.FromResult(AgentToolResult.Fail("device_name 必填:给出画布上设备的显示名(先用 list_devices 查画布上的设备)。"));

            var (vm, display, err) = _ctx.FindDevice(name);
            if (vm == null)
                return Task.FromResult(AgentToolResult.Fail(err + "。如实转告用户,并建议先用 list_devices 查画布上的设备清单。"));

            try
            {
                AgentToolContext.RunOnUi(() =>
                    _ctx.Events.GetEvent<ShowDevicePropertyDialogRequestEvent>()?.Publish(vm.DeviceId));
                return Task.FromResult(AgentToolResult.Ok(
                    $"已打开「{display}」的连接界面(设备属性窗口)。用户可在里面配置连接参数并点「连接」;界面操作由用户完成,我不代做。"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("打开连接界面失败:" + ex.Message));
            }
        }
    }
}
