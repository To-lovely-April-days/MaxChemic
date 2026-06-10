// MaxChemical.Shell/ViewModels/ProjectNameInputDialogViewModel.cs
using MaxChemical.Infrastructure.Services;
using Prism.Commands;
using Prism.Ioc;
using Prism.Mvvm;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class ProjectNameInputDialogViewModel : BindableBase
    {
        private readonly ILocalizationService _localizationService;
        private string _projectName = string.Empty;
        private string _errorMessage = string.Empty;
        private bool _hasError = false;

        public ProjectNameInputDialogViewModel(string defaultName = null)
        {

            _localizationService = ContainerLocator.Container.Resolve<ILocalizationService>() ?? throw new NullReferenceException();

            ConfirmCommand = new DelegateCommand(ExecuteConfirm, CanExecuteConfirm);
            CancelCommand = new DelegateCommand(ExecuteCancel);

            // 设置默认名称
            ProjectName = defaultName ?? $"新建项目_{DateTime.Now:yyyyMMdd_HHmmss}";
            //
            LacalizationLabel();
        }

        [Required(ErrorMessage = "项目名称不能为空")]
        [StringLength(100, ErrorMessage = "项目名称长度不能超过100个字符")]
        public string ProjectName
        {
            get => _projectName;
            set
            {
                if (SetProperty(ref _projectName, value))
                {
                    ValidateProjectName();
                    ConfirmCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        public bool HasError
        {
            get => _hasError;
            private set => SetProperty(ref _hasError, value);
        }

        public DelegateCommand ConfirmCommand { get; }
        public DelegateCommand CancelCommand { get; }

        // 事件回调
        public Action<string> OnConfirmed { get; set; }
        public Action OnCancelled { get; set; }
        public Action SetFocusAction { get; set; }

        private void ValidateProjectName()
        {
            ErrorMessage = string.Empty;
            HasError = false;

            if (string.IsNullOrWhiteSpace(ProjectName))
            {
                ErrorMessage = _localizationService.GetString("NewProj_NotNull", "项目名称不能空");
                HasError = true;
                return;
            }

            if (ProjectName.Length > 100)
            {
                ErrorMessage = _localizationService.GetString("NewProj_NotThan100", "项目名称长度不能超过100个字符");
                HasError = true;
                return;
            }

            // 检查文件名无效字符
            var invalidChars = Path.GetInvalidFileNameChars();
            if (ProjectName.Any(c => invalidChars.Contains(c)))
            {
                ErrorMessage = _localizationService.GetString("NewProj_NameInvalid", "项目名称包含无效字符，请使用有效的文件名字符");
                HasError = true;
                return;
            }

            // 检查保留名称
            var reservedNames = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (reservedNames.Contains(ProjectName.ToUpper()))
            {
                ErrorMessage = _localizationService.GetString("NewProj_NotReserve", "项目名称不能使用系统保留名称");
                HasError = true;
                return;
            }
        }

        private bool CanExecuteConfirm()
        {
            return !HasError && !string.IsNullOrWhiteSpace(ProjectName);
        }

        private void ExecuteConfirm()
        {
            if (CanExecuteConfirm())
            {
                OnConfirmed?.Invoke(ProjectName);
            }
        }

        private void ExecuteCancel()
        {
            OnCancelled?.Invoke();
        }

        #region 本地化

        public string NewProjTitle { get; set; }
        public string PleaseTipText { get; set; }
        public string ProjNameLabel { get; set; }
        public string ProjNameDesc { get; set; }
        public string OkText { get; set; }
        public string CancelText { get; set; }

        private void LacalizationLabel()
        {
            NewProjTitle = _localizationService.GetString("NewProj_Title", "新建项目");
            PleaseTipText = _localizationService.GetString("NewProj_Please", "请输入新项目名称。");
            ProjNameLabel = _localizationService.GetString("NewProj_NameLabel", "项目名称：");
            ProjNameDesc = _localizationService.GetString("NewProj_Desc", "项目名称将用作文件名和实验记录的标识。");
            OkText = _localizationService.GetString("NewProj_OK", "确定");
            CancelText = _localizationService.GetString("NewProj_Cancel", "取消");
        }

        #endregion
    }
}