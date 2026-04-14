using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class EffectsNormalPlotView : UserControl
    {
        public static readonly DependencyProperty NormalPlotDataProperty =
            DependencyProperty.Register(nameof(NormalPlotData), typeof(EffectsNormalPlotData),
                typeof(EffectsNormalPlotView),
                new PropertyMetadata(null, OnNormalPlotDataChanged));

        public static readonly DependencyProperty HalfNormalPlotDataProperty =
            DependencyProperty.Register(nameof(HalfNormalPlotData), typeof(EffectsNormalPlotData),
                typeof(EffectsNormalPlotView),
                new PropertyMetadata(null, OnHalfNormalPlotDataChanged));

        public EffectsNormalPlotData? NormalPlotData
        {
            get => (EffectsNormalPlotData?)GetValue(NormalPlotDataProperty);
            set => SetValue(NormalPlotDataProperty, value);
        }

        public EffectsNormalPlotData? HalfNormalPlotData
        {
            get => (EffectsNormalPlotData?)GetValue(HalfNormalPlotDataProperty);
            set => SetValue(HalfNormalPlotDataProperty, value);
        }

        private bool _normalWebViewReady;
        private bool _halfNormalWebViewReady;
        private EffectsNormalPlotData? _pendingNormalData;
        private EffectsNormalPlotData? _pendingHalfNormalData;

        public EffectsNormalPlotView()
        {
            InitializeComponent();
            InitWebViews();
        }

        private async void InitWebViews()
        {
            try
            {
                await NormalPlotWebView.EnsureCoreWebView2Async();
                string resourcesPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Resources");
                NormalPlotWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "local.resources", resourcesPath,
                    CoreWebView2HostResourceAccessKind.Allow);
                _normalWebViewReady = true;
                if (_pendingNormalData != null)
                {
                    RenderNormalPlot(_pendingNormalData);
                    _pendingNormalData = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Normal WebView2 init failed: {ex.Message}");
            }

            try
            {
                await HalfNormalPlotWebView.EnsureCoreWebView2Async();
                string resourcesPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Resources");
                HalfNormalPlotWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "local.resources", resourcesPath,
                    CoreWebView2HostResourceAccessKind.Allow);
                _halfNormalWebViewReady = true;
                if (_pendingHalfNormalData != null)
                {
                    RenderHalfNormalPlot(_pendingHalfNormalData);
                    _pendingHalfNormalData = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HalfNormal WebView2 init failed: {ex.Message}");
            }
        }

        private static void OnNormalPlotDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is EffectsNormalPlotView view && e.NewValue is EffectsNormalPlotData data)
            {
                if (view._normalWebViewReady) view.RenderNormalPlot(data);
                else view._pendingNormalData = data;
            }
        }

        private static void OnHalfNormalPlotDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is EffectsNormalPlotView view && e.NewValue is EffectsNormalPlotData data)
            {
                if (view._halfNormalWebViewReady) view.RenderHalfNormalPlot(data);
                else view._pendingHalfNormalData = data;
            }
        }

        private void RenderNormalPlot(EffectsNormalPlotData data)
        {
            if (data?.Points == null || data.Points.Count == 0) return;
            NormalPlaceholder.Visibility = Visibility.Collapsed;
            NormalPlotWebView.NavigateToString(GenerateHtml(data, false));
        }

        private void RenderHalfNormalPlot(EffectsNormalPlotData data)
        {
            if (data?.Points == null || data.Points.Count == 0) return;
            HalfNormalPlaceholder.Visibility = Visibility.Collapsed;
            HalfNormalPlotWebView.NavigateToString(GenerateHtml(data, true));
        }

        /// <summary>
        /// 生成专业统计图表 HTML (IBM Plex Sans, probit Y轴, 石板灰参考线)
        /// </summary>
        private static string GenerateHtml(EffectsNormalPlotData data, bool isHalfNormal)
        {
            var sigPts = data.Points.Where(p => p.IsSignificant).ToList();
            var nsPts = data.Points.Where(p => !p.IsSignificant).ToList();

            string title = isHalfNormal ? "标准化效应的半正态图" : "标准化效应的正态图";
            string subtitle = $"(响应为 {data.ResponseName}, α = {data.Alpha:F2})";
            string xTitle = isHalfNormal ? "标准化绝对效应" : "标准化效应";

            string sigJson = JsonConvert.SerializeObject(sigPts);
            string nsJson = JsonConvert.SerializeObject(nsPts);
            string refMean = data.RefLineMean.HasValue ? data.RefLineMean.Value.ToString("G10") : "null";
            string refStd = data.RefLineStd.HasValue ? data.RefLineStd.Value.ToString("G10") : "null";

            return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<link href=""https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@300;400;500&display=swap"" rel=""stylesheet"">
<script src=""https://local.resources/plotly.js""></script>
<style>
  *{{margin:0;padding:0}}
  body{{background:#fff;overflow:hidden;font-family:'IBM Plex Sans',sans-serif}}
  #plot{{width:100vw;height:100vh}}
</style>
</head>
<body>
<div id=""plot""></div>
<script>
function probit(p){{
  if(p<=1e-4)return-3.72;if(p>=.9999)return 3.72;if(p===.5)return 0;
  function r(t){{return t-(2.515517+.802853*t+.010328*t*t)/(1+1.432788*t+.189269*t*t+.001308*t*t*t)}}
  return p<.5?-r(Math.sqrt(-2*Math.log(p))):r(Math.sqrt(-2*Math.log(1-p)));
}}

var sigPts={sigJson};
var nsPts={nsJson};
var isHalf={(isHalfNormal ? "true" : "false")};
var xF=isHalf?'absEffect':'stdEffect';
var refMean={refMean};
var refStd={refStd};

var tPcts=[1,5,10,20,30,40,50,60,70,80,90,95,99];
var tVals=tPcts.map(function(p){{return probit(p/100)}});
var tLbls=tPcts.map(function(p){{return p.toString()}});

var traces=[];

// ── 参考线: 石板灰，视觉退后 ──
if(refMean!==null&&refStd!==null&&refStd>0){{
  var yP0=probit(.003),yP1=probit(.997);
  traces.push({{
    x:[refMean+refStd*yP0,refMean+refStd*yP1],
    y:[yP0,yP1],
    mode:'lines',type:'scatter',name:'参考线',
    line:{{color:'#94A3B8',width:1.2}},
    showlegend:false,hoverinfo:'skip'
  }});
}}

// ── 不显著: 蓝色实心圆, 标签常规灰 ──
traces.push({{
  x:nsPts.map(function(p){{return p[xF]}}),
  y:nsPts.map(function(p){{return probit(p.percent/100)}}),
  text:nsPts.map(function(p){{return p.name}}),
  customdata:nsPts.map(function(p){{return[p[xF].toFixed(4),p.percent.toFixed(2)]}}),
  mode:'markers+text',type:'scatter',name:'不显著',
  marker:{{symbol:'circle',size:9,color:'#2563EB',line:{{width:1.5,color:'#1D4ED8'}}}},
  textposition:'middle right',
  textfont:{{size:10,color:'#4B5563',family:'IBM Plex Sans'}},
  hovertemplate:'<b>%{{text}}</b><br>{xTitle}: %{{customdata[0]}}<br>百分比: %{{customdata[1]}}%<extra>不显著</extra>',
  cliponaxis:false
}});

// ── 显著: 红色实心方块, 标签加粗暗红 ──
traces.push({{
  x:sigPts.map(function(p){{return p[xF]}}),
  y:sigPts.map(function(p){{return probit(p.percent/100)}}),
  text:sigPts.map(function(p){{return p.name}}),
  customdata:sigPts.map(function(p){{return[p[xF].toFixed(4),p.percent.toFixed(2)]}}),
  mode:'markers+text',type:'scatter',name:'显著',
  marker:{{symbol:'square',size:8,color:'#DC2626',line:{{width:1.5,color:'#B91C1C'}}}},
  textposition:'middle right',
  textfont:{{size:10,color:'#B91C1C',family:'IBM Plex Sans'}},
  hovertemplate:'<b>%{{text}}</b><br>{xTitle}: %{{customdata[0]}}<br>百分比: %{{customdata[1]}}%<extra>显著</extra>',
  cliponaxis:false
}});

Plotly.newPlot('plot',traces,{{
  title:{{
    text:'{title}<br><span style=""font-size:11px;font-weight:300;color:#9CA3AF"">{subtitle}</span>',
    font:{{size:13,family:'IBM Plex Sans',color:'#111827'}},
    x:0.5,y:0.97
  }},
  xaxis:{{
    title:{{text:'{xTitle}',font:{{size:11,family:'IBM Plex Sans',color:'#374151'}},standoff:12}},
    tickfont:{{size:10,family:'IBM Plex Sans',color:'#6B7280'}},
    gridcolor:'#EDF0F4',gridwidth:0.5,
    zerolinecolor:'#D1D5DC',zerolinewidth:0.8,
    {(isHalfNormal ? "rangemode:'tozero'," : "")}
    showline:true,linecolor:'#C9CDD6',linewidth:1,
    ticklen:3,tickcolor:'#C9CDD6',
 zeroline: false,
    fixedrange:true
  }},
  yaxis:{{
    title:{{text:'百分比',font:{{size:11,family:'IBM Plex Sans',color:'#374151'}},standoff:8}},
    tickfont:{{size:10,family:'IBM Plex Sans',color:'#6B7280'}},
    tickmode:'array',tickvals:tVals,ticktext:tLbls,
    range:[probit(.005),probit(.995)],
    gridcolor:'#EDF0F4',gridwidth:0.5,
    showline:true,linecolor:'#C9CDD6',linewidth:1,
    ticklen:3,tickcolor:'#C9CDD6',
 zeroline: false,
    fixedrange:true
  }},
 legend: {{
    x: 0.02, y: 0.98, xanchor: 'left', yanchor: 'top',
    font: {{ size: 10, family: 'IBM Plex Sans', color: '#6B7280' }},
    bgcolor: 'rgba(255,255,255,0.85)',
    bordercolor: '#E5E7EB', borderwidth: 0.5,
    itemsizing: 'constant'
}},
  margin:{{l:58,r:20,t:58,b:52}},
  paper_bgcolor:'#FFFFFF',
  plot_bgcolor:'#F9FAFB',
  hovermode:'closest',
  hoverlabel:{{
    bgcolor:'#FFFFFF',
    bordercolor:'#E5E7EB',
    font:{{family:'IBM Plex Sans',size:11,color:'#111827'}}
  }}
}},{{
  staticPlot:false,
  displayModeBar:false,
  responsive:true,
  scrollZoom:false,
  doubleClick:false,
  dragmode:false,
  showTips:false
}});
</script>
</body>
</html>";
        }
    }

    /// <summary>效应正态图/半正态图数据</summary>
    public class EffectsNormalPlotData
    {
        public string ResponseName { get; set; } = "";
        public double Alpha { get; set; } = 0.05;
        public List<EffectsNormalPoint> Points { get; set; } = new();
        /// <summary>参考线: x = RefLineMean + RefLineStd * y_probit</summary>
        public double? RefLineMean { get; set; }
        /// <summary>参考线: 斜率（probit空间）</summary>
        public double? RefLineStd { get; set; }
    }

    /// <summary>正态图上的单个效应点</summary>
    public class EffectsNormalPoint
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("stdEffect")] public double StdEffect { get; set; }
        [JsonProperty("absEffect")] public double AbsEffect { get; set; }
        [JsonProperty("percent")] public double Percent { get; set; }
        [JsonProperty("isSignificant")] public bool IsSignificant { get; set; }
    }
}