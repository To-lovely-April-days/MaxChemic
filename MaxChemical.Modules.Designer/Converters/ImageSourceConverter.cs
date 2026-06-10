using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace MaxChemical.Modules.Designer.Converters
{
    public class ImageSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value == null || string.IsNullOrEmpty(value.ToString()))
                {
                    // 返回默认图标或透明图像
                    return null; // 或者返回一个默认图标
                }

                string path = value.ToString();

                // 处理pack://格式的URI
                if (path.StartsWith("pack://"))
                {
                    return new BitmapImage(new Uri(path));
                }

                // 处理相对路径
                if (!System.IO.Path.IsPathRooted(path))
                {
                    return new BitmapImage(new Uri($"pack://application:,,,/{path}"));
                }

                // 处理绝对路径
                if (System.IO.File.Exists(path))
                {
                    return new BitmapImage(new Uri(path));
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
