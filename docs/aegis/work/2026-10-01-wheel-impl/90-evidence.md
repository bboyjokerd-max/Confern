# 90-evidence — 輪盤視覺方向 D 落地（計畫 §5.3）

所有數字都是**量測出來的**；沒量到的寫「未驗」，不用描述代替。

---

## 1. 建置

```
dotnet build src\Sidecar -c Release -v minimal
建置成功。 0 個警告 0 個錯誤
```

標準輸出與臨時輸出的執行檔雜湊相同（確認驗收跑的是同一顆二進位）：

```
src\Sidecar\bin\Release\net8.0-windows\Sidecar.exe  8B65C778…9AF1DE32A
%TEMP%\tangerine-render\Sidecar.exe                  8B65C778…9AF1DE32A
```

## 2. 畫面驗證：`check-frame.ps1` ⇒ 16/16 全過（exit 0）

輸入：`--wheel-demo 900 400 --wheel-selected 1 --wheel-targets PNG,JPG,WEBP,BMP`
（4 扇區、選中第 2 格＝JPG），深／淺合成桌布各產出「扁平化」與「保留 alpha」兩張。

| 檢查 | 量測值 |
|---|---|
| 點陣圖尺寸 | 304×349（深／淺皆同） |
| 玻璃本體 alpha | r≤106 共 35265 px，**0 px 非 255** |
| 選中扇區外擴處 alpha | r≤111 共 756 px，**0 px 非 255** |
| r>160 純透明 | 8355 px，**0 px 非 0** |
| 柔暈是純圖層 | 同一 alpha（＝同一半徑）下**預乘** RGB 最大落差 **2**（最差 alpha=124，13 px） |
| 柔暈隨半徑衰減 | 平均 alpha r118=103.5 → r130=46.2 → r145=25.5 |
| 薄罩生效（深底） | 圓盤內 rgb=(58,80,94) luma=76.5（薄罩被抵銷會是 ~193） |
| 薄罩生效（淺底） | 圓盤內 rgb=(99,117,129) luma=114.1（薄罩被抵銷會是 ~239） |
| **薄罩壓縮率** | 深淺兩底圓盤內亮度差 **37.6**；理論 0.2×186 = **37**，無薄罩會是 ~186 |
| 琥珀描邊 | r114 `#FFC24D`、r115 `#FFC24D` |
| 外緣薄荷髮絲線 | r109 `#608084` |
| 四個標籤 | s0=182 s1=255 s2=182 s3=175（選中最亮，未選中為 .42 透明） |
| 鍵帽藥丸 | 區域 max=246、1594/5550 px >200 |
| 說明文字 | min=18（暗色描邊）max=242（白字身） |
| 檔名預覽列 | min=16 max=245 |
| 首次顯示耗時 | 深色 73.9 ms、淺色 79.6 ms（規格 §9.3 預算 100 ms；此數字含**合成桌布繪製**，真實抓螢幕只需 ~16.7 ms ＋ 模糊） |

### 兩個「檢查腳本自己的錯」記在這裡，避免下次重踩

1. **`GetPixel` 會反預乘**：低 alpha 像素讀回來的 RGB 是量化雜訊（alpha=6 可以讀成 `#BFFFFF`）。
   要比顏色必須比**預乘值**，或限制 alpha 門檻。第一版因此誤報「柔暈不是純圖層」。
2. **同一半徑的像素取樣會有 ±0.7px 誤差**，而柔暈 alpha 每 px 變化約 7 ⇒ 沿「整數半徑圓環」比較
   會看到假的落差。改成「同一個 alpha 值＝同一半徑」分組比較才成立。

## 3. 回歸：`verify-m1a/m1b/m1c/m1d`（最終二進位）

| 跑者 | 範圍 | 結果 |
|---|---|---|
| `verify-m1a` | S0,S1,S2,S3,S4,S5 | **39/40**（唯一紅的是 S4.2，見 §6；不是本次改動） |
| `verify-m1b` | S6,S7 | **50/50** |
| `verify-m1c` | S9 | **20/20** |
| `verify-m1d` | S12 | **43/43** |

`verify-m1b` 的 S7.2（幾何自測）、S7.13（3 次收放循環）、S7.32（hover 自測）都吃新的幾何期望值，
且是在**保留 alpha 的**畫面契約下通過。需要人手的 S4a/S4b（真實 Explorer 拖曳）與 S8
（真實拖曳 → 輪盤 ≤100 ms）**未跑**。

### 真實抓螢幕路徑其實有被跑到

驗收期間的證據檔（`sidecar-b\m1b-s6.log` 等）顯示走的是真桌面，不是合成桌布：

```
17:02:04.114 glass captured in 25.7 ms (one-off per drag; every repaint reuses this surface)
17:02:04.114 glass ready source=screen (876,376) 304x349 meanLuma=51 glow=mint(dark backdrop)
17:02:04.166 wheel shown cursor=(900,400) origin=(876,376) size=304x349
```

抓螢幕 22.2–33.4 ms（＝一個 vblank 16.7 ms ＋ 模糊與點陣圖往返），**抓完到顯示 52 ms**，都在 100 ms 預算內。
但這仍**不等於**「真實桌布上的外觀已驗」——那要看 §5 第 1 點。

## 4. `ProgressPresenter` 對齊（同一套視覺語言）

改了三件有實質後果的事，並逐項量測：

| 改動 | 為什麼 | 量測證據（`--progress-dump` 的 5 張 PNG） |
|---|---|---|
| 字級 `Point` → `Pixel` | 96 DPI 下 15pt ＝ 20px（比輪盤的 17px 膠囊字還大），且會隨 DPI 縮放——150 % 時同一張 54px 卡片裝不下 | 白字字身 **11 px**（15px Segoe UI Bold 的實際字身；舊的會是 ~15px） |
| 圓角 12px → 整顆藥丸（半高 26） | 與方向 D 的膠囊／鍵帽／預覽列同一種形狀語言 | 距頂 3px 處左緣內縮 **16 px**（r=26 的理論值正好 16；舊 r=12 會是 ~5） |
| 錯誤紅改共用 `WheelDesign.NoticeFill` | 同一個產品不該有兩種紅（原本兩處各寫一份字面值） | 藥丸底色 **(150,39,39)** ＝ NoticeFill(150,40,40) |

## 5. 未驗（誠實清單）

1. **真實桌布上的外觀**未驗：抓螢幕的**機制**跑過了（見 §3），但「在真實壁紙＋視窗上好不好看」沒有看過。
   風險：抓螢幕失敗時退回純色玻璃的外觀、以及實際桌布下的對比。
2. 中心鏤空的**真實來源縮圖**未接（介面已留 `Show(..., Bitmap? sourceThumbnail)`，目前畫佔位圖）。
3. 需要人手的驗收未跑：`verify-m1a` 的 S4a/S4b（真實 Explorer 拖曳）、`verify-m1b` 的 S8。
4. 視覺判斷（VLM）僅作旁證：本專案已多次記錄它會自相矛盾，一律以像素量測為準。

## 6. 附帶發現：`verify-m1a` 的 S4.2 自 2026-09-30 15:51 起就是紅的（**不是**本次改動）

**現象**：補上 `-DragSim` 讓 S4 真的跑之後，7 項裡 S4.2 失敗：

```
PASS  S4.1 hit layer raised for the drop test
FAIL  S4.2 drop intercepted (WM_DROPFILES delivered)
PASS  S4.3 the received path is the sample file
PASS  S4.4 hidden immediately after the drop
PASS  S4.5 the app is still alive after the drop
PASS  S4.6 source file untouched: same SHA256   ← 產品行為是對的
PASS  S4.7 source file untouched: same mtime
```

**排除本次改動**：S4 用 `--raise` 啟動（純命中層，與輪盤繪製無關），且 S4 的六項行為斷言全過。

**對照實驗**（把 OLE 接線前的那顆 commit 建起來跑同一支 S4，跑完即移除 worktree）：

| 版本 | 放手時的證據行 |
|---|---|
| `125070a`（`RegisterDragDrop` 之前） | `*** DROP RECEIVED on hit#0: 1 file(s) ***`（WM_DROPFILES）→ **S4 7/7 通過** |
| 現在（`159952a` 起的 OLE 接線） | 只有 `*** OLE DROP RECEIVED (IDropTarget): hit#0 files=1 ***` → S4.2 紅 |

**結論**：一旦註冊 `IDropTarget`，合成 OLE 拖放就不會再送 `WM_DROPFILES`。
`HitLayer` 原本的註解（「後者仍是放手的檔案清單權威」）與 `OleDropObserved` 的說明
（「交叉確認用」）**與事實相反**，已在原始碼就地更正並註明量測日期（只改註解，行為未動）。

**為什麼直到今天才發現**：`159952a` 之後的每一次回歸都是以「S4 INCONCLUSIVE（缺 `-DragSim`）」
帶過（本日稍早的紀錄也是這樣寫的），於是紅燈被 inconclusive 蓋住。

**待決**（不屬於本次 UI 切片，未擅自改）：S4.2 該改成接受 OLE 那行（並註明真實 shell 拖曳的
`WM_DROPFILES` 由人工 S4m 覆蓋），或先查真實 Explorer 拖曳是否仍會補上 `WM_DROPFILES`。

## 7. 過程中的環境發現（不影響程式，但影響可重現性）

- 有一顆 **11:36 啟動的孤立 Sidecar 行程**（父行程已消失，跑的是舊二進位）同時佔住
  單一實例 mutex 與 `bin\Release\Sidecar.exe`。它會讓**所有**驗收與診斷靜默失敗
  （診斷會印 `Sidecar is already running.` 後 exit 3）。已終止；日後驗收前應先確認沒有殘留行程。
- Sidecar 是 **GUI 子系統**執行檔：PowerShell 的 `&` **不會等待**、也收不到 stdout。
  自動化必須用 `Start-Process -Wait -PassThru` 並讀 `-log` 檔案，否則會出現「看似跑完其實還在跑」的競態。
- 本回合為了做 §6 的對照實驗，曾用 `git worktree add --detach` 建一顆臨時工作區後**已移除**
  （`git worktree list` 只剩主工作區）；主工作區、分支與 HEAD 全程未動。

