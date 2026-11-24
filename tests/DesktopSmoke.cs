using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace VoiceKey {
    internal static class DesktopSmoke {
        const uint DesktopReadObjects=0x0001;
        const uint MouseLeftDown=0x0002, MouseLeftUp=0x0004, KeyUpFlag=0x0002;
        const uint KeyboardActivate=0x0001, KeyboardSetForProcess=0x0100;
        const int UserObjectName=2;
        const int Unavailable=77;

        [DllImport("user32.dll",SetLastError=true)]
        static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="GetUserObjectInformationW")]
        static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder value,int length,out int needed);
        [DllImport("user32.dll",SetLastError=true)]
        static extern bool CloseDesktop(IntPtr handle);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll",SetLastError=true)]
        static extern bool SetCursorPos(int x,int y);
        [DllImport("user32.dll")]
        static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
        [DllImport("user32.dll",SetLastError=true)]
        static extern bool GetWindowRect(IntPtr handle,out Rect rectangle);
        [DllImport("user32.dll",SetLastError=true)]
        static extern uint SendInput(uint count,Input[] inputs,int size);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="LoadKeyboardLayoutW")]
        static extern IntPtr LoadKeyboardLayout(string name,uint flags);
        [DllImport("user32.dll",SetLastError=true)]
        static extern IntPtr ActivateKeyboardLayout(IntPtr layout,uint flags);
        [StructLayout(LayoutKind.Explicit,Size=40)]
        struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyboardInput Keyboard; }
        [StructLayout(LayoutKind.Sequential)]
        struct KeyboardInput { public ushort VirtualKey,Scan; public uint Flags,Time; public UIntPtr Extra; }
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr handle,int index);
        [StructLayout(LayoutKind.Sequential)]
        struct Rect { public int Left,Top,Right,Bottom; }

        sealed class DictationWindow : Form {
            internal readonly DictationButton HoldButton=new DictationButton { Text="Hold to dictate",Dock=DockStyle.Fill };
            internal ShortcutController Shortcut;
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override void WndProc(ref Message message) {
                if(Shortcut!=null && Shortcut.Dispatch(ref message)) return;
                base.WndProc(ref message);
            }
            internal DictationWindow() {
                Text="VoiceKey desktop smoke test";
                FormBorderStyle=FormBorderStyle.FixedToolWindow;
                ShowInTaskbar=false;
                StartPosition=FormStartPosition.Manual;
                TopMost=true;
                ClientSize=new System.Drawing.Size(280,88);
                Controls.Add(HoldButton);
            }
        }

        static int pressCount,releaseCount,shortcutPressCount,shortcutReleaseCount;
        static bool eligible,pasteSent;
        static Desktop.Target shortcutTarget;
        static Desktop.Target destination;

        [STAThread]
        static int Main() {
            if(!IsAuthorizedRunner()) {
                Console.Error.WriteLine("Refusing to send desktop input outside a Windows CI runner.");
                return Unavailable;
            }
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT) {
                Console.Error.WriteLine("This smoke test requires Windows.");
                return Unavailable;
            }
            IntPtr inputDesktop=OpenInputDesktop(0,false,DesktopReadObjects);
            if(inputDesktop==IntPtr.Zero) {
                Console.Error.WriteLine("No accessible interactive input desktop is available. Win32 error "+Marshal.GetLastWin32Error()+".");
                return Unavailable;
            }
            try {
                var name=new StringBuilder(128);
                int needed;
                if(!GetUserObjectInformation(inputDesktop,UserObjectName,name,name.Capacity*2,out needed) ||
                    !name.ToString().Equals("Default",StringComparison.OrdinalIgnoreCase)) {
                    Console.Error.WriteLine("The active input desktop is unavailable for UI smoke testing."+(needed>0?" Desktop="+name:""));
                    return Unavailable;
                }
                return RunSmoke();
            } finally {
                CloseDesktop(inputDesktop);
            }
        }

        static bool IsAuthorizedRunner() {
            string actions=Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
            string ci=Environment.GetEnvironmentVariable("CI");
            string runnerOs=Environment.GetEnvironmentVariable("RUNNER_OS");
            string runnerTemp=Environment.GetEnvironmentVariable("RUNNER_TEMP");
            return string.Equals(actions,"true",StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(ci,"true",StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(runnerOs,"Windows",StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(runnerTemp));
        }

        static int RunSmoke() {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try {
                using(var first=new Form { Text="VoiceKey paste target A",Width=480,Height=160,StartPosition=FormStartPosition.Manual,Left=70,Top=140 })
                using(var firstText=new TextBox { Dock=DockStyle.Fill,Multiline=true })
                using(var second=new Form { Text="VoiceKey paste target B",Width=480,Height=160,StartPosition=FormStartPosition.Manual,Left=610,Top=140 })
                using(var secondText=new TextBox { Dock=DockStyle.Fill,Multiline=true })
                using(var dictation=new DictationWindow())
                using(var timer=new System.Windows.Forms.Timer { Interval=16 }) {
                    UseUsKeyboardLayout();
                    first.Controls.Add(firstText);
                    second.Controls.Add(secondText);
                    var area=Screen.PrimaryScreen.WorkingArea;
                    dictation.Location=new System.Drawing.Point(area.Left+30,area.Top+30);
                    first.Show(); second.Show(); dictation.Show();
                    Application.DoEvents();
                    dictation.BringToFront();
                    ActivateTarget(first,firstText);
                    if(GetForegroundWindow()!=first.Handle) throw new InvalidOperationException("The first test editor did not become the foreground window.");
                    long styles=GetWindowLongPtr(dictation.Handle,-20).ToInt64();
                    if(!dictation.TopMost || (styles&0x8)==0) throw new InvalidOperationException("The dictation test window did not acquire WS_EX_TOPMOST.");

                    dictation.HoldButton.HoldStarted+=delegate {
                        pressCount++;
                        destination=Desktop.CurrentTarget();
                        eligible=destination.Window==first.Handle && destination.Focus==firstText.Handle;
                    };
                    dictation.HoldButton.HoldEnded+=delegate {
                        releaseCount++;
                        if(eligible && destination.Matches(Desktop.CurrentTarget()))
                            pasteSent=Desktop.Paste(destination);
                    };
                    timer.Tick+=delegate {
                        if(dictation.Shortcut!=null) dictation.Shortcut.Tick();
                        if(eligible && !destination.Matches(Desktop.CurrentTarget())) eligible=false;
                    };
                    timer.Start();

                    string expected="VoiceKey smoke π 漢字";
                    firstText.Clear();
                    ActivateTarget(first,firstText);
                    PressAndRelease(dictation.HoldButton,350);
                    Application.DoEvents();
                    if(pressCount!=1 || releaseCount!=1) throw new InvalidOperationException("Mouse hold and release did not each reach the production button exactly once.");
                    if(!pasteSent) throw new InvalidOperationException("The transcript paste was not sent to the focused target.");
                    if(firstText.Text!=expected) throw new InvalidOperationException("The Unicode transcript did not appear exactly once in the target. Actual text: "+firstText.Text);
                    if(GetForegroundWindow()!=first.Handle || Desktop.CurrentTarget().Focus!=firstText.Handle)
                        throw new InvalidOperationException("The dictation button took focus away from the target editor.");
                    Console.WriteLine("PASS topmost hold button preserves the text caret and pastes Unicode once after release.");

                    if(ShortcutBinding.Default.Key!=(int)Keys.OemSemicolon || ShortcutBinding.Default.Modifiers!=0)
                        throw new InvalidOperationException("The default global shortcut is not bare semicolon.");
                    if(ShortcutBinding.Parse("0").Key!=(int)Keys.F8)
                        throw new InvalidOperationException("A pre-1.1 numeric F8 setting did not retain its legacy meaning.");
                    Console.WriteLine("PASS the default shortcut is bare semicolon and legacy F8 settings still load.");

                    using(var shortcut=new ShortcutController(dictation.Handle,ShortcutBinding.Default)) {
                        string error;
                        dictation.Shortcut=shortcut;
                        shortcut.CanStart=true;
                        shortcut.Pressed+=delegate(object sender,ShortcutTargetEventArgs e) { shortcutPressCount++; shortcutTarget=e.Target; };
                        shortcut.Released+=delegate(object sender,ShortcutTargetEventArgs e) { shortcutReleaseCount++; shortcutTarget=e.Target; };
                        if(!shortcut.TryRegister(out error)) throw new InvalidOperationException("The default shortcut could not register: "+error);

                        firstText.Clear();
                        ActivateTarget(first,firstText);
                        PressAndReleaseKey((int)Keys.OemSemicolon,75);
                        if(shortcutPressCount!=0 || shortcutReleaseCount!=0 || firstText.Text!=";")
                            throw new InvalidOperationException("A quick semicolon tap was not replayed as exactly one punctuation character.");
                        Console.WriteLine("PASS a quick semicolon tap reaches the editor as punctuation.");

                        shortcutPressCount=shortcutReleaseCount=0;
                        firstText.Clear();
                        ActivateTarget(first,firstText);
                        PressKeyDown((int)Keys.OemSemicolon);
                        Pump(350);
                        if(shortcutPressCount!=1 || shortcutTarget.Window!=first.Handle || shortcutTarget.Focus!=firstText.Handle)
                            throw new InvalidOperationException("Holding semicolon did not start dictation with the focused editor as target.");
                        PressKeyUp((int)Keys.OemSemicolon);
                        Pump(100);
                        if(shortcutReleaseCount!=1 || firstText.Text.Length!=0)
                            throw new InvalidOperationException("A semicolon hold did not release once without inserting punctuation.");
                        Console.WriteLine("PASS holding semicolon starts on the focused editor and release does not insert punctuation.");

                        firstText.Clear();
                        ActivateTarget(first,firstText);
                        PressShiftedKey((int)Keys.OemSemicolon);
                        if(shortcutPressCount!=1 || firstText.Text!=":")
                            throw new InvalidOperationException("Shift+semicolon did not pass through as a colon.");
                        Console.WriteLine("PASS Shift+semicolon passes through as a colon.");
                        dictation.Shortcut=null;
                    }

                    pressCount=releaseCount=0;
                    pasteSent=false;
                    eligible=false;
                    expected="Must not paste into a switched window";
                    firstText.Clear(); secondText.Clear();
                    ActivateTarget(first,firstText);
                    MoveToButton(dictation.HoldButton);
                    ClickDown(); Pump(100);
                    if(pressCount!=1 || !eligible) throw new InvalidOperationException("The second press did not capture editor A as its paste target.");
                    ActivateTarget(second,secondText);
                    Pump(120);
                    Clipboard.SetText(expected,TextDataFormat.UnicodeText);
                    ClickUp(); Pump(180);
                    if(releaseCount!=1) throw new InvalidOperationException("The switched-window hold did not receive a release.");
                    if(pasteSent || firstText.Text.Length!=0 || secondText.Text.Length!=0)
                        throw new InvalidOperationException("The transcript pasted after focus switched to another window.");
                    Console.WriteLine("PASS changing editors during a hold prevents paste into either window.");
                    timer.Stop();
                }
                return 0;
            } catch(Exception ex) {
                Console.Error.WriteLine("Desktop smoke test failed: "+ex);
                return 1;
            }
        }

        static void ActivateTarget(Form form,TextBox box) {
            form.Activate();
            box.Focus();
            Application.DoEvents();
        }
        static void PressAndRelease(Control button,int holdMilliseconds) {
            MoveToButton(button);
            ClickDown(); Pump(holdMilliseconds);
            Clipboard.SetText("VoiceKey smoke π 漢字",TextDataFormat.UnicodeText);
            ClickUp(); Pump(180);
        }
        static void MoveToButton(Control button) {
            Rect rect;
            if(!GetWindowRect(button.Handle,out rect)) throw new InvalidOperationException("GetWindowRect failed for the hold button.");
            int x=(rect.Left+rect.Right)/2, y=(rect.Top+rect.Bottom)/2;
            if(!SetCursorPos(x,y)) throw new InvalidOperationException("SetCursorPos failed with Win32 error "+Marshal.GetLastWin32Error()+".");
        }
        static void ClickDown() { mouse_event(MouseLeftDown,0,0,0,UIntPtr.Zero); }
        static void ClickUp() { mouse_event(MouseLeftUp,0,0,0,UIntPtr.Zero); }
        static void UseUsKeyboardLayout() {
            IntPtr layout=LoadKeyboardLayout("00000409",KeyboardActivate|KeyboardSetForProcess);
            if(layout==IntPtr.Zero || ActivateKeyboardLayout(layout,KeyboardSetForProcess)==IntPtr.Zero)
                throw new InvalidOperationException("The US keyboard layout could not be selected for the isolated smoke test.");
        }
        static void PressAndReleaseKey(int key,int holdMilliseconds) {
            PressKeyDown(key); Pump(holdMilliseconds); PressKeyUp(key); Pump(180);
        }
        static void PressKeyDown(int key) {
            var input=new Input {Type=1,Keyboard=new KeyboardInput {VirtualKey=(ushort)key}};
            if(SendInput(1,new[]{input},Marshal.SizeOf(typeof(Input)))!=1)
                throw new InvalidOperationException("SendInput could not press virtual key 0x"+key.ToString("X2")+".");
        }
        static void PressKeyUp(int key) {
            var input=new Input {Type=1,Keyboard=new KeyboardInput {VirtualKey=(ushort)key,Flags=KeyUpFlag}};
            if(SendInput(1,new[]{input},Marshal.SizeOf(typeof(Input)))!=1)
                throw new InvalidOperationException("SendInput could not release virtual key 0x"+key.ToString("X2")+".");
        }
        static void PressShiftedKey(int key) {
            var events=new[]{
                new Input {Type=1,Keyboard=new KeyboardInput {VirtualKey=(ushort)Keys.ShiftKey}},
                new Input {Type=1,Keyboard=new KeyboardInput {VirtualKey=(ushort)key}},
                new Input {Type=1,Keyboard=new KeyboardInput {VirtualKey=(ushort)key,Flags=KeyUpFlag}},
                new Input {Type=1,Keyboard=new KeyboardInput {VirtualKey=(ushort)Keys.ShiftKey,Flags=KeyUpFlag}}
            };
            if(SendInput((uint)events.Length,events,Marshal.SizeOf(typeof(Input)))!=events.Length)
                throw new InvalidOperationException("SendInput could not type Shift+semicolon.");
            Pump(120);
        }
        static void Pump(int milliseconds) {
            var timer=Stopwatch.StartNew();
            while(timer.ElapsedMilliseconds<milliseconds) {
                Application.DoEvents();
                Thread.Sleep(8);
            }
            Application.DoEvents();
        }
    }
}
