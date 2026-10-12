namespace ScrollStrip.Tests;

/// <summary>Physical-size decoding from HID Unit / Unit Exponent, which needs no device.</summary>
public class HidUnitsTests
{
    private const uint SiLinearCm   = 0x11;   // system 1 (SI linear), length exponent 1
    private const uint EnglishInch  = 0x13;   // system 3 (English linear), length exponent 1
    private const uint ExpMinus2    = 0xE;    // raw 4-bit nibble for -2
    private const uint ExpMinus3    = 0xD;

    private static NativeMethods.HIDP_VALUE_CAPS Caps(uint units, uint exp, int physMin, int physMax) =>
        new() { Units = units, UnitsExp = exp, PhysicalMin = physMin, PhysicalMax = physMax };

    [Fact]
    public void Decodes_centimetres_with_exponent_minus_two()
    {
        // The usual precision-touchpad encoding; 1600 × 10⁻² cm is the development pad's width.
        Assert.True(HidContactParser.TryPhysicalMm(Caps(SiLinearCm, ExpMinus2, 0, 1600), out double mm));
        Assert.Equal(160.0, mm, precision: 9);
    }

    [Fact]
    public void Accepts_a_sign_extended_exponent()
    {
        Assert.True(HidContactParser.TryPhysicalMm(Caps(SiLinearCm, unchecked((uint)-2), 0, 1600), out double mm));
        Assert.Equal(160.0, mm, precision: 9);
    }

    [Fact]
    public void Uses_the_physical_range_not_the_maximum()
    {
        Assert.True(HidContactParser.TryPhysicalMm(Caps(SiLinearCm, ExpMinus2, 100, 1249), out double mm));
        Assert.Equal(114.9, mm, precision: 9);
    }

    [Fact]
    public void Decodes_inches()
    {
        Assert.True(HidContactParser.TryPhysicalMm(Caps(EnglishInch, ExpMinus3, 0, 6299), out double mm));
        Assert.Equal(6.299 * 25.4, mm, precision: 6);
    }

    [Theory]
    [InlineData(0x21u, "an area, not a length")]
    [InlineData(0x12u, "SI rotation")]
    [InlineData(0x00u, "no unit")]
    public void Rejects_units_that_are_not_a_length(uint units, string because)
    {
        Assert.False(HidContactParser.TryPhysicalMm(Caps(units, ExpMinus2, 0, 1600), out _), because);
    }

    [Fact]
    public void Rejects_a_missing_physical_range()
    {
        Assert.False(HidContactParser.TryPhysicalMm(Caps(SiLinearCm, ExpMinus2, 0, 0), out _));
    }

    [Fact]
    public void Rejects_an_implausible_size()
    {
        // 5000 cm: a descriptor error, not a touchpad.
        Assert.False(HidContactParser.TryPhysicalMm(Caps(SiLinearCm, 0, 0, 5000), out _));
    }
}
