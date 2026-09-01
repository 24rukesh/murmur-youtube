# Murmur for Windows

The Windows port of Murmur — push-to-talk dictation, on-device.

> **Status: feature-complete, never run on real hardware.** Every layer exists and CI
> builds, tests and publishes a working single-file executable that starts and passes its
> own self-test on Windows. What has *not* happened is a human holding the key and speaking
> into a real microphone — see [Honesty](#honesty).

---

## Why this is a rewrite, not a port

Almost every layer of the macOS app is Apple-specific:

| Layer | macOS | Windows |
|---|---|---|
| UI | SwiftUI | Avalonia |
| Audio capture | `AVAudioEngine` | WASAPI via NAudio |
| **Default speech engine** | `SpeechAnalyzer` (ships with macOS 26) | **nothing equivalent exists** |
| Parakeet | FluidAudio → CoreML | sherpa-onnx → ONNX Runtime |
| Hotkey | `CGEventTap` | `SetWindowsHookEx(WH_KEYBOARD_LL)` |
| Text injection | Accessibility API | `SendInput` |

The consequence that shapes everything: **Windows has no counterpart to Apple's
`SpeechAnalyzer`.** On macOS, Parakeet is the optional upgrade. On Windows it is the only
engine, and the app cannot transcribe until the model is downloaded —
see [`docs/PARAKEET-WINDOWS.md`](../docs/PARAKEET-WINDOWS.md).

What *is* genuinely shared is the dictionary's behaviour, and it is shared as a contract
rather than as code: [`shared/dictionary-test-vectors.json`](../shared/dictionary-test-vectors.json).
Both implementations run those vectors in CI. Changing correction semantics starts there.

---

## Decisions, and why

**Avalonia, not WPF or WinUI 3.** WPF cannot be run or UI-tested on macOS, so every mistake
would cost a full CI round-trip. Avalonia's headless test platform runs on macOS in ~100 ms,
including simulated keyboard input and real pixel capture. Win32 interop is unaffected —
hooks, `SendInput` and WASAPI are P/Invoke, not UI-framework code. WinUI 3 was rejected
outright: Microsoft's own docs contradict each other on whether unpackaged single-file
publishing works, with open bugs reporting an exe that won't launch.

**.NET 10, not .NET 8.** .NET 8 reaches end-of-life on **2026-11-10**.

**Right Ctrl is the default hotkey, not Right Alt.** Right Alt is AltGr on German, Polish,
UK, Nordic and most Latin-American layouts — it is how those users type `@`, `€`, `\`, `|`.
Binding push-to-talk there would break basic typing for a large fraction of users. Right
Ctrl produces no character on any layout.

**The hotkey is observed, never swallowed.** The macOS build consumes Right Option because
on macOS that key types characters. On Windows, suppression buys nothing and risks a much
worse failure: if the key-down is swallowed but the key-up escapes — a hook that timed out
mid-gesture, or focus crossing into an elevated window — the target app believes Ctrl is
held down forever.

**The Copilot key is the one exception, and it is bindable.** Select `COPILOT` in Settings.

That key is not a key. Firmware sends a chord about a millisecond apart — `LWin↓`,
`LShift↓`, `F23↓` — and releases it in reverse: `F23↑`, `LShift↑`, `LWin↑`. F23 stays down
while the key is held, which is the only reason push-to-talk works on it.

Murmur binds **F23 alone** and swallows it, because passing it through opens Copilot on
every dictation. Swallowing is safe here in the way it is not for a modifier: F23 holds no
state, so a lost key-up leaves nothing stuck. The two modifiers are deliberately left
alone — nothing distinguishes a synthesised `LWin` from a real one at the instant it
arrives, so suppressing it would mean deferring and replaying every genuine Left Win press
through a timer.

One consequence needs handling: the shell opens the Start menu when Win is released with
nothing pressed in between, and the only thing pressed in between was the F23 we just ate.
So the hook taps Ctrl — harmless alone, harmless with Win — to spend that flag.

Not every vendor ships this chord; some send Win+C. To see what a particular machine sends:

```powershell
.\Murmur.App.exe --keylog     # 15 seconds, prints every key event with its VK code
```

`0x86` in that trace means `COPILOT` will work on that keyboard.

**Playback is paused while you dictate.** The laptop microphone hears the laptop speakers,
so music playing during an utterance is transcribed along with the speech — and nothing
downstream can tell a lyric from a word the user meant. On by default; off for headset users,
in Settings.

Windows has no supported way to tell an arbitrary app to pause, so the media transport key is
used: every player already listens for it, with no permission and no per-app integration. But
that key is a *toggle*, and sent blind it starts music on a machine that was deliberately
silent — a worse bug than failing to pause. So WASAPI is asked first whether any other process
is actually rendering audio, and the key is sent only if something is. `Resume` is only ever
called when a pause really happened.

**CPU-only inference.** sherpa-onnx ships no GPU package; DirectML is five versions behind
and forbids the variable tensor shapes this model requires; CUDA would force every user to
install a toolkit. On CPU with int8 weights, transcription runs ~40× faster than real time.

**Three pinned versions that would break at "latest":**

| Package | Pinned | Why |
|---|---|---|
| `NAudio` | **2.3.0** | 3.x targets .NET 9+ and will not restore |
| `Avalonia.Headless.XUnit` | **11.3.20** | 12.x requires xUnit **v3**, a different package line |
| `org.k2fsa.sherpa.onnx` | 1.13.5 | Bundles ONNX Runtime; never also reference `Microsoft.ML.OnnxRuntime` |

---

## Layout

```
windows/
├─ Directory.Build.props          strict analysis, applied to every project
├─ Directory.Packages.props       central version pinning
├─ global.json                    SDK pin
├─ src/
│  ├─ Murmur.Dictionary/          corrections + biasing          net10.0
│  ├─ Murmur.Abstractions/        the platform interfaces        net10.0
│  ├─ Murmur.Core/                engine, segmenter, storage     net10.0
│  ├─ Murmur.Speech/              Parakeet via sherpa-onnx       net10.0
│  ├─ Murmur.Testing/             fakes for the interfaces       net10.0
│  ├─ Murmur.App/                 Avalonia UI                    net10.0
│  └─ Murmur.Platform.Windows/    the ONLY Win32 code            net10.0-windows
└─ tests/
   ├─ Murmur.Dictionary.Tests/    the shared vectors             24 tests
   ├─ Murmur.Core.Tests/          engine, chunking, storage      28 tests
   └─ Murmur.App.Tests/           headless UI + model download   17 tests
```

**Only one project targets `-windows`.** Everything else is platform-neutral, so `CA1416`
turns an accidental Win32 call into a build error — and, more usefully, the whole app
builds, runs and tests on macOS.

`Murmur.App` loads the platform layer **by reflection** rather than referencing it. A direct
reference would drag the UI onto `net10.0-windows` and destroy the local loop. The published
self-test verifies that reflection works from inside the single-file bundle, because that is
where the arrangement would otherwise fail — silently, at the moment the user first pressed
the key.

Keeping the platform layer logic-free is deliberate: anything that lives there is code CI
cannot exercise. Retries, debouncing, device-change handling all belong in the neutral
projects, behind an interface.

---

## Building

**On Windows** — everything, including the platform layer:

```bash
cd windows
dotnet build Murmur.sln --no-incremental -warnaserror
dotnet test  Murmur.sln
```

**On macOS or Linux** — use the solution filter. `Murmur.Platform.Windows` targets
`net10.0-windows` and cannot compile off Windows; the filter omits it and everything else
builds and tests normally, including the full UI suite:

```bash
cd windows
dotnet test Murmur.CrossPlatform.slnf -c Release      # ~0.5s, 69 tests
```

`--no-incremental` is not optional in CI. Roslyn does not re-emit analyzer warnings on an
incremental build, so `-warnaserror` would pass on cached results and prove nothing.

**Publishing needs the Release solution build first.** The `PublishWindowsPlatformLayer`
target looks for `Murmur.Platform.Windows.dll` at `bin\Release\net10.0-windows\`, but the
nested MSBuild call inherits `RuntimeIdentifier=win-x64` from the publish and writes it to a
`win-x64\` subfolder instead. CI never trips over this because it builds the solution first;
publishing straight from a clean tree fails in `GenerateBundle` with a `FileNotFoundException`.

```powershell
dotnet build Murmur.sln -c Release
dotnet publish src/Murmur.App/Murmur.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output artifacts/publish
```

---

## Installing it

The release is an **MSI**, so Murmur behaves like an installed application: a Start menu
entry, an Add/Remove Programs entry, an uninstaller, and an upgrade that replaces the previous
version instead of sitting beside it. A bare exe is also published for anyone who wants to run
it from a folder.

```powershell
cd windows
dotnet build Murmur.sln -c Release
dotnet publish src/Murmur.App/Murmur.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output artifacts/publish

dotnet tool install --global wix --version 5.0.2
wix build installer/Murmur.wxs -arch x64 -d Version=1.5.0 `
  -d PublishDir=<absolute path to artifacts/publish> -o artifacts/installer/Murmur-1.5.0-win-x64.msi
```

**WiX 5, not 7.** Version 7 refuses to build without accepting the Open Source Maintenance
Fee EULA. Version 5 is the last MS-RL release and produces the same MSI.

The 116 MB exe compresses to a **42 MB** MSI, because a single-file bundle is mostly
uncompressed IL and the CAB squeezes it.

### Decisions in `installer/Murmur.wxs`

**Per-user, into `%LOCALAPPDATA%\Programs\Murmur`.** A per-machine install writes to Program
Files and therefore raises UAC — and this exe is unsigned, so the prompt would say "unknown
publisher" while asking for administrator rights, which is precisely the dialog people are
taught to refuse. Nothing here wants machine scope: settings, history and the speech model all
live in the user's profile already.

**`AllowSameVersionUpgrades="yes"`.** Every build generates a fresh ProductCode, so
reinstalling the same version number without this leaves the old install in place and Apps &
features lists Murmur twice. Observed, not theorised — it happened during testing, and the
fix was verified by installing 1.5.0 over itself and counting one entry and one shortcut.

**Installed as `Murmur.exe`, published as `Murmur.App.exe`.** The project name is not the
product name, and the product name is what shows in Task Manager.

**Known wart:** despite `Scope="perUser"`, the product registers under `HKLM` rather than
`HKCU` on a machine whose user can write there. Everything works — install, upgrade,
uninstall, no elevation prompt — but on a shared PC another user would see an Apps & features
entry for something they cannot run. `MSIINSTALLPERUSER=1` and `ALLUSERS=2` were both tried;
`ALLUSERS=2` means "per-machine where permitted", which is exactly what happens. Forcing it
needs an empty `ALLUSERS`, and was not worth another round for a single-user laptop.

Verified end to end: install, Start menu shortcut, Apps & features entry, `--selftest` from
the installed location, same-version reinstall leaving one entry, and an uninstall that
removes the files, the shortcut folder and the registration.

---

## Shipping it to someone

**The release is the 116 MB exe. Nothing else.** The app installs its own model on first run.

The alternative was tried and abandoned: `-p:IncludeAllContentForSelfExtract=true` folds the
weights into the binary and genuinely works — a 748 MB single file, model resolving out of
`%TEMP%\.net\<app>\<hash>\`, no setup step at all. It was dropped because the cost lands
in the wrong places. Every release becomes a 748 MB re-download for a one-line code change,
the machine carries ~1.5 GB (the exe plus its extraction), and the build takes on
redistributing someone else's CC-BY-4.0 weights along with the attribution duty that follows
them. Fetching once from the publisher is smaller in every direction.

So on first launch, if no model is found, Settings opens on top of the main window with a
DOWNLOAD MODEL button. `ModelDownloader` pulls the four files straight from the Hugging Face
repository into `%LOCALAPPDATA%\Murmur\models\parakeet-v2\` — no admin rights, so it works
from Program Files.

Two details there are load-bearing:

**Every file lands as `.part` and is renamed only when the last byte is written.** A
half-downloaded encoder satisfies `ParakeetTranscriber.IsComplete`, and sherpa-onnx then dies
with an opaque protobuf parse error the first time the user speaks — hours later, reading like
a corrupt build rather than a missing byte range. A short body is caught explicitly too: a
server that simply stops is not a socket error. Interrupted downloads leave nothing behind, so
the button is a clean retry rather than a repair.

**The progress bar is weighted by bytes, not files.** The encoder is 622 MB of a 661 MB
download; a per-file bar would sit at 0% for the entire wait and then jump to 100%, which is
the bar that makes people kill an installer.

`ModelDownloader.DownloadAsync` takes the install directory as a parameter. That is not
premature generality: `SpecialFolder.LocalApplicationData` resolves through the shell and
ignores the environment variable, so without it a test writes into the real installation —
which is exactly what happened before the parameter existed.

## <a id="honesty"></a>Honesty about what is verified

**Verified, on Windows, every push:** 69 tests pass — 24 dictionary (the shared vectors),
28 core (dictation state machine, audio chunking, all three storage formats, media pausing),
17 headless Avalonia UI and model-download handling. CI then publishes a self-contained ~116 MB executable, **runs it**, and the
binary reports back that the dictionary works, the source-generated JSON round-trips, and
the Windows platform layer loads and constructs out of the bundle.

**Verified on macOS, in ~0.5s:** the same 69 tests. The UI genuinely runs headless here,
which is why bugs like a `Render` method mutating a property get caught while writing them
rather than three CI round-trips later.

**Known divergences between the two regex engines**, measured across 30 cases — 9 differed.
The two that affect this code are both handled: culture-sensitive case-insensitive matching
(fixed by `CultureInvariant`) and NFC/NFD mismatch (fixed by normalizing both sides). Two
that are *not* fixable are simply avoided: ICU folds `ß` to `ss` and .NET does not, and
.NET's `.` splits surrogate pairs. Neither is reachable from the patterns this code builds.

**Cannot be verified anywhere but a real machine:**

- Text injection into a foreground app. Runners have an interactive desktop but cannot take
  the foreground.
- A real microphone: device format negotiation, the OS microphone-privacy block, unplugging
  mid-capture.
- The low-level keyboard hook actually firing on a physical keypress.
- Parakeet transcribing real speech, and whether the ~2 GB working set is tolerable.

Everything those depend on is behind an interface and exercised with fakes, so the logic
around them is tested. The bindings themselves are not.
