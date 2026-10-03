# 微軟注音：外部候選讀取與套用實測

**結論：外部候選讀取成功；外部候選選擇／套用成功。** 獨立 reader 從微軟注音 popup 的 HWND 與 UI Automation 讀到目前頁面 9 個有序候選；外部操作送出候選數字鍵 `2`，讓微軟注音在原測試欄位、原位置把未提交的「你」改成「妳」。套用當下仍是未定稿組字，沒有按 Enter。

範圍：2026-10-02，Asia/Taipei；Windows build 26200.9457、25H2、x64；WinForms 原生多行 EDIT 空白測試欄位。只驗證原版微軟注音候選讀取與原位置選字；沒有接 Jev、要求手動預覽替換、安裝或永久修改輸入法及系統設定，也沒有提交／推送。

## 原位置套用：最新完整驗證

使用者新增授權後，先重新啟動 observer，再把先前人工字元用 Ctrl+A、Backspace 清空，從文件位置 0 重做 `s u 3 Down`。這個清空只用於測試準備；實際選字沒有直接設定或貼上 textbox 文字。

* 獨立 reader PID **6536**、host PID **16604**、IME popup PID **62212**。
* 選字前的外部 sample：UTC `11:40:23.3492316Z`。UIA ListItem 順序為 `你、妳、擬、隬、旎、昵、伱、伲、儗`，與候選畫面一致。
* 選字前 composition 為 **你**；從這份清單選可見索引 **2**（zero-based 1）的 **妳**。
* UTC `11:40:34.555Z` 外部操作：Computer Use `sky.press_key(key='2')`，使用微軟注音自己的候選數字快捷鍵。
* UTC `11:40:34.6779183Z`，同一 edit HWND `822042A` 的 `WM_IME_COMPOSITION` (`0x10F`) 回報 composition **妳**、2 bytes；cursor 1、deltaStart 0、clauses 0–1。
* result bytes **0**、result 空字串，操作至畫面捕捉間沒有 `WM_IME_ENDCOMPOSITION`；畫面仍有組字底線。這證明「妳」套入同一單字組字範圍，尚未定稿。

沒有用 host F9、直接設定文字、貼上或 Enter 來套用候選。這是外部鍵盤操作驅動 IME 選字，沒有測 `ITfCandidateListUIElementBehavior.SetSelection/Finalize` API。

證據：

* [完整讀取與套用摘要](evidence/candidate-selection-summary.json)
* [選字前獨立 reader 的完整機器可讀清單](evidence/candidate-selection-before-read.json)
* [本次外部 observer JSONL](evidence/candidate-selection-read.jsonl)
* [選字前畫面](evidence/candidate-selection-before.png)、[選字後畫面](evidence/candidate-selection-after.png)
* [操作後 host composition/result 事件](evidence/candidate-selection-after-events.json)
* [外部觸發按鍵紀錄](evidence/actions.jsonl)、[套用證據 SHA-256](evidence/candidate-selection-sha256.json)

本次 observer PID 6536 已停止。以下保留前一次外部讀取成功的定位與對照結果。

## 可核對的成功證據

| 角色 | 實際身份 |
|---|---|
| 承載人工輸入的 host | ImeTestHost-tsf.exe，PID 16604 |
| 獨立外部 reader | CandidatePopupProbe-direct.exe，PID 23456 |
| 微軟候選 popup | TextInputHost.exe，PID 62212，HWND `0xB81065C`，class `Windows.UI.Core.CoreWindow` |
| active TSF profile | 描述「微軟注音」，profile GUID `b2f9c502-1742-11d4-9790-0080c882687e`，CLSID `b115690a-ea02-48d5-a231-e3578d2fdf80` |
| 成功 sample | UTC `2026-10-02T11:36:12.0075039Z`，台北 19:36:12 |
| UIA candidate container | `IME_Candidate_Window` → `TEMPLATE_PART_CandidatePanel` |
| 實際讀取欄位 | 依 UIA tree order 的 `ControlType.ListItem.Name` |

外部取得的順序：

```json
["你", "妳", "擬", "隬", "旎", "昵", "伱", "伲", "儗"]
```

這 9 項的 UIA bounds 由上至下排列，和保存的候選畫面相符；也和 host 同時間的 TSF 清單前 9 項相符。外部 reader PID 和 host／IME PID 都不同，原始 JSONL 保留全部身份、時間、UIA node、順序及 bounds。

* [精簡機器可讀結果](evidence/candidate-external-summary.json)
* [外部成功 sample 完整 UIA 資料](evidence/candidate-external-success-sample.json)
* [外部 reader 原始 JSONL](evidence/candidate-external-uia-direct.jsonl)
* [同時可見的候選 screenshot](evidence/candidate-direct-active-screen.png)
* [實際 active profile](evidence/candidate-active-profile.json)
* [host 內部 TSF 對照資料](evidence/candidate-host-tsf.json)
* [本輪程式與 screenshot SHA-256](evidence/candidate-round2-sha256.json)

## 實際採用路徑

1. 開啟新的空白 WinForms 測試欄位，讀取 owning thread 的 active profile，確認為「微軟注音」。原有設定保持不變。
2. 在測試欄位輸入標準注音實體鍵 `s u 3`（ㄋㄧˇ），按 Down 打開候選；全程沒有按 Enter。
3. 從本次畫面限定 candidate popup 的螢幕範圍。成功 reader 的 seed point 為螢幕 `(175, 340)`，scope 為 `(left=105, top=274, width=164, height=362)`。screenshot 原點是 `(87,80)`；範圍使用了螢幕座標，並用 native popup bounds `(106,275)-(268,634)` 校核。
4. 獨立 reader 只在 PID 16604、固定測試 title 為前景時讀取。`WindowFromPoint` 命中已驗證的 Windows TextInputHost.exe candidate HWND。
5. UIA `FromPoint` 在這次候選窗上仍回到背後 EDIT 欄位，故不能用它的空結果宣稱候選不可讀。改對已知 popup HWND 使用 `AutomationElement.FromHandle` 後，取得候選 Window/Menu/List/ListItem subtree。
6. 只列舉驗證過的 popup PID、可見且落在限定矩形中的 nodes；讀取 `ListItem.Name`，按 tree index 保留順序。

Read-only reader 沒有 SendInput、IME activation、clipboard、UIA Invoke/SetValue、TSF candidate selection 或網路呼叫。它不掃描其他 app 的文字。兩個本輪 observer PID 36204、23456 已停止，成功原始紀錄已保留。

## 先前失敗與本輪對照

* 外部 IMM32 `ImmGetContext` null、欄位 UIA 不含候選的先前結果仍有效；它們不涵蓋獨立 popup UIA 路徑。
* 本輪第一次 45 秒 observer 的採樣沒有覆蓋重新打開候選的時段，因此其 EDIT 命中結果**不列為介面失敗證據**。保留於 `candidate-external-uia-attempt1.jsonl`。
* 第二次 observer 運行期間，native hit 已是 TextInputHost 的 CoreWindow，但 UIA `FromPoint` 返回 EDIT；保留於 `candidate-external-uia.jsonl`。第三次加上 `FromHandle`，並讓 observer 先運行、再打開候選，取得成功資料。
* owning-thread TSF `ITfCandidateListUIElement` 同時讀到 25 項，包括末項 `ㄋㄧˇ`，原候選 UI 保持可見。這是 **host 內部** 可讀的對照資料；本輪外部成功只證明可見頁面 9 個候選。
* 前一輪的實體 Escape 停止已被保留；本輪取得新的明確恢復授權後才恢復 Computer Use，沒有繞過停止保護。

## 重現與原型

外部原型絕對路徑：
`C:\Users\timhuang\OneDrive\文件\ChatGPT\Jevboard\diagnostics\external-probe\CandidatePopupProbe.cs`

成功執行檔：`diagnostics\external-probe\CandidatePopupProbe-direct.exe`。
設定：同目錄的 `candidate-config.json`；每次重跑必須更新實際 host PID、當次 popup 的螢幕座標和 output 路徑。這份 prototype 是針對已觀察到的 popup 定位，不是任意應用程式的自動發現產品。

啟動 reader 會記錄 `probe_start`；最長運行 180 秒。把受控欄位保持前景，在 observer 已運行時重新輸入固定人工字串及 Down。檢查 `popup_observation` 的 `bounded_uia_read`、`FromHandle(observed popup HWND)`、`IME_Candidate_Window` 與有序 ListItem nodes。若只有 EDIT 命中、前景不符、observer 已完成或候選已關閉，不能算有效負面測試。

本機原有 .NET Framework C# compiler 已成功編譯。編譯需 reference System.Web.Extensions.dll，以及 Framework64 v4.0.30319/WPF 的 UIAutomationClient.dll、UIAutomationTypes.dll、WindowsBase.dll；不需安裝套件。承載欄位源碼是 `diagnostics\ime-probe\ImeTestHost.cs`，TSF 只讀 helper 是 `TsfProbe.cs`。

報告絕對路徑：
`C:\Users\timhuang\OneDrive\文件\ChatGPT\Jevboard\diagnostics\REPORT.md`

第一輪完整紀錄保存在 [REPORT-round1.md](REPORT-round1.md)，其中未完成的回寫／預覽功能不屬於本輪收窄後的目標。

## 剩餘限制

這次確認的是 **WinForms 測試欄位、當前單音節位置、可見第一頁候選，以及外部數字鍵選字套入同一組字範圍**。沒有測其他頁面、整句候選、候選關閉時讀取、其他框架／任意 app 或 TSF 直接選字 API。WPF 視窗此前只開啟，未完成組字測試。

保留微軟注音、由另一個程式讀其現成候選再以候選數字鍵套用，在這個實測範圍內可行；跨 app 的 popup 定位、生命週期及更多頁面仍須另行驗證。
