using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// 校验区域设置中输入的行、列数量是否在规定范围 (0，10] 内
    /// </summary>
    public sealed class MaxRowsColsValidationRule : ValidationRule
    {
        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            if (value is null || string.IsNullOrWhiteSpace(value.ToString()))
            {
                return new ValidationResult(false, "输入参数无效");
            }

            try
            {
                var count = Convert.ToInt32(value);
                if (count <= 0 || count >10)
                {
                    return new ValidationResult(false, "输入的数值必须介于 0 - 10 之间，且不包含 0.");
                }

                return ValidationResult.ValidResult;
            }
            catch (Exception ex)
            {
                return new ValidationResult(false, ex.Message);
            }

        }
    }
}
