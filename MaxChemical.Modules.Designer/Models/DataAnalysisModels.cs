using Newtonsoft.Json;
using System.Collections.Generic;

namespace MaxChemical.Modules.Designer.Models
{
    public class PredictionResult
    {
        [JsonProperty("conversion")]
        public double Conversion { get; set; }

        [JsonProperty("lower_bound")]
        public double LowerBound { get; set; }

        [JsonProperty("upper_bound")]
        public double UpperBound { get; set; }

        [JsonProperty("uncertainty")]
        public double Uncertainty { get; set; }
    }

    public class ProcessWindowData
    {
        [JsonProperty("pressure_curves")]
        public List<PressureCurve> PressureCurves { get; set; }

        [JsonProperty("optimal_region")]
        public OptimalRegion OptimalRegion { get; set; }

        [JsonProperty("historical_points")]
        public List<HistoricalPoint> HistoricalPoints { get; set; }
    }

    public class PressureCurve
    {
        [JsonProperty("pressure")]
        public double Pressure { get; set; }

        [JsonProperty("temperatures")]
        public List<double> Temperatures { get; set; }

        [JsonProperty("conversions")]
        public List<double> Conversions { get; set; }
    }

    public class OptimalRegion
    {
        [JsonProperty("temp_min")]
        public double TempMin { get; set; }

        [JsonProperty("temp_max")]
        public double TempMax { get; set; }

        [JsonProperty("temp_optimal")]
        public double TempOptimal { get; set; }

        [JsonProperty("pressure_min")]
        public double PressureMin { get; set; }

        [JsonProperty("pressure_max")]
        public double PressureMax { get; set; }

        [JsonProperty("pressure_optimal")]
        public double PressureOptimal { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }
    }

    public class HistoricalPoint
    {
        [JsonProperty("temperature")]
        public double Temperature { get; set; }

        [JsonProperty("conversion")]
        public double Conversion { get; set; }

        [JsonProperty("pressure")]
        public double Pressure { get; set; }

        [JsonProperty("entry")]
        public int Entry { get; set; }
    }

    public class SensitivityAnalysisData
    {
        [JsonProperty("sensitivities")]
        public List<ParameterSensitivity> Sensitivities { get; set; }

        [JsonProperty("base_conditions")]
        public Dictionary<string, double> BaseConditions { get; set; }

        [JsonProperty("base_conversion")]
        public double BaseConversion { get; set; }

        [JsonProperty("analysis_summary")]
        public AnalysisSummary AnalysisSummary { get; set; }
    }

    public class ParameterSensitivity
    {
        [JsonProperty("parameter")]
        public string Parameter { get; set; }

        [JsonProperty("parameter_chinese")]
        public string ParameterChinese { get; set; }

        [JsonProperty("sensitivity")]
        public double Sensitivity { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("unit_change")]
        public double UnitChange { get; set; }

        [JsonProperty("negative_effect")]
        public double NegativeEffect { get; set; }

        [JsonProperty("positive_effect")]
        public double PositiveEffect { get; set; }

        [JsonProperty("direction")]
        public string Direction { get; set; }

        [JsonProperty("importance_level")]
        public string ImportanceLevel { get; set; }

        [JsonProperty("control_priority")]
        public string ControlPriority { get; set; }
    }

    public class AnalysisSummary
    {
        [JsonProperty("most_sensitive_parameter")]
        public string MostSensitiveParameter { get; set; }

        [JsonProperty("high_sensitive_count")]
        public int HighSensitiveCount { get; set; }

        [JsonProperty("critical_control_count")]
        public int CriticalControlCount { get; set; }

        [JsonProperty("key_findings")]
        public List<string> KeyFindings { get; set; }
    }
}