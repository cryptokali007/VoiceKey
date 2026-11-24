using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceKey {
    internal static class Checks {
        static void Require(bool ok,string what) { if(!ok) throw new InvalidDataException(what); }
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        internal static void CheckFloatingWindow(MainWindow window) {
            var talk=window.Controls.Cast<Control>().SelectMany(Descendants).Single(c=>c.Text=="Hold to dictate");
            Require(window.TopMost,"VoiceKey must float above normal windows");
            // Ask the real HWND how it handles an inactive left click in its client area.
            IntPtr activation=SendMessage(talk.Handle,0x0021,window.Handle,new IntPtr((0x0201<<16)|1));
            Require(activation==new IntPtr(3),"The dictation button must accept the click without activating VoiceKey");
            var controls=Descendants(window).ToList();
            Require(controls.Any(c=>c.Text=="Dictation widget") && controls.Any(c=>c.Text=="Full app"),"Switch between dictation widget and full recorder");
        }
        static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent) {
            yield return parent;
            foreach(Control child in parent.Controls)
                foreach(var item in Descendants(child)) yield return item;
        }
        static void CheckModes(MainWindow window) {
            window.SetWidgetMode(false);
            var controls=Descendants(window).ToList();
            var talk=controls.Single(c=>c.Text=="Hold to dictate");
            var record=controls.Single(c=>c.Text=="Record WAV");
            var back=controls.Single(c=>c.Text=="Full app");
            var width=window.Width;
            IntPtr registeredWindow=window.Handle;
            for(int i=0;i<2;i++) {
                window.SetWidgetMode(true);
                Require(window.Width<width && talk.Visible && back.Visible && !record.Visible,"compact dictation view");
                window.SetWidgetMode(false);
                Require(window.Width==width && talk.Visible && record.Visible && !back.Visible,"full recorder restored");
            }
            Require(window.Handle==registeredWindow,"mode changes must preserve registered keyboard shortcuts");
        }
        internal static int Run(string[] args) {
            string dir=Path.Combine(Path.GetTempPath(),"VoiceKey-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try {
                Require(IntPtr.Size==8 && Marshal.SizeOf(typeof(AudioNative.Header))==48 && Marshal.SizeOf(typeof(AudioNative.Format))==40,"Win64 audio bindings");
                foreach(int bits in new[]{16,24}) {
                    int rate=bits==24?48000:16000;
                    string path=Path.Combine(dir,bits+".wav");
                    var sample=bits==24?new byte[]{0x11,0x22,0x80}:new byte[]{0,128};
                    using(var writer=new Wav(path,rate,bits)) writer.Append(sample,sample.Length);
                    byte[] f=File.ReadAllBytes(path);
                    Require(Encoding.ASCII.GetString(f,0,4)=="RIFF" && Encoding.ASCII.GetString(f,8,4)=="WAVE","WAV signature");
                    Require(BitConverter.ToUInt32(f,4)==f.Length-8 && BitConverter.ToUInt32(f,40)==sample.Length,"RIFF padding");
                    Require(BitConverter.ToInt32(f,24)==rate && BitConverter.ToInt16(f,34)==bits && BitConverter.ToInt16(f,22)==1,"PCM format");
                    Require(f.Skip(44).Take(sample.Length).SequenceEqual(sample) && Wav.Peak(sample,sample.Length,bits)>0.99,"PCM preservation and meter");
                }
                var t=new Desktop.Target {Window=new IntPtr(2), Focus=new IntPtr(3),Thread=4};
                Require(t.Matches(t) && !t.Matches(new Desktop.Target()) && !new Desktop.Target().Matches(new Desktop.Target()),"focus guard");
                Require(Model.Choices.Select(m=>m.Id).Distinct().Count()==6,"six distinct Whisper models");
                ShortcutChecks.Run();
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using(var window=new MainWindow()) {
                    CheckFloatingWindow(window);
                    window.Show(); Application.DoEvents(); Require(window.Visible && window.Text.StartsWith("VoiceKey"),"window creation");
                    CheckModes(window); window.Close();
                }
                if(args.Length>1) {
                    var m=Model.Choices[0];
                    Whisper.Install(m,new Progress<string>(),CancellationToken.None).GetAwaiter().GetResult();
                    string text=Whisper.Transcribe(m,"en",Path.GetFullPath(args[1]),CancellationToken.None);
                    Console.WriteLine(text);
                    Require(text.IndexOf("country",StringComparison.OrdinalIgnoreCase)>=0,"Whisper did not recognize the speech fixture: "+text);
                }
                Console.WriteLine("VoiceKey checks passed."); return 0;
            } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
            finally { Directory.Delete(dir,true); }
        }
    }
}
