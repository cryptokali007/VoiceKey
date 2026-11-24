using System;
using System.IO;
using System.Windows.Forms;

namespace VoiceKey {
    internal static class ShortcutChecks {
        static void Require(bool condition,string message) {
            if(!condition) throw new InvalidDataException(message);
        }
        internal static void Run() {
            ShortcutBinding semicolon=ShortcutBinding.CaptureKeyData(Keys.OemSemicolon);
            Require(semicolon!=null && semicolon.Key==(int)Keys.OemSemicolon && semicolon.Modifiers==0,
                "OEM semicolon capture must not invent a Windows modifier");
            Require(ShortcutBinding.Parse(semicolon.Serialize()).Key==(int)Keys.OemSemicolon,
                "OEM semicolon binding must survive settings serialization");
            ShortcutBinding shifted=ShortcutBinding.CaptureKeyData(Keys.Shift|Keys.OemSemicolon);
            Require(shifted!=null && shifted.Modifiers==4 && shifted.Key==(int)Keys.OemSemicolon,
                "Shift plus OEM semicolon must capture only Shift");
            ShortcutBinding chord=ShortcutBinding.CaptureKeyData(Keys.Control|Keys.Shift|Keys.A);
            Require(chord!=null && chord.Key==(int)Keys.A && chord.Modifiers==6 && chord.UsesRegisterHotKey,
                "Ctrl+Shift+A must capture both modifiers and use global hotkey registration");
            ShortcutBinding paste=ShortcutBinding.CaptureKeyData(Keys.Control|Keys.V);
            Require(paste!=null && paste.IsReservedPasteChord,
                "Ctrl+V must be reserved for VoiceKey paste output");
            string conflict;
            using(var reserved=new ShortcutController(IntPtr.Zero,paste))
                Require(!reserved.TryRegister(out conflict) && conflict.IndexOf("uses Ctrl+V",StringComparison.Ordinal)>=0,
                    "The controller must explain why Ctrl+V cannot be registered");
            Require(ShortcutBinding.CaptureKeyData(Keys.ControlKey)==null &&
                ShortcutBinding.CaptureKeyData(Keys.ShiftKey)==null,
                "A modifier by itself must not become a shortcut");
            Require(ShortcutBinding.Parse("0").Key==(int)Keys.F8 &&
                ShortcutBinding.Parse("1").Key==(int)Keys.F9 &&
                ShortcutBinding.Parse("2").Key==(int)Keys.F10 &&
                ShortcutBinding.Parse("3").IsOff,
                "Existing F8/F9/F10/Off setting values must remain supported");

            Desktop.Target first=new Desktop.Target {Window=new IntPtr(11),Focus=new IntPtr(12),Thread=13};
            Desktop.Target second=new Desktop.Target {Window=new IntPtr(21),Focus=new IntPtr(22),Thread=23};
            var press=new BareKeyPress();
            Require(!press.Down(first,false,100),"A bare key must pass through while dictation cannot start");
            Require(!press.Down(new Desktop.Target(),true,100),"A bare key must pass through without a focused target");
            Require(press.Down(first,true,100),"A valid bare key should be held pending tap-versus-dictation");
            Require(press.Down(second,true,110) && press.Target.Matches(first),
                "A repeated keydown must keep the original press and target snapshot");
            Require(press.Tick(349,true)==BareKeyAction.None,"A short key press must not start dictation");
            Require(press.Up()==BareKeyAction.Replay,"A short key press must request normal punctuation replay");
            Require(press.Tick(1000,true)==BareKeyAction.None && press.Up()==BareKeyAction.None,
                "A released tap must not start late or release a nonexistent recording");
            Require(ShortcutDecisions.CanReplayTap(first,first,false),"A tap replays to its original field");
            Require(!ShortcutDecisions.CanReplayTap(first,second,false),"A tap is dropped after focus changes");
            Require(!ShortcutDecisions.CanReplayTap(first,first,true),"A tap is never replayed while modifiers are down");

            Require(press.Down(second,true,2000),"A later repeated punctuation press starts a new state");
            Require(press.Tick(2250,true)==BareKeyAction.Start,"A held bare key starts dictation after the threshold");
            Require(press.Tick(2500,true)==BareKeyAction.None,"A long hold starts only once");
            Require(press.Up()==BareKeyAction.Release,"Releasing a long hold ends dictation");
            Require(press.Down(first,true,3000),"A new press after release can begin normally");
            Require(press.Tick(3300,false)==BareKeyAction.None && press.Up()==BareKeyAction.Replay,
                "A press that loses readiness before the hold threshold falls back to a tap");
        }
    }
}
