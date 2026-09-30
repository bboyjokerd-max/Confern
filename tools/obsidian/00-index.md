---
tags: [專案, Tangerine, Windows版, 索引, Sidecar]
專案: Tangerine Converter Windows
狀態: 規格已定案，待使用者審閱
建立: 2026-09-30
---

# Tangerine Converter Windows（工作代號 Sidecar）

把 macOS 的 **Tangerine** 檔案轉換器移植成 Windows 版。

> **目標**：拖著檔案、按一個鍵 → 格式輪盤在游標處出現 → 選了就轉，**輸出寫在原檔旁邊，原檔永不變動**。

## 筆記

- [[01 解構與移植路線]] — 標的產品解構、為什麼 Windows 不能照抄、路線 A／B／C／C+ 比較、引擎授權紅線、工具鏈實測
- [[02 設計規格 - 權威]] — 權威設計規格：互動契約（接收層狀態機）、檔案處理契約、錯誤處理、**驗收條件 A1–A14**、決策記錄 D1–D9、ADR 訊號
- [[03 實作計畫 - M0.5 生死線]] — 第一步的可執行計畫：原型 `M05`＋自動化驗證器 `DragSim`、8 項實驗矩陣、GO／NO-GO 判定規則
- [[04 證據與判定 - M0.5 生死線]] — **實驗結果：GO**。真實拖曳命中實錄、50 次收層循環、零侵入驗證、四個執行期缺陷、R1–R4 殘餘項
- [[05 M1 切片路線圖]] — M1（alpha）切成六片：生命週期／輪盤／編解碼／寫檔契約／Worker／設定；含依賴順序與五項待決

## 已定案決策

| # | 決策 | 內容 |
|---|------|------|
| D1 | 攔截層 | **C+**：拖曳觸發時升起全螢幕、topmost、`WS_EX_LAYERED` 且 **alpha=1/255** 的接收層 → 「任意位置放手」成立 |
| D2 | 路線 A | **完全不做**（不寫 COM 殼層擴充、不進 explorer 行程） |
| D3 | 保底 | C（浮動接收目標）僅在 C+ 原型失敗時啟用 |
| D4 | 觸發鍵 | 預設 **空白鍵**（可設定；不用 Shift——Explorer 拖曳時 Shift=移動，會誤搬檔案） |
| D5 | MVP 範圍 | **只做圖片**（JPG/PNG/BMP/TIFF/GIF/WebP）；影音、PDF、去背延後 |
| D6 | 發行與收費 | 暫緩，不阻塞實作 |
| D7 | HEIC | **僅輸入、不輸出**；不散布 HEVC 編解碼器，依賴系統的 HEIF 擴充 |
| D8 | 混合類別選取 | **拒絕**（不存在共同可用的操作集） |
| D9 | PDF → DOCX | **永久不做** |

## ⚠️ 生死線（M0.5，實作前必須先過）

先證明 **alpha=1/255 的 layered 視窗仍能被 OLE 拖放命中**（桌面／資料夾／第二螢幕 **10/10**）。
通過 → 進入規格 §4 實作；**任一失敗 → 退回浮動接收目標（路線 C），並重新協商「任意位置放手」這個需求。**

## 待拍板（尚未定案）

1. **HEIC 只能進不能出**（D7）——為法律而非技術讓步，需你確認。
2. **drop 未選格式時，要不要還原 Explorer 的移動／複製語意**（規格 §4.2.3）——不做的話，使用者在攔截期間放手會覺得「檔案卡住不動」。
3. **正式產品名稱**（現用工作代號 Sidecar）。

## 位置與同步

| | 路徑 |
|---|---|
| **權威版本（repo）** | `C:\Users\User\Desktop\專案\tangerine-win` |
| 本資料夾 | Obsidian vault 內 `DSH 4070\Tangerine Converter Windows`（**鏡像**） |

重新同步（改完 repo 後執行一次）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\User\Desktop\專案\tangerine-win\tools\sync-to-obsidian.ps1" -ProjectRoot "C:\Users\User\Desktop\專案\tangerine-win" -VaultRoot "C:\Users\User\Documents\Obsidian\Danny_Vault_01"
```

> [!warning] 鏡像筆記，請勿直接編輯
> `01`、`02` 兩篇由同步腳本產生，**直接編輯會在下次同步被覆蓋**。要改內容請改 repo，再重新同步。
