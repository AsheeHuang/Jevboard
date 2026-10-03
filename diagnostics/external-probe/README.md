# External Microsoft Bopomofo diagnostic

`ExternalImeProbe.cs` is a separate, read-only process. Build with the existing Windows .NET Framework compiler:

```powershell
./diagnostics/external-probe/build.ps1
```

Run after launching the synthetic test hosts, supplying their actual PIDs:

```powershell
./diagnostics/external-probe/ExternalImeProbe.exe --pid 12345 --pid 12346 --output ./diagnostics/external-probe/session.jsonl --seconds 240 --interval-ms 400
```

Use `--once` for a single sample. The probe never changes keyboard layouts, activates an IME, sends input, modifies a control, accesses the clipboard, or calls a model/network service. Root test-window titles must start with `Jevboard IME Probe -`. The process IDs are required; title matching alone does not grant access. Out-of-scope foreground windows emit only a fixed status, without their PID, title, text, or UI tree. No unrelated existing text is collected.

For a GUI launch API that accepts only the executable path, place `probe-config.json` beside the executable and launch it without arguments. Example schema (replace PIDs and output path):

```json
{"pids":[12345,12346],"output":"C:\\absolute\\session.jsonl","seconds":600,"interval_ms":400,"once":false}
```

It builds as a Windows subsystem executable and opens no console/window. Invalid launch/configuration errors go to `probe-error.jsonl` beside the executable if the requested log cannot be opened. A GUI launched from a sandbox shell may run on an isolated desktop; use the authorized Computer Use runtime to launch it onto the same user desktop as the test hosts.

For each accepted target, the probe records:

- `RtlGetVersion`, target GUI thread and HKL, IMM description/filename, and file version if a filename exists. A zero IMM filename is explicitly not considered proof that a modern TSF IME is absent.
- Focus/caret HWND and caret rectangle from `GetGUIThreadInfo`.
- External `ImmGetContext`; if non-null, composition text, composition reading, result/reading text, cursor offset, candidate count and index-zero/list returns. Null contexts, negative IMM errors, and zero-sized results remain distinct.
- Managed UI Automation `ValuePattern` and `TextPattern` document/selection, only after focused-element PID validation. TextPattern selection is not reported as an IME composition range.
- Native COM `IUIAutomationTextEditPattern` (`10032`), `GetActiveComposition`, and `GetConversionTarget`, including range text and a document-relative UTF-16 offset when supported. The offset uses a clone of the document range, not a UI selection change. Reads are capped at 4,096 UTF-16 characters for this synthetic test.

The log does not claim that external IMM32 access is universally supported. [Microsoft documents `ImmGetContext` as returning the context associated with a window](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immgetcontext), and its [IMM programming guidance](https://learn.microsoft.com/en-us/windows/win32/intl/input-method-manager) describes input-context ownership at the thread level. A null/zero external result must be correlated with the host's own event and IMM/TSF logs and the observed candidate UI. An inaccessible external route does not establish that the IME itself has no data.

Native COM layout and GUIDs follow [Microsoft's generated UIAutomationClient.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/UIAutomationClient.h). `IUIAutomationTextEditPattern` inherits `IUIAutomationTextPattern`, not `IUIAutomationTextPattern2`: full vtable slots 9 and 10 are the composition/conversion reads. [Microsoft's TextEditPattern documentation](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationtexteditpattern) explains the additional composition-range access. No candidate window belonging to a different process is automatically searched or read; candidates in such windows require separately established scope and a separate probe.

Candidate granularity is deliberately not inferred from string length. Correlate candidates with synthetic reading, caret position, visual candidate UI and phrase changes to distinguish a position's alternatives from whole-sentence alternatives. Successful reads are not evidence of write-back support. The external probe contains no write-back mechanism; explicit candidate selection and replacement tests belong to the controlled host/UI workflow.
