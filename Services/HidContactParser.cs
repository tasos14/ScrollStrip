using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

/// <param name="Confident">
/// HID Confidence (Digitizer usage 0x47): the firmware's own palm/accidental-touch verdict.
/// True when the device does not declare the usage at all.
/// </param>
public record TouchContact(int ContactId, double NormX, double NormY, bool TipDown, bool Confident);

/// <summary>One HID input report, before hybrid-mode frames are assembled.</summary>
public sealed class ReportParse
{
    /// <summary>
    /// One entry per declared finger collection, in report order. Never filtered — the assembler
    /// indexes this list by slot and trims it to Contact Count, which is what makes a slot valid.
    /// </summary>
    public List<TouchContact> Slots { get; } = new();
    public bool AnySlotValid;
    public bool HasContactCount;
    public int ContactCount;
    public bool HasScanTime;
    public ushort ScanTime;
}

/// <summary>
/// Parses precision touchpad HID reports using preparsed data obtained
/// from GetRawInputDeviceInfo(RIDI_PREPARSEDDATA) — no CreateFile needed.
/// </summary>
public sealed class HidContactParser : IDisposable
{
    private const ushort UP_GENERIC   = 0x01;
    private const ushort UP_DIGITIZER = 0x0D;
    private const ushort U_X             = 0x30;
    private const ushort U_Y             = 0x31;
    private const ushort U_TIP           = 0x42;
    private const ushort U_CONFIDENCE    = 0x47;
    private const ushort U_CONTACT_ID    = 0x51;
    private const ushort U_CONTACT_COUNT = 0x54;
    private const ushort U_SCAN_TIME     = 0x56;

    private sealed class FingerCollection
    {
        public ushort Link;
        public int XMax;
        public int YMax;
        public bool HasButtons;
    }

    private IntPtr _preparsedData;
    private HIDP_CAPS _caps;

    // Finger collections in ascending link-collection order, which is report (slot) order.
    private readonly List<FingerCollection> _fingers = new();
    private bool _declaresConfidence;

    private bool _hasContactCountCap;
    private ushort _contactCountLink;
    private bool _hasScanTimeCap;
    private ushort _scanTimeLink;

    public bool IsValid => _preparsedData != IntPtr.Zero;

    /// <summary>Touch surface width in millimetres, or 0 when the descriptor gives no physical units.</summary>
    public double PadWidthMm { get; private set; }

    /// <summary>Touch surface height in millimetres, or 0 when the descriptor gives no physical units.</summary>
    public double PadHeightMm { get; private set; }

    /// <summary>
    /// Initialize using the hDevice from RAWINPUTHEADER via RIDI_PREPARSEDDATA.
    /// This is the correct approach — no CreateFile required.
    /// </summary>
    public bool Init(IntPtr hDevice)
    {
        Free();

        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return false;

        _preparsedData = Marshal.AllocHGlobal((int)size);
        uint result = GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, _preparsedData, ref size);
        if (result == uint.MaxValue) { Free(); return false; }

        if (HidP_GetCaps(_preparsedData, out _caps) != HIDP_STATUS_SUCCESS)
        { Free(); return false; }

        BuildCollectionMaps();
        return _fingers.Count > 0;
    }

    private void BuildCollectionMaps()
    {
        _fingers.Clear();
        _declaresConfidence = false;
        _hasContactCountCap = false;
        _hasScanTimeCap     = false;
        PadWidthMm  = 0;
        PadHeightMm = 0;

        // Value caps → find which link collections have X and Y
        ushort numValueCaps = _caps.NumberInputValueCaps;
        if (numValueCaps == 0) return;

        var valueCaps = new HIDP_VALUE_CAPS[numValueCaps];
        if (HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, valueCaps, ref numValueCaps, _preparsedData)
            != HIDP_STATUS_SUCCESS) return;

        var byLink = new Dictionary<ushort, FingerCollection>();
        double widthMm = 0, heightMm = 0;

        foreach (var vc in valueCaps)
        {
            if (vc.UsagePage == UP_DIGITIZER && vc.UsageMin == U_CONTACT_COUNT && !_hasContactCountCap)
            { _hasContactCountCap = true; _contactCountLink = vc.LinkCollection; }
            else if (vc.UsagePage == UP_DIGITIZER && vc.UsageMin == U_SCAN_TIME && !_hasScanTimeCap)
            { _hasScanTimeCap = true; _scanTimeLink = vc.LinkCollection; }

            if (vc.LinkCollection == 0) continue; // skip root collection
            if (vc.UsagePage != UP_GENERIC) continue;
            if (vc.UsageMin != U_X && vc.UsageMin != U_Y) continue;

            if (!byLink.TryGetValue(vc.LinkCollection, out var fc))
                byLink[vc.LinkCollection] = fc = new FingerCollection { Link = vc.LinkCollection };

            if (vc.UsageMin == U_X)
            {
                fc.XMax = vc.LogicalMax;
                if (widthMm == 0 && TryPhysicalMm(vc, out double mm)) widthMm = mm;
            }
            else
            {
                fc.YMax = vc.LogicalMax;
                if (heightMm == 0 && TryPhysicalMm(vc, out double mm)) heightMm = mm;
            }
        }

        // Keep only collections that carry both axes, in report order.
        _fingers.AddRange(byLink.Values
            .Where(f => f.XMax > 0 && f.YMax > 0)
            .OrderBy(f => f.Link));

        if (widthMm > 0 && heightMm > 0) { PadWidthMm = widthMm; PadHeightMm = heightMm; }

        if (_fingers.Count == 0) return;

        // Button caps → which collections carry Tip Switch / Confidence
        ushort numButtonCaps = _caps.NumberInputButtonCaps;
        if (numButtonCaps == 0) return;

        var buttonCaps = new HIDP_BUTTON_CAPS[numButtonCaps];
        if (HidP_GetButtonCaps(HIDP_REPORT_TYPE.HidP_Input, buttonCaps, ref numButtonCaps, _preparsedData)
            != HIDP_STATUS_SUCCESS) return;

        foreach (var bc in buttonCaps)
        {
            if (bc.UsagePage != UP_DIGITIZER) continue;
            bool isTip        = InUsageRange(bc, U_TIP);
            bool isConfidence = InUsageRange(bc, U_CONFIDENCE);
            if (!isTip && !isConfidence) continue;
            if (isConfidence) _declaresConfidence = true;

            var fc = _fingers.FirstOrDefault(f => f.Link == bc.LinkCollection);
            if (fc != null) fc.HasButtons = true;
        }
    }

    private static bool InUsageRange(in HIDP_BUTTON_CAPS bc, ushort usage) =>
        bc.IsRange ? usage >= bc.UsageMin && usage <= bc.UsageMax : bc.UsageMin == usage;

    /// <summary>
    /// Converts a value cap's physical range to millimetres using the HID Unit and Unit Exponent
    /// nibbles. Windows precision touchpads report centimetres with exponent -2.
    /// </summary>
    private static bool TryPhysicalMm(in HIDP_VALUE_CAPS vc, out double mm)
    {
        mm = 0;
        int range = vc.PhysicalMax - vc.PhysicalMin;
        if (range <= 0 || vc.Units == 0) return false;

        int system    = (int)(vc.Units & 0xF);              // 1 = SI linear (cm), 3 = English linear (inch)
        int lengthExp = SignedNibble((int)((vc.Units >> 4) & 0xF));
        if (lengthExp != 1) return false;                   // a plain length, not an area or a rate

        double baseToMm = system switch { 1 => 10.0, 3 => 25.4, _ => 0.0 };
        if (baseToMm == 0) return false;

        // UnitsExp arrives as the raw 4-bit descriptor nibble. Masking also normalizes a
        // sign-extended value, since (-2 & 0xF) == 0xE decodes back to -2.
        int exp = SignedNibble((int)(vc.UnitsExp & 0xF));
        mm = range * Math.Pow(10, exp) * baseToMm;
        return mm > 1.0 && mm < 1000.0;
    }

    private static int SignedNibble(int n) => n > 7 ? n - 16 : n;

    public ReportParse Parse(byte[] report)
    {
        var parse = new ReportParse();
        if (!IsValid || report.Length == 0 || _fingers.Count == 0) return parse;

        unsafe
        {
            fixed (byte* pReport = report)
            {
                IntPtr pRep = (IntPtr)pReport;
                uint repLen = (uint)report.Length;

                if (_hasContactCountCap &&
                    HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, UP_DIGITIZER, _contactCountLink,
                        U_CONTACT_COUNT, out uint count, _preparsedData, pRep, repLen) == HIDP_STATUS_SUCCESS)
                {
                    parse.HasContactCount = true;
                    parse.ContactCount    = (int)count;
                }

                if (_hasScanTimeCap &&
                    HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input, UP_DIGITIZER, _scanTimeLink,
                        U_SCAN_TIME, out uint scan, _preparsedData, pRep, repLen) == HIDP_STATUS_SUCCESS)
                {
                    parse.HasScanTime = true;
                    parse.ScanTime    = (ushort)scan;
                }

                var usages = new ushort[16];

                foreach (var fc in _fingers)
                {
                    var xResult = HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input,
                        UP_GENERIC, fc.Link, U_X, out uint xVal, _preparsedData, pRep, repLen);
                    var yResult = HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input,
                        UP_GENERIC, fc.Link, U_Y, out uint yVal, _preparsedData, pRep, repLen);

                    if (xResult != HIDP_STATUS_SUCCESS || yResult != HIDP_STATUS_SUCCESS)
                    {
                        parse.Slots.Add(new TouchContact(-1, 0, 0, false, false));
                        continue;
                    }

                    HidP_GetUsageValue(HIDP_REPORT_TYPE.HidP_Input,
                        UP_DIGITIZER, fc.Link, U_CONTACT_ID, out uint contactId, _preparsedData, pRep, repLen);

                    // Tip Switch and Confidence are buttons — one HidP_GetUsages call returns both.
                    bool tipDown   = false;
                    bool confident = !_declaresConfidence;
                    if (fc.HasButtons)
                    {
                        uint usageLen = (uint)usages.Length;
                        if (HidP_GetUsages(HIDP_REPORT_TYPE.HidP_Input, UP_DIGITIZER, fc.Link,
                                usages, ref usageLen, _preparsedData, pRep, repLen) == HIDP_STATUS_SUCCESS)
                        {
                            for (int i = 0; i < (int)usageLen; i++)
                            {
                                if (usages[i] == U_TIP) tipDown = true;
                                else if (usages[i] == U_CONFIDENCE) confident = true;
                            }
                        }
                    }
                    else
                    {
                        // Fallback: if X/Y are non-zero, assume finger is down
                        tipDown = xVal != 0 || yVal != 0;
                    }

                    parse.AnySlotValid = true;
                    parse.Slots.Add(new TouchContact((int)contactId,
                        (double)xVal / fc.XMax, (double)yVal / fc.YMax, tipDown, confident));
                }
            }
        }

        return parse;
    }

    private void Free()
    {
        if (_preparsedData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_preparsedData);
            _preparsedData = IntPtr.Zero;
        }
        _fingers.Clear();
        PadWidthMm  = 0;
        PadHeightMm = 0;
    }

    public void Dispose() => Free();
}
