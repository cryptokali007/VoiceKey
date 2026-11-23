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
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using(var window=new MainWindow()) { window.Show(); Application.DoEvents(); Require(window.Visible && window.Text=="VoiceKey","window creation"); window.Close(); }
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
