using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace MaxChemical.Modules.Designer.Converters
{
    public sealed class PathToBitmapSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 空值直接返回null，避免异常
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return null;

            string imgPath = value.ToString();
            BitmapImage bitmap = new BitmapImage();

            try
            {
                bitmap.BeginInit();
                // 本地文件路径
                if (File.Exists(imgPath))
                {
                    bitmap.UriSource = new Uri(imgPath, UriKind.Absolute);
                }
                // 网络图片url
                else if (imgPath.StartsWith("http"))
                {
                    bitmap.UriSource = new Uri(imgPath);
                }
                // 资源内嵌图片（pack://）
                else
                {
                    bitmap.UriSource = new Uri($"pack://application:,,,/{imgPath}");
                }
                // 解决图片锁定、占用文件问题
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                return bitmap;
            }
            catch
            {
                // 加载失败返回占位图/空
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
