# M1a 拖曳生命週期與五層硬保險 - Checkpoint

## Current Checkpoint

- Current todo: T6 已完成（證據歸檔、原型退休、路線圖與規格更正、Obsidian 同步）
- Active slice: M1a（完成）
- Completed todos:
- T5 驗收跑者 45/45；R4 spike 推翻 M0.5 結論
- Evidence refs:
- docs/aegis/work/2026-09-30-m1a-lifecycle/90-evidence.md; tools/verify-m1a.ps1
- Blocked on: none
- Next step: M1b：視覺層（輪盤）渲染；或先補 A7 人工手勢證據

## Recent Checkpoint History

## Checkpoint Update

- Current todo: T5 驗收跑者 verify-m1a.ps1 + R4 有界 spike
- Active slice: T5
- Completed todos:
- T4 生命週期與五層硬保險（ReleaseGuard 單一收層權威；合成手勢驗證 13/13）
- Evidence refs:
- src/Sidecar/Interaction/ReleaseGuard.cs; %TEMP%/sidecar-verify/t4.log
- Blocked on: none
- Next step: 把 T4 的合成驗證正式化為 tools/verify-m1a.ps1（S1-S4）
## Checkpoint Update

- Current todo: T4 生命週期與五層硬保險（Hooks/DragDetector/ReleaseGuard）
- Active slice: T4
- Completed todos:
- T1 產品骨架; T2 螢幕幾何; T3 命中層（多螢幕、預先建立隱藏、exstyle 斷言、單一實例）
- Evidence refs:
- src/Sidecar/Interaction/HitLayer.cs; src/Sidecar/Interaction/MonitorLayout.cs
- Blocked on: none
- Next step: 移植 Hooks/DragDetector 並新增 ReleaseGuard（唯一收層權威）
