using System;
using System.Runtime.InteropServices;
namespace VoiceKey {
    internal static class Desktop {
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window,int id,uint mods,uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window,int id);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint process);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread,ref Gui info);
        [StructLayout(LayoutKind.Sequential)]
        struct Gui {
            public uint size, flags;
            public IntPtr active, focus, capture, menu, move, caret;
            public int l,t,r,b;
        }
        internal struct Target {
            internal IntPtr Window, Focus;
            internal uint Thread;
            internal bool Matches(Target other) {
                return Window!=IntPtr.Zero && Focus!=IntPtr.Zero && Window==other.Window && Focus==other.Focus && Thread==other.Thread;
            }
        }
        internal static Target CurrentTarget() {
            IntPtr w=GetForegroundWindow(); if(w==IntPtr.Zero) return new Target();
            uint pid, tid=GetWindowThreadProcessId(w,out pid);
            var gui=new Gui { size=(uint)Marshal.SizeOf(typeof(Gui)) };
            if(tid==0 || !GetGUIThreadInfo(tid,ref gui)) return new Target();
            return new Target { Window=w, Focus=gui.focus, Thread=tid };
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Keyboard { public ushort key,scan; public uint flags,time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Explicit,Size=40)]
        struct Input {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public Keyboard keyboard; // Win64 INPUT union starts at byte 8.
        }
        [DllImport("user32.dll")] static extern uint SendInput(uint count,Input[] input,int size);
        internal static bool Down(int key) { return (GetAsyncKeyState(key)&0x8000)!=0; }
        static Input Key(ushort key,bool up) { return new Input { type=1,keyboard=new Keyboard { key=key,flags=up?2u:0u } }; }
        internal static bool Paste(Target target) {
            if(Down(0x10)||Down(0x11)||Down(0x12)||Down(0x5b)||Down(0x5c)||!target.Matches(CurrentTarget())) return false;
            var keys=new[]{Key(0x11,false),Key(0x56,false),Key(0x56,true),Key(0x11,true)};
            return SendInput(4,keys,Marshal.SizeOf(typeof(Input)))==4;
        }
    }
}
