using System;
using System.Globalization;

namespace PBZTrainer.Models
{
    public static class NumericInput
    {
        public const string ErrorMessage = "请输入有效数字，例如 0 或 1.5";

        public static bool TryParse(string text, out double value)
        {
            bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                          double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            if (!parsed || !double.IsFinite(value)) return false;
            if (value != 0) return true;

            // 小于 double 精度的非零输入也会被解析为 0，不能据此执行清零。
            var mantissa = text.AsSpan();
            int exponent = mantissa.IndexOfAny('e', 'E');
            if (exponent >= 0) mantissa = mantissa[..exponent];
            return mantissa.IndexOfAnyInRange('1', '9') < 0;
        }
    }
}
