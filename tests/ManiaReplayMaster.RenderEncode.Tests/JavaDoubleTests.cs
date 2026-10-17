using ManiaReplayMaster.Encode.Compat;

namespace ManiaReplayMaster.RenderEncode.Tests;

/// <summary>
/// <c>JavaDouble.ToString</c> exists because the reference builds its ffmpeg filter graph with
/// Kotlin string interpolation, so <c>1.0</c> must render as <c>"1.0"</c> (not <c>"1"</c>) and
/// <c>2.5</c> as <c>"2.5"</c> regardless of the machine's decimal separator.
/// Expected values are <c>java.lang.Double.toString</c> output.
/// </summary>
public class JavaDoubleTests
{
    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1.0, "1.0")]
    [InlineData(2.5, "2.5")]
    [InlineData(5.0, "5.0")]
    [InlineData(100.0, "100.0")]
    [InlineData(1234.0, "1234.0")]
    [InlineData(-500.0, "-500.0")]
    [InlineData(0.5, "0.5")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.001, "0.001")]
    [InlineData(0.0001, "1.0E-4")]
    [InlineData(0.00003, "3.0E-5")]
    [InlineData(10000000.0, "1.0E7")]
    [InlineData(1234567.0, "1234567.0")]
    [InlineData(12345678.0, "1.2345678E7")]
    [InlineData(1.23456789e7, "1.23456789E7")]
    [InlineData(1.0e22, "1.0E22")]
    [InlineData(1.0e-322, "1.0E-322")]
    public void MatchesJavaDoubleToString(double value, string expected)
    {
        Assert.Equal(expected, JavaDouble.ToString(value));
    }

    [Fact]
    public void RepeatingDecimal_MatchesJava()
    {
        // 1.0 / 60.0, i.e. a slice duration at 60 fps would be exactly representable; 1/60 is not.
        Assert.Equal("0.016666666666666666", JavaDouble.ToString(1.0 / 60.0));
    }

    [Fact]
    public void FloatArtefact_MatchesJava()
    {
        Assert.Equal("0.30000000000000004", JavaDouble.ToString(0.1 + 0.2));
    }

    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void MatchesJavaSpecials(double value, string expected)
    {
        Assert.Equal(expected, JavaDouble.ToString(value));
    }

    [Fact]
    public void IsCultureIndependent()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A comma-decimal culture is exactly the failure this helper protects the filter graph from.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("2.5", JavaDouble.ToString(2.5));
            Assert.Equal("1.0", JavaDouble.ToString(1.0));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
