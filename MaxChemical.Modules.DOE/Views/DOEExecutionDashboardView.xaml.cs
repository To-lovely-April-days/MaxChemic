using MaxChemical.Modules.DOE.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEExecutionDashboardView : UserControl
    {
        public DOEExecutionDashboardView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// DataGrid 自动生成列时的样式定制（靛蓝主题）：
        /// - # 列：窄宽度、左对齐、灰色
        /// - 因子/响应列：Consolas 等宽、右对齐
        /// - 状态列：居中、Segoe UI
        /// - 耗时列：右对齐、灰色
        /// </summary>
        private void OnAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            // 隐藏内部匹配用的列
            if (e.PropertyName == "_RunIndex")
            {
                e.Cancel = true;
                return;
            }

            var col = e.Column as DataGridTextColumn;
            if (col == null) return;

            switch (e.PropertyName)
            {
                case "#":
                    col.Width = new DataGridLength(48);
                    col.ElementStyle = CreateCellStyle(
                        hAlign: HorizontalAlignment.Left,
                        foreground: "#9CA3AF",
                        fontFamily: "Segoe UI",
                        fontWeight: FontWeights.Normal,
                        fontSize: 11);
                    col.HeaderStyle = CreateHeaderStyle(HorizontalAlignment.Left);
                    break;

                case "状态":
                    col.Width = new DataGridLength(76);
                    col.ElementStyle = CreateCellStyle(
                        hAlign: HorizontalAlignment.Center,
                        foreground: "#374151",
                        fontFamily: "Segoe UI",
                        fontSize: 11);
                    col.HeaderStyle = CreateHeaderStyle(HorizontalAlignment.Center);
                    break;

                case "耗时":
                    col.Width = new DataGridLength(58);
                    col.ElementStyle = CreateCellStyle(
                        hAlign: HorizontalAlignment.Right,
                        foreground: "#9CA3AF",
                        fontFamily: "Consolas",
                        fontSize: 11);
                    col.HeaderStyle = CreateHeaderStyle(HorizontalAlignment.Right);
                    break;

                default:
                    col.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                    col.ElementStyle = CreateCellStyle(
                        hAlign: HorizontalAlignment.Right,
                        foreground: "#1A1F36",
                        fontFamily: "Consolas",
                        fontSize: 12);
                    col.HeaderStyle = CreateHeaderStyle(HorizontalAlignment.Right);
                    break;
            }
        }

        /// <summary>
        /// 创建单元格样式（靛蓝主题色系）
        /// </summary>
        private static Style CreateCellStyle(
            HorizontalAlignment hAlign = HorizontalAlignment.Right,
            string foreground = "#1A1F36",
            string fontFamily = "Consolas",
            FontWeight? fontWeight = null,
            double fontSize = 12)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, hAlign));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                new BrushConverter().ConvertFromString(foreground) as Brush));
            style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily(fontFamily)));
            style.Setters.Add(new Setter(TextBlock.FontSizeProperty, fontSize));
            style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 8, 0)));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            if (fontWeight.HasValue)
                style.Setters.Add(new Setter(TextBlock.FontWeightProperty, fontWeight.Value));
            return style;
        }

        /// <summary>
        /// 创建列头样式（靛蓝主题色系）
        /// </summary>
        private static Style CreateHeaderStyle(HorizontalAlignment hAlign)
        {
            var style = new Style(typeof(DataGridColumnHeader));
            style.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty,
                new BrushConverter().ConvertFromString("#FAFBFC") as Brush));
            style.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty,
                new BrushConverter().ConvertFromString("#9CA3AF") as Brush));
            style.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 11.0));
            style.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
            style.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(12, 0, 12, 0)));
            style.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty,
                new BrushConverter().ConvertFromString("#E8EAED") as Brush));
            style.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            style.Setters.Add(new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, hAlign));
            style.Setters.Add(new Setter(DataGridColumnHeader.SeparatorVisibilityProperty, Visibility.Collapsed));
            return style;
        }
    }
}