using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceKey {
    internal sealed class ShortcutBinding {
        internal readonly int Key;
        internal readonly uint Modifiers;
        internal bool IsOff { get { return Key==0; } }
        internal bool UsesRegisterHotKey {
            get { return Modifiers!=0 || ((Keys)Key>=Keys.F1 && (Keys)Key<=Keys.F24); }
        }
        internal bool IsReservedPasteChord { get { return Modifiers==2 && Key==(int)Keys.V; } }
        internal static ShortcutBinding Default { get { return new ShortcutBinding((int)Keys.OemSemicolon,0); } }
        internal static ShortcutBinding Off { get { return new ShortcutBinding(0,0); } }
        internal ShortcutBinding(int key,uint modifiers) { Key=key; Modifiers=modifiers; }

        internal string Display {
            get {
                if(IsOff) return "Off";
                string prefix="";
                if((Modifiers&2)!=0) prefix+="Ctrl+";
                if((Modifiers&4)!=0) prefix+="Shift+";
                if((Modifiers&1)!=0) prefix+="Alt+";
                return prefix+KeyName((Keys)Key);
            }
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetKeyNameText(int data,System.Text.StringBuilder name,int length);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code,uint type);
        static string KeyName(Keys key) {
            int virtualKey=(int)key;
            if((virtualKey>=0xBA && virtualKey<=0xC0) || (virtualKey>=0xDB && virtualKey<=0xE2)) {
                var name=new System.Text.StringBuilder(64);
                int scan=(int)MapVirtualKey((uint)virtualKey,0);
                if(scan!=0 && GetKeyNameText(scan<<16,name,name.Capacity)>0) return name.ToString();
            }
            if(key>=Keys.D0 && key<=Keys.D9) return ((int)key-(int)Keys.D0).ToString();
            if(key>=Keys.NumPad0 && key<=Keys.NumPad9) return "Numpad "+((int)key-(int)Keys.NumPad0);
            if(key==Keys.Add) return "Numpad +";
            if(key==Keys.Subtract) return "Numpad -";
            if(key==Keys.Multiply) return "Numpad *";
            if(key==Keys.Divide) return "Numpad /";
            return key.ToString();
        }
        internal string Serialize() { return IsOff?"Off":Modifiers.ToString("X")+":"+Key.ToString("X"); }
        internal static ShortcutBinding CaptureKeyData(Keys keyData) {
            Keys key=keyData&Keys.KeyCode;
            if(key==Keys.None || key==Keys.ControlKey || key==Keys.ShiftKey || key==Keys.Menu ||
                key==Keys.LWin || key==Keys.RWin) return null;
            uint mods=0;
            Keys modifiers=keyData&Keys.Modifiers;
            if((modifiers&Keys.Control)!=0) mods|=2;
            if((modifiers&Keys.Shift)!=0) mods|=4;
            if((modifiers&Keys.Alt)!=0) mods|=1;
            return new ShortcutBinding((int)key,mods);
        }
        internal static ShortcutBinding Parse(string value) {
            if(string.IsNullOrEmpty(value)) return Default;
            string v=value.Trim();
            if(v.Equals("Off",StringComparison.OrdinalIgnoreCase) || v=="3") return Off;
            if(v=="0") return new ShortcutBinding((int)Keys.F8,0);
            if(v=="1") return new ShortcutBinding((int)Keys.F9,0);
            if(v=="2") return new ShortcutBinding((int)Keys.F10,0);
            if(v.Equals("F8",StringComparison.OrdinalIgnoreCase)) return new ShortcutBinding((int)Keys.F8,0);
            if(v.Equals("F9",StringComparison.OrdinalIgnoreCase)) return new ShortcutBinding((int)Keys.F9,0);
            if(v.Equals("F10",StringComparison.OrdinalIgnoreCase)) return new ShortcutBinding((int)Keys.F10,0);
            string[] parts=v.Split(':');
            uint mods; int key;
            if(parts.Length!=2 || !uint.TryParse(parts[0],System.Globalization.NumberStyles.HexNumber,null,out mods) ||
                !int.TryParse(parts[1],System.Globalization.NumberStyles.HexNumber,null,out key) ||
                (mods&~7u)!=0 || key<=0 || key>255) return Default;
            return new ShortcutBinding(key,mods);
        }
    }

    internal sealed class ShortcutCaptureDialog : Form {
        readonly TextBox input=new TextBox { ReadOnly=true,TabStop=false,TextAlign=HorizontalAlignment.Center,Left=12,Top=43,Width=286 };
        internal ShortcutBinding Binding { get; private set; }
        internal static ShortcutCaptureDialog Create(IWin32Window owner,ShortcutBinding current) {
            return new ShortcutCaptureDialog(current);
        }
        ShortcutCaptureDialog(ShortcutBinding current) {
            Text="Set dictation shortcut"; FormBorderStyle=FormBorderStyle.FixedDialog; StartPosition=FormStartPosition.CenterParent;
            MinimizeBox=false; MaximizeBox=false; ShowInTaskbar=false; KeyPreview=true; ClientSize=new System.Drawing.Size(310,132);
            Binding=current;
            var label=new Label { Text="Press a key or a Ctrl, Shift or Alt combination.",AutoSize=true,Left=12,Top=12 };
            input.Text=current.Display;
            var clear=new Button { Text="Off",Left=12,Top=84,Width=72 };
            var cancel=new Button { Text="Cancel",Left=146,Top=84,Width=72,DialogResult=DialogResult.Cancel };
            var ok=new Button { Text="Use shortcut",Left=226,Top=84,Width=80,DialogResult=DialogResult.OK };
            Controls.AddRange(new Control[]{label,input,clear,cancel,ok});
            AcceptButton=ok; CancelButton=cancel;
            clear.Click+=delegate { Binding=ShortcutBinding.Off; DialogResult=DialogResult.OK; Close(); };
            KeyDown+=CaptureKey;
        }
        protected override void OnShown(EventArgs e) {
            base.OnShown(e);
            input.Focus();
        }
        void CaptureKey(object sender,KeyEventArgs e) {
            e.SuppressKeyPress=true; e.Handled=true;
            ShortcutBinding captured=ShortcutBinding.CaptureKeyData(e.KeyData);
            if(captured==null) { input.Text="Press a key, with optional Ctrl, Shift or Alt"; return; }
            Binding=captured; input.Text=captured.Display;
        }
    }

    internal sealed class ShortcutTargetEventArgs : EventArgs {
        internal readonly Desktop.Target Target;
        internal ShortcutTargetEventArgs(Desktop.Target target) { Target=target; }
    }

    internal enum BareKeyAction { None, Start, Release, Replay }
    internal static class ShortcutDecisions {
        internal static bool CanReplayTap(Desktop.Target press,Desktop.Target current,bool modifiersDown) {
            return !modifiersDown && press.Matches(current);
        }
    }
    internal sealed class BareKeyPress {
        Desktop.Target target;
        long startedAt;
        bool pending,started;
        internal bool IsArmed { get { return pending; } }
        internal bool IsStarted { get { return started; } }
        internal Desktop.Target Target { get { return target; } }
        internal bool Down(Desktop.Target pressTarget,bool canStart,long now) {
            if(pending) return true;
            if(!canStart || pressTarget.Window==IntPtr.Zero || pressTarget.Focus==IntPtr.Zero) return false;
            target=pressTarget; startedAt=now; pending=true; started=false; return true;
        }
        internal BareKeyAction Tick(long now,bool canStart) {
            if(!pending || started || !canStart || unchecked((uint)(now-startedAt))<250) return BareKeyAction.None;
            started=true; return BareKeyAction.Start;
        }
        internal BareKeyAction Up() {
            if(!pending) return BareKeyAction.None;
            BareKeyAction action=started?BareKeyAction.Release:BareKeyAction.Replay;
            pending=started=false; startedAt=0;
            return action;
        }
    }

    internal sealed class ShortcutController : IDisposable {
        const int HookType=13,KeyDown=0x100,KeyUp=0x101,SysKeyDown=0x104,SysKeyUp=0x105;
        const uint NoRepeat=0x4000,PrivateMessage=0x8000+0x56,KeyUpFlag=2;
        internal const ulong InjectionMarker=0x564F4943454B4559UL;
        const int HotkeyId=0x564B;
        readonly IntPtr window;
        readonly ShortcutBinding binding;
        readonly BareKeyPress bare=new BareKeyPress();
        readonly HookProc hookProc;
        readonly List<PostedAction> actions=new List<PostedAction>();
        IntPtr hook;
        bool registered,disposed,blockedUntilRelease,modifiedStarted;
        Desktop.Target modifiedTarget;

        internal bool CanStart;
        internal event EventHandler<ShortcutTargetEventArgs> Pressed;
        internal event EventHandler<ShortcutTargetEventArgs> Released;
        internal static int PostedMessage { get { return (int)PrivateMessage; } }
        sealed class PostedAction {
            internal readonly BareKeyAction Action;
            internal readonly Desktop.Target Target;
            internal PostedAction(BareKeyAction action,Desktop.Target target) { Action=action; Target=target; }
        }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
        [StructLayout(LayoutKind.Sequential)] struct LowKey { public uint vk,scan,flags,time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Explicit,Size=40)] struct Input { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KeyInput key; }
        [StructLayout(LayoutKind.Sequential)] struct KeyInput { public ushort vk,scan; public uint flags,time; public UIntPtr extra; }
        [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id,HookProc proc,IntPtr module,uint thread);
        [DllImport("user32.dll",SetLastError=true)] static extern bool UnhookWindowsHookEx(IntPtr handle);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr handle,int code,IntPtr message,IntPtr data);
        [DllImport("kernel32.dll",CharSet=CharSet.Auto)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll",SetLastError=true)] static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd,int id);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern uint SendInput(uint count,Input[] inputs,int size);

        internal ShortcutController(IntPtr hwnd,ShortcutBinding value) {
            window=hwnd; binding=value??ShortcutBinding.Off; hookProc=Hook;
        }
        internal bool TryRegister(out string error) {
            error=null;
            if(disposed) { error="The shortcut controller is closed."; return false; }
            if(binding.IsReservedPasteChord) {
                error="VoiceKey uses Ctrl+V to paste. Choose a different shortcut."; return false;
            }
            if(binding.IsOff) return true;
            if(binding.UsesRegisterHotKey) {
                registered=RegisterHotKey(window,HotkeyId,binding.Modifiers|NoRepeat,(uint)binding.Key);
                if(!registered) { error="That shortcut is already in use by Windows or another app. Choose a different shortcut."; return false; }
                blockedUntilRelease=AnyBindingKeyDown();
                return true;
            }
            hook=SetWindowsHookEx(HookType,hookProc,GetModuleHandle(null),0);
            if(hook==IntPtr.Zero) { error="Windows could not enable the global keyboard shortcut."; return false; }
            blockedUntilRelease=Down(binding.Key)||AnyModifierDown();
            return true;
        }
        internal void Tick() {

            if(blockedUntilRelease) { if(!AnyBindingKeyDown()) blockedUntilRelease=false; return; }
            if(registered && modifiedStarted && !Down(binding.Key)) {
                modifiedStarted=false;
                Raise(Released,modifiedTarget);
                return;
            }
            if(!registered && bare.IsArmed) {
                BareKeyAction action=bare.Tick(Environment.TickCount,CanStart);
                if(action==BareKeyAction.Start) Post(action,bare.Target);
            }
        }
        internal bool Dispatch(ref Message message) {
            if(message.Msg==0x312 && message.WParam.ToInt32()==HotkeyId) {
                if(modifiedStarted && !Down(binding.Key)) {
                    modifiedStarted=false; Raise(Released,modifiedTarget); return true;
                }
                if(!blockedUntilRelease && registered && !modifiedStarted && CanStart) {
                    modifiedTarget=Desktop.CurrentTarget();
                    if(modifiedTarget.Window!=IntPtr.Zero && modifiedTarget.Focus!=IntPtr.Zero) {
                        modifiedStarted=true; Raise(Pressed,modifiedTarget);
                    }
                }
                return true;
            }
            if(message.Msg!=(int)PrivateMessage) return false;
            PostedAction action=null;
            lock(actions) { if(actions.Count>0) { action=actions[0]; actions.RemoveAt(0); } }
            if(action!=null) {
                if(action.Action==BareKeyAction.Start) Raise(Pressed,action.Target);
                else if(action.Action==BareKeyAction.Release) Raise(Released,action.Target);
            }
            return true;
        }
        IntPtr Hook(int code,IntPtr message,IntPtr data) {
            if(code<0 || disposed) return CallNextHookEx(hook,code,message,data);
            try {
                LowKey k=(LowKey)Marshal.PtrToStructure(data,typeof(LowKey));
                if(k.extra.ToUInt64()==InjectionMarker) return CallNextHookEx(hook,code,message,data);
                if(blockedUntilRelease) {
                    if(!AnyBindingKeyDown()) blockedUntilRelease=false;
                    return CallNextHookEx(hook,code,message,data);
                }
                int msg=message.ToInt32();
                if(k.vk!=binding.Key) return CallNextHookEx(hook,code,message,data);
                bool down=msg==KeyDown||msg==SysKeyDown,up=msg==KeyUp||msg==SysKeyUp;
                if(down) {
                    if(bare.IsArmed) return new IntPtr(1);
                    if(!CanStart || AnyModifierDown()) return CallNextHookEx(hook,code,message,data);
                    Desktop.Target t=Desktop.CurrentTarget();
                    if(t.Window==window || !bare.Down(t,true,Environment.TickCount)) return CallNextHookEx(hook,code,message,data);
                    return new IntPtr(1);
                }
                if(up && bare.IsArmed) {
                    BareKeyAction action=bare.Up();
                    if(action==BareKeyAction.Replay) ReplayTap(bare.Target);
                    else if(action==BareKeyAction.Release) Post(action,bare.Target);
                    return new IntPtr(1);
                }
            } catch {}
            return CallNextHookEx(hook,code,message,data);
        }
        void Post(BareKeyAction action,Desktop.Target target) {
            lock(actions) {
                actions.Add(new PostedAction(action,target));
                if(!PostMessage(window,PrivateMessage,IntPtr.Zero,IntPtr.Zero))
                    actions.RemoveAt(actions.Count-1);
            }
        }
        void ReplayTap(Desktop.Target target) {
            if(!ShortcutDecisions.CanReplayTap(target,Desktop.CurrentTarget(),AnyModifierDown())) return;
            var key=new KeyInput {vk=(ushort)binding.Key,extra=new UIntPtr(InjectionMarker)};
            var inputs=new[]{new Input {type=1,key=key},new Input {type=1,key=new KeyInput {vk=(ushort)binding.Key,flags=KeyUpFlag,extra=new UIntPtr(InjectionMarker)}}};
            SendInput(2,inputs,Marshal.SizeOf(typeof(Input)));
        }
        void Raise(EventHandler<ShortcutTargetEventArgs> handler,Desktop.Target target) {
            if(handler!=null) handler(this,new ShortcutTargetEventArgs(target));
        }
        static bool AnyModifierDown() {
            return Down(0x10)||Down(0xA0)||Down(0xA1)||Down(0x11)||Down(0xA2)||Down(0xA3)||
                Down(0x12)||Down(0xA4)||Down(0xA5)||Down(0x5B)||Down(0x5C);
        }
        bool AnyBindingKeyDown() {
            return Down(binding.Key)||
                ((binding.Modifiers&1)!=0&&(Down(0x12)||Down(0xA4)||Down(0xA5)))||
                ((binding.Modifiers&2)!=0&&(Down(0x11)||Down(0xA2)||Down(0xA3)))||
                ((binding.Modifiers&4)!=0&&(Down(0x10)||Down(0xA0)||Down(0xA1)));
        }
        static bool Down(int key) { return (GetAsyncKeyState(key)&0x8000)!=0; }
        public void Dispose() {
            if(disposed) return; disposed=true;
            if(hook!=IntPtr.Zero) UnhookWindowsHookEx(hook);
            if(registered) UnregisterHotKey(window,HotkeyId);
            hook=IntPtr.Zero; registered=false;
        }
    }
}
