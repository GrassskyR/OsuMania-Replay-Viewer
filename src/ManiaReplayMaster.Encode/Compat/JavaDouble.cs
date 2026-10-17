using System.Globalization;
using System.Text;

namespace ManiaReplayMaster.Encode.Compat;

/// <summary>
/// Formats a <see cref="double"/> the way <c>java.lang.Double.toString(double)</c> does, which is
/// what Kotlin's <c>Double.toString()</c> (and therefore string interpolation such as
/// <c>"$sliceDuration"</c>) produces.
///
/// This matters because the ffmpeg filter graph is assembled by string interpolation in the
/// reference implementation (<c>Encoder.kt</c>): <c>trim=end=2.5</c>, <c>atempo=1.0</c>,
/// <c>adelay=1000|1000</c>. A plain <c>double.ToString()</c> would emit <c>2.5</c> but <c>1</c>
/// instead of <c>1.0</c>, and <c>1E-05</c> instead of <c>1.0E-5</c>; under a comma-decimal
/// culture it would even emit <c>2,5</c> and corrupt the filter graph. Everything here is
/// therefore invariant-culture by construction.
///
/// Java's rules (JLS / Double.toString javadoc): shortest decimal that round-trips, plain
/// notation when 1e-3 &lt;= |d| &lt; 1e7, scientific notation otherwise, and always at least one
/// digit after the decimal point.
/// </summary>
public static class JavaDouble
{
    public static string ToString(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        var negative = double.IsNegative(value);
        if (value == 0.0)
        {
            // Java distinguishes 0.0 from -0.0.
            return negative ? "-0.0" : "0.0";
        }

        var (digits, pointPos) = ShortestDigits(Math.Abs(value));

        // value == digits * 10^k, and the first significant digit sits at 10^(pointPos-1).
        var exponent = pointPos - 1;

        var builder = new StringBuilder(digits.Length + 8);
        if (negative)
        {
            builder.Append('-');
        }

        if (exponent >= -3 && exponent < 7)
        {
            var shift = pointPos - digits.Length;
            if (shift >= 0)
            {
                builder.Append(digits);
                builder.Append('0', shift);
                builder.Append(".0");
            }
            else if (pointPos > 0)
            {
                builder.Append(digits, 0, pointPos);
                builder.Append('.');
                builder.Append(digits, pointPos, digits.Length - pointPos);
            }
            else
            {
                builder.Append("0.");
                builder.Append('0', -pointPos);
                builder.Append(digits);
            }
        }
        else
        {
            builder.Append(digits[0]);
            builder.Append('.');
            if (digits.Length > 1)
            {
                builder.Append(digits, 1, digits.Length - 1);
            }
            else
            {
                builder.Append('0');
            }

            builder.Append('E');
            builder.Append(exponent.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits the shortest round-trippable decimal representation of <paramref name="value"/>
    /// (which must be finite, non-zero and non-negative) into its significant digits and the
    /// position of the decimal point relative to those digits.
    /// </summary>
    private static (string Digits, int PointPos) ShortestDigits(double value)
    {
        // .NET Core 3.0+ "R" is the shortest round-trippable representation, same intent as
        // Java's FloatingDecimal. Possible shapes: "1234", "1.5", "1E-07", "1.2345E+10".
        var text = value.ToString("R", CultureInfo.InvariantCulture);

        var exponent = 0;
        var e = text.IndexOfAny(new[] { 'E', 'e' });
        if (e >= 0)
        {
            exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.');
        var integerPart = dot >= 0 ? text[..dot] : text;
        var fractionPart = dot >= 0 ? text[(dot + 1)..] : string.Empty;

        var digits = integerPart + fractionPart;
        var pointPos = integerPart.Length + exponent;

        // Strip leading zeros (the decimal point follows them).
        var leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0')
        {
            leading++;
        }

        digits = digits[leading..];
        pointPos -= leading;

        // Strip trailing zeros (they carry no significance, the exponent already encodes them).
        var end = digits.Length;
        while (end > 1 && digits[end - 1] == '0')
        {
            end--;
        }

        digits = digits[..end];
        return (digits, pointPos);
    }
}
