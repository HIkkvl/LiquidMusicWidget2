using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace LiquidMusic;

internal static class Native
{
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window,out uint processId);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] internal static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] internal static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint handle);
    [DllImport("winmm.dll")] internal static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] internal static extern uint timeEndPeriod(uint period);
    [DllImport("user32.dll")] internal static extern nint SendMessage(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] internal static extern nint SetWindowsHookEx(int id, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint w, nint l);
    internal delegate nint Hook(int code, nint w, nint l);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Mouse { public Point Point; public uint Data,Flags,Time; public nuint Extra; }
}

internal sealed class GlobalHotkeys(nint window) : IDisposable
{
    public static readonly Dictionary<string,uint> Keys = MakeKeys();
    private readonly Dictionary<(uint,uint),(int,string)> active = new();
    private int next = 0x5000;
    private static Dictionary<string,uint> MakeKeys()
    {
        var result = new Dictionary<string,uint>();
        for(uint i=0;i<10;i++) result[$"Num{i}"]=0x60+i;
        for(uint i=1;i<=12;i++) result[$"F{i}"]=0x6F+i;
        for(uint i=65;i<=90;i++) result[((char)i).ToString()]=i;
        foreach(var (name,key) in new[] {("Space",0x20u),("Home",0x24u),("End",0x23u),("PageUp",0x21u),("PageDown",0x22u),("Insert",0x2Du)}) result[name]=key;
        return result;
    }
    internal static Dictionary<(uint,uint),string> Validate(JsonObject bindings)
    {
        var wanted = new Dictionary<(uint,uint),string>();
        foreach(var action in Settings.Actions.Keys)
        {
            var binding = bindings[action] ?? throw new ArgumentException("Не задана клавиша: " + Settings.Actions[action]);
            var key = binding["key"]?.GetValue<string>() ?? "";
            if(key.Length==0) { if(action=="toggle") throw new ArgumentException("Выберите клавишу открытия виджета"); continue; }
            if(!Keys.TryGetValue(key,out var vk)) throw new ArgumentException("Неизвестная клавиша: " + key);
            uint mods = (binding["ctrl"]?.GetValue<bool>()==true?2u:0) | (binding["alt"]?.GetValue<bool>()==true?1u:0) | (binding["shift"]?.GetValue<bool>()==true?4u:0);
            if(!wanted.TryAdd((mods,vk),action)) throw new ArgumentException("Одна комбинация назначена нескольким действиям");
        }
        return wanted;
    }
    public void Apply(JsonObject bindings)
    {
        var wanted = Validate(bindings);
        var added = new Dictionary<(uint,uint),(int,string)>();
        try
        {
            foreach(var (combo,action) in wanted)
            {
                if(active.ContainsKey(combo)) continue;
                var id=++next;
                if(!Native.RegisterHotKey(window,id,combo.Item1|0x4000,combo.Item2)) throw new InvalidOperationException("Клавиша для «"+Settings.Actions[action]+"» занята другим приложением. Закройте другую копию виджета или выберите другую клавишу.");
                added[combo]=(id,action);
            }
        }
        catch { foreach(var (id,_) in added.Values) Native.UnregisterHotKey(window,id); throw; }
        foreach(var (combo,(id,_)) in active) if(!wanted.ContainsKey(combo)) Native.UnregisterHotKey(window,id);
        var updated = wanted.ToDictionary(pair=>pair.Key,pair=>(active.TryGetValue(pair.Key,out var old)?old.Item1:added[pair.Key].Item1,pair.Value));
        active.Clear(); foreach(var item in updated) active.Add(item.Key,item.Value);
    }
    public string? Action(int id) => active.Values.FirstOrDefault(pair=>pair.Item1==id).Item2;
    public void Dispose() { foreach(var (id,_) in active.Values) Native.UnregisterHotKey(window,id);active.Clear(); }
}

internal sealed class WheelControl : IDisposable
{
    private readonly Native.Hook callback;
    private sealed record WheelOptions(bool Enabled,int Modifier,double Step);
    private WheelOptions options=new(false,0x11,.05);
    private readonly SynchronizationContext dispatcher;
    private readonly object pendingLock=new();
    private double pendingDelta;
    private bool posted;
    private volatile bool disposed;
    private Control? pump;
    private nint handle;
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Ready=>ready.Task;
    public JsonNode? Options
    {
        set=>Volatile.Write(ref options,new(value?["enabled"]?.GetValue<bool>()==true,
            value?["modifier"]?.GetValue<string>() switch {"alt"=>0x12,"shift"=>0x10,"none"=>0,_=>0x11},
            (value?["step"]?.GetValue<double>()??5)/100));
    }
    public event Action<double>? Changed;
    public WheelControl()
    {
        dispatcher=SynchronizationContext.Current??throw new InvalidOperationException("Wheel control requires a UI dispatcher.");
        callback=OnMouse;
        // Windows waits for this callback on EVERY mouse move. Never install
        // the low-level hook on the WebView/UI thread: a slow render or service
        // startup would otherwise delay input across the whole desktop.
        var thread=new Thread(()=>
        {
            using var control=new Control();
            _=control.Handle;Volatile.Write(ref pump,control);
            try
            {
                if(disposed)return;
                handle=Native.SetWindowsHookEx(14,callback,0,0);
                if(handle==0)ready.TrySetException(new InvalidOperationException("Не удалось подключить управление колёсиком."));
                else ready.TrySetResult();
                if(!disposed)Application.Run();
            }
            finally
            {
                if(handle!=0)Native.UnhookWindowsHookEx(handle);
                handle=0;Volatile.Write(ref pump,null);
            }
        }){IsBackground=true,Name="Liquid Music mouse hook"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
    }
    private nint OnMouse(int code,nint w,nint l)
    {
        var current=Volatile.Read(ref options);
        if(!disposed&&code==0 && w==0x020A && current.Enabled)
        {
            var vk=current.Modifier;
            if(vk==0 || (Native.GetAsyncKeyState(vk)&0x8000)!=0)
            {
                var mouse=Marshal.PtrToStructure<Native.Mouse>(l);
                var delta=(short)(mouse.Data>>16)/120.0 * current.Step;
                lock(pendingLock)
                {
                    pendingDelta+=delta;
                    if(!posted){posted=true;dispatcher.Post(_=>Deliver(),null);}
                }
                return 1;
            }
        }
        return Native.CallNextHookEx(handle,code,w,l);
    }
    private void Deliver()
    {
        double delta;lock(pendingLock){delta=pendingDelta;pendingDelta=0;posted=false;}
        if(!disposed)Changed?.Invoke(delta);
    }
    internal Task ProbeAsync()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control=Volatile.Read(ref pump);
        if(control==null)done.SetException(new InvalidOperationException("Mouse hook is not ready."));
        else control.BeginInvoke(()=>done.TrySetResult());
        return done.Task;
    }
    public void Dispose()
    {
        disposed=true;
        try{Volatile.Read(ref pump)?.BeginInvoke(Application.ExitThread);}catch(InvalidOperationException){}
    }
}
