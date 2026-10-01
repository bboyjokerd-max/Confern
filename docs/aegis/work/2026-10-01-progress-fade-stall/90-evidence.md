# 證據：進度那一行卡住不淡出（2026-10-01）

**一句話**：進度的淡出時鐘靠「從未完變成已完成」的狀態邊緣啟動，而它的初始狀態
`ConversionSnapshot.Idle` 的 `Finished` 本來就是 `true`；只要第一次週期回呼發生在轉檔
**已經結束之後**，那個邊緣永遠不出現，那一行就永遠留在畫面上（規格 §4.4 紅線）。

## 1. 症狀與最初的誤判

`tools/verify-m1d.ps1` 的 `S12.10d progress faded out on its own` 轉紅：

```
10:19:55.373 conversion finished: finished=True ok=1 failed=0
10:19:55.373 progress instrument: updates=0 frames=0 visible=False
10:19:55.377 progress: ✓ 1 ... finished=True alpha=255 (update #1)
10:20:07.380 progress faded out on its own: False visible=True     ← 撐滿 12 秒逾時
```

12.003 秒內只出現一次進度更新，而同一場工作的第二批卻正常淡出（`hiding after 2357 ms`）。
當時的推論是「週期回呼的計時器停了」，並懷疑 **`SetTimer`/`WM_TIMER` 被餓死**
（`WM_TIMER` 是低優先權的合成訊息，佇列一忙就不被產生）。於是把驅動來源換成
「執行緒計時器 + `PostMessage`」。

**這個推論是錯的**，而且它有傳染性：換掉計時器之後同一個症狀又出現了一次
（同一次 43 項回歸裡 `S12.10d` 仍然轉紅）。錯在把「日誌安靜」當成「回呼沒跑」——
`Tick()` 只有在內容改變、或進入淡出時才會寫日誌，所以卡住時它本來就會安靜。

## 2. 決定性證據（加 `--progress-trace`，連跑到重現）

第 6 次重現（同一顆 exe、同一份輸入），第 1 段的每一次回呼都在：

```
progress tick #1   started=True shown=False finished=True  elapsed=-  timerAlive=True
progress tick #2   started=True shown=True  finished=True  elapsed=-  timerAlive=True
progress tick #6   started=True shown=True  finished=True  elapsed=-  timerAlive=True
progress tick #143 started=True shown=True  finished=True  elapsed=-  timerAlive=True
```

- `timerAlive=True`、共 **183 次**回呼 → **回呼從來沒停**，計時器理論當場被否證。
- `elapsed=-`（也就是 `_finishedAt == null`）**每一次都是** → 淡出時鐘從來沒啟動。
- `finished=True` 在第 1 次回呼時就已經是 `true`。

## 3. 根因（canonical owner：`ProgressPresenter.Update`）

`ConversionClient.cs`：

```csharp
public static readonly ConversionSnapshot Idle =
    new(0, 0, null, true, 0, 0, Array.Empty<string>(), null);   // Finished = true
```

`ProgressPresenter.Start()` 把 `_last = ConversionSnapshot.Idle`，而舊的 `Update()` 用

```csharp
bool justFinished = snapshot.Finished && !_last.Finished;
if (justFinished) _finishedAt = Stopwatch.StartNew();
```

判斷「剛剛完成」。因為 `Idle.Finished == true`，這個邊緣**只在一開始先看到「進行中」的
畫面時才會出現**：

| 第一個回呼的時機 | `_last` 變成 | 邊緣出現？ | 淡出 |
|---|---|---|---|
| 轉檔還在跑（多數情況） | `0/1 finished=False` → 之後 `✓ 1` | 是 | 正常 |
| 轉檔已經結束 | `✓ 1 finished=True` | **否** | **永遠不淡出** |

第二種情況不是罕見時序：`CommitWheel` → `ConversionClient.Start` 會為了等 worker
連線而**擋住 UI 執行緒約 300 ms**，而 worker 的實際工作只有 3–8 ms，所以
「回呼來得太晚」相當常見（實測約 6 次出現 1 次）。

## 4. 修法（最小、在正確的擁有者）

淡出時鐘改由**「第一次畫出已完成的那一幀」**直接啟動，不再推論狀態邊緣：

```csharp
if (snapshot.Finished && _finishedAt == null)
    _finishedAt = System.Diagnostics.Stopwatch.StartNew();
```

與時序無關，`Idle` 的語意不需要改（它本來就該是「已完成／沒事做」）。
沒有新增 fallback、沒有第二個擁有者：`ProgressPresenter` 本來就是淡出時鐘的擁有者。

## 5. 驗證

- 修好後同一顆 exe **連跑 12 次**：12/12 淡出，`ticks=[38,32]`、`updates=[5–6,6–7]`。
- 為了讓這個時序**每次都被走到**（而不是 1/6 機率），`--wheel-payload` 診斷在第一段
  轉檔之後刻意 `Thread.Sleep(600)`：第一段的 `ticks` 因此固定是 38，代表那條路徑
  每次都被覆蓋。
- 新增兩項自動斷言（`ticks` 是驅動真的活著的證據，`updates` 是同一行原地重畫的證據）：

| 檢查 | 內容 |
|---|---|
| `S12.10d2` | 兩段淡出的 `ticks >= 10`（2.4 s ÷ 80 ms ≈ 30；抓「只跳一兩次」的塌陷） |
| `S12.10d3` | 兩段淡出的 `updates >= 3`（真的走了 alpha 階梯，不是直接消失） |

- 回歸：**M1a 38/38（S4 仍 INCONCLUSIVE＝需真人拖曳）、M1b 50/50、M1c 20/20、M1d 43/43**，建置 0 警告 0 錯誤。

## 6. 保留與退役

- **保留**：`VisualLayer` 的週期回呼改用執行緒計時器 + `PostMessage`（`WM_APP_TICK`）。
  它**不是**這個 bug 的修法（已在前述用 `timerAlive=True`／183 次回呼否證），留下來的
  理由是它讓**回呼次數變成可斷言的訊號**（`S12.10d2`），真的被餓死時看得出來；
  程式碼註解與本文件都明講它是防禦性選擇。
- **退役**：`TickTimerId` 常數與 `WM_TIMER` 分支已移除；`Native.WM_TIMER` 只留作對照。
- **診斷債**：`--progress-trace`（每次回呼的決策）與 `ticks=` 計數保留，是這次能一次
  定位的原因；`--progress-trace` 應與其他診斷旗標一起記進文件。

## 7. 教訓（可跨專案重用）

1. **日誌安靜 ≠ 元件沒跑。** 推論「回呼停了」卻沒有直接證據，導致一次錯誤的根因宣告
   與一次不必要的修復。要斷言「有沒有跑」，就要有**計數器**，不是靠症狀推測。
2. **不要用「狀態邊緣」偵測事件，當初始狀態落在事件那一側時。** `Idle` 這種
   sentinel 的 `Finished=true` 讓邊緣判定靜默失效；直接條件（`finished && 時鐘未啟動`）
   與時序無關。
3. **時序相依的 bug 要在腳本裡被強制走到。** 靠機率（1/6）漏掉之後，用一段刻意的
   延遲把該時序固定下來，才是真的修完。
