using MaxChemical.Infrastructure.Events;
using Prism.Events;
using System.IO;
using System.Text.Json;

namespace MaxChemical.Infrastructure.Services;

public interface IProjectService
{
    void NewProject();
    bool OpenProject(string fileName);
    bool SaveProject(string? fileName = null);
    void CloseProject();
    bool HasUnsavedChanges { get; }
    string? CurrentProjectFile { get; }
    event EventHandler? ProjectChanged;
}

public class ProjectService : IProjectService
{
    private readonly IEventAggregator _eventAggregator;
    private string? _currentProjectFile;
    private bool _hasUnsavedChanges;

    public ProjectService(IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator;
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (_hasUnsavedChanges != value)
            {
                _hasUnsavedChanges = value;
                ProjectChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string? CurrentProjectFile
    {
        get => _currentProjectFile;
        private set
        {
            if (_currentProjectFile != value)
            {
                _currentProjectFile = value;
                ProjectChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? ProjectChanged;

    public void NewProject()
    {
        CurrentProjectFile = null;
        HasUnsavedChanges = false;
        _eventAggregator.GetEvent<ProjectOpenedEvent>().Publish("新建项目");
    }

    public bool OpenProject(string fileName)
    {
        if (!File.Exists(fileName))
            return false;

        CurrentProjectFile = fileName;
        HasUnsavedChanges = false;
        _eventAggregator.GetEvent<ProjectOpenedEvent>().Publish(fileName);
        return true;
    }

    public bool SaveProject(string? fileName = null)
    {
        var targetFile = fileName ?? CurrentProjectFile;
        if (string.IsNullOrEmpty(targetFile))
            return false;

        // 这里实现实际的保存逻辑
        CurrentProjectFile = targetFile;
        HasUnsavedChanges = false;
        _eventAggregator.GetEvent<ProjectSavedEvent>().Publish(targetFile);
        return true;
    }

    public void CloseProject()
    {
        CurrentProjectFile = null;
        HasUnsavedChanges = false;
    }
}