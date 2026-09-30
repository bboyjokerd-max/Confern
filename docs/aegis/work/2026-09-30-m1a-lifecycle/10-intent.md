# M1a 拖曳生命週期與五層硬保險 - Intent

## TaskIntentDraft

- Requested outcome: 把接收層從原型提升為可交付生命週期：多螢幕正確、五層硬保險齊備、觸發鍵可設定，且任何情況都不會困住使用者
- Goal: 把接收層從原型提升為可交付生命週期：多螢幕正確、五層硬保險齊備、觸發鍵可設定，且任何情況都不會困住使用者
- Success evidence:
- verify-m1a.ps1 S1-S4 PASS；A2/A6/A7/A13/A14 逐項證據；原型刪除後建置仍成功
- Stop condition: done=驗收全綠且原型已退休；needs-verification=第二螢幕/網路磁碟未測；scope-exceeded=需要碰使用者檔案或繪圖
- Non-goals:
- 輪盤 UI(M1b)、編解碼(M1c)、寫檔契約與放手語意(M1d)、行程邊界(M1e)、設定頁(M1f)
- Scope: src/Sidecar 產品骨架、MonitorLayout、HitLayer、DragDetector、ReleaseGuard、verify-m1a.ps1
- Change kinds:
- implementation
- Risk hints:
- 接收層收層失效會癱瘓桌面；多螢幕無法在本機驗證；R4 自動化已知不可用

## BaselineReadSetHint

- docs/aegis/specs/2026-09-30-sidecar-drag-convert-design.md
- docs/aegis/work/2026-09-30-m05-feasibility/90-evidence.md
- src/M05

## BaselineUsageDraft

- Required baseline refs:
- docs/aegis/specs/2026-09-30-sidecar-drag-convert-design.md
- docs/aegis/work/2026-09-30-m05-feasibility/90-evidence.md
- src/M05
- Acknowledged before plan:
- none
- Cited in plan:
- none
- Missing refs:
- docs/aegis/specs/2026-09-30-sidecar-drag-convert-design.md
- docs/aegis/work/2026-09-30-m05-feasibility/90-evidence.md
- src/M05
- Advisory decision: needs-baseline-readback

## ImpactStatementDraft

- Compatibility boundary: 不裝 shell 擴充、不寫 HKLM、非管理員、不碰使用者檔案
- Affected layers:
- Windows 視窗生命週期與拖放攔截
- Owners:
- ReleaseGuard=唯一收層權威; Native=唯一 P/Invoke 來源; HitLayer=視窗唯一擁有者; MonitorLayout=唯一座標來源
- Invariants:
- 被拖曳檔案唯讀；接收層只在 CAPTURING 可見；非觸發拖曳位元級等價
- Non-goals:
- 輪盤 UI(M1b)、編解碼(M1c)、寫檔契約與放手語意(M1d)、行程邊界(M1e)、設定頁(M1f)

These records are Method Pack drafts / hints, not authoritative runtime decisions.
