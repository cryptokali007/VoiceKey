using System;
using System.Windows.Forms;

namespace VoiceKey {
    // Only this button leaves keyboard focus in the destination application.
    // An ordinary WinForms Button focuses itself while processing a left press.
    internal sealed class DictationButton : Button {
        const int MouseActivate=0x0021, LeftDown=0x0201, LeftUp=0x0202, LeftDoubleClick=0x0203;
        internal event EventHandler HoldStarted, HoldEnded;

        protected override void WndProc(ref Message message) {
            switch(message.Msg) {
                case MouseActivate:
                    message.Result=new IntPtr(3); // MA_NOACTIVATE, deliver the click.
                    return;
                case LeftDown:
                case LeftDoubleClick:
                    if(Enabled && HoldStarted!=null) HoldStarted(this,EventArgs.Empty);
                    return;
                case LeftUp:
                    if(HoldEnded!=null) HoldEnded(this,EventArgs.Empty);
                    return;
            }
            base.WndProc(ref message);
        }
    }
}
