using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceKey {
    internal sealed class Model {
        internal readonly string Name, Id, Hash;
        internal Model(string name, string id, string hash) { Name=name; Id=id; Hash=hash; }
        public override string ToString() { return Name; }
        internal static readonly Model[] Choices = {
            new Model("Tiny  ·  quickest  ·  31 MiB","tiny-q5_1","2827a03e495b1ed3048ef28a6a4620537db4ee51"),
            new Model("Base  ·  everyday  ·  57 MiB","base-q5_1","a3733eda680ef76256db5fc5dd9de8629e62c5e7"),
            new Model("Small  ·  more detail  ·  181 MiB","small-q5_1","6fe57ddcfdd1c6b07cdcc73aaf620810ce5fc771"),
            new Model("Medium  ·  514 MiB","medium-q5_0","7718d4c1ec62ca96998f058114db98236937490e"),
            new Model("Large v3 Turbo  ·  fast large model  ·  547 MiB","large-v3-turbo-q5_0","e050f7970618a659205450ad97eb95a18d69c9ee"),
            new Model("Large v3  ·  original F16 weights  ·  2.9 GiB","large-v3","ad82bf6a9043ceed055076d0fd39f5f186ff8062")
        };
    }
    internal static class Whisper {
        internal static readonly string Home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VoiceKey");
        static readonly string engine=Path.Combine(Home,"whisper-1.9.2-x64");
        static readonly HttpClient http=new HttpClient { Timeout=TimeSpan.FromMinutes(2) };
        const string EngineUrl="https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.2/whisper-bin-x64.zip";
        const string EngineHash="49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a";
        internal static string Weights(Model m) { return Path.Combine(Home,"models","ggml-"+m.Id+".bin"); }
        internal static string Cli() {
            return Directory.Exists(engine) ? Directory.GetFiles(engine,"whisper-cli.exe",SearchOption.AllDirectories).FirstOrDefault() : null;
        }
        internal static bool Ready(Model m) { return m!=null && File.Exists(Weights(m)) && Cli()!=null; }
        static async Task Download(string url, string dest, string hash, string label, IProgress<string> progress, CancellationToken token) {
            using (var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false)) {
                response.EnsureSuccessStatusCode();
                var length=response.Content.Headers.ContentLength;
                using (var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output=new FileStream(dest,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
                using (HashAlgorithm digest=hash.Length==64 ? (HashAlgorithm)SHA256.Create() : SHA1.Create()) {
                    var block=new byte[81920]; long sum=0; var last=Stopwatch.StartNew();
                    while(true) {
                        int n;
                        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                            timeout.CancelAfter(TimeSpan.FromSeconds(75));
                            using(timeout.Token.Register(delegate { input.Dispose(); })) {
                                try { n=await input.ReadAsync(block,0,block.Length,timeout.Token).ConfigureAwait(false); }
                                catch(Exception) { token.ThrowIfCancellationRequested(); if(timeout.IsCancellationRequested) throw new IOException("Download stalled. Try Download model again."); throw; }
                            }
                        }
                        if(n==0) break;
                        await output.WriteAsync(block,0,n,token).ConfigureAwait(false);
                        digest.TransformBlock(block,0,n,block,0); sum+=n;
                        if(last.ElapsedMilliseconds>=250) {
                            progress.Report(label+"  ·  "+(sum/1048576)+" MiB"+(length.HasValue?" / "+(length.Value/1048576)+" MiB":""));
                            last.Restart();
                        }
                    }
                    digest.TransformFinalBlock(new byte[0],0,0);
                    string actual=BitConverter.ToString(digest.Hash).Replace("-","");
                    if(!actual.Equals(hash,StringComparison.OrdinalIgnoreCase)) throw new IOException(label+" failed its published checksum. Retry the download.");
                }
            }
        }
        internal static async Task Install(Model m,IProgress<string> progress,CancellationToken token) {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(Path.GetDirectoryName(Weights(m)));
            string stage=Path.Combine(Home,"setup-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            try {
                if(Cli()==null) {
                    progress.Report("Downloading the Whisper runtime…");
                    string zip=Path.Combine(stage,"runtime.zip"), unpack=Path.Combine(stage,"runtime");
                    await Download(EngineUrl,zip,EngineHash,"Whisper runtime",progress,token).ConfigureAwait(false);
                    ZipFile.ExtractToDirectory(zip,unpack);
                    if(Directory.GetFiles(unpack,"whisper-cli.exe",SearchOption.AllDirectories).Length!=1)
                        throw new IOException("The Whisper runtime archive has an unexpected layout.");
                    token.ThrowIfCancellationRequested();
                    Directory.Move(unpack,engine);
                }
                if(!File.Exists(Weights(m))) {
                    progress.Report("Downloading "+m.Id+"…");
                    string file=Path.Combine(stage,"model.bin");
                    string url="https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-"+m.Id+".bin";
                    await Download(url,file,m.Hash,m.Id,progress,token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    File.Move(file,Weights(m));
                }
            } finally { try { Directory.Delete(stage,true); } catch(IOException) {} }
        }
        internal static string Arg(string value) { return "\""+value+"\""; } // File arguments never end with a slash.
        internal static string Transcribe(Model m,string language,string audio,CancellationToken token) {
            string outputName="transcript-"+Guid.NewGuid().ToString("N");
            string basePath=Path.Combine(Home,outputName);
            string inputName=Path.GetDirectoryName(Path.GetFullPath(audio)).Equals(Home,StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(audio) : audio;
            string exe=Cli(); if(exe==null) throw new IOException("Download the Whisper runtime first.");
            var info=new ProcessStartInfo(exe) {
                WorkingDirectory=Home, UseShellExecute=false, CreateNoWindow=true,
                RedirectStandardError=true, RedirectStandardOutput=true,
                Arguments="-m "+Arg(Path.Combine("models",Path.GetFileName(Weights(m))))+" -f "+Arg(inputName)+" -l "+language+" -t "+Math.Max(1,Math.Min(8,Environment.ProcessorCount/2))+
                    " -nt -otxt -of "+Arg(outputName)
            };
            try {
                token.ThrowIfCancellationRequested();
                using(var p=new Process { StartInfo=info }) {
                    p.Start();
                    using(token.Register(delegate { try { p.Kill(); } catch(InvalidOperationException) {} catch(System.ComponentModel.Win32Exception) {} })) {
                        var errors=p.StandardError.ReadToEndAsync(); var stdout=p.StandardOutput.ReadToEndAsync();
                        p.WaitForExit(); Task.WaitAll(errors,stdout);
                        token.ThrowIfCancellationRequested();
                        if(p.ExitCode!=0 || !File.Exists(basePath+".txt")) {
                            string detail=errors.Result.Trim();
                            if(detail.Length>420) detail=detail.Substring(detail.Length-420);
                            throw new IOException("Whisper exited with code "+p.ExitCode+". "+detail);
                        }
                        string[] lines=File.ReadAllLines(basePath+".txt",Encoding.UTF8);
                        return string.Join(" ",lines.Select(s=>s.Trim()).Where(s=>s.Length>0)).Trim();
                    }
                }
            } finally { try { File.Delete(basePath+".txt"); } catch(IOException) {} }
        }
    }
}
