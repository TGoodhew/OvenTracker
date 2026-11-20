using System.Runtime.InteropServices;
using System.Text;

namespace OvenTracker.Meter;

/// <summary>
/// The VISA C library (NI-VISA, Keysight IO Libraries, R&amp;S VISA), for GPIB, USB-TMC and LAN instruments.
/// It's loaded only when one is asked for, so the app builds and runs without VISA: <see cref="IsAvailable"/>
/// says whether it could be found, and <see cref="MissingHelp"/> what to install.
/// </summary>
public static class Visa
{
    const string Lib = "ovt-visa";

    static readonly Lazy<IntPtr> Handle = new(Load);

    static Visa() => NativeLibrary.SetDllImportResolver(typeof(Visa).Assembly, (name, _, _) =>
        name == Lib ? Handle.Value : IntPtr.Zero);

    /// <summary>The library names tried, in order, for this system.</summary>
    public static IReadOnlyList<string> Candidates =>
        OperatingSystem.IsWindows() ? Environment.Is64BitProcess ? ["visa64.dll"] : ["visa32.dll"]
        : OperatingSystem.IsMacOS() ? ["/Library/Frameworks/VISA.framework/VISA"]
        : ["libvisa.so", "libvisa.so.0", "libiovisa.so", "librsvisa.so"];

    static IntPtr Load()
    {
        foreach (var name in Candidates)
            if (NativeLibrary.TryLoad(name, out var h)) return h;
        return IntPtr.Zero;
    }

    /// <summary>Whether a VISA library is installed (one of <see cref="Candidates"/> loads).</summary>
    public static bool IsAvailable => Handle.Value != IntPtr.Zero;

    public static string MissingHelp => OperatingSystem.IsWindows()
        ? $"VISA isn't installed (no {Candidates[0]}). For GPIB, install NI-VISA (with NI-488.2 for an NI adapter) or Keysight IO Libraries Suite, 64-bit."
        : $"VISA isn't installed (none of {string.Join(", ", Candidates)}). For GPIB on Linux, install NI-VISA with NI-488.2 (or linux-gpib with a VISA that uses it).";

    // Status codes and attributes, from visa.h.
    public const int Success = 0;
    public const int ErrorTimeout = unchecked((int)0xBFFF0015);
    public const int ErrorResourceNotFound = unchecked((int)0xBFFF0011);
    public const uint AttrTimeout = 0x3FFF001A;
    const uint GpibRenAddressGtl = 6;

    static class Native
    {
        [DllImport(Lib, EntryPoint = "viOpenDefaultRM", CallingConvention = CallingConvention.Winapi)]
        public static extern int OpenDefaultRM(out uint session);

        [DllImport(Lib, EntryPoint = "viOpen", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern int Open(uint rm, string resource, uint accessMode, uint openTimeout, out uint vi);

        [DllImport(Lib, EntryPoint = "viClose", CallingConvention = CallingConvention.Winapi)]
        public static extern int Close(uint vi);

        [DllImport(Lib, EntryPoint = "viWrite", CallingConvention = CallingConvention.Winapi)]
        public static extern int Write(uint vi, byte[] buffer, uint count, out uint written);

        [DllImport(Lib, EntryPoint = "viRead", CallingConvention = CallingConvention.Winapi)]
        public static extern int Read(uint vi, byte[] buffer, uint count, out uint read);

        [DllImport(Lib, EntryPoint = "viSetAttribute", CallingConvention = CallingConvention.Winapi)]
        public static extern int SetAttribute(uint vi, uint attribute, nuint value);

        [DllImport(Lib, EntryPoint = "viClear", CallingConvention = CallingConvention.Winapi)]
        public static extern int Clear(uint vi);

        [DllImport(Lib, EntryPoint = "viGpibControlREN", CallingConvention = CallingConvention.Winapi)]
        public static extern int GpibControlRen(uint vi, ushort mode);

        [DllImport(Lib, EntryPoint = "viFindRsrc", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern int FindRsrc(uint rm, string expression, out uint findList, out uint count, byte[] description);

        [DllImport(Lib, EntryPoint = "viFindNext", CallingConvention = CallingConvention.Winapi)]
        public static extern int FindNext(uint findList, byte[] description);

        [DllImport(Lib, EntryPoint = "viStatusDesc", CallingConvention = CallingConvention.Winapi)]
        public static extern int StatusDesc(uint vi, int status, byte[] description);
    }

    static uint? rm;

    /// <summary>The default resource manager, opened once.</summary>
    static uint ResourceManager()
    {
        if (!IsAvailable) throw new MeterException(MissingHelp);
        if (rm is { } open) return open;
        Check(0, Native.OpenDefaultRM(out var s), "open the VISA resource manager");
        rm = s;
        return s;
    }

    static string Describe(uint vi, int status)
    {
        var buf = new byte[256];
        try
        {
            if (Native.StatusDesc(vi, status, buf) >= Success) return Text(buf);
        }
        catch (EntryPointNotFoundException) { }
        return $"VISA error 0x{status:X8}";
    }

    static string Text(byte[] buf)
    {
        var end = Array.IndexOf(buf, (byte)0);
        return Encoding.ASCII.GetString(buf, 0, end < 0 ? buf.Length : end).Trim();
    }

    static void Check(uint vi, int status, string what)
    {
        if (status >= Success) return;
        if (status == ErrorTimeout) throw new TimeoutException($"VISA timed out trying to {what}.");
        throw new MeterException($"Couldn't {what}: {Describe(vi, status)}.");
    }

    /// <summary>The instruments VISA knows matching an expression, e.g. "GPIB?*INSTR". Empty if VISA isn't installed or none match.</summary>
    public static IReadOnlyList<string> Find(string expression)
    {
        if (!IsAvailable) return [];
        var list = new List<string>();
        try
        {
            var buf = new byte[256];
            if (Native.FindRsrc(ResourceManager(), expression, out var find, out var count, buf) < Success) return list;
            list.Add(Text(buf));
            for (var i = 1; i < count; i++)
            {
                if (Native.FindNext(find, buf) < Success) break;
                list.Add(Text(buf));
            }
            Native.Close(find);
        }
        catch (Exception ex) when (ex is MeterException or DllNotFoundException or EntryPointNotFoundException) { }
        return list;
    }

    /// <summary>Opens an instrument by its VISA resource string.</summary>
    public static VisaSession Open(string resource, TimeSpan timeout)
    {
        var mgr = ResourceManager();
        var status = Native.Open(mgr, resource, 0, 2000, out var vi);
        if (status == ErrorResourceNotFound) throw new MeterException($"VISA found nothing at {resource}.");
        Check(0, status, $"open {resource}");
        var session = new VisaSession(vi, resource);
        session.Timeout = timeout;
        return session;
    }

    /// <summary>One open instrument.</summary>
    public sealed class VisaSession(uint vi, string resource) : IDisposable
    {
        bool closed;

        public string Resource => resource;

        public TimeSpan Timeout
        {
            set => Check(vi, Native.SetAttribute(vi, AttrTimeout, (nuint)Math.Max(1, value.TotalMilliseconds)), "set the timeout");
        }

        public void Write(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            Check(vi, Native.Write(vi, bytes, (uint)bytes.Length, out _), $"send {text.Trim()} to {resource}");
        }

        /// <summary>Reads one reply, up to the END (EOI) or the termination character.</summary>
        public string Read()
        {
            var sb = new StringBuilder();
            var buf = new byte[1024];
            while (true)
            {
                var status = Native.Read(vi, buf, (uint)buf.Length, out var n);
                Check(vi, status, $"read from {resource}");
                sb.Append(Encoding.ASCII.GetString(buf, 0, (int)n));
                // VI_SUCCESS_MAX_CNT (0x3FFF0006): the buffer filled before the end, so there's more.
                if (status != 0x3FFF0006) return sb.ToString();
            }
        }

        /// <summary>The device clear (SDC): stops whatever it was doing and empties its buffers.</summary>
        public void Clear() => Check(vi, Native.Clear(vi), $"clear {resource}");

        /// <summary>Sends a GPIB instrument back to local, so its front panel works again.</summary>
        public void GoToLocal()
        {
            try { Native.GpibControlRen(vi, (ushort)GpibRenAddressGtl); } catch { /* not GPIB, or not supported */ }
        }

        public void Dispose()
        {
            if (closed) return;
            closed = true;
            try { Native.Close(vi); } catch { }
        }
    }
}
