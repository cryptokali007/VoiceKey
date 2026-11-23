[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [Alias('Exe', 'Path', 'VoiceKeyExe', 'VoiceKeyPath')]
    [string] $ExePath
)

$ErrorActionPreference = 'Stop'
$voiceKey = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) ('VoiceKeyPaste-' + [Guid]::NewGuid().ToString('N'))
[void] [IO.Directory]::CreateDirectory($work)
$exitCode = 1
$process = $null

# Run 64-bit .NET Framework code on a fresh STA thread in its own process,
# regardless of the PowerShell edition or apartment state of the caller.
$source = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class PasteProbe : Form {
    const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    readonly MethodInfo current, paste;
    readonly FieldInfo windowField, focusField;
    readonly TextBox first = new TextBox(), second = new TextBox();
    readonly Timer timer = new Timer { Interval = 50 };
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    readonly uint pid = (uint)Process.GetCurrentProcess().Id;
    readonly string sample = "VoiceKey " + Guid.NewGuid().ToString("N") + " caf\u00e9 \u03a9 \u09ac";
    object firstTarget;
    int stage;
    long deadline;
    uint clipboardVersion;
    bool wroteClipboard;
    int result = 2;

    PasteProbe(string path) {
        Type desktop = Assembly.LoadFrom(path).GetType("VoiceKey.Desktop", true);
        current = desktop.GetMethod("CurrentTarget", StaticPrivate);
        paste = desktop.GetMethod("Paste", StaticPrivate);
        if (current == null || paste == null) throw new MissingMethodException("VoiceKey Desktop API");
        windowField = current.ReturnType.GetField("Window", InstancePrivate);
        focusField = current.ReturnType.GetField("Focus", InstancePrivate);
        if (windowField == null || focusField == null) throw new MissingFieldException("VoiceKey target handles");
        Text = "Disposable VoiceKey paste check " + pid;
        SetClientSizeCore(380, 85);
        first.SetBounds(10, 10, 355, 24);
        second.SetBounds(10, 46, 355, 24);
        Controls.AddRange(new Control[] { first, second });
        AcceptButton = null;
        Shown += delegate {
            Activate();
            first.Focus();
            deadline = elapsed.ElapsedMilliseconds + 4500;
            timer.Start();
        };
        timer.Tick += delegate {
            try { Step(); }
            catch (Exception ex) { Finish(1, ex.GetBaseException().ToString()); }
        };
    }

    bool Owned(IntPtr handle) {
        uint owner;
        return handle != IntPtr.Zero && GetWindowThreadProcessId(handle, out owner) != 0 && owner == pid;
    }
    bool Focused(TextBox box, out object target) {
        target = current.Invoke(null, null);
        IntPtr win = (IntPtr)windowField.GetValue(target), edit = (IntPtr)focusField.GetValue(target);
        return GetForegroundWindow() == Handle && win == Handle && edit == box.Handle &&
            Owned(win) && Owned(edit) && box.Focused;
    }
    bool ModifiersHeld() {
        foreach (int k in new int[] { 16, 17, 18, 0x5b, 0x5c })
            if ((GetAsyncKeyState(k) & 0x8000) != 0) return true;
        return false;
    }
    bool Paste(object target) { return (bool)paste.Invoke(null, new object[] { target }); }
    void Finish(int code, string message) {
        timer.Stop();
        result = code;
        Console.WriteLine((code == 0 ? "PASS" : code == 2 ? "UNTESTED" : "FAIL") + ": " + message);
        Close();
    }
    void Step() {
        if (elapsed.ElapsedMilliseconds > deadline) {
            if (stage == 1) {
                object now;
                if (Focused(first, out now))
                    Finish(1, "Ctrl+V was submitted, but the native edit never contained the exact expected text.");
                else Finish(2, "The test lost foreground before insertion could be verified.");
            } else Finish(2, "The test could not establish or maintain ownership of the foreground edit.");
            return;
        }
        object seen;
        if (stage == 0) {
            if (!Focused(first, out seen)) { Activate(); first.Focus(); return; }
            if (ModifiersHeld()) return;
            try {
                // Avoid displacing any other user's clipboard data. The hosted runner should start empty.
                IDataObject before = Clipboard.GetDataObject();
                if (before != null && before.GetFormats(false).Length != 0) {
                    Finish(2, "Clipboard already occupied; use a disposable desktop with an empty clipboard."); return;
                }
                Clipboard.SetText(sample, TextDataFormat.UnicodeText);
                clipboardVersion = GetClipboardSequenceNumber();
                wroteClipboard = true;
                if (Clipboard.GetText(TextDataFormat.UnicodeText) != sample) {
                    Finish(2, "Could not verify the test clipboard payload."); return;
                }
            } catch (ExternalException ex) { Finish(2, "Clipboard unavailable: " + ex.Message); return; }
            if (!Focused(first, out firstTarget) || ModifiersHeld()) {
                Finish(2, "The target or modifiers changed while preparing the clipboard."); return;
            }
            if (!Paste(firstTarget)) {
                Finish(1, "Released Desktop.Paste rejected the verified native target or Windows rejected SendInput."); return;
            }
            stage = 1; deadline = elapsed.ElapsedMilliseconds + 2500;
        } else if (stage == 1) {
            if (first.Text != sample) return;
            if (second.Text.Length != 0) { Finish(1, "Unexpected insertion in the other edit."); return; }
            Console.WriteLine("Exact Unicode insertion verified in native edit 1.");
            second.Focus();
            stage = 2; deadline = elapsed.ElapsedMilliseconds + 2500;
        } else if (stage == 2) {
            if (!Focused(second, out seen)) return;
            if (ModifiersHeld()) return;
            if (Paste(firstTarget)) { Finish(1, "Released focus guard accepted the stale target."); return; }
            stage = 3; deadline = elapsed.ElapsedMilliseconds + 1000;
        } else if (stage == 3) {
            if (!Focused(second, out seen)) { Finish(2, "Lost foreground while checking stale-target rejection."); return; }
            if (first.Text != sample || second.Text.Length != 0) {
                Finish(1, "A native field changed after stale-target rejection."); return;
            }
            if (elapsed.ElapsedMilliseconds >= deadline - 750)
                Finish(0, "Released Desktop.CurrentTarget/Paste inserted exact text and rejected a different native edit on the same form.");
        }
    }
    [STAThread] static int Main(string[] args) {
        try {
            if (!Environment.UserInteractive) {
                Console.WriteLine("UNTESTED: No interactive Windows desktop."); return 2;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new PasteProbe(Path.GetFullPath(args[0]))) {
                try { Application.Run(form); return form.result; }
                finally {
                    form.timer.Dispose();
                    if (form.wroteClipboard && GetClipboardSequenceNumber() == form.clipboardVersion) {
                        try { Clipboard.Clear(); } catch (ExternalException) {}
                    }
                }
            }
        } catch (Exception ex) { Console.WriteLine("FAIL: " + ex.GetBaseException()); return 1; }
    }
}
'@

try {
    $cs = Join-Path $work 'Probe.cs'
    $exe = Join-Path $work 'Probe.exe'
    [IO.File]::WriteAllText($cs, $source)
    & $compiler /nologo /target:exe /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$exe" $cs
    if ($LASTEXITCODE -ne 0) { throw "Windows probe compilation failed with exit code $LASTEXITCODE." }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $exe
    $start.Arguments = '"' + $voiceKey + '"'
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if ($process.WaitForExit(20000)) {
        $exitCode = $process.ExitCode
    } else {
        $process.Kill()
        $process.WaitForExit()
        Write-Output 'UNTESTED: Disposable Windows UI process timed out.'
        $exitCode = 2
    }
    Write-Output $output.GetAwaiter().GetResult()
    $stderr = $errors.GetAwaiter().GetResult()
    if ($stderr) { [Console]::Error.WriteLine($stderr) }
} finally {
    if ($process) { $process.Dispose() }
    Remove-Item -LiteralPath $work -Recurse -Force
}
exit $exitCode
