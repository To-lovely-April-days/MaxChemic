using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MaxChemical.Shell.Utils
{
    public static class NumberToChineseConverter
    {
        private static readonly string[] ChineseNumbers = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        private static readonly string[] ChineseUnits = { "", "十", "百", "千" };
        private static readonly string[] ChineseBigUnits = { "", "万", "亿" };

        public static string ConvertNumbersInText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // 匹配 "13点56分" 格式
            text = Regex.Replace(text, @"(\d{1,2})点(\d{1,2})分", match =>
            {
                int hour = int.Parse(match.Groups[1].Value);
                int minute = int.Parse(match.Groups[2].Value);

                return ConvertToChineseNumber(hour) + "点" + ConvertToChineseNumber(minute) + "分";
            });

            // 匹配 "13点" 格式（整点）
            text = Regex.Replace(text, @"(\d{1,2})点(?![\d分])", match =>
            {
                int hour = int.Parse(match.Groups[1].Value);
                return ConvertToChineseNumber(hour) + "点";
            });

            // 匹配 "HH:mm:ss" 格式
            text = Regex.Replace(text, @"(\d{1,2}):(\d{2}):(\d{2})", match =>
            {
                int hour = int.Parse(match.Groups[1].Value);
                int minute = int.Parse(match.Groups[2].Value);
                int second = int.Parse(match.Groups[3].Value);

                string result = ConvertToChineseNumber(hour) + "点";
                if (minute > 0)
                {
                    result += ConvertToChineseNumber(minute) + "分";
                }
                if (second > 0)
                {
                    result += ConvertToChineseNumber(second) + "秒";
                }
                return result;
            });

            // 匹配 "HH:mm" 格式
            text = Regex.Replace(text, @"(\d{1,2}):(\d{2})", match =>
            {
                int hour = int.Parse(match.Groups[1].Value);
                int minute = int.Parse(match.Groups[2].Value);

                string result = ConvertToChineseNumber(hour) + "点";
                if (minute > 0)
                {
                    result += ConvertToChineseNumber(minute) + "分";
                }
                return result;
            });

            // 匹配 "2025年1月15日" 格式
            text = Regex.Replace(text, @"(\d{4})年(\d{1,2})月(\d{1,2})日", match =>
            {
                int year = int.Parse(match.Groups[1].Value);
                int month = int.Parse(match.Groups[2].Value);
                int day = int.Parse(match.Groups[3].Value);

                return ConvertYearToChineseNumber(year) + "年" +
                       ConvertToChineseNumber(month) + "月" +
                       ConvertToChineseNumber(day) + "日";
            });

            // 匹配百分比 "80%"
            text = Regex.Replace(text, @"(\d+(?:\.\d+)?)%", match =>
            {
                return "百分之" + ConvertToChineseNumber(match.Groups[1].Value);
            });

            // 匹配小数 "25.5"
            text = Regex.Replace(text, @"\b(\d+)\.(\d+)\b", match =>
            {
                string intPart = ConvertToChineseNumber(match.Groups[1].Value);
                string decimalPart = ConvertDigitsToChineseDigits(match.Groups[2].Value);
                return intPart + "点" + decimalPart;
            });

            // 匹配普通整数（但不包括已经处理过的）
            text = Regex.Replace(text, @"\b\d+\b", match =>
            {
                return ConvertToChineseNumber(match.Value);
            });

            return text;
        }

        private static string ConvertToChineseNumber(string numberString)
        {
            if (!long.TryParse(numberString, out long number))
                return numberString;

            return ConvertToChineseNumber(number);
        }

        private static string ConvertToChineseNumber(long number)
        {
            if (number == 0) return "零";
            if (number < 0) return "负" + ConvertToChineseNumber(-number);

            StringBuilder result = new StringBuilder();
            int unitIndex = 0;

            while (number > 0)
            {
                int section = (int)(number % 10000);
                if (section > 0)
                {
                    string sectionStr = ConvertSectionToChineseNumber(section);
                    result.Insert(0, sectionStr + ChineseBigUnits[unitIndex]);
                }
                else if (result.Length > 0)
                {
                    result.Insert(0, "零");
                }

                number /= 10000;
                unitIndex++;
            }

            string final = result.ToString();
            final = Regex.Replace(final, "零+", "零");
            final = Regex.Replace(final, "零([万亿])", "$1");
            final = final.TrimEnd('零');

            return final;
        }

        private static string ConvertSectionToChineseNumber(int number)
        {
            StringBuilder result = new StringBuilder();
            int unitIndex = 0;
            bool needZero = false;

            while (number > 0)
            {
                int digit = number % 10;

                if (digit == 0)
                {
                    needZero = true;
                }
                else
                {
                    if (needZero && result.Length > 0)
                    {
                        result.Insert(0, "零");
                    }
                    result.Insert(0, ChineseNumbers[digit] + ChineseUnits[unitIndex]);
                    needZero = false;
                }

                number /= 10;
                unitIndex++;
            }

            string final = result.ToString();
            if (final.StartsWith("一十"))
            {
                final = final.Substring(1);
            }

            return final;
        }

        private static string ConvertYearToChineseNumber(int year)
        {
            return ConvertDigitsToChineseDigits(year.ToString());
        }

        private static string ConvertDigitsToChineseDigits(string digits)
        {
            StringBuilder result = new StringBuilder();
            foreach (char c in digits)
            {
                if (char.IsDigit(c))
                {
                    result.Append(ChineseNumbers[c - '0']);
                }
                else
                {
                    result.Append(c);
                }
            }
            return result.ToString();
        }
    }
}