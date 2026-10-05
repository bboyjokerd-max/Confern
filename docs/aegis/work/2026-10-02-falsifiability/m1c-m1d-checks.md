# m1c／m1d 驗收套件的可證偽性稽核

**範圍**：`tools/verify-m1c.ps1`（S9，20 條 `Check`）與 `tools/verify-m1d.ps1`（S12，54 條 `Check`），共 **74 條**，全數逐條列出。

**方法**：每一條 `Check` 的 predicate 都追到它依賴的 app 端實作（`CodecRegistry.cs`、`Program.cs`、`ProgressPresenter.cs`、`VisualLayer.cs`、`WheelPresenter.cs`、`FormatCatalog.cs`、`ConversionEngine.cs`、`ConversionClient.cs`、`WorkerServer.cs`、`ConversionRunner.cs`、`SettingsStore.cs`、`AtomicWriter.cs`、`NamingPolicy.cs`、`TempJournal.cs`、`Evidence.cs`、`ConversionTypes.cs`），判斷「這個 predicate 在什麼情況下會變成紅燈」。

**分類定義**

- **REAL** — 它保護的行為若壞掉，這條會轉紅。
- **WEAK** — 只會在很窄／不現實的條件下失敗；或它斷言的是**自述**（我們自己的日誌行、自己設的旗標、自己寫的檔）而不是外部觀察到的結果；或功能部分缺席時它照樣過。
- **DEAD** — 根本不可能失敗：predicate 恆真、把受測程式剛寫進去的同一個值再斷言一次、拿一個值跟自己比、或條件不可達。

**統計**：REAL **42**／WEAK **25**／DEAD **7**。
m1c：8 REAL／7 WEAK／5 DEAD（20 條）。m1d：34 REAL／18 WEAK／2 DEAD（54 條）。

與 `tools/obsidian/00-index.md:90-91` 記載的「m1c 20/20、m1d 54/54」條數完全吻合 —— 也就是說，下面每一個 DEAD 都對那些綠燈有貢獻。

**稽核方式**：唯讀。未修改任何檔案、未建置、未執行這兩支套件、未終止任何行程。文中所有「預測」都是讀碼推導，尚未實測（本機當時有另一個工作在跑）。

---

## 1. 逐條表格（74 條）

### 1.1 `tools/verify-m1c.ps1`（S9，20 條）

| 檢查 id | file:line | 分類 | 一句話理由 |
|---|---|---|---|
| S9.1 | m1c:141 | REAL | 任何一條內部 `Report` 失敗都會讓 `RunCodecProbe` 回 7，exit≠0 即轉紅 |
| S9.2 | m1c:142 | WEAK | 純否定式代理（日誌沒有 `FAIL` 字樣）；日誌不存在或探測提早中止時空字串也過 |
| S9.3 | m1c:147 | REAL | 五個內建編碼器任一 round-trip 壞掉→app 印 `FAIL`→`Count -eq 5` 不成立 |
| S9.4a | m1c:154 | DEAD | `offered` 是 `MvpOutputs.Where(...)` 生出來的，`declared` 又是同一份字面清單的複本→恆真 |
| S9.4b | m1c:156 | DEAD | `offered` 就是被 `EncodableFormats` 過濾出來的，子集關係是建構出來的 |
| S9.4c | m1c:158 | DEAD | `DeclaredButUnavailable` 定義成 `MvpOutputs` 減 encodable，與 offered 恰好互補→分割恆真 |
| S9.4d | m1c:160 | DEAD | `CapableButNotDeclared ∩ MvpOutputs = ∅` 是定義上的必然；而且它根本沒讀輪盤 |
| S9.5 | m1c:164 | REAL | 對不存在路徑的實測；`ProbeDecode` 若永遠回 Ok 就會印 `FAIL` |
| S9.6 | m1c:168 | WEAK | 只回讀 `DeclareWebp` 的字面宣告（`PerFile`／`false`）；檢查名聲稱的「not offered」不在 predicate 裡 |
| S9.7 | m1c:170 | WEAK | 同上，`DeclareHeic` 的常數回聲 |
| S9.8 | m1c:176 | REAL | 腳本自建的合法 PNG 真的解出 `status=Ok` |
| S9.9 | m1c:182 | REAL | 腳本自建的截斷檔真的被判 `Corrupt` |
| S9.10 | m1c:190 | REAL | 假 `.heic` 必須 `Unsupported` 且不得出現 `heifBrand=` |
| S9.11 | m1c:200 | REAL | 真 `ftyp` box → `heifBrand=heic` |
| S9.12 | m1c:201 | WEAK | `Corrupt` 或 `NeedsExtension` 的聯集把兩種可能答案全收，只有第三種狀態才會紅 |
| S9.13 | m1c:238 | WEAK | 自報 Stopwatch 值，且 500 ms 是規格 100 ms 預算的 5 倍 |
| S9.14 | m1c:225 | WEAK | 期望值（`--codecs` 的 `offered=[...]`）與實際值（demo 的 `targets=[...]`）都讀同一份 app 日誌、同一個欄位 |
| S9.15 | m1c:228 | REAL | 讀的是真正傳進繪製端的扇區清單，出現原格式 `JPG` 就紅 |
| S9.16 | m1c:250 | DEAD | 前一行 `Stop-AllSidecar` 已經殺光所有 Confern，檢查的是腳本自己的殺成功 |
| S9.17 | m1c:245 | WEAK | 自報 Stopwatch，量的只是 `Show`（`BuildCatalog` 在計時窗之外），宣稱的「探測成本洩漏」測不到 |

（`m1c:223` 是 S9.14 在解析不到 `targets=[...]` 時硬寫 `$false` 的分支，仍算同一條檢查。）

### 1.2 `tools/verify-m1d.ps1`（S12，54 條）

| 檢查 id | file:line | 分類 | 一句話理由 |
|---|---|---|---|
| S12.1a | m1d:110 | REAL | `RunConvert` 只有全成功才回 0 |
| S12.1b | m1d:111 | REAL | 直接看輸出檔在不在 |
| S12.1c | m1d:115 | REAL | 讀前 4 個 byte 驗 PNG 簽章 |
| S12.1d | m1d:117 | REAL | 把輸出解回 Bitmap 比對 320×200 |
| S12.2 | m1d:132 | REAL | 25 個取樣點與來源逐通道比對、容差 2；整支套件最強的一條 |
| S12.3a | m1d:138 | REAL | 轉檔前後 SHA256 對比 |
| S12.3b | m1d:139 | REAL | 來源 LastWriteTimeUtc 前後對比 |
| S12.3c | m1d:140 | REAL | 來源長度前後對比 |
| S12.4a | m1d:145 | REAL | 第二次真的產生 `art (1).png` |
| S12.4b | m1d:146 | REAL | 第一個輸出的 hash 沒變＝沒被覆寫 |
| S12.4c | m1d:147 | REAL | 在檔案系統上找 `*.tmp-*`（與 `AtomicWriter` 的命名一致，有牙） |
| S12.6a | m1d:162 | REAL | 截斷 PNG 的分類真的來自解碼例外 |
| S12.6b | m1d:163 | WEAK | 只證明原因出現在日誌「某處」；檔案自己的註解就承認抓不到漂移 |
| S12.6c | m1d:164 | WEAK | `App.Run` 的頂層 catch 也回 9，分不出「§7.1 處置」與「行程崩掉」 |
| S12.6d | m1d:165 | REAL | 讀的是 `summary.Line` 這個被 render 的字串 |
| S12.7a | m1d:168 | REAL | 來源不存在→`SourceMissing` |
| S12.7b | m1d:169 | WEAK | 同 S12.6b，anywhere-in-log |
| S12.7c | m1d:170 | REAL | 同 S12.6d |
| S12.8a | m1d:175 | REAL | 真 PDF 被拒（`Unsupported` 或 `DecodeFailed` 二擇一） |
| S12.8b | m1d:176 | WEAK | 同 S12.6b，anywhere-in-log |
| S12.8c | m1d:177 | REAL | 同 S12.6d |
| S12.8d | m1d:183 | WEAK | 兩個運算元都是本腳本自己的字串常數；而且提取函式壞掉（回 false）時它照樣綠 |
| S12.8e | m1d:184 | WEAK | 同上（這一半至少會在提取函式壞掉時紅） |
| S12.8f | m1d:190 | REAL | 部分失敗必須是 `✓ 1 / ✗ 1`，走的是規格 §7.3 的另一個分支 |
| S12.8g | m1d:191 | WEAK | 重述同一行日誌尾端無條件印出的自家計數；`Line` 就算畫錯也照過 |
| S12.9 | m1d:202 | REAL | 期望值是腳本端的硬常數，與 app 自稱的 offered 對比 |
| S12.9b | m1d:203 | DEAD | 「GIF 不在 offered」是 `MvpOutputs` 這個字面清單的算術結果，不是行為 |
| S12.9c | m1d:204 | WEAK | WEBP 的缺席靠 `DeclareWebp` 硬寫 `false`；能力真的改變時才會紅 |
| S12.10a | m1d:215 | REAL | `ok=1` 來自 worker 跨管道送回的實際結果 |
| S12.10b | m1d:216 | WEAK | `progress instrument:` 這行是無條件印的；updates=0 frames=0 也 PASS |
| S12.10c | m1d:218 | DEAD | 兩個數在 `RedrawAlpha` 裡被同一件事各加一次（`Show` 內 `_frames++`、函式尾 `_updates++`）＝自己跟自己比 |
| S12.10d | m1d:220 | WEAK | 藥丸從沒畫過時 `IsVisible` 就是 false，`PumpUntil` 立刻回 true→`True` 是空的 |
| S12.10d2 | m1d:231 | WEAK | 只有「兩行 hiding 都在、其中一行 ticks<10」才紅；tick 鏈整條斷掉時落到 INCONCLUSIVE 而不是 FAIL |
| S12.10d3 | m1d:233 | WEAK | 同上；門檻 3 由淡出自己的重畫就輕鬆滿足 |
| S12.10e | m1d:238 | WEAK | 檢查名寫的 pipe deadlock 不會產生 `timed out`（見 §3.3） |
| S12.10f | m1d:239 | WEAK | 讀跨回合累積的 `%LOCALAPPDATA%\Confern\worker.log`；`$before`（m1d:211）量了卻沒用 |
| S12.10g | m1d:240 | REAL | 沒有殘留 Confern 行程（這裡沒有先殺，與 m1c:250 不同） |
| S12.10h | m1d:244 | REAL | 放手路徑真的走完一次轉檔 |
| S12.10i | m1d:251 | REAL | 釋放後那個 `art.bmp` 只可能由這次放手產生（`$Work` 開場已清空） |
| S12.15a | m1d:264 | REAL | 讀 `progress: {_last.Line}`＝真正交給 render 的字串 |
| S12.15b | m1d:265 | WEAK | 同一份 `ConversionSnapshot.Line` 的第二個出口，與 15a 同源不獨立 |
| S12.15c | m1d:266 | WEAK | 數到的「1」就是輸入 fixture 自己（見 §3.2） |
| S12.15d | m1d:267 | WEAK → **REAL**（2026-10-05 補強） | 原本只斷言自報的 `hold=5000 ms` 常數，`elapsed=\d+` 抓了卻從不比。2026-10-05 改成同時比對 `elapsed`（2000..3200 ms）並把量到的值印在證據欄；同日兩次假故障（常數改回 5000 → 紅、上界改成 1999 → 紅）都當場量到。 |
| S12.11a | m1d:275 | REAL | 移除 clamp 就會印 `jpgQuality=40` |
| S12.11b | m1d:276 | REAL | 同上（webp 200→100） |
| S12.11c | m1d:277 | REAL | 同上（9999→120） |
| S12.11d | m1d:278 | REAL | 夾制註記若不再輸出就紅 |
| S12.12a | m1d:285 | REAL | 崩潰會回 9，不會回 0 |
| S12.12b | m1d:286 | WEAK | `jpgQuality=85` 也是「根本沒讀那個檔」的輸出，分不出退回預設與忽略檔案 |
| S12.13a | m1d:299 | REAL | `ok=3` 來自三個檔案的實際結果，少一個就變 2 |
| S12.13b | m1d:301 | REAL | 三個 `.png` 真的在磁碟上 |
| S12.13c | m1d:314 | REAL | 六個檔名逐一點名，第二輪沒編號就紅 |
| S12.13e | m1d:319 | REAL | hash 對比抓覆寫（唯一漏洞：檔案被刪掉時 `Get-FileHash` 拋錯、`$untouched` 仍為 true） |
| S12.13d | m1d:320 | REAL | 同 S12.4c |

**條數核對**：m1c 20 列 ＋ m1d 54 列 ＝ **74**。

---

## 2. 七條 DEAD

### 2.1 S9.4a–d：作者註解聲稱「各自都能失敗」，實際上是集合運算的恆真式

`Program.cs:2009-2025` 的註解寫著：

> 實際提供的輸出＝宣告 ∩ 實測。三條不變式都要成立，**而且各自都能失敗**

`src/Sidecar/Formats/CodecRegistry.cs:112-114` 卻是：

```csharp
OfferedOutputs        = MvpOutputs.Where(f => EncodableFormats.Contains(f)).ToArray();
CapableButNotDeclared = EncodableFormats.Where(f => !MvpOutputs.Contains(f)).ToArray();
DeclaredButUnavailable= MvpOutputs.Where(f => !EncodableFormats.Contains(f)).ToArray();
```

- **S9.4a** `offered.All(declared.Contains)`：`offered ⊆ MvpOutputs` 是 `Where` 直接保證的，而 `declared`（`Program.cs:2015`）是 `MvpOutputs` 的**第二份字面複本**且內容相同 → 恆真。它唯一可能變紅的方式，是那兩份複本被人改到不同步；那守的是「常數有沒有同步」，不是行為。
- **S9.4b** `offered.All(encodable.Contains)`：`offered` 就是被 `EncodableFormats` 過濾出來的 → 恆真，連複本漂移都不需要。
- **S9.4c** `declared.All(f => offered.Contains(f) || DeclaredButUnavailable.Contains(f))`：`DeclaredButUnavailable` 的定義正是 `MvpOutputs \ encodable`，與 `offered = MvpOutputs ∩ encodable` 恰好互補 → 這是分割（partition）的定義，恆真。
- **S9.4d** `CapableButNotDeclared.All(f => !offered.Contains(f))`：前者定義為 `encodable \ MvpOutputs`，後者必為 `MvpOutputs` 的子集 → 兩集合交集為空是定義上的必然。**而且這一條從頭到尾沒有讀輪盤**：輪盤的扇區是 `FormatCatalog.TargetsFor()` 給的（`FormatCatalog.cs:82-91`），S9.4d 讀的卻是 registry 的三個欄位。也就是說「能力上可行但沒宣告的格式不准變成扇區」這件事，S9.4d 根本驗不到。

**後果**：這四條在報告上永遠是四個綠燈，而它們的名字（offered 不得超出宣告矩陣／不得超出實測／不得消失／GIF 不得進輪盤）會讓人以為這四件事被驗過了。實際上真正會因為「輪盤顯示了不該顯示的格式」而轉紅的，只有 **S9.14** 一條。

### 2.2 S12.9b

`-not ($offered -contains 'GIF')`，而 `$offered` 讀自 app 的 `offered=[...]`，那個清單是 `MvpOutputs.Where(...)` 產生的，`MvpOutputs`（`CodecRegistry.cs:98`）裡沒有 GIF 這個字串。這是「字面清單的算術」被寫成斷言：只要那行字面不改，它永遠是綠的。它也不看輪盤（真正看輪盤的是 m1c:225）。

### 2.3 S12.10c：「frames == updates」

`Check 'S12.10c frames == updates (same window, no line spam)' ($Matches[1] -eq $Matches[2])`，兩個數字來自 `Program.cs:873` 印出的 `progress.Updates` 與 `progress.Frames`。

`ProgressPresenter.Frames => _layer.Frames`（`ProgressPresenter.cs:78`）；`VisualLayer._frames++` 在 `Show()` 尾端（`VisualLayer.cs:150`），而 `Show()` 在**整個進度層**只有一個呼叫點：`ProgressPresenter.RedrawAlpha` 的 `_layer.Show(left, top, bitmap)`（`ProgressPresenter.cs:201`），就在 `_updates++`（同檔 207 行）的前幾行。

於是兩個數是「同一件事在同一段程式碼裡被各加一次」：只要 `Show` 回得來就必定相等，`Show` 拋例外則兩者都不加。藥丸完全不畫時更是 `0 == 0` 直接過關。它不是「同一個視窗被原地更新」的證據，而是 instrumentation 接線的鏡像。

### 2.4 S9.16：把自己的清理動作當成受測行為

```powershell
Stop-AllSidecar                                   # m1c:249 —— 殺掉機器上所有 Confern
Check "S9.16 no Confern process is left behind" `
  (@(Get-Process -Name Confern -ErrorAction SilentlyContinue).Count -eq 0)   # m1c:250-251
```

`Stop-AllSidecar`（m1c:53-58）會對每個 Confern 行程呼叫 `Kill()` 並 `WaitForExit(2000)`，再睡 300 ms，然後才做這個斷言。所以它驗的是「腳本自己殺成功了」，不是「受測程式沒有留下行程」：app 就算洩漏十個背景行程，也會被前一行殺光，然後這條印出 PASS。它只有在「有行程在 300 ms 內重生」時才會紅 —— 那是極窄且與受測性質無關的條件。

對照組：**m1d 的 S12.10g（m1d:240）是真的**，因為 m1d 全程沒有殺行程的前置動作。

---

## 3. 三個結構性盲點

### 3.1 S12.15c：把輸入檔當成「沒有輸出」的證據

```powershell
Check 'S12.15c a failed conversion writes no output and no .tmp-*' `
  ((@(Get-ChildItem -LiteralPath $Work -Filter 'corrupt.*' -File).Count -eq 1) -and ...)
```

`$Work` 裡的 `corrupt.png` 正是**這條檢查自己準備的輸入 fixture**（m1d:157-158）。所以 `Count -eq 1` 的「1」是輸入檔，不是「沒有輸出」的證據。它的實際行為是：

- app 什麼都沒寫 → 數到 1（輸入）→ **PASS**；
- app 把輸出覆寫回 `corrupt.png`（也就是毀掉使用者的來源檔，規格 §5 的第一紅線）→ 還是 1 → **PASS**；
- app 另外寫了 `corrupt (1).png` → 2 → 才會紅。

也就是說，這條檢查唯一抓得到的失敗模式是「多了一個同前綴的檔案」，而它宣稱要守的「失敗不留輸出」與「來源神聖」兩件事都驗不到。m1d 的來源神聖性檢查（S12.3a-c）只看另一顆 fixture `art.jpg`，不會發現 `corrupt.png` 被動過；檢查本身也不讀內容、不比 hash。

### 3.2 S12.10e／S12.10f：管線檢查咬不住它自己點名的失效

**S12.10e** 的檢查名是「no worker timeout (regression: pipe deadlock)」，predicate 是 `-not ($payloadText -match 'timed out')`。但在 app 裡：

- worker 連不上（正是 deadlock 的症狀）走的是 `ConversionClient.cs:129` 的 `worker did not connect within 8000 ms` —— **不含 `timed out`**；
- 管道中途斷掉走的是同檔 142 行的 `pipe closed before summary` —— **也不含**；
- 唯一會產生該字串的是 `ConversionClient.cs:253` 的 30 秒 inactivity watchdog（`treating job as timed out`），而那需要管道**已經連上**之後才可能發生。

所以：worker 從頭到尾沒起來、一個檔案都沒轉，這條照樣 PASS。它是一條否定式的日誌代理（"沒出現某個字" 就當沒事），而那個字在它宣稱守的失效路徑上根本不會出現。

**S12.10f** 讀 `$env:LOCALAPPDATA\Confern\worker.log`，那是一個**跨回合累積**的共享檔案：腳本從不刪它、不截斷它，`Evidence` 的 `StreamWriter(append: false)` 只在 worker 真的啟動時才把它清掉。所以「worker 這次根本沒 ready」的情境下，上一次成功回合留下的 `worker: ready protocolVersion=1` 還在，斷言就過。更有意思的是 m1d:211 已經量了 `$before`（檔案長度），**然後全檔再也沒有用到它** —— 作者顯然想過陳舊日誌的問題，卻沒有把那個防護寫完。

只有在「這台機器從來沒有成功跑過 payload 轉檔」時 S12.10f 才會紅；對一個每天照跑的回歸套件來說，這使它實質上等於 DEAD。

### 3.3 m1c 與 m1d 彼此不相容（也會干擾同機的其他工作）

- `verify-m1c.ps1` 的 `Stop-AllSidecar` 會在 m1c:129、212、249、265 被呼叫，內容是 `Get-Process -Name Confern | Kill()` —— **殺掉機器上所有的 Confern 行程**，不分是誰啟動的。
- `verify-m1d.ps1` 在 m1d:210 讀**共用**的 `%LOCALAPPDATA%\Confern\worker.log`，而 UI 行程與 worker 行程都會對同一個路徑寫 `Evidence`（`Evidence.cs:34` 用 `append: false`，也就是會截斷）。

後果：兩支套件並行執行會互相殺掉對方的行程、互相截斷對方的 worker 日誌（S12.10a／S12.10f 會讀到別人的回合）；任何第三個使用 Confern 的工作也一樣會被 m1c 殺掉或污染。這兩支套件必須序列化執行。順帶一提，S12.10f 的陳舊日誌問題（§3.2）正是這個共用狀態的直接後果。

（m1c:250 的 S9.16 也是同一個共用狀態的產物：它之所以要先殺，就是因為 m1c 自己會留下行程。它驗的是清理，不是洩漏。）

---

## 4. 五條最可疑：最小破壞性實驗

以下全部是**讀碼推導的預測**，未實測（當時機器上有另一個工作在跑）。每一條都刻意只改一行、只針對一個性質。

### 實驗 1 — S9.4a–d（`m1c:154,156,158,160`）

**破壞**：`src/Sidecar/Formats/FormatCatalog.cs:84`，把 `IReadOnlyList<string> offered = _codecs.OfferedOutputs;` 改成 `_codecs.EncodableFormats`。輪盤從此多出 GIF 扇區 —— 正是規格 §6.2 明禁的「承諾沒宣告的格式」，也正是 S9.4a／S9.4d 自稱在守的東西。

**預測**：**S9.4a–d 四條全綠**。它們的 predicate 從頭到尾沒讀輪盤，只重述 `CodecRegistry` 建構子的 `Where`（見 §2.1）。真正轉紅的是 **S9.14**（`targets=[PNG,BMP,TIF,GIF]` ≠ `PNG,BMP,TIF`）。
想讓 S9.4d 自己變紅，得**同時**改 `CodecRegistry.MvpOutputs` 與 `Program.cs:2015` 的第二份字面清單 —— 那恰好證明它守的是「兩份複本有沒有同步」。

### 實驗 2 — S12.10b／c／d ＋ d2／d3（`m1d:216-237`，整個進度區塊）

**破壞**：`src/Sidecar/Visual/ProgressPresenter.cs:134`，在 `Update(ConversionSnapshot snapshot)` 開頭直接 `return;`（藥丸永遠不畫）。

**預測**：

- **S12.10b PASS** —— `progress instrument: updates=0 frames=0 visible=False ...` 在 `Program.cs:873` 是**無條件**印的，predicate 只要求這行存在。
- **S12.10c PASS** —— `0 == 0`。
- **S12.10d PASS** —— `bool faded = PumpUntil(() => !progress.IsVisible, 12000);`（`Program.cs:875`）；`PumpUntil` 是「先問條件、再等」（`Program.cs:2156` 是迴圈內第一個判斷），從沒顯示過的層 `_visible` 就是 false，所以**立刻**回 true，於是日誌印出 `progress faded out on its own: True`。
- **S12.10d2／d3 → INCONCLUSIVE**（落到 m1d:235-237 的 `else`），不是 FAIL；而本專案自己的慣例是「exit 3＝有未定項，不是失敗」（`docs/aegis/plans/2026-10-02-feature-handoff.md:214`）。

也就是說：整個進度顯示功能消失，這一區留下的記錄是「3 綠 ＋ 1 不確定」。唯一會轉紅的是 **S12.15a**（另一條轉檔、不同小節）。

### 實驗 3 — S12.15c（`m1d:266`）

**破壞**：`src/Sidecar/Convert/ConversionEngine.cs:45-46`，在 `if (probe.Status != DecodeStatus.Ok) return Fail(sourcePath, MapDecodeStatus(probe.Status), probe.Detail);` 之前插入一行 `File.WriteAllBytes(sourcePath, Array.Empty<byte>());` —— 失敗時把使用者的來源檔覆寫成 0 byte。

**預測**：**S12.15c 仍然 PASS**。`Get-ChildItem -Filter 'corrupt.*'` 數到的「1」就是那顆輸入 fixture（檔案還在，只是被清空），檢查既沒讀內容也沒比 hash。規格 §5「原檔永不修改」被踩破，而 m1d 的來源神聖性檢查（S12.3a-c）只看另一顆 `art.jpg`，不會發現任何異狀。

（同一個盲點的另一種形態：把失敗時的殘檔寫到別的目錄、或用不匹配 `corrupt.*` 的名字，也一樣全綠。）

### 實驗 4 — S12.10e（`m1d:238`）

**破壞**：`src/Sidecar/Convert/WorkerServer.cs:98`，把 `pipe.Connect(10000)` 改成連一個不存在的管道名（＝檢查名自己寫的 pipe deadlock 情境）。

**預測**：**S12.10e PASS**。它 grep 的 `timed out` 只有 30 秒 inactivity watchdog 會產生（`ConversionClient.cs:253`）；worker 連不上那條路徑寫的是 `worker did not connect within 8000 ms`（`ConversionClient.cs:129`），完全不含該字串。會紅的是 S12.10a／S12.10h —— 也就是說，紅燈會來自別的地方，這條檢查對它自己點名的回歸沒有任何咬合力。

### 實驗 5 — S12.10f（`m1d:239`）

**破壞**：同實驗 4（worker 永遠起不來），並在一台「上一次 m1d 成功跑過」的機器上重跑。

**預測**：**S12.10f PASS**。它讀 `%LOCALAPPDATA%\Confern\worker.log` —— 一個跨回合累積的檔案，腳本從不刪也不截斷，只在 m1d:211 量了 `$before` 然後完全沒使用它。上一次回合寫進去的 `worker: ready protocolVersion=1` 還在，斷言就成立。只有「這台機器從來沒成功跑過 payload 轉檔」時才會紅，也就是說在例行回歸中它實質等於 DEAD。

### 同級候選（各一句）

- **m1c:250（S9.16）**：把 m1c:249 的 `Stop-AllSidecar` 拿掉，它才會變成真檢查；現在它只能抓到「300 ms 內重生的行程」。
- **m1d:203／204（S12.9b／c）**：兩條都不看輪盤；GIF 那條是字面清單的算術（DEAD），WEBP 那條靠 `DeclareWebp` 硬寫 `false`（WEAK）。真正看輪盤的只有 m1c:225。
- **m1d:286（S12.12b）**：`settings probe result: ... jpgQuality=85 ...` 同時也是「根本沒讀那個檔」的輸出，分不出「壞檔退回預設」與「忽略檔案」。
- **m1d:183（S12.8d）**：兩個運算元都是這支腳本自己的字串常數；而且當 `Test-SummaryCarries` 解析不出東西（回 false，正是它想守的失效）時，它照樣印 PASS。

---

## 5. 結論

**m1d 的檔案層是真的回歸網，m1c 和 m1d 的進度／worker 層大半是戲。**

**強的部分（可以拿來當證據的）**：`tools/verify-m1d.ps1` 的檔案契約那一段 —— S12.1b／1c／1d（輸出存在、PNG 簽章、尺寸 320×200）、**S12.2**（25 個取樣點與來源逐通道比對、容差 2，這是全套最強的一條）、S12.3a-c（來源 hash／時間戳／大小不變）、S12.4a-c（`art (1).png` 編號、第一個輸出未被覆寫、沒有 `.tmp-*` 殘檔）、S12.13b／13c／13e／13d（批次三個輸出、per-file 編號鏈、雜湊比對無覆寫、無殘檔）、S12.10i（放手路徑真的產出自己的檔案）、以及 S12.11a-d（夾制與回報）、S12.12a（壞檔不崩）、S12.13a。這些看的是磁碟上的位元組、尺寸、像素、SHA256 與檔名，而且 `$Work` 開場就整個清掉（m1d:42）——它們沒有 stale-artifact 陷阱，會因為真實行為壞掉而轉紅。`tools/verify-m1c.ps1` 的真檔探測（S9.8／9.9／9.10／9.11，四條各自用腳本自建的位元組去逼出 `Ok`／`Corrupt`／`Unsupported`／`heifBrand=heic`）也是真的。

**弱的部分**：m1c 20 條裡有 5 條是死的 —— 四條「不變式」是 `CodecRegistry` 建構子裡兩個 `Where` 的白話重述（作者註解還寫著「各自都能失敗」，集合運算上不可能失敗），加上一條把自己剛殺掉的行程數成 0；另有 7 條是弱的，其中兩條（S9.6／S9.7）只是把硬寫常數印出來再 grep 回去。m1d 的 54 條裡有 2 條是死的、18 條是弱的，而其中最嚴重的是**整個進度區塊**：藥丸完全不畫時 S12.10b／c／d 照樣三綠（`0 == 0`、從沒顯示＝已經淡出），只有 d2／d3 掉進本專案自己定義為「不算失敗」的 INCONCLUSIVE。管線那一側（S12.10e／f）的兩條檢查也咬不住它們自己點名的失效：一個 grep 的字串在 deadlock 路徑上不會出現，一個讀跨回合累積的共享日誌。

合起來看：`tools/obsidian/00-index.md:90-91` 記載的 **m1c 20/20、m1d 54/54** 裡，有 **7 條是無論如何都不會紅的綠燈**，另有 25 條只會在最窄的條件下紅、或只是把我們自己的自述再講一次。這些綠燈不會只是「沒抓到問題」，它們讓那兩個數字看起來像證據 —— 這正是「一個永遠不會失敗的檢查比沒有檢查更糟」的實例。要修，優先順序是：把 S9.4a-d 換成對 `FormatCatalog.TargetsFor()` 的實際斷言、把 S12.10b／c／d 改成對「藥丸真的畫過一次」的觀察（例如要求 updates≥1 且 `visible=True` 至少出現一次）、把 S12.15c 改成「轉檔前先清掉輸出、轉檔後比對目錄清單」、把 S12.10f 改成「本次回合新寫入的行」（用已經量好的 `$before`）、把 S12.10e 改成斷言 `worker did not connect` 與 `timed out` 兩者皆未出現、並把 m1c 的 `Stop-AllSidecar` 縮限到本回合自己啟動的 PID（同時解掉 §3.3 的並行不相容）。
