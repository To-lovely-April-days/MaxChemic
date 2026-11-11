using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Shell.Services.Agent.Knowledge;
using Microsoft.Win32;
using Prism.Commands;
using Prism.Mvvm;

namespace MaxChemical.Shell.ViewModels
{
    /// <summary>知识库文档列表条目。</summary>
    public class KnowledgeDocVm : BindableBase
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int ChunkCount { get; set; }
        public DateTime ImportedAt { get; set; }
        public bool HasVectors { get; set; }

        public string MetaText => $"{ChunkCount} 个片段 · {ImportedAt:yyyy-MM-dd HH:mm}";
        public string VectorText => HasVectors ? "已向量化" : "关键词模式";
    }

    /// <summary>
    /// 知识库管理:导入文档(txt/pdf/docx)、查看列表、删除。
    /// Embedding 未配置时文档仍可导入(关键词检索模式),状态栏会明确提示。
    /// </summary>
    public class KnowledgeBaseViewModel : BindableBase
    {
        private readonly KnowledgeBaseService _kb;
        private readonly EmbeddingService _embedding;
        private readonly IDialogService _dialog;
        private readonly ILogService _logger = LogManager.GetLogger<KnowledgeBaseViewModel>();

        private string _statusText = "";
        private bool _isBusy;

        public ObservableCollection<KnowledgeDocVm> Documents { get; } = new();

        public DelegateCommand ImportCommand { get; }
        public DelegateCommand RefreshCommand { get; }
        public DelegateCommand<KnowledgeDocVm> DeleteCommand { get; }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public KnowledgeBaseViewModel(KnowledgeBaseService kb, EmbeddingService embedding, IDialogService dialog)
        {
            _kb = kb;
            _embedding = embedding;
            _dialog = dialog;

            ImportCommand = new DelegateCommand(OnImport);
            RefreshCommand = new DelegateCommand(Refresh);
            DeleteCommand = new DelegateCommand<KnowledgeDocVm>(OnDelete, _ => !IsBusy);

            Refresh();
            UpdateModeStatus();
        }

        /// <summary>底部状态栏:Embedding 配置状态 + 操作结果。</summary>
        private void UpdateModeStatus()
        {
            StatusText = _embedding.IsConfigured
                ? "Embedding 已配置(向量语义检索)"
                : "Embedding 未配置(关键词检索模式)——在 appsettings.json → AiKnowledge:Embedding 配置 ApiKey 后可获得语义检索";
        }

        private void Refresh()
        {
            Documents.Clear();
            foreach (var d in _kb.ListDocuments())
            {
                Documents.Add(new KnowledgeDocVm
                {
                    Id = d.Id,
                    Name = d.Name,
                    ChunkCount = d.ChunkCount,
                    ImportedAt = d.ImportedAt,
                    HasVectors = d.HasVectors
                });
            }
        }

        private void OnImport()
        {
            if (IsBusy) return;
            var dlg = new OpenFileDialog
            {
                Title = "选择要导入知识库的文档",
                Filter = "支持文档|*.txt;*.pdf;*.docx|文本文件|*.txt|PDF 文档|*.pdf|Word 文档|*.docx",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0) return;

            IsBusy = true;
            StatusText = "正在导入…";

            _ = Task.Run(async () =>
            {
                var results = new List<string>();
                foreach (var file in dlg.FileNames)
                {
                    var r = await _kb.ImportFileAsync(file).ConfigureAwait(false);
                    results.Add(r.Success ? $"√ {r.Message}" : $"× {r.Message}");
                }
                RunOnUi(() =>
                {
                    Refresh();
                    IsBusy = false;
                    UpdateModeStatus();
                    // 操作结果追加在模式状态之后,失败信息(如格式不支持)必须完整可见
                    if (results.Count > 0)
                        StatusText += "　" + string.Join("　", results);
                });
            });
        }

        private void OnDelete(KnowledgeDocVm doc)
        {
            if (doc == null || IsBusy) return;
            if (!_dialog.ShowConfirmation($"确定从知识库删除「{doc.Name}」吗?删除后 AI 将检索不到该资料。", "删除文档"))
                return;

            if (_kb.DeleteDocument(doc.Id))
            {
                Refresh();
                UpdateModeStatus();
                _logger.LogInformation($"知识库文档已删除: {doc.Name}");
            }
            else
            {
                _dialog.ShowError("删除失败:文档不存在或已被删除。");
            }
        }

        private static void RunOnUi(Action action)
        {
            var app = Application.Current;
            if (app?.Dispatcher == null || app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.Invoke(action);
        }
    }
}
