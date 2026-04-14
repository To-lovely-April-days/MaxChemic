using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class ResponseSurface3DView : UserControl
    {
        // ═══ 依赖属性 ═══

        public static readonly DependencyProperty SurfaceDataProperty =
            DependencyProperty.Register(nameof(SurfaceData), typeof(Surface3DData),
                typeof(ResponseSurface3DView),
                new PropertyMetadata(null, OnSurfaceDataChanged));

        public static readonly DependencyProperty XLabelProperty =
            DependencyProperty.Register(nameof(XLabel), typeof(string),
                typeof(ResponseSurface3DView), new PropertyMetadata("X"));

        public static readonly DependencyProperty YLabelProperty =
            DependencyProperty.Register(nameof(YLabel), typeof(string),
                typeof(ResponseSurface3DView), new PropertyMetadata("Y"));

        public static readonly DependencyProperty ZLabelProperty =
            DependencyProperty.Register(nameof(ZLabel), typeof(string),
                typeof(ResponseSurface3DView), new PropertyMetadata("Z"));

        public Surface3DData? SurfaceData
        {
            get => (Surface3DData?)GetValue(SurfaceDataProperty);
            set => SetValue(SurfaceDataProperty, value);
        }

        public string XLabel
        {
            get => (string)GetValue(XLabelProperty);
            set => SetValue(XLabelProperty, value);
        }

        public string YLabel
        {
            get => (string)GetValue(YLabelProperty);
            set => SetValue(YLabelProperty, value);
        }

        public string ZLabel
        {
            get => (string)GetValue(ZLabelProperty);
            set => SetValue(ZLabelProperty, value);
        }

        private bool _webViewReady;
        private Surface3DData? _pendingData;

        public ResponseSurface3DView()
        {
            InitializeComponent();
            InitWebView();
        }

        private async void InitWebView()
        {
            try
            {
                await WebView.EnsureCoreWebView2Async();

                // ★ 新增：将 Resources 文件夹映射为虚拟主机
                string resourcesPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Resources");
                WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "local.resources",                              // 虚拟域名
                    resourcesPath,                                  // 本地文件夹路径
                    CoreWebView2HostResourceAccessKind.Allow);

                _webViewReady = true;

                if (_pendingData != null)
                {
                    RenderSurface(_pendingData);
                    _pendingData = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebView2 init failed: {ex.Message}");
            }
        }

        private static void OnSurfaceDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResponseSurface3DView view && e.NewValue is Surface3DData data)
            {
                if (view._webViewReady)
                    view.RenderSurface(data);
                else
                    view._pendingData = data;
            }
        }

        private void RenderSurface(Surface3DData data)
        {
            if (data?.X == null || data.Y == null || data.Z == null) return;

            PlaceholderText.Visibility = Visibility.Collapsed;

            // 构建 JSON 数组
            string xJson = "[" + string.Join(",", data.X.Select(v => v.ToString("G6"))) + "]";
            string yJson = "[" + string.Join(",", data.Y.Select(v => v.ToString("G6"))) + "]";

            var zRows = data.Z.Select(row =>
                "[" + string.Join(",", row.Select(v => v.ToString("G6"))) + "]");
            string zJson = "[" + string.Join(",", zRows) + "]";

            string xLabel = data.XLabel ?? XLabel ?? "X";
            string yLabel = data.YLabel ?? YLabel ?? "Y";
            string zLabel = ZLabel ?? "Z";

            string html = GenerateHtml(xJson, yJson, zJson, xLabel, yLabel, zLabel);
            WebView.NavigateToString(html);
        }

        private static string GenerateHtml(string xJson, string yJson, string zJson,
            string xLabel, string yLabel, string zLabel)
        {
            return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<script src=""https://local.resources/plotly.js""></script>
<style>
  * {{ margin: 0; padding: 0; }}
  body {{ background: white; overflow: hidden; }}
  #plot {{ width: 100vw; height: 100vh; }}
</style>
</head>
<body>
<div id=""plot""></div>
<script>
var x = {xJson};
var y = {yJson};
var z = {zJson};

var data = [{{
  type: 'surface',
  x: x,
  y: y,
  z: z,
  colorscale: 'Viridis',
  showscale: true,
  colorbar: {{
    len: 0.6,
    thickness: 15,
    tickfont: {{ size: 10 }},
    title: {{ text: '{zLabel}', font: {{ size: 11 }} }}
  }},
  contours: {{
    z: {{ show: true, usecolormap: true, highlightcolor: '#fff', project: {{ z: true }} }}
  }},
  lighting: {{
    ambient: 0.6,
    diffuse: 0.5,
    specular: 0.2,
    roughness: 0.5
  }}
}}];

var layout = {{
  margin: {{ l: 0, r: 0, t: 0, b: 0 }},
  paper_bgcolor: 'white',
  scene: {{
    xaxis: {{ title: {{ text: '{xLabel}', font: {{ size: 12 }} }}, tickfont: {{ size: 9 }} }},
    yaxis: {{ title: {{ text: '{yLabel}', font: {{ size: 12 }} }}, tickfont: {{ size: 9 }} }},
    zaxis: {{ title: {{ text: '{zLabel}', font: {{ size: 12 }} }}, tickfont: {{ size: 9 }} }},
    camera: {{
      eye: {{ x: 1.5, y: 1.5, z: 1.0 }},
      up: {{ x: 0, y: 0, z: 1 }}
    }},
    aspectmode: 'manual',
    aspectratio: {{ x: 1, y: 1, z: 0.7 }}
  }}
}};

var config = {{
  displayModeBar: true,
  modeBarButtonsToRemove: ['toImage', 'sendDataToCloud'],
  displaylogo: false,
  responsive: true,
  scrollZoom: true
}};

Plotly.newPlot('plot', data, layout, config);
</script>
</body>
</html>";
        }
    }

    /// <summary>3D 响应曲面数据</summary>
    public class Surface3DData
    {
        public double[]? X { get; set; }
        public double[]? Y { get; set; }
        public double[][]? Z { get; set; }
        public string? XLabel { get; set; }
        public string? YLabel { get; set; }
    }
}