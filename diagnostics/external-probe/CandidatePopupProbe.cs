// Read-only external UIA probe. Scope: one observed candidate popup rectangle,
// only while the explicitly approved synthetic test host is foreground.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;

static class CandidatePopupProbe
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x,y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left,top,right,bottom; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h,uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h,StringBuilder text,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h,StringBuilder text,int size);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT rect);
    static JavaScriptSerializer json=new JavaScriptSerializer();
    static StreamWriter output;
    static int hostPid;
    static Rect scope;
    static Point point;
    static object D(params object[] args) { var result=new Dictionary<string,object>(); for(int i=0;i<args.Length;i+=2)result[(string)args[i]]=args[i+1];return result; }
    static string H(IntPtr h) { return "0x"+h.ToInt64().ToString("X"); }
    static string Class(IntPtr h) { var s=new StringBuilder(128);GetClassNameW(h,s,s.Capacity);return s.ToString(); }
    static bool Bounded(Rect r) { return !r.IsEmpty && r.Width>0 && r.Height>0 && r.Left>=scope.Left-5 && r.Top>=scope.Top-5 && r.Right<=scope.Right+5 && r.Bottom<=scope.Bottom+5; }
    static object Bounds(Rect r) { return D("x",r.X,"y",r.Y,"width",r.Width,"height",r.Height); }
    static void Emit(string kind,object data) { output.WriteLine(json.Serialize(D("utc",DateTime.UtcNow.ToString("o"),"probe_pid",Process.GetCurrentProcess().Id,"host_pid",hostPid,"kind",kind,"data",data)));output.Flush(); }
    [STAThread] static void Main()
    {
        try
        {
            var config=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"candidate-config.json"),Encoding.UTF8));
            hostPid=Convert.ToInt32(config["host_pid"]);
            scope=new Rect(Convert.ToDouble(config["left"]),Convert.ToDouble(config["top"]),Convert.ToDouble(config["width"]),Convert.ToDouble(config["height"]));
            point=new Point(Convert.ToDouble(config["point_x"]),Convert.ToDouble(config["point_y"]));
            if(hostPid<=0 || !scope.Contains(point) || scope.Width>500 || scope.Height>800)throw new ArgumentException("Invalid narrow popup scope");
            output=new StreamWriter(Convert.ToString(config["output"]),false,new UTF8Encoding(false));
            Emit("probe_start",D("route","external UI Automation FromPoint + bounded RawView subtree","observed_popup_scope",Bounds(scope),"seed_x",point.X,"seed_y",point.Y,"no_input_injection",true));
            var timer=Stopwatch.StartNew();
            do { try { Sample(); } catch(Exception ex) {Emit("sample_error",D("type",ex.GetType().Name,"hresult",ex.HResult,"message",ex.Message));} Thread.Sleep(500); } while(timer.Elapsed.TotalSeconds<180);
            Emit("probe_complete",D("seconds",timer.Elapsed.TotalSeconds));
        }
        catch(Exception ex) { if(output!=null)Emit("fatal_error",D("type",ex.GetType().Name,"message",ex.Message)); }
        finally {if(output!=null)output.Dispose();}
    }
    static void Sample()
    {
        uint fgPid;IntPtr foreground=GetForegroundWindow();GetWindowThreadProcessId(foreground,out fgPid);
        if(fgPid!=hostPid){Emit("sample",D("status","foreground_outside_approved_host"));return;}
        var title=new StringBuilder(128);GetWindowTextW(foreground,title,title.Capacity);
        if(!title.ToString().StartsWith("Jevboard IME Probe -",StringComparison.Ordinal)){Emit("sample",D("status","host_title_mismatch"));return;}
        IntPtr hit=WindowFromPoint(new POINT{x=(int)point.X,y=(int)point.Y});uint hitPid;GetWindowThreadProcessId(hit,out hitPid);
        string imagePath;try{imagePath=Process.GetProcessById((int)hitPid).MainModule.FileName;}catch(Exception ex){Emit("sample",D("status","point_process_identity_unavailable","hresult",ex.HResult));return;}
        string exe=Path.GetFileName(imagePath),windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        bool approved=hitPid==hostPid || (imagePath.StartsWith(windows+"\\",StringComparison.OrdinalIgnoreCase) && (exe.Equals("TextInputHost.exe",StringComparison.OrdinalIgnoreCase)||exe.Equals("ChtIME.exe",StringComparison.OrdinalIgnoreCase)));
        if(!approved){Emit("sample",D("status","point_process_not_approved"));return;}
        IntPtr rootHandle=GetAncestor(hit,2);RECT native;GetWindowRect(rootHandle,out native);
        var seed=AutomationElement.FromPoint(point);
        string seedRoute="FromPoint";
        if(seed.Current.ProcessId!=(int)hitPid || !Bounded(seed.Current.BoundingRectangle)) { seed=AutomationElement.FromHandle(hit); seedRoute="FromHandle(observed popup HWND)"; }
        if(seed.Current.ProcessId!=(int)hitPid || !Bounded(seed.Current.BoundingRectangle)){Emit("popup_observation",D("status","uia_seed_not_bounded_popup","hit_pid",hitPid,"hit_hwnd",H(hit),"hit_class",Class(hit),"root_hwnd",H(rootHandle),"root_class",Class(rootHandle),"image",imagePath,"native_bounds",D("left",native.left,"top",native.top,"right",native.right,"bottom",native.bottom),"uia_seed_bounds",Bounds(seed.Current.BoundingRectangle),"no_names_read",true));return;}
        var popup=seed;
        for(int i=0;i<12;i++){var parent=TreeWalker.RawViewWalker.GetParent(popup);if(parent==null || parent.Current.ProcessId!=(int)hitPid || !Bounded(parent.Current.BoundingRectangle))break;popup=parent;}
        var elements=popup.FindAll(TreeScope.Subtree,Condition.TrueCondition);
        var nodes=new List<object>();
        for(int i=0;i<elements.Count && i<256;i++)
        {
            var element=elements[i];var current=element.Current;
            if(current.ProcessId!=(int)hitPid || current.IsOffscreen || !Bounded(current.BoundingRectangle))continue;
            string text=null;object pattern;
            if(element.TryGetCurrentPattern(TextPattern.Pattern,out pattern))text=((TextPattern)pattern).DocumentRange.GetText(1024);
            nodes.Add(D("tree_index",i,"control_type",current.ControlType.ProgrammaticName,"name",current.Name,"automation_id",current.AutomationId,"class",current.ClassName,"bounds",Bounds(current.BoundingRectangle),"text_pattern",text));
        }
        Emit("popup_observation",D("status","bounded_uia_read","seed_route",seedRoute,"hit_pid",hitPid,"hit_hwnd",H(hit),"hit_class",Class(hit),"image",imagePath,"node_count",nodes.Count,"nodes",nodes));
    }
}
