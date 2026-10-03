// Optional read-only TSF instrumentation for the synthetic test host only.
// Call Init/Snapshot/Dispose on the text control's owning GUI thread.
// Uses the existing thread manager; never creates/activates a TIP or hides UI.
// Native slots checked against Microsoft's win32metadata msctf.h.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace JevboardImeProbe
{
    [ComVisible(true), Guid("ea1ea136-19df-11d7-a6d2-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITsfProbeUiElementSink
    {
        [PreserveSig] int BeginUIElement(uint id, ref int show);
        [PreserveSig] int UpdateUIElement(uint id);
        [PreserveSig] int EndUIElement(uint id);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class TsfProbe : ITsfProbeUiElementSink, IDisposable
    {
        [DllImport("msctf.dll")] static extern int TF_GetThreadMgr(out IntPtr manager);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Advise(IntPtr self, ref Guid iid, IntPtr sink, out uint cookie);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Unadvise(IntPtr self, uint cookie);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPtr(IntPtr self, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetElement(IntPtr self, uint id, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetUInt(IntPtr self, out uint result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetString(IntPtr self, uint index, out IntPtr bstr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NextElement(IntPtr self, uint count, IntPtr array, out uint fetched);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetActiveProfile(IntPtr self, ref Guid category, out Profile profile);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDescription(IntPtr self, ref Guid clsid, ushort langid, ref Guid profile, out IntPtr bstr);
        [StructLayout(LayoutKind.Sequential)] struct Profile
        {
            public uint type;
            public ushort langid;
            public Guid clsid, guidProfile, category;
            public IntPtr hklSubstitute;
            public uint caps;
            public IntPtr hkl;
            public uint flags;
        }

        IntPtr manager, elements, source;
        uint cookie = uint.MaxValue;
        readonly uint owner;
        readonly string label;
        bool disposed;
        TsfProbe(string label) { this.label = label; owner = GetCurrentThreadId(); }
        static string HR(int hr) { return "0x" + unchecked((uint)hr).ToString("X8"); }
        static string H(IntPtr value) { return "0x" + value.ToInt64().ToString("X"); }
        static T Slot<T>(IntPtr obj, int slot) where T : class
        {
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T)) as T;
        }
        static Dictionary<string, object> D(params object[] values)
        {
            var data = new Dictionary<string, object>();
            for (int i = 0; i < values.Length; i += 2) data[(string)values[i]] = values[i + 1];
            return data;
        }
        void Write(string name, object data) { Log.Write(name, new { host = label, nativeThread = owner, data = data }); }
        static int Query(IntPtr value, string iid, out IntPtr result)
        {
            Guid guid = new Guid(iid); return Marshal.QueryInterface(value, ref guid, out result);
        }
        static void Release(ref IntPtr value) { if (value != IntPtr.Zero) { Marshal.Release(value); value = IntPtr.Zero; } }
        public static TsfProbe Init(string label)
        {
            var probe = new TsfProbe(label); probe.Snapshot("init"); return probe;
        }
        void Attach()
        {
            if (manager != IntPtr.Zero) return;
            int hr = TF_GetThreadMgr(out manager);
            Write("tsf_existing_thread_manager", D("hresult", HR(hr), "available", manager != IntPtr.Zero,
                "note", "No manager is created or activated by this probe; retry on Snapshot after the control receives focus."));
            if (hr < 0 || manager == IntPtr.Zero) return;
            hr = Query(manager, "ea1ea135-19df-11d7-a6d2-00065b84435c", out elements);
            Write("tsf_ui_element_manager", D("hresult", HR(hr), "available", elements != IntPtr.Zero));
            if (hr < 0 || elements == IntPtr.Zero) return;
            hr = Query(elements, "4ea48a35-60ae-446f-8fd6-e6a8d82459f7", out source);
            if (hr < 0 || source == IntPtr.Zero) { Write("tsf_advise", D("status", "source_unavailable", "hresult", HR(hr))); return; }
            IntPtr sink = Marshal.GetIUnknownForObject(this);
            try
            {
                Guid iid = new Guid("ea1ea136-19df-11d7-a6d2-00065b84435c");
                hr = Slot<Advise>(source, 3)(source, ref iid, sink, out cookie);
                if (hr < 0) cookie = uint.MaxValue;
                Write("tsf_advise", D("hresult", HR(hr), "cookie", cookie, "ui_visibility", "original TIP UI allowed"));
            }
            finally { Marshal.Release(sink); }
        }
        public void Snapshot(string reason)
        {
            if (disposed || GetCurrentThreadId() != owner) return;
            try
            {
                Attach(); SnapshotProfile(reason);
                if (elements == IntPtr.Zero) return;
                IntPtr enumeration = IntPtr.Zero, array = IntPtr.Zero;
                try
                {
                    int hr = Slot<GetPtr>(elements, 7)(elements, out enumeration);
                    if (hr < 0 || enumeration == IntPtr.Zero) { Write("tsf_enum", D("reason", reason, "hresult", HR(hr))); return; }
                    array = Marshal.AllocHGlobal(IntPtr.Size);
                    int total = 0;
                    for (; total < 64; total++)
                    {
                        Marshal.WriteIntPtr(array, IntPtr.Zero); uint fetched;
                        hr = Slot<NextElement>(enumeration, 4)(enumeration, 1, array, out fetched);
                        IntPtr element = Marshal.ReadIntPtr(array);
                        if (fetched == 0 || element == IntPtr.Zero) break;
                        try { CaptureElement(element, reason, null); }
                        finally { Marshal.Release(element); }
                        if (hr != 0) break;
                    }
                    Write("tsf_enum", D("reason", reason, "element_count", total, "note", "This is the owning thread's UIElement collection, not a global IME candidate enumeration."));
                }
                finally { if (array != IntPtr.Zero) Marshal.FreeHGlobal(array); Release(ref enumeration); }
            }
            catch (Exception ex) { Write("tsf_snapshot_error", D("reason", reason, "type", ex.GetType().Name, "hresult", HR(ex.HResult), "message", ex.Message)); }
        }
        void Capture(uint id, string reason)
        {
            if (disposed || elements == IntPtr.Zero || GetCurrentThreadId() != owner) return;
            IntPtr element = IntPtr.Zero;
            try
            {
                int hr = Slot<GetElement>(elements, 6)(elements, id, out element);
                if (hr < 0 || element == IntPtr.Zero) { Write("tsf_element", D("id", id, "reason", reason, "hresult", HR(hr))); return; }
                CaptureElement(element, reason, id);
            }
            catch (Exception ex) { Write("tsf_capture_error", D("id", id, "reason", reason, "message", ex.Message, "hresult", HR(ex.HResult))); }
            finally { Release(ref element); }
        }
        void CaptureElement(IntPtr element, string reason, object id)
        {
            IntPtr candidates = IntPtr.Zero, behavior = IntPtr.Zero;
            try
            {
                int hr = Query(element, "ea1ea138-19df-11d7-a6d2-00065b84435c", out candidates);
                if (hr < 0 || candidates == IntPtr.Zero) { Write("tsf_element", D("id", id, "reason", reason, "candidate_interface_hresult", HR(hr))); return; }
                uint count, selection, page, flags;
                int countHr = Slot<GetUInt>(candidates, 9)(candidates, out count);
                int selectionHr = Slot<GetUInt>(candidates, 10)(candidates, out selection);
                int pageHr = Slot<GetUInt>(candidates, 14)(candidates, out page);
                int flagsHr = Slot<GetUInt>(candidates, 7)(candidates, out flags);
                var strings = new List<object>();
                if (countHr >= 0 && count <= 512)
                    for (uint i = 0; i < count; i++)
                    {
                        IntPtr text = IntPtr.Zero;
                        try { int strHr = Slot<GetString>(candidates, 11)(candidates, i, out text); strings.Add(D("index", i, "hresult", HR(strHr), "text", strHr >= 0 && text != IntPtr.Zero ? Marshal.PtrToStringBSTR(text) : null)); }
                        finally { if (text != IntPtr.Zero) Marshal.FreeBSTR(text); }
                    }
                int behaviorHr = Query(element, "85fad185-58ce-497a-9460-355366b64b9a", out behavior);
                Write("tsf_candidates", D("id", id, "reason", reason, "count_hresult", HR(countHr), "count", count,
                    "selection_hresult", HR(selectionHr), "selection", selectionHr == 0 ? (object)selection : null,
                    "page_hresult", HR(pageHr), "page", page, "flags_hresult", HR(flagsHr), "flags", flags,
                    "behavior_interface_hresult", HR(behaviorHr), "behavior_available", behavior != IntPtr.Zero, "strings", strings,
                    "note", "Read-only owning-thread probe; no candidate selection/finalization. Begin may occur before strings are ready. Candidate granularity is not inferred."));
            }
            finally { Release(ref behavior); Release(ref candidates); }
        }
        void SnapshotProfile(string reason)
        {
            IntPtr profileManager = IntPtr.Zero, profiles = IntPtr.Zero, text = IntPtr.Zero;
            try
            {
                Guid clsid = new Guid("33c53a50-f456-4884-b049-85fd643ecfed"), iid = new Guid("71c6e74c-0f28-11d8-a82a-00065b84435c");
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out profileManager);
                if (hr < 0 || profileManager == IntPtr.Zero) { Write("tsf_active_profile", D("reason", reason, "status", "profile_manager_unavailable", "hresult", HR(hr))); return; }
                Guid category = new Guid("34745c63-b2f0-4784-8b67-5e12c8701a31"); Profile profile;
                hr = Slot<GetActiveProfile>(profileManager, 10)(profileManager, ref category, out profile);
                if (hr < 0) { Write("tsf_active_profile", D("reason", reason, "status", "get_active_failed", "hresult", HR(hr), "thread_hkl", H(GetKeyboardLayout(owner)))); return; }
                int descHr = Query(profileManager, "1F02B6C5-7842-4EE6-8A0B-9A24183A95CA", out profiles);
                if (descHr >= 0 && profiles != IntPtr.Zero && profile.type == 1)
                    descHr = Slot<GetDescription>(profiles, 12)(profiles, ref profile.clsid, profile.langid, ref profile.guidProfile, out text);
                Write("tsf_active_profile", D("reason", reason, "hresult", HR(hr), "profile_type", profile.type, "language_id", "0x" + profile.langid.ToString("X4"),
                    "clsid", profile.clsid.ToString(), "profile_guid", profile.guidProfile.ToString(), "category_guid", profile.category.ToString(),
                    "profile_hkl", H(profile.hkl), "substitute_hkl", H(profile.hklSubstitute), "thread_hkl", H(GetKeyboardLayout(owner)), "caps", profile.caps, "flags", profile.flags,
                    "description_hresult", HR(descHr), "description", text != IntPtr.Zero ? Marshal.PtrToStringBSTR(text) : null));
            }
            finally { if (text != IntPtr.Zero) Marshal.FreeBSTR(text); Release(ref profiles); Release(ref profileManager); }
        }
        public int BeginUIElement(uint id, ref int show)
        {
            show = 1;
            try { Write("tsf_ui_begin", D("id", id, "show", true)); Capture(id, "begin"); } catch { }
            return 0;
        }
        public int UpdateUIElement(uint id) { try { Capture(id, "update"); } catch { } return 0; }
        public int EndUIElement(uint id) { try { Write("tsf_ui_end", D("id", id)); } catch { } return 0; }
        public void Dispose()
        {
            if (disposed || GetCurrentThreadId() != owner) return;
            disposed = true;
            try { if (source != IntPtr.Zero && cookie != uint.MaxValue) Write("tsf_unadvise", D("hresult", HR(Slot<Unadvise>(source, 4)(source, cookie)))); }
            finally { cookie = uint.MaxValue; Release(ref source); Release(ref elements); Release(ref manager); }
        }
    }
}
