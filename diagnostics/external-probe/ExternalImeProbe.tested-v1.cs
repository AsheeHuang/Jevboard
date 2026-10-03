// Read-only diagnostic. No keyboard injection, window messages that mutate text,
// clipboard operations, input-method activation, or UI Automation writes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;

internal static class ExternalImeProbe
{
    private const string Prefix = "Jevboard IME Probe -";
    private static readonly HashSet<uint> AllowedPids = new HashSet<uint>();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static StreamWriter Output;
    private static IntPtr NativeUia;
    private static readonly HashSet<string> MetadataKeys = new HashSet<string>();

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GUITHREADINFO
    {
        public uint cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OSVERSIONINFOEX
    {
        public uint size, major, minor, build, platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string servicePack;
        public ushort servicePackMajor, servicePackMinor, suiteMask;
        public byte productType, reserved;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("imm32.dll", SetLastError = true)] private static extern IntPtr ImmGetContext(IntPtr hwnd);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);
    [DllImport("imm32.dll", SetLastError = true)] private static extern int ImmGetCompositionStringW(IntPtr context, uint index, IntPtr buffer, uint size);
    [DllImport("imm32.dll", SetLastError = true)] private static extern uint ImmGetCandidateListW(IntPtr context, uint index, IntPtr buffer, uint size);
    [DllImport("imm32.dll", SetLastError = true)] private static extern uint ImmGetCandidateListCountW(IntPtr context, out uint count);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint ImmGetIMEFileNameW(IntPtr hkl, StringBuilder text, uint size);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern uint ImmGetDescriptionW(IntPtr hkl, StringBuilder text, uint size);
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref OSVERSIONINFOEX info);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    // Exact COM method order is checked against Microsoft's generated
    // UIAutomationClient.h (win32metadata), not a partial interface declaration.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPointer(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInt(IntPtr self, out int result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPatternAs(IntPtr self, int id, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetText(IntPtr self, int maxLength, out IntPtr bstr);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MoveEndpointByRange(IntPtr self, int endpoint, IntPtr range, int targetEndpoint);
    private static T Slot<T>(IntPtr obj, int slot) where T : class
    {
        IntPtr table = Marshal.ReadIntPtr(obj);
        return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T)) as T;
    }
    private static Dictionary<string, object> D(params object[] items)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < items.Length; i += 2) result[(string)items[i]] = items[i + 1];
        return result;
    }
    private static string H(IntPtr p) { return "0x" + p.ToInt64().ToString("X"); }
    private static string HR(int value) { return "0x" + unchecked((uint)value).ToString("X8"); }
    private static Dictionary<string, object> Error(Exception ex)
    { return D("status", "exception", "type", ex.GetType().Name, "hresult", HR(ex.HResult), "message", ex.Message); }
    private static void Emit(Dictionary<string, object> item)
    {
        item["utc"] = DateTime.UtcNow.ToString("o");
        item["probe_pid"] = Process.GetCurrentProcess().Id;
        Output.WriteLine(Json.Serialize(item)); Output.Flush();
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string path = null; int seconds = 240, interval = 400; bool once = false;
        try
        {
            if (args.Length == 0)
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-config.json");
                var config = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath, Encoding.UTF8));
                var launchArgs = new List<string>();
                object configuredPids;
                if (!config.TryGetValue("pids", out configuredPids)) throw new ArgumentException("probe-config.json requires a pids array.");
                foreach (object configuredPid in (System.Collections.IEnumerable)configuredPids)
                { launchArgs.Add("--pid"); launchArgs.Add(Convert.ToUInt32(configuredPid).ToString()); }
                launchArgs.Add("--output"); launchArgs.Add(Convert.ToString(config["output"]));
                object option;
                if (config.TryGetValue("seconds", out option)) { launchArgs.Add("--seconds"); launchArgs.Add(Convert.ToInt32(option).ToString()); }
                if (config.TryGetValue("interval_ms", out option)) { launchArgs.Add("--interval-ms"); launchArgs.Add(Convert.ToInt32(option).ToString()); }
                if (config.TryGetValue("once", out option) && Convert.ToBoolean(option)) launchArgs.Add("--once");
                args = launchArgs.ToArray();
            }
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--pid":
                        uint approvedPid = uint.Parse(args[++i]);
                        if (approvedPid == 0) throw new ArgumentException("Host PID must be positive.");
                        AllowedPids.Add(approvedPid); break;
                    case "--output": path = args[++i]; break;
                    case "--seconds": seconds = int.Parse(args[++i]); break;
                    case "--interval-ms": interval = int.Parse(args[++i]); break;
                    case "--once": once = true; break;
                    default: throw new ArgumentException("Unknown option " + args[i]);
                }
            }
            if (AllowedPids.Count == 0 || path == null || seconds < 1 || seconds > 3600 || interval < 100)
                throw new ArgumentException("Usage: ExternalImeProbe.exe --pid HOST_PID [--pid SECOND_HOST_PID] --output LOG.jsonl [--seconds 240] [--interval-ms 400] [--once]");
            Output = new StreamWriter(Path.GetFullPath(path), false, new UTF8Encoding(false));
            var os = new OSVERSIONINFOEX(); os.size = (uint)Marshal.SizeOf(os);
            int versionResult = RtlGetVersion(ref os);
            Emit(D("kind", "environment", "os_api", "RtlGetVersion", "os_hresult", HR(versionResult), "major", os.major, "minor", os.minor, "build", os.build,
                "is_64bit_process", Environment.Is64BitProcess, "allowed_pids", new List<uint>(AllowedPids), "required_title_prefix", Prefix,
                "scope", "only the focused element of explicitly allowed synthetic host processes; no IME configuration changes or text writes"));
            int com = CoInitializeEx(IntPtr.Zero, 2);
            Guid clsid = new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E");
            Guid iid = new Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
            int create = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out NativeUia);
            Emit(D("kind", "native_uia_init", "coinitialize_hresult", HR(com), "create_hresult", HR(create)));
            var timer = Stopwatch.StartNew();
            do
            {
                try { Sample(); }
                catch (Exception ex) { Emit(D("kind", "sample_failure", "error", Error(ex))); }
                if (once) break;
                Thread.Sleep(interval);
            } while (timer.Elapsed.TotalSeconds < seconds);
            Emit(D("kind", "probe_complete", "seconds", timer.Elapsed.TotalSeconds));
            if (NativeUia != IntPtr.Zero) Marshal.Release(NativeUia);
            if (com >= 0) CoUninitialize();
            Output.Dispose(); return 0;
        }
        catch (Exception ex)
        {
            try
            {
                if (Output == null) Output = new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-error.jsonl"), false, new UTF8Encoding(false));
                Emit(D("kind", "fatal_error", "error", Error(ex)));
                Output.Dispose();
            }
            catch { }
            return 1;
        }
    }
    private static void Sample()
    {
        IntPtr fg = GetForegroundWindow(); uint fgPid;
        uint thread = GetWindowThreadProcessId(fg, out fgPid);
        if (fg == IntPtr.Zero || !AllowedPids.Contains(fgPid)) { Emit(D("kind", "sample", "status", "foreground_outside_allowlist")); return; }
        var gui = new GUITHREADINFO(); gui.cbSize = (uint)Marshal.SizeOf(gui);
        if (!GetGUIThreadInfo(thread, ref gui)) { Emit(D("kind", "sample", "status", "gui_thread_info_failed", "win32_error", Marshal.GetLastWin32Error())); return; }
        IntPtr focus = gui.hwndFocus; uint focusPid;
        uint focusThread = GetWindowThreadProcessId(focus, out focusPid);
        IntPtr root = GetAncestor(focus, 2); uint rootPid; GetWindowThreadProcessId(root, out rootPid);
        if (focus == IntPtr.Zero || focusPid != fgPid || rootPid != fgPid || !AllowedPids.Contains(focusPid))
        { Emit(D("kind", "sample", "status", "focus_outside_allowlist")); return; }
        var title = new StringBuilder(256); GetWindowTextW(root, title, title.Capacity);
        if (!title.ToString().StartsWith(Prefix, StringComparison.Ordinal))
        { Emit(D("kind", "sample", "status", "title_prefix_mismatch", "host_pid", focusPid)); return; }
        var klass = new StringBuilder(128); GetClassNameW(focus, klass, klass.Capacity);
        IntPtr hkl = GetKeyboardLayout(focusThread);
        string key = focusPid + ":" + H(hkl);
        if (MetadataKeys.Add(key)) EmitImeMetadata(hkl, focusPid, focusThread);
        var sample = D("kind", "sample", "status", "approved_target", "host_pid", focusPid, "target_thread", focusThread,
            "root_hwnd", H(root), "focus_hwnd", H(focus), "focus_class", klass.ToString(), "hkl", H(hkl),
            "gui_caret_hwnd", H(gui.hwndCaret), "gui_caret_rect", D("left", gui.rcCaret.left, "top", gui.rcCaret.top, "right", gui.rcCaret.right, "bottom", gui.rcCaret.bottom));
        sample["imm32"] = ReadImm(focus);
        sample["managed_uia"] = ReadManagedUia(focusPid);
        sample["native_text_edit"] = ReadNativeTextEdit(focusPid);
        Emit(sample);
    }
    private static void EmitImeMetadata(IntPtr hkl, uint pid, uint thread)
    {
        var file = new StringBuilder(1024); var description = new StringBuilder(1024);
        uint count = ImmGetIMEFileNameW(hkl, file, (uint)file.Capacity);
        int lastError = Marshal.GetLastWin32Error();
        uint descriptionCount = ImmGetDescriptionW(hkl, description, (uint)description.Capacity);
        var metadata = D("kind", "target_ime_metadata", "host_pid", pid, "target_thread", thread, "hkl", H(hkl),
            "imm_ime_filename_count", count, "imm_ime_filename", file.ToString(), "filename_win32_error_diagnostic_only", lastError,
            "imm_description_count", descriptionCount, "imm_description", description.ToString(),
            "note", "HKL and IMM description do not uniquely establish modern TSF profile identity; a zero filename return is not proof that no IME is active");
        if (count > 0)
        {
            string full = Path.IsPathRooted(file.ToString()) ? file.ToString() : Path.Combine(Environment.SystemDirectory, file.ToString());
            if (File.Exists(full)) { var version = FileVersionInfo.GetVersionInfo(full); metadata["ime_file_path"] = full; metadata["ime_file_version"] = version.FileVersion; metadata["ime_product_version"] = version.ProductVersion; }
        }
        Emit(metadata);
    }
    private static object ReadImm(IntPtr hwnd)
    {
        IntPtr context = ImmGetContext(hwnd); int error = Marshal.GetLastWin32Error();
        if (context == IntPtr.Zero) return D("status", "null_context", "win32_error_diagnostic_only", error,
            "note", "External/thread-bound IMM access failed; this does not establish absence of composition or candidates");
        try
        {
            return D("status", "context_obtained", "context", H(context), "scope", "external_process",
                "compstr", ReadComposition(context, 0x0008), "compreadstr", ReadComposition(context, 0x0001),
                "resultstr", ReadComposition(context, 0x0800), "resultreadstr", ReadComposition(context, 0x0200),
                "cursorpos", ReadScalar(context, 0x0080), "candidates", ReadCandidates(context));
        }
        finally { ImmReleaseContext(hwnd, context); }
    }
    private static object ReadScalar(IntPtr context, uint index)
    {
        int n = ImmGetCompositionStringW(context, index, IntPtr.Zero, 0);
        return D("return", n, "status", n < 0 ? "imm_error" : "scalar", "note", n == -1 ? "IMM_ERROR_NODATA" : n == -2 ? "IMM_ERROR_GENERAL" : null);
    }
    private static object ReadComposition(IntPtr context, uint index)
    {
        int n = ImmGetCompositionStringW(context, index, IntPtr.Zero, 0);
        if (n <= 0) return D("status", n < 0 ? "imm_error" : "zero_length_return", "size_return", n,
            "note", n == -1 ? "IMM_ERROR_NODATA" : n == -2 ? "IMM_ERROR_GENERAL" : "No bytes returned through this external route; not proof of no composition");
        if (n > 1048576) return D("status", "rejected_oversized", "size_return", n);
        IntPtr buf = Marshal.AllocHGlobal(n);
        try
        {
            int got = ImmGetCompositionStringW(context, index, buf, (uint)n);
            return D("status", got > 0 ? "read" : got == 0 ? "zero_length_return" : "imm_error", "size_return", n, "read_return", got,
                "text", got > 0 && got <= n ? Marshal.PtrToStringUni(buf, got / 2) : null);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
    private static object ReadCandidates(IntPtr context)
    {
        uint count; uint total = ImmGetCandidateListCountW(context, out count);
        var lists = new List<object>();
        // Probe index 0 even when count returns zero, and distinguish its zero return.
        uint attempts = Math.Min(Math.Max(count, 1), 32);
        for (uint index = 0; index < attempts; index++)
        {
            uint bytes = ImmGetCandidateListW(context, index, IntPtr.Zero, 0);
            if (bytes == 0) { lists.Add(D("index", index, "status", "zero_size_return", "size_return", 0, "note", "No list exposed by this route; not proof there are no IME candidates")); continue; }
            if (bytes > 1048576 || bytes < 24) { lists.Add(D("index", index, "status", "invalid_size", "size_return", bytes)); continue; }
            IntPtr buf = Marshal.AllocHGlobal((int)bytes);
            try
            {
                uint got = ImmGetCandidateListW(context, index, buf, bytes);
                if (got < 24 || got > bytes) { lists.Add(D("index", index, "status", "read_failed_or_size_changed", "size_return", bytes, "read_return", got)); continue; }
                uint n = unchecked((uint)Marshal.ReadInt32(buf, 8)); var strings = new List<string>();
                if (n > 4096 || 24L + 4L * n > got) { lists.Add(D("index", index, "status", "invalid_offset_table", "candidate_count", n)); continue; }
                for (int j = 0; j < n; j++)
                {
                    uint offset = unchecked((uint)Marshal.ReadInt32(buf, 24 + 4 * j));
                    if (offset < 24 + 4 * n || offset >= got || offset % 2 != 0) { strings.Add(null); continue; }
                    var s = new StringBuilder();
                    for (uint pos = offset; pos + 1 < got; pos += 2) { ushort ch = unchecked((ushort)Marshal.ReadInt16(buf, (int)pos)); if (ch == 0) break; s.Append((char)ch); }
                    strings.Add(s.ToString());
                }
                lists.Add(D("index", index, "status", "read", "size_return", bytes, "read_return", got, "style", Marshal.ReadInt32(buf, 4),
                    "candidate_count", n, "selection", Marshal.ReadInt32(buf, 12), "page_start", Marshal.ReadInt32(buf, 16), "page_size", Marshal.ReadInt32(buf, 20), "strings", strings,
                    "granularity", "not inferred; correlate with caret, reading and observed IME candidate UI"));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return D("count_return", count, "total_bytes_return", total, "lists", lists);
    }
    private static object ReadManagedUia(uint pid)
    {
        try
        {
            AutomationElement element = AutomationElement.FocusedElement;
            if (element == null) return D("status", "null_element");
            if (element.Current.ProcessId != pid) return D("status", "focused_element_process_mismatch");
            var info = D("status", "approved_element", "process_id", element.Current.ProcessId, "control_type", element.Current.ControlType.ProgrammaticName,
                "framework", element.Current.FrameworkId, "native_hwnd", "0x" + element.Current.NativeWindowHandle.ToString("X"));
            object pattern;
            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) { var value = ((ValuePattern)pattern).Current; info["value"] = D("status", "read", "text", value.Value, "is_read_only", value.IsReadOnly); }
                else info["value"] = D("status", "unsupported");
            }
            catch (Exception ex) { info["value"] = Error(ex); }
            try
            {
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    var text = (TextPattern)pattern; var selections = new List<object>();
                    foreach (var range in text.GetSelection()) selections.Add(D("text", range.GetText(4096), "bounds", range.GetBoundingRectangles()));
                    info["text"] = D("status", "read", "document", text.DocumentRange.GetText(4096), "selections", selections,
                        "note", "TextPattern selection/caret is not the active composition range");
                }
                else info["text"] = D("status", "unsupported");
            }
            catch (Exception ex) { info["text"] = Error(ex); }
            return info;
        }
        catch (Exception ex) { return Error(ex); }
    }
    private static object ReadNativeTextEdit(uint pid)
    {
        if (NativeUia == IntPtr.Zero) return D("status", "uia_init_failed");
        IntPtr element = IntPtr.Zero, pattern = IntPtr.Zero;
        try
        {
            int hr = Slot<GetPointer>(NativeUia, 8)(NativeUia, out element);
            if (hr < 0 || element == IntPtr.Zero) return D("status", "focused_element_failed", "hresult", HR(hr));
            int elementPid; hr = Slot<GetInt>(element, 20)(element, out elementPid);
            if (hr < 0 || elementPid != pid) return D("status", "focused_element_process_mismatch", "hresult", HR(hr));
            Guid iid = new Guid("17E21576-996C-4870-99D9-BFF323380C06");
            hr = Slot<GetPatternAs>(element, 14)(element, 10032, ref iid, out pattern);
            if (hr < 0 || pattern == IntPtr.Zero) return D("status", "pattern_not_available", "pattern_id", 10032, "hresult", HR(hr));
            return D("status", "pattern_obtained", "pattern_id", 10032, "active_composition", ReadTextEditRange(pattern, 9), "conversion_target", ReadTextEditRange(pattern, 10));
        }
        catch (Exception ex) { return Error(ex); }
        finally { if (pattern != IntPtr.Zero) Marshal.Release(pattern); if (element != IntPtr.Zero) Marshal.Release(element); }
    }
    private static object ReadTextEditRange(IntPtr pattern, int rangeSlot)
    {
        IntPtr range = IntPtr.Zero, document = IntPtr.Zero, prefix = IntPtr.Zero;
        try
        {
            int hr = Slot<GetPointer>(pattern, rangeSlot)(pattern, out range);
            if (hr < 0 || range == IntPtr.Zero) return D("status", hr < 0 ? "range_failed" : "null_range", "hresult", HR(hr));
            var result = D("status", "range_obtained", "text", NativeRangeText(range));
            int docHr = Slot<GetPointer>(pattern, 7)(pattern, out document);
            if (docHr >= 0 && document != IntPtr.Zero)
            {
                int cloneHr = Slot<GetPointer>(document, 3)(document, out prefix);
                if (cloneHr >= 0 && prefix != IntPtr.Zero)
                {
                    // Only a cloned range endpoint changes; no UI selection or text is changed.
                    int moveHr = Slot<MoveEndpointByRange>(prefix, 15)(prefix, 1, range, 0);
                    result["prefix_range_hresult"] = HR(moveHr);
                    if (moveHr >= 0)
                    {
                        var read = NativeRangeText(prefix);
                        object value;
                        if (read.TryGetValue("text", out value) && value is string) result["start_utf16_offset"] = ((string)value).Length;
                    }
                }
            }
            return result;
        }
        catch (Exception ex) { return Error(ex); }
        finally { if (prefix != IntPtr.Zero) Marshal.Release(prefix); if (document != IntPtr.Zero) Marshal.Release(document); if (range != IntPtr.Zero) Marshal.Release(range); }
    }
    private static Dictionary<string, object> NativeRangeText(IntPtr range)
    {
        IntPtr text = IntPtr.Zero;
        try
        {
            int hr = Slot<GetText>(range, 12)(range, 4096, out text);
            return D("status", hr < 0 ? "text_failed" : "read", "hresult", HR(hr), "text", hr >= 0 ? text == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(text) : null);
        }
        finally { if (text != IntPtr.Zero) Marshal.FreeBSTR(text); }
    }
}
