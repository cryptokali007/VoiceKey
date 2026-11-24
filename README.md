# VoiceKey for Windows

Record a clean vocal take for Suno or hold a key to dictate into a Windows text box. VoiceKey uses OpenAI Whisper models locally through whisper.cpp.

**[Download VoiceKey for Windows x64](https://github.com/cryptokali007/VoiceKey/releases/latest/download/VoiceKey-Windows-x64.zip)** · [Release notes and checksum](https://github.com/cryptokali007/VoiceKey/releases/latest)

## Install

Extract the ZIP, then double-click **Install.cmd**. It adds VoiceKey to your Start menu and opens the app. For portable use, double-click **VoiceKey.exe** inside the extracted release instead. The public download works without a GitHub account. Windows 11 x64 is the target. The app uses .NET Framework 4.8 or newer.

## Record a vocal

1. Select your microphone and **Suno vocal** for 48 kHz, 24-bit, mono PCM WAV. The **Standard vocal** setting captures 48 kHz, 16-bit audio for devices that reject the 24-bit format.
2. Click **Record WAV**. Watch the peak meter while singing. If the clip indicator appears, lower the microphone or interface gain and record a fresh take.
3. Click **Stop and save**. **Play last** opens the take in your default audio player. **Open folder** shows the timestamped WAVs in your Windows Music folder under **VoiceKey**. Choose the WAV in Suno’s **Upload Audio** dialog.

VoiceKey writes the delivered PCM samples directly without compression, gain changes or effects. The microphone, input driver and Windows audio enhancements can affect what is delivered. The recorder works immediately, including offline.

## Dictate into another app

1. Pick a Whisper model, choose your language and click **Download model**. The first download also installs the CPU runtime. Each downloaded model stays on this PC.
2. Choose **Dictation widget** for a compact window that floats above your other apps. Click the field where you want the words to appear. Press and hold **Hold to dictate** with the mouse, speak, then let go. Once Whisper finishes, VoiceKey pastes into that field. You can also use the hold button in the full window.
3. To use the keyboard, click the destination field and hold your dictation shortcut. A fresh install starts with **semicolon** on a US keyboard. Tap it briefly to type punctuation, or hold it until VoiceKey shows **Listening** and speak. In the full window, **Set shortcut** lets you press a different key or a combination such as **Ctrl+Shift+Space**. Select **Off** for mouse-only use. Keyboard dictation also works while VoiceKey is minimized. Upgrades retain the shortcut saved in 1.0.
4. Click **Full app** to record or save a WAV, change your microphone or model, or edit and copy the transcript. VoiceKey remembers which view you used last. WAV recording controls are available in the full window.

Leave **Paste into the active app** checked for automatic paste. If you change windows or native controls during dictation, VoiceKey keeps the transcript on the clipboard. Press **Ctrl+V** when you are ready. You can also clear the checkbox for clipboard-only use. Windows can block automatic paste into elevated apps, and different fields within a browser can share a native focus handle. Keep the caret in your intended field until the words appear.

## Pick a model

| Model | Download | Typical use |
| --- | ---: | --- |
| Tiny Q5 | 31 MiB | Quick messages and the lightest CPU load |
| Base Q5 | 57 MiB | Everyday dictation, selected initially |
| Small Q5 | 181 MiB | More demanding speech |
| Medium Q5 | 514 MiB | An intermediate larger model |
| Large v3 Turbo Q5 | 547 MiB | A faster-decoding large model |
| Large v3 F16 | 2.9 GiB | Full-size weights for difficult takes |

All six are multilingual. The language menu supports English, Spanish and automatic detection. Model speed and accuracy depend on the hardware, recording and language. The distributed runtime uses the CPU. Large v3 can take appreciably longer on a laptop. To compare models on the same words, switch models, download if needed, then click **Retry last dictation**. A retry copies the result and leaves the original recording intact.

## Local files

- Vocal takes are kept in **Music / VoiceKey** until you remove them.
- Dictation keeps one completed clip at **%LOCALAPPDATA%/VoiceKey/last-dictation.wav** for retry. Each new dictation replaces it. The transcript remains in the app and Windows clipboard. Clipboard history and sync follow your Windows settings.
- Downloaded models, the runtime and preferences live under **%LOCALAPPDATA%/VoiceKey**. Downloads use GitHub and Hugging Face. Audio and transcripts stay local to VoiceKey.
- To remove the app, close it and delete **%LOCALAPPDATA%/Programs/VoiceKey** and the VoiceKey shortcut in the Start menu. Remove the separate data folder to clear cached models and the last dictation. Vocal recordings are stored separately.

## Build from source

Download the [source ZIP](https://github.com/cryptokali007/VoiceKey/archive/refs/heads/main.zip), extract it and run **build.cmd** on Windows x64. The executable is written to **dist/VoiceKey.exe**. **Install.cmd** can also build directly from the extracted source. Building uses the Windows .NET Framework compiler and needs no NuGet downloads.

The release workflow builds on a Windows runner, validates the WAV writer and peak meter, opens the form, installs the Tiny model and runs a known speech clip through the actual transcription engine. Test a physical microphone and your destination app on the target machine.

## License and upstream projects

VoiceKey is MIT-licensed. The runtime and model weights are downloaded on demand and retain their upstream licenses. VoiceKey is an independent project.

[OpenAI Whisper](https://github.com/openai/whisper) · [whisper.cpp](https://github.com/ggml-org/whisper.cpp) · [Converted model weights and checksums](https://huggingface.co/ggerganov/whisper.cpp)
