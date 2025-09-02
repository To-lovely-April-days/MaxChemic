// MaxChemical.Shell/ViewModels/ProjectNameInputDialogViewModel.cs
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class ProjectNameInputDialogViewModel : BindableBase
    {
        private string _projectName = string.Empty;
        private string _errorMessage = string.Empty;
        private bool _hasError = false;

        public ProjectNameInputDialogViewModel(string defaultName = null)
        {
            ConfirmCommand = new DelegateCommand(ExecuteConfirm, CanExecuteConfirm);
            CancelCommand = new DelegateCommand(ExecuteCancel);

            // 设置默认名称
            ProjectName = defaultName ?? $"新建项目_{DateTime.Now:yyyyMMdd_HHmmss}";
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
                ErrorMessage = "项目名称不能为空";
                HasError = true;
                return;
            }

            if (ProjectName.Length > 100)
            {
                ErrorMessage = "项目名称长度不能超过100个字符";
                HasError = true;
                return;
            }

            // 检查文件名无效字符
            var invalidChars = Path.GetInvalidFileNameChars();
            if (ProjectName.Any(c => invalidChars.Contains(c)))
            {
                ErrorMessage = "项目名称包含无效字符，请使用有效的文件名字符";
                HasError = true;
                return;
            }

            // 检查保留名称
            var reservedNames = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (reservedNames.Contains(ProjectName.ToUpper()))
            {
                ErrorMessage = "项目名称不能使用系统保留名称";
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


    }
}