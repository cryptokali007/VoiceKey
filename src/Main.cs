using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VoiceKey {
    internal sealed class MainWindow : Form {
        readonly ComboBox mic=List(), format=List(), model=List(), language=List();
        readonly Button record=Button("Record WAV"), open=Button("Open folder"), listen=Button("Play last"),
            get=Button("Download model"), copy=Button("Copy text"), again=Button("Retry last dictation"),
            setKey=Button("Set shortcut…"), widget=Button("Dictation widget"), full=Button("Full app");
        readonly DictationButton talk=new DictationButton { Text="Hold to dictate",AutoSize=true,Padding=new Padding(8,3,8,3),UseVisualStyleBackColor=true };
        readonly TableLayoutPanel root=new TableLayoutPanel(), compact=new TableLayoutPanel();
        readonly FlowLayoutPanel dictationActions=Row(), widgetActions=Row();
        readonly FlowLayoutPanel fullStatus=Row(), widgetStatus=Row();
        readonly Label bindingName=Label("");
        readonly CheckBox paste=new CheckBox { Text="Paste into the active app", AutoSize=true, Checked=true };
        readonly TextBox transcript=new TextBox { Multiline=true,ScrollBars=ScrollBars.Vertical,Height=75,Dock=DockStyle.Fill,MaxLength=0,AcceptsReturn=true };
        readonly Label audioStatus=Label("Ready to record"), dictStatus=Label(""), keyHint=Label("");
        readonly ProgressBar meter=new ProgressBar { Minimum=0, Maximum=60, Height=10,Dock=DockStyle.Fill };
        readonly System.Windows.Forms.Timer tick=new System.Windows.Forms.Timer { Interval=32 };
        readonly string takes=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),"VoiceKey");
        readonly string prefs=Path.Combine(Whisper.Home,"settings.txt");
        readonly string latest=Path.Combine(Whisper.Home,"last-dictation.wav");
        readonly ToolTip tip=new ToolTip();
        Capture capture;
        CancellationTokenSource operation;
        bool held, keyboard, loading=true, downloading, closeRequested, eligible, widgetMode;
        Size fullSize;
        bool fullMaximized;
        ShortcutBinding binding=ShortcutBinding.Default;
        ShortcutController shortcut;
        Desktop.Target target;
        string lastTake;
        readonly Stopwatch clock=new Stopwatch();
        static ComboBox List() { return new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill,IntegralHeight=false,DropDownHeight=220 }; }
        static Label Label(string text) { return new Label { Text=text, AutoSize=true, Anchor=AnchorStyles.Left, Margin=new Padding(3,4,3,4) }; }
        static Button Button(string text) { return new Button { Text=text,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(6,3,6,3),UseVisualStyleBackColor=true,Margin=new Padding(0,3,7,3) }; }
        static Label Heading(string text) { var l=Label(text); l.Font=new Font("Segoe UI Semibold",14); l.Margin=new Padding(0,11,0,3); return l; }
        static FlowLayoutPanel Row(params Control[] items) {
            var f=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill,Margin=Padding.Empty,WrapContents=true };
            f.Controls.AddRange(items); return f;
        }
        static TableLayoutPanel Fields(string[] names,params Control[] controls) {
            var t=new TableLayoutPanel {AutoSize=true,ColumnCount=2,Dock=DockStyle.Fill,Margin=Padding.Empty};
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,98)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<names.Length;i++) {t.Controls.Add(Label(names[i]),0,i); t.Controls.Add(controls[i],1,i);}
            return t;
        }
        static void Add(TableLayoutPanel root,Control child) {
            int row=root.RowCount++; root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.Controls.Add(child,0,row);
        }
        internal MainWindow() {
            Text="VoiceKey"; Font=new Font("Segoe UI",9.5f); AutoScaleMode=AutoScaleMode.Dpi;
            TopMost=true;
            AutoScroll=true; StartPosition=FormStartPosition.CenterScreen; ClientSize=new Size(570,630); MinimumSize=new Size(470,500);
            root.Dock=DockStyle.Top; root.AutoSize=true; root.ColumnCount=1; root.Padding=new Padding(16,3,16,12);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); Controls.Add(root);
            compact.Dock=DockStyle.Top; compact.AutoSize=true; compact.ColumnCount=1; compact.Padding=new Padding(9,4,9,5);
            compact.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); compact.Visible=false; Controls.Add(compact);
            foreach(var d in AudioNative.Devices()) mic.Items.Add(d); mic.SelectedIndex=0;
            format.Items.AddRange(new object[]{"Suno vocal  ·  48 kHz / 24-bit mono WAV","Standard vocal  ·  48 kHz / 16-bit mono WAV"}); format.SelectedIndex=0;
            model.Items.AddRange(Model.Choices); model.SelectedIndex=1;
            language.Items.AddRange(new object[]{"English","Español","Auto-detect"}); language.SelectedIndex=0;
            Add(root,Heading("Voice recorder"));
            Add(root,Fields(new[]{"Microphone","WAV format"},mic,format));
            Add(root,Row(record,listen,open)); Add(root,meter); Add(root,audioStatus);
            Add(root,Heading("Push-to-talk"));
            Add(root,Fields(new[]{"Whisper model","Language","Hold key"},model,language,Row(bindingName,setKey)));
            dictationActions.Controls.AddRange(new Control[]{get,talk,widget});
            fullStatus.Controls.Add(dictStatus);
            Add(root,dictationActions); Add(root,Row(paste)); Add(root,keyHint); Add(root,fullStatus);
            Add(root,transcript); Add(root,Row(copy,again));
            widgetActions.Controls.Add(full); Add(compact,widgetActions); Add(compact,widgetStatus);
            tip.SetToolTip(talk,"Click a text field first. Hold this button to speak, then release to transcribe and paste.");
            tip.SetToolTip(again,"Run the currently selected model on the most recent dictation.");
            tip.SetToolTip(meter,"Peak level from the microphone during capture.");
            Directory.CreateDirectory(Whisper.Home); LoadSettings(); bindingName.Text=binding.Display;
            record.Click+=delegate { if(capture!=null && !held) FinishCapture(); else if(capture==null) StartCapture(false,false,new Desktop.Target()); };
            listen.Click+=delegate { if(File.Exists(lastTake)) Launch(lastTake); };
            open.Click+=delegate { try { Directory.CreateDirectory(takes); Launch(takes); } catch(Exception ex) { Error(ex.Message); } };
            get.Click+=async delegate { if(operation!=null) operation.Cancel(); else await Install(); };
            talk.HoldStarted+=delegate { StartCapture(true,false,Desktop.CurrentTarget()); };
            talk.HoldEnded+=delegate { if(held && !keyboard) FinishCapture(); };
            widget.Click+=delegate { SetWidgetMode(true); SaveSettings(); };
            full.Click+=delegate { SetWidgetMode(false); SaveSettings(); };
            setKey.Click+=delegate { ChooseShortcut(); };
            tick.Tick+=delegate { Tick(); };
            copy.Click+=delegate { CopyText(); };
            again.Click+=async delegate { if(File.Exists(latest)) { eligible=false; await Decode(); } };
            model.SelectedIndexChanged+=delegate { if(!loading) { UpdateModel(); SaveSettings(); } };
            mic.SelectedIndexChanged+=SettingsChanged; format.SelectedIndexChanged+=SettingsChanged; language.SelectedIndexChanged+=SettingsChanged;
            paste.CheckedChanged+=SettingsChanged; transcript.TextChanged+=delegate { copy.Enabled=!string.IsNullOrWhiteSpace(transcript.Text); };
            Shown+=delegate { loading=false; SetHotkey(); UpdateModel(); tick.Start(); };
            FormClosing+=ClosingWindow;
        }
        Model Selected { get { return (Model)model.SelectedItem; } }
        internal void SetWidgetMode(bool compactMode) {
            if(compactMode==widgetMode || capture!=null) return;
            SuspendLayout();
            float scale;
            using(var graphics=CreateGraphics()) scale=graphics.DpiX/96f;
            if(compactMode) {
                fullMaximized=WindowState==FormWindowState.Maximized;
                WindowState=FormWindowState.Normal;
                fullSize=Size;
                MinimumSize=Size.Empty;
                widgetActions.Controls.Add(talk); widgetActions.Controls.SetChildIndex(talk,0);
                widgetStatus.Controls.Add(dictStatus);
                root.Visible=false;
                compact.Visible=true;
                int width=(int)Math.Ceiling(275*scale);
                ClientSize=new Size(width,(int)Math.Ceiling(100*scale));
                MinimumSize=Size;
                dictStatus.MaximumSize=new Size(width-compact.Padding.Horizontal-10,0);
                Text="VoiceKey · Dictation";
            } else {
                compact.Visible=false;
                WindowState=FormWindowState.Normal;
                dictationActions.Controls.Add(talk); dictationActions.Controls.SetChildIndex(talk,1);
                fullStatus.Controls.Add(dictStatus); dictStatus.MaximumSize=Size.Empty;
                root.Visible=true;
                MinimumSize=new Size((int)(470*scale),(int)(500*scale)); Size=fullSize;
                Text="VoiceKey";
            }
            widgetMode=compactMode;
            ResumeLayout(true);
            if(IsHandleCreated) {
                var bounds=Screen.FromControl(this).WorkingArea;
                Location=new Point(Math.Max(bounds.Left,Math.Min(Left,bounds.Right-Width)),Math.Max(bounds.Top,Math.Min(Top,bounds.Bottom-Height)));
            }
            if(!compactMode && fullMaximized) WindowState=FormWindowState.Maximized;
        }
        void LoadSettings() {
            try {
                if(!File.Exists(prefs)) return;
                var lines=File.ReadAllLines(prefs);
                foreach(string line in lines) {
                    int n=line.IndexOf('='); if(n<1) continue; string v=line.Substring(n+1);
                    switch(line.Substring(0,n)) {
                        case "model": for(int j=0;j<Model.Choices.Length;j++) if(Model.Choices[j].Id==v) model.SelectedIndex=j; break;
                        case "mic": for(int j=0;j<mic.Items.Count;j++) if(mic.Items[j].ToString()==v) mic.SelectedIndex=j; break;
                        case "format": Select(format,v); break; case "language": Select(language,v); break;
                        case "key": binding=ShortcutBinding.Parse(v); break; case "paste": paste.Checked=v=="1"; break;
                        case "widget": SetWidgetMode(v=="1"); break;
                    }
                }
            } catch(IOException) {}
        }
        static void Select(ComboBox cb,string v) { int n; if(int.TryParse(v,out n)&&n>=0&&n<cb.Items.Count) cb.SelectedIndex=n; }
        void SettingsChanged(object sender,EventArgs e) { if(!loading) SaveSettings(); }
        void SaveSettings() {
            try { File.WriteAllLines(prefs,new[]{"model="+Selected.Id,"mic="+mic.SelectedItem,"format="+format.SelectedIndex,"language="+language.SelectedIndex,"key="+binding.Serialize(),"paste="+(paste.Checked?"1":"0"),"widget="+(widgetMode?"1":"0")}); }
            catch(IOException) {} catch(UnauthorizedAccessException) {}
        }
        void Error(string text) { MessageBox.Show(this,text,"VoiceKey",MessageBoxButtons.OK,MessageBoxIcon.Information); }
        void Launch(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute=true }); } catch(Exception ex) { Error(ex.Message); } }
        void ChooseShortcut() {
            if(shortcut!=null) { shortcut.Dispose(); shortcut=null; }
            var previous=binding;
            using(var dialog=ShortcutCaptureDialog.Create(this,binding)) {
                if(dialog.ShowDialog(this)==DialogResult.OK) binding=dialog.Binding;
            }
            string error=SetHotkey();
            if(error!=null && binding.Serialize()!=previous.Serialize()) {
                binding=previous; SetHotkey(); Error(error);
            }
            SaveSettings();
        }
        string SetHotkey() {
            if(shortcut!=null) shortcut.Dispose();
            shortcut=new ShortcutController(Handle,binding);
            shortcut.Pressed+=delegate(object sender,ShortcutTargetEventArgs e) { StartCapture(true,true,e.Target); };
            shortcut.Released+=delegate { if(held && keyboard) FinishCapture(); };
            bindingName.Text=binding.Display;
            string error;
            bool registered=shortcut.TryRegister(out error);
            keyHint.Text=!registered?error:binding.IsOff?"Use Hold to dictate with the mouse.":
                "Click a text field, then hold "+binding.Display+" or the dictation button to speak.";
            RefreshControls();
            return registered?null:error;
        }
        protected override void WndProc(ref Message m) {
            if(shortcut!=null && shortcut.Dispatch(ref m)) return;
            base.WndProc(ref m);
        }
        void UpdateModel() {
            bool ready=Whisper.Ready(Selected);
            dictStatus.Text=ready?"Model ready. Dictation runs on this PC.":"Download the selected model once to enable dictation.";
            RefreshControls();
        }
        void RefreshControls() {
            bool recording=capture!=null, busy=operation!=null, ready=Whisper.Ready(Selected);
            record.Enabled=!held && (!busy||downloading); record.Text=recording&&!held?"Stop and save":"Record WAV";
            mic.Enabled=format.Enabled=!recording; listen.Enabled=!recording && File.Exists(lastTake);
            model.Enabled=language.Enabled=setKey.Enabled=!recording&&!busy;
            widget.Enabled=full.Enabled=!recording;
            if(shortcut!=null) shortcut.CanStart=!closeRequested&&!recording&&!busy&&ready;
            talk.Enabled=(held&&!keyboard)||(!recording&&!busy&&ready);
            get.Text=busy?(downloading?"Cancel download":"Cancel transcription"):(ready?"Model downloaded":"Download model");
            get.Enabled=busy||(!recording&&!ready); again.Enabled=!recording&&!busy&&ready&&File.Exists(latest);
            copy.Enabled=!string.IsNullOrWhiteSpace(transcript.Text);
        }
        async Task Install() {
            var op=new CancellationTokenSource(); operation=op; downloading=true; RefreshControls();
            var progress=new Progress<string>(s=>{ if(operation==op) dictStatus.Text=s; });
            try {
                var which=Selected;
                await Task.Run(()=>Whisper.Install(which,progress,op.Token)); dictStatus.Text="Model ready. Hold the key to dictate.";
            } catch(OperationCanceledException) { dictStatus.Text="Download cancelled. You can retry it."; }
            catch(Exception ex) { dictStatus.Text="Download failed. Choose Download model to retry."; if(!closeRequested) Error(ex.Message); }
            finally { operation=null; downloading=false; op.Dispose(); RefreshControls(); if(closeRequested) Close(); }
        }
        void StartCapture(bool dictation,bool fromKey,Desktop.Target destination) {
            if(capture!=null || closeRequested || (operation!=null && (dictation||!downloading))) return;
            if(dictation&&!Whisper.Ready(Selected)) { dictStatus.Text="Choose Download model first."; return; }
            string path;
            try {
                if(dictation) { path=Path.Combine(Whisper.Home,"dict-"+Guid.NewGuid().ToString("N")+".wav"); }
                else {
                    Directory.CreateDirectory(takes); path=Path.Combine(takes,"Vocal "+DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff")+".wav");
                }
                target=destination;
                eligible=dictation && target.Window!=Handle && target.Matches(Desktop.CurrentTarget());
                int bits=dictation?16:(format.SelectedIndex==0?24:16);
                capture=new Capture(path,((Mic)mic.SelectedItem).Id,dictation?16000:48000,bits);
                held=dictation; keyboard=fromKey; clock.Restart();
                if(dictation) {
                    dictStatus.Text="Listening… release to transcribe.";
                    if(!fromKey) talk.Capture=true;
                } else audioStatus.Text="Recording…";
                RefreshControls();
            } catch(Exception ex) {
                eligible=false; held=keyboard=false; capture=null; RefreshControls();
                string message=ex.Message+"\n\nCheck your selected input and Windows microphone access for desktop apps.";
                if(!dictation && format.SelectedIndex==0) message+="\nIf your device rejects 24-bit capture, select the 16-bit vocal format.";
                Error(message);
            }
        }
        void Tick() {
            if(shortcut!=null) shortcut.Tick();
            if(eligible && !target.Matches(Desktop.CurrentTarget())) eligible=false;
            if(capture==null) return;
            if(capture.Failure!=null || (held && !keyboard && !Desktop.Down(1))) { FinishCapture(); return; }
            double db=20*Math.Log10(Math.Max(0.0001,capture.Peak)); meter.Value=(int)Math.Max(0,Math.Min(60,60+db));
            string level=db<=-75?"silence":db.ToString("0")+" dBFS";
            string info=clock.Elapsed.ToString(@"hh\:mm\:ss")+"  ·  "+level+(capture.Clipped?"  ·  CLIP":"");
            if(held) dictStatus.Text="Listening  ·  "+info; else audioStatus.Text="Recording  ·  "+info;
        }
        async void FinishCapture() {
            var c=capture; if(c==null) return;
            bool dict=held; capture=null; held=keyboard=false; talk.Capture=false; clock.Stop(); meter.Value=0;
            try {
                c.Stop();
                if(c.Written==0) { File.Delete(c.Path); throw new IOException("The input returned no audio. Try another microphone."); }
                if(!dict) { lastTake=c.Path; audioStatus.Text="Saved "+Path.GetFileName(lastTake)+(!c.HasSignal?"  ·  input was silent":(c.Clipped?"  ·  clipping detected":"")); }
                else {
                    if(File.Exists(latest)) File.Replace(c.Path,latest,null); else File.Move(c.Path,latest);
                    if(!c.HasSignal) { eligible=false; dictStatus.Text="Mic delivered silence. Select another input and try again."; }
                    else if(!closeRequested) await Decode();
                }
            } catch(Exception ex) {
                eligible=false;
                if(dict) dictStatus.Text="Dictation stopped."; else audioStatus.Text="Recording stopped.";
                Error(ex.Message+"\n\nRecording location: "+c.Path);
            } finally { if(!IsDisposed) RefreshControls(); }
        }
        bool CopyText() {
            if(string.IsNullOrWhiteSpace(transcript.Text)) return false;
            try { Clipboard.SetDataObject(transcript.Text,true,8,45); dictStatus.Text="Transcript copied."; return true; }
            catch(System.Runtime.InteropServices.ExternalException) { dictStatus.Text="Clipboard busy. Your transcript is below. Click Copy text to retry."; return false; }
        }
        async Task Decode() {
            if(operation!=null || !File.Exists(latest)) return;
            var op=new CancellationTokenSource(); operation=op; RefreshControls();
            try {
                var which=Selected; string lang=new[]{"en","es","auto"}[language.SelectedIndex];
                dictStatus.Text="Transcribing with "+which.Id+"…";
                string text=await Task.Run(()=>Whisper.Transcribe(which,lang,latest,op.Token));
                if(closeRequested) return;
                if(string.IsNullOrWhiteSpace(text)) { dictStatus.Text="No words detected. The last dictation is available to retry."; return; }
                transcript.Text=text;
                // With a combination shortcut, the dictation key can come up before Ctrl or Shift.
                for(int i=0;i<30 && paste.Checked && eligible && Desktop.PasteModifierDown();i++)
                    await Task.Delay(40,op.Token);
                if(closeRequested) return;
                if(CopyText()) {
                    if(paste.Checked && eligible && Desktop.Paste(target)) dictStatus.Text="Paste sent. Transcript also copied.";
                    else dictStatus.Text="Transcript copied. Paste it anywhere with Ctrl+V.";
                }
            } catch(OperationCanceledException) { dictStatus.Text="Transcription cancelled. Last dictation kept."; }
            catch(Exception ex) { dictStatus.Text="Transcription failed. Last dictation kept for retry."; if(!closeRequested) Error(ex.Message); }
            finally { eligible=false; operation=null; op.Dispose(); RefreshControls(); if(closeRequested) BeginInvoke(new Action(Close)); }
        }
        void ClosingWindow(object sender,FormClosingEventArgs e) {
            closeRequested=true; eligible=false;
            if(capture!=null) FinishCapture();
            if(operation!=null) { e.Cancel=true; operation.Cancel(); return; }
            tick.Stop(); if(shortcut!=null) { shortcut.Dispose(); shortcut=null; }
        }
    }

    internal static class Program {
        [STAThread] static int Main(string[] args) {
            if(args.Length>0 && args[0]=="--self-test") return Checks.Run(args);
            try {
                bool first;
                using(var mutex=new Mutex(true,@"Local\VoiceKey.Desktop.1",out first)) {
                    if(!first) { MessageBox.Show("VoiceKey is already open. Use the taskbar to show it."); return 0; }
                    Desktop.SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainWindow()); return 0;
                }
            } catch(Exception ex) { MessageBox.Show(ex.ToString(),"VoiceKey could not start"); return 1; }
        }
    }
}
