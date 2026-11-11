using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MaxChemical.Modules.Lab3D.Models;

public sealed class LabDevice : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isOnline;
    private bool _hasFreshData;
    private string _primaryValue = string.Empty;
    private string _secondaryValue = string.Empty;
    private string _primaryName = string.Empty;
    private string _primaryUnit = string.Empty;
    private string _secondaryName = string.Empty;
    private string _secondaryUnit = string.Empty;
    private string _protocol = string.Empty;
    private string? _sourceDeviceId;
    private DateTime? _lastUpdateTime;
    private string _name = string.Empty;
    private string _englishName = string.Empty;
    private string _imagePath = string.Empty;
    private double _modelHeight = 2;
    private double _focusDistance = 8.5;
    private string _inspectionStateKey = string.Empty;

    public required string Id { get; init; }
    public required string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }
    public required string EnglishName
    {
        get => _englishName;
        set
        {
            if (_englishName == value) return;
            _englishName = value;
            OnPropertyChanged();
        }
    }
    public required string Kind { get; init; }
    public required string ImagePath
    {
        get => _imagePath;
        set
        {
            if (_imagePath == value) return;
            _imagePath = value;
            OnPropertyChanged();
        }
    }
    public required string Location { get; init; }
    public required string Description { get; init; }
    public required Point3D Position { get; init; }
    public string? WebImagePath { get; set; }
    public string? LibraryId { get; set; }
    public double ImageAspect { get; init; } = 1;
    public double ModelHeight
    {
        get => _modelHeight;
        set
        {
            if (_modelHeight.Equals(value)) return;
            _modelHeight = value;
            OnPropertyChanged();
        }
    }
    public double FocusDistance
    {
        get => _focusDistance;
        set
        {
            if (_focusDistance.Equals(value)) return;
            _focusDistance = value;
            OnPropertyChanged();
        }
    }

    public string? SourceDeviceId
    {
        get => _sourceDeviceId;
        set
        {
            if (_sourceDeviceId == value) return;
            _sourceDeviceId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionText));
        }
    }

    public required string PrimaryName
    {
        get => _primaryName;
        set
        {
            if (_primaryName == value) return;
            _primaryName = value;
            OnPropertyChanged();
        }
    }

    public required string PrimaryUnit
    {
        get => _primaryUnit;
        set
        {
            if (_primaryUnit == value) return;
            _primaryUnit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PrimaryDockText));
        }
    }

    public required string SecondaryName
    {
        get => _secondaryName;
        set
        {
            if (_secondaryName == value) return;
            _secondaryName = value;
            OnPropertyChanged();
        }
    }

    public required string SecondaryUnit
    {
        get => _secondaryUnit;
        set
        {
            if (_secondaryUnit == value) return;
            _secondaryUnit = value;
            OnPropertyChanged();
        }
    }

    public required string Protocol
    {
        get => _protocol;
        set
        {
            if (_protocol == value) return;
            _protocol = value;
            OnPropertyChanged();
        }
    }

    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (_isOnline == value) return;
            _isOnline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ActivityText));
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(PrimaryDisplay));
            OnPropertyChanged(nameof(SecondaryDisplay));
            OnPropertyChanged(nameof(StatusKey));
            OnPropertyChanged(nameof(HasLiveValues));
            OnPropertyChanged(nameof(PrimaryDisplayText));
            OnPropertyChanged(nameof(SecondaryDisplayText));
            OnPropertyChanged(nameof(PrimaryDockText));
            OnPropertyChanged(nameof(DockStatusText));
        }
    }

    public bool HasFreshData
    {
        get => _hasFreshData;
        set
        {
            if (_hasFreshData == value) return;
            _hasFreshData = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivityText));
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(PrimaryDisplay));
            OnPropertyChanged(nameof(SecondaryDisplay));
            OnPropertyChanged(nameof(StatusKey));
            OnPropertyChanged(nameof(HasLiveValues));
            OnPropertyChanged(nameof(PrimaryDisplayText));
            OnPropertyChanged(nameof(SecondaryDisplayText));
            OnPropertyChanged(nameof(PrimaryDockText));
            OnPropertyChanged(nameof(DockStatusText));
        }
    }

    public DateTime? LastUpdateTime
    {
        get => _lastUpdateTime;
        set
        {
            if (_lastUpdateTime == value) return;
            _lastUpdateTime = value;
            OnPropertyChanged();
        }
    }

    public string StatusText => IsOnline ? "已连接" : "未连接";
    public string ActivityText => !IsOnline ? "未连接" : HasFreshData ? "运行中" : "待数据";
    public string ConnectionText
    {
        get
        {
            if (!IsOnline) return SourceDeviceId is null ? "未绑定" : "未连接";
            return HasFreshData ? "已连接" : "已连接·等待数据";
        }
    }
    public string PrimaryDisplay => IsOnline && HasFreshData ? PrimaryValue : string.Empty;
    public string SecondaryDisplay => IsOnline && HasFreshData ? SecondaryValue : string.Empty;

    public string StatusKey => !IsOnline ? "off" : HasFreshData ? "live" : "stale";
    public bool HasLiveValues => IsOnline && HasFreshData;
    public string PrimaryDisplayText => string.IsNullOrWhiteSpace(PrimaryDisplay) ? "--" : PrimaryDisplay;
    public string SecondaryDisplayText => string.IsNullOrWhiteSpace(SecondaryDisplay) ? "--" : SecondaryDisplay;
    public string PrimaryDockText
    {
        get
        {
            string display = PrimaryDisplay;
            if (string.IsNullOrWhiteSpace(display) || display == "--") return string.Empty;
            return $"{display} {PrimaryUnit}".TrimEnd();
        }
    }

    public string InspectionStateKey
    {
        get => _inspectionStateKey;
        set
        {
            value ??= string.Empty;
            if (_inspectionStateKey == value) return;
            _inspectionStateKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInspected));
            OnPropertyChanged(nameof(DockStatusText));
        }
    }
    public bool IsInspected => InspectionStateKey.Length > 0;
    public string DockStatusText => IsInspected ? $"{ActivityText} · AI 巡检中" : ActivityText;

    public string PrimaryValue
    {
        get => _primaryValue;
        set
        {
            if (_primaryValue == value) return;
            _primaryValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PrimaryDisplay));
            OnPropertyChanged(nameof(PrimaryDisplayText));
            OnPropertyChanged(nameof(PrimaryDockText));
        }
    }

    public string SecondaryValue
    {
        get => _secondaryValue;
        set
        {
            if (_secondaryValue == value) return;
            _secondaryValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SecondaryDisplay));
            OnPropertyChanged(nameof(SecondaryDisplayText));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public PointCollection PrimaryTrend { get; private set; } = [];
    public PointCollection SecondaryTrend { get; private set; } = [];
    public IReadOnlyList<Lab3DParameterPoint> PrimarySamples { get; private set; } = [];
    public IReadOnlyList<Lab3DParameterPoint> SecondarySamples { get; private set; } = [];
    public bool HasPrimaryTrend => PrimaryTrend.Count > 1;
    public bool HasSecondaryTrend => SecondaryTrend.Count > 1;
    public Visibility PrimaryTrendVisibility => HasPrimaryTrend ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SecondaryTrendVisibility => HasSecondaryTrend ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PrimaryTrendPlaceholderVisibility => HasPrimaryTrend ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SecondaryTrendPlaceholderVisibility => HasSecondaryTrend ? Visibility.Collapsed : Visibility.Visible;
    public string PrimaryRangeText { get; private set; } = string.Empty;
    public string SecondaryRangeText { get; private set; } = string.Empty;

    public void SetTrend(Lab3DTrendSeries trend)
    {
        PrimaryTrend = CreatePoints(trend.Primary);
        SecondaryTrend = CreatePoints(trend.Secondary);
        PrimarySamples = trend.Primary;
        SecondarySamples = trend.Secondary;
        PrimaryRangeText = trend.PrimaryRangeText;
        SecondaryRangeText = trend.SecondaryRangeText;
        OnPropertyChanged(nameof(PrimaryTrend));
        OnPropertyChanged(nameof(SecondaryTrend));
        OnPropertyChanged(nameof(PrimarySamples));
        OnPropertyChanged(nameof(SecondarySamples));
        OnPropertyChanged(nameof(HasPrimaryTrend));
        OnPropertyChanged(nameof(HasSecondaryTrend));
        OnPropertyChanged(nameof(PrimaryTrendVisibility));
        OnPropertyChanged(nameof(SecondaryTrendVisibility));
        OnPropertyChanged(nameof(PrimaryTrendPlaceholderVisibility));
        OnPropertyChanged(nameof(SecondaryTrendPlaceholderVisibility));
        OnPropertyChanged(nameof(PrimaryRangeText));
        OnPropertyChanged(nameof(SecondaryRangeText));
    }

    private static PointCollection CreatePoints(IReadOnlyList<Lab3DParameterPoint> samples)
    {
        if (samples.Count < 2) return [];
        double minimum = samples.Min(point => point.Value);
        double maximum = samples.Max(point => point.Value);
        if (maximum - minimum < 0.000001)
        {
            double padding = Math.Max(0.5, Math.Abs(maximum) * 0.05);
            minimum -= padding;
            maximum += padding;
        }
        var points = new PointCollection(samples.Count);
        for (int i = 0; i < samples.Count; i++)
        {
            points.Add(new Point(
                i * 50.0 / (samples.Count - 1),
                2 + (maximum - samples[i].Value) * 16 / (maximum - minimum)));
        }
        return points;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
