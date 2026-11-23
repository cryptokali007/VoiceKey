using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VoiceKey {
    internal sealed class Mic {
        public readonly int Id;
        public readonly string Name;
        public Mic(int id, string name) { Id = id; Name = name; }
        public override string ToString() { return Name; }
    }

    internal static class AudioNative {
        [StructLayout(LayoutKind.Sequential, Pack=2)]
        internal struct Format {
            public ushort tag, channels;
            public uint rate, bytesPerSecond;
            public ushort block, bits, extra, validBits;
            public uint mask;
            public Guid subFormat;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Header {
            public IntPtr data;
            public uint capacity, used;
            public UIntPtr user;
            public uint flags, loops;
            public IntPtr next;
            public UIntPtr reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
        internal struct Caps {
            public ushort manufacturer, product;
            public uint driver;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string name;
            public uint formats;
            public ushort channels, reserved;
        }
        [DllImport("winmm.dll")] internal static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet=CharSet.Unicode)] internal static extern uint waveInGetDevCapsW(UIntPtr id, out Caps info, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInOpen(out IntPtr handle, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] internal static extern uint waveInPrepareHeader(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInUnprepareHeader(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInAddBuffer(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInStart(IntPtr h);
        [DllImport("winmm.dll")] internal static extern uint waveInReset(IntPtr h);
        [DllImport("winmm.dll")] internal static extern uint waveInClose(IntPtr h);
        [DllImport("winmm.dll", CharSet=CharSet.Unicode)] static extern uint waveInGetErrorTextW(uint error, StringBuilder text, uint count);
        internal static void Check(uint code) {
            if (code == 0) return;
            var message = new StringBuilder(256);
            waveInGetErrorTextW(code, message, 256);
            throw new IOException("Microphone: " + message + " (" + code + ")");
        }
        internal static List<Mic> Devices() {
            var result = new List<Mic> { new Mic(-1, "Windows default input") };
            for (uint id=0; id < waveInGetNumDevs(); id++) {
                Caps caps;
                if (waveInGetDevCapsW(new UIntPtr(id), out caps, (uint)Marshal.SizeOf(typeof(Caps))) == 0)
                    result.Add(new Mic((int)id, caps.name));
            }
            return result;
        }
        internal static Format Pcm(int rate, int bits, bool extended) {
            return new Format {
                tag = (ushort)(extended ? 0xfffe : 1), channels = 1, rate = (uint)rate,
                bytesPerSecond = (uint)(rate * (bits / 8)), block = (ushort)(bits / 8),
                bits = (ushort)bits, extra = (ushort)(extended ? 22 : 0), validBits = (ushort)bits,
                mask = 4, subFormat = new Guid("00000001-0000-0010-8000-00aa00389b71")
            };
        }
    }

    internal sealed class Wav : IDisposable {
        readonly FileStream file;
        readonly BinaryWriter writer;
        readonly int bits, rate;
        internal long Bytes { get; private set; }
        internal Wav(string path, int sampleRate, int depth) {
            bits=depth; rate=sampleRate;
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            writer = new BinaryWriter(file);
            try { Header(); } catch { writer.Dispose(); throw; }
        }
        void Four(string value) { writer.Write(Encoding.ASCII.GetBytes(value)); }
        void Header() {
            file.Position=0;
            Four("RIFF"); writer.Write((uint)(36 + Bytes + (Bytes & 1))); Four("WAVE");
            Four("fmt "); writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write(rate); writer.Write(rate*(bits/8)); writer.Write((ushort)(bits/8)); writer.Write((ushort)bits);
            Four("data"); writer.Write((uint)Bytes);
        }
        internal void Append(byte[] pcm, int count) {
            if (count % (bits / 8) != 0) throw new IOException("The microphone returned an incomplete audio frame.");
            if (Bytes + count > uint.MaxValue - 64L) throw new IOException("This WAV reached the RIFF size limit. Start a new take.");
            writer.Write(pcm, 0, count); Bytes += count;
        }
        internal static float Peak(byte[] pcm, int count, int bits) {
            int step=bits/8, max=0;
            for (int i=0; i+step<=count; i+=step) {
                int signed = step == 3 ? ((pcm[i] | pcm[i+1]<<8 | pcm[i+2]<<16)<<8)>>8 :
                    (short)(pcm[i] | pcm[i+1]<<8);
                max = Math.Max(max, Math.Abs(signed));
            }
            return (float)max / (step == 3 ? 8388608 : 32768);
        }
        public void Dispose() {
            try {
                if ((Bytes & 1) != 0) writer.Write((byte)0);
                Header(); writer.Flush();
            } finally { writer.Dispose(); }
        }
    }

    // One dedicated writer drains completed headers in queue order. Stop resets
    // the device and drains the last partial header before patching the WAV sizes.
    internal sealed class Capture {
        sealed class Buffer { internal IntPtr Data, Header; internal bool Prepared; }
        readonly List<Buffer> buffers = new List<Buffer>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly uint headerSize = (uint)Marshal.SizeOf(typeof(AudioNative.Header));
        IntPtr handle;
        Wav wav;
        Thread thread;
        byte[] scratch;
        volatile bool stopping;
        int next;
        internal volatile float Peak;
        internal volatile bool Clipped;
        internal volatile bool HasSignal;
        internal volatile Exception Failure;
        internal long Written;
        readonly int bits;
        internal readonly string Path;
        internal Capture(string path, int device, int rate, int bitDepth) {
            Path = path; bits = bitDepth;
            try {
                var format=AudioNative.Pcm(rate,bits,bits>16);
                IntPtr callback=signal.SafeWaitHandle.DangerousGetHandle();
                uint rc=AudioNative.waveInOpen(out handle, unchecked((uint)device), ref format, callback, IntPtr.Zero, 0x50000);
                if (rc != 0 && bits>16) {
                    format=AudioNative.Pcm(rate,bits,false);
                    rc=AudioNative.waveInOpen(out handle, unchecked((uint)device), ref format, callback, IntPtr.Zero, 0x50000);
                }
                AudioNative.Check(rc);
                int capacity = (rate / 20) * (bits / 8);
                scratch = new byte[capacity];
                wav = new Wav(path,rate,bits);
                for (int i=0; i<12; i++) {
                    var buf=new Buffer();
                    buffers.Add(buf);
                    buf.Data=Marshal.AllocHGlobal(capacity);
                    buf.Header=Marshal.AllocHGlobal((int)headerSize);
                    var header = new AudioNative.Header { data=buf.Data, capacity=(uint)capacity };
                    Marshal.StructureToPtr(header,buf.Header,false);
                    AudioNative.Check(AudioNative.waveInPrepareHeader(handle,buf.Header,headerSize));
                    buf.Prepared=true;
                    AudioNative.Check(AudioNative.waveInAddBuffer(handle,buf.Header,headerSize));
                }
                AudioNative.Check(AudioNative.waveInStart(handle));
                thread=new Thread(Run); thread.IsBackground=true; thread.Start();
            } catch {
                bool made=wav!=null; Cleanup();
                if (made) try { File.Delete(path); } catch (IOException) {}
                throw;
            }
        }
        void Drain(bool requeue) {
            for (int n=0; n<buffers.Count; n++) {
                Buffer buf=buffers[next];
                var h=(AudioNative.Header)Marshal.PtrToStructure(buf.Header,typeof(AudioNative.Header));
                if ((h.flags & 1)==0) break;
                int count=checked((int)h.used);
                if (count > scratch.Length) throw new IOException("Invalid audio buffer from device.");
                if (count>0) {
                    Marshal.Copy(buf.Data,scratch,0,count);
                    wav.Append(scratch,count);
                    Interlocked.Add(ref Written,count);
                    Peak=Wav.Peak(scratch,count,bits);
                    if (Peak>0) HasSignal=true;
                    if (Peak>=0.9998f) Clipped=true;
                }
                next=(next+1)%buffers.Count;
                if (requeue) {
                    // Requeue any buffer consumed before reset, even if the UI
                    // just signalled Stop. Only this worker resets the queue.
                    Marshal.WriteInt32(buf.Header,IntPtr.Size+4,0);
                    AudioNative.Check(AudioNative.waveInAddBuffer(handle,buf.Header,headerSize));
                }
            }
        }
        void Run() {
            try {
                while (!stopping) { signal.WaitOne(100); if (!stopping) Drain(true); }
                AudioNative.Check(AudioNative.waveInReset(handle));
                Drain(false);
            } catch (Exception ex) { Failure=ex; }
            finally { try { Cleanup(); } catch (Exception ex) { Failure=ex; } }
        }
        void Cleanup() {
            if (handle!=IntPtr.Zero) AudioNative.waveInReset(handle);
            foreach (var b in buffers) {
                if (b.Prepared && AudioNative.waveInUnprepareHeader(handle,b.Header,headerSize)!=0) continue;
                if (b.Header!=IntPtr.Zero) Marshal.FreeHGlobal(b.Header);
                if (b.Data!=IntPtr.Zero) Marshal.FreeHGlobal(b.Data);
            }
            if (handle!=IntPtr.Zero) AudioNative.waveInClose(handle);
            handle=IntPtr.Zero;
            try { if(wav!=null) wav.Dispose(); }
            finally { signal.Dispose(); }
        }
        internal void Stop() {
            stopping=true;
            try { signal.Set(); } catch (ObjectDisposedException) {}
            if (thread!=null) thread.Join();
            if (Failure!=null) throw new IOException(Failure.Message,Failure);
        }
    }
}
