# Windows Microsoft Bopomofo 本機實測紀錄

日期：2026-10-02，Asia/Taipei。受控按鍵實測約 19:17–19:18。
狀態：**部分實測完成；尚不能宣稱可從外部取得候選並可靠回寫。**

報告絕對路徑：`C:\Users\timhuang\OneDrive\文件\ChatGPT\Jevboard\diagnostics\REPORT.md`。
原型絕對路徑：`C:\Users\timhuang\OneDrive\文件\ChatGPT\Jevboard\diagnostics\ime-probe\ImeTestHost.cs`；
外部讀取器：`C:\Users\timhuang\OneDrive\文件\ChatGPT\Jevboard\diagnostics\external-probe\ExternalImeProbe.cs`。

本次在真正的 Windows 桌面操作了 WinForms 原生多行 EDIT 欄位，並從另一個
process 同時讀取。WPF TextBox 的空白視窗已開啟，但尚未完成其組字及回寫測試。
所有自動輸入都是固定人工按鍵 `s u 3 Down`；沒有呼叫 Jev、索取金鑰或送出訊息。
沒有安裝輸入法、切換舊版相容模式、永久修改系統設定、提交或推送 Git。

## 實際結果

| 問題／路徑 | WinForms 實測結果 | 判讀限制 |
|---|---|---|
| 欄位內部讀取尚未輸入聲調的組字 | IMM `GCS_COMPSTR` 成功讀到 `ㄋㄧ`，4 bytes，cursor 2 | 這是欄位所屬 GUI thread 的讀取，不能視為外部可讀 |
| 聲調 `3` 後、未按 Enter | 畫面顯示底線組字 `你`；內部 `GCS_COMPSTR` 為 `你`，2 bytes | 實際輸入過第三聲，但 reading API 沒保留出可讀的 `ㄋㄧˇ` |
| 外部 IMM32 | `ImmGetContext` 為 null，診斷 error 0 | 此路徑無法取得 composition/candidates；不是證明 IME 沒有這些資料 |
| 外部 UIA TextPattern | 組字及候選窗開啟期間 document 為空；提交之後能讀到 `你` | 外部可讀已提交文字；本次未讀到未提交 composition |
| 外部 UIA TextEditPattern | pattern 10032，HRESULT S_OK，但 pattern pointer 為 null | 本欄位未提供這個 pattern，沒有 active composition 或 conversion range |
| 外部游標位置 | `GetGUIThreadInfo` 成功讀到 edit/caret HWND、caret rectangle | 游標矩形不等於組字文字範圍 |
| 原始讀音及聲調 | 同 thread `GCS_COMPREADSTR` 和 `GCS_RESULTREADSTR` 為空 | `su` 階段可由 COMPSTR 讀当前注音；轉字後無法藉本 API 還原原讀音及聲調 |
| 現成候選 | Down 成功打開候選 GUI，畫面有編號 `1 你`、`2 妳` 等項目 | 這是視覺證據；程式化候選取得尚未成功 |
| 內部 IMM 候選清單 | 候選 UI 開啟事件中 `candidateListCount=0`、index 0 回傳 0 bytes | 連同 thread IMM adapter 也沒有給出這次候選；不能推論 TSF/UIA popup 一律不可讀 |
| 欄位 UIA tree 的候選 | 受控欄位的 accessibility tree 沒有候選項目 | 沒有針對独立 IME popup 做已授權 handle 的 UIA 讀取測試 |
| 外部選候選、原位置回寫 | **未測** | 在嘗試捕捉候選視窗身份前，實體 Escape 停止了 Computer Use |
| 選取文字 → 預覽 → 確認 → 替換 | 已建立固定測試原型，**尚未實測** | host 的 F9 是 host 內部替換；不能拿它直接證明外部工具通用回寫 |
| 單一位置／整句候選 | 本次只打開一音節 `你` 的當前位置候選 GUI | 沒有多音節、多位置或整句候選證據 |
| WPF 相容性 | 空白 WPF 視窗已啟動，host_ready 已記錄 | 尚未在 WPF 輸入注音，不能聲稱組字讀取或回寫相容 |

**目前結論：**保留微軟注音的前提可維持，但本次測到的「外部 IMM32 + 欄位 UIA」
不足以完成未提交組字的選字整合。先保留「選取已提交文字 → 固定候選／AI 選擇 →
預覽 → 確認 → 替換」作為下一個要驗證的流程。這只是實作優先順序，還不是回寫
已成功的結論。Jev 的繁中選擇品質、同音候選產生與多音字讀音還原都未在本次測試。

## 可重現證據

日志使用 UTC；下列時間加 8 小時為台北時間。

* `11:17:22.511Z`：WinForms `wm_ime_before/after`，composition=`ㄋㄧ`。
* `11:17:47.051Z`：composition=`你`，reading 空字串；自動流程沒有按 Enter。
* `11:18:02.380Z`：`WM_IME_NOTIFY` (`0x282`)，`IMN_OPENCANDIDATE` (`wParam=5`)，
  內部候選 count 仍為 0；同時操作工具的視窗畫面顯示候選列表。
* 候選開啟至停止期間：外部 observer 為 approved_target，IMM null context、
  TextPattern document 空字串、TextEditPattern unavailable。
* `11:18:21` 之後組字結束，外部 TextPattern 可讀到已提交的 `你`。
  停止／焦點變化時的提交不算受控的候選選擇或回寫成功。

原始資料與精選片段：

* [按鍵紀錄](evidence/actions.jsonl)
* [精選 API 實測片段](evidence/key-observations.json)
* [外部 process 原始紀錄](evidence/external-session.jsonl)
* [WinForms 受控視窗紀錄](ime-probe/logs/host-winforms-20261002-111554-10936.jsonl)
* [WPF 啟動紀錄](ime-probe/logs/host-wpf-20261002-111618-54756.jsonl)
* [受測二進位 SHA-256](evidence/tested-build-sha256.json)
* [系統及已安裝 IME 檔案版本](evidence/environment.json)

判斷只採用已記錄的受控按鍵期間；停止後自行在測試欄位輸入的其他文字不納入結論。
候選畫面當時已由 Computer Use 顯示，但停止發生在寫入 PNG 前，因此沒有假稱保存
候選 screenshot 檔案。

環境：native `RtlGetVersion` build 26200；registry UBR 9457、DisplayVersion 25H2、x64。
已安裝 `C:\Windows\System32\InputMethod\CHT\ChtIME.exe` FileVersion 為 10.0.26100.9278。
這是已安裝檔案版本，不是已完成核對的 active TSF profile 身份／載入版本。
欄位 HKL 為 `0x4040404`；IMM 的 IME filename/description query 為空。
下一版加入 active profile GUID／description，尚未執行，不能宣稱已確認此設定細節。

## 原型、建置與重現

程式只用本機既有 .NET Framework C# compiler；本機 .NET 8 有 runtime、沒有 SDK。

* [受測 V1 host source](ime-probe/ImeTestHost.tested-v1.cs)：Forms.TextBox 與 WPF TextBox。
* [目前 host source](ime-probe/ImeTestHost.cs)、[TSF 只讀 helper](ime-probe/TsfProbe.cs)：
  保持原候選 UI 可見，僅接既有 GUI thread manager；記錄候選接口與 active profile。
* [Host 建置／啟動腳本](ime-probe/Start-ImeTestHosts.ps1)。
* [受測 V1 外部 reader source](external-probe/ExternalImeProbe.tested-v1.cs)。
* [目前外部 reader source](external-probe/ExternalImeProbe.cs)、[建置脚本](external-probe/build.ps1)。

受測 V1 executable 保留為 `ImeTestHost.exe`、`WpfImeTestHost.exe`、`ExternalImeProbe.exe`。
加入 TSF 的 `ImeTestHost-tsf.exe`、`WpfImeTestHost-tsf.exe` 和 reader 改善分類的
`ExternalImeProbe-prepared.exe` 均**編譯成功，但未執行驗證**。prepared reader 將查詢
HRESULT 失敗與 S_OK/null pattern 分開；V1 本次觀察為後者，因此不影響既有結論。

在使用者的普通 Windows 桌面上可建置（在專案目錄執行）：

```powershell
& .\diagnostics\ime-probe\Start-ImeTestHosts.ps1 -BuildOnly -OutputSuffix '-tsf'
& .\diagnostics\external-probe\build.ps1 -OutputSuffix '-prepared'
```

然後開啟兩個 `*-tsf.exe` 測試視窗，在欄位保持焦點，依序用實體鍵 `s`、`u`、`3`、
`Down`。每個階段按 F6 作 snapshot；只在 Chinese input mode 輸入上述標準鍵位。
如果目前是英數 mode，Shift 是本次使用的 session 切換；不需改輸入法或相容設定。
若候選 2 當時可見為 `妳`，下一輪用數字 2 選擇，驗證 composition/result；再用
多音節人工串，在不同 caret position 開候選，檢查 candidate span。

外部 reader 需用新 host 的實際 PID；現有 `probe-config.json` 是本次 PID 快照，不能
直接套到新的 process。它同時檢查 PID 與固定視窗 title，前景不是受控視窗時只記錄
固定的 outside_allowlist 狀態，不記錄其他 app 的文字或標題。設定方法見
[reader README](external-probe/README.md)。

命令列 sandbox 啟動的 GUI 實際出現在隔離桌面，雖有 HWND，Computer Use 看不到。
本次改用已安裝的 Computer Use `sky.launch_app` 在真正使用者桌面啟動才完成實測。
Headless reader 的 launch 工具回報沒有 targetable window，但日志證明 process 已
正確運行並讀到受控欄位；這個工具訊息不代表 observer 沒有啟動。

## 現在可做的一個最小手動步驟

最後檢查：原 V1 WinForms PID 10936、WPF PID 54756 還在執行。
外部 observer PID 46180 已停止；不會繼續在背景收集。

**在 `Jevboard IME Probe - WPF` 的測試輸入框按 `F7 → F8 → F9`。**
F7 載入人工固定文字並選取 `台灣`，F8 顯示差異，F9 是你的確認按鍵。
預期結果為 `前文｜臺灣｜後文`；host 會立即寫入 `replacement_preview` 和
`replacement_applied`，保留前後文字並記錄 matches。這一步不需按 Enter，沒有聊天
送出機制，也不需輸入自己的對話。

這個步驟可補 host 內部「預覽／確認／替換」證據。它不能補外部讀音、候選取得、
外部回寫或 WPF 未提交組字的證據。下一輪自動測試還需重新啟動 allowlisted observer，
驗證 selection 讀取後，以外部文字輸入替換固定 `台灣`，檢查 prefix/suffix 保持一致。

## 自動操作停止原因與剩餘測試

Computer Use 回報使用者實體 Escape 停止。
[computer-use SKILL.md](C:/Users/timhuang/.codex/plugins/cache/openai-bundled/computer-use/26.928.20755/skills/computer-use/SKILL.md)
指定其 [guidance.md](C:/Users/timhuang/.codex/plugins/cache/openai-bundled/computer-use/26.928.20755/docs/guidance.md)
規則："If Computer Use reports that the turn ended or that the user stopped Computer Use,
stop issuing app input."

使用者的「繼續」到達同一個執行回合；自動審核仍拒絕 `sky.list_windows` 的重新取得
視窗動作，理由是實體 Escape 已明確停止，這一輪禁止再用 Computer Use。
沒有改用其他工具、直接 SendInput、PowerShell UIA 或間接操作繞過停止保護。
檔案整理、準備版編譯及停止本次 headless observer 已完成。

恢复條件來自審核的「this turn」限制：必須先結束這一輪，再於新的執行回合明確
啟動電腦操作實測。這一輪內反覆說「繼續」不能消除該限制。現在可以先做上面的
手動固定測試，或不操作，保留已取得紀錄；不需要重複授權。

仍待實測：active profile 身份、TSF UIElementSink 候選及 Behavior 暴露狀態、獨立
IME popup 的外部 UIA、外部已知候選選擇、兩種 framework 的外部 selection 回寫、
WPF 組字、雙音節與整句候選範圍。不要據本報告推廣到 Notepad、Edge、Electron、
聊天 app 或其他尚未測過的欄位。

## 官方 API 判讀來源

* [IMM 多執行緒限制](https://learn.microsoft.com/en-us/windows/win32/intl/developing-ime-aware-multiple-thread-applications)：外部 null context 需與同 thread 控制樣本比較。
* [TextEdit pattern](https://learn.microsoft.com/en-us/windows/win32/winauto/textedit-control-pattern)：active composition 與 conversion target 是不同 range。
* [TSF thread manager](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-tf_getthreadmgr)、[UIElementSink](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nn-msctf-itfuielementsink)：既有承載輸入 thread 的資料不是全域任意 app 列舉。
* [微軟繁體中文 IME 操作](https://support.microsoft.com/en-us/windows/hardware/input-devices/microsoft-traditional-chinese-ime)：Down 開候選、數字選擇、候選窗 Space 翻頁。
* [UIA SDK header](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/UIAutomationClient.h)、[TSF SDK header](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/msctf.h)：COM inheritance、GUID 及 vtable 順序核對。
