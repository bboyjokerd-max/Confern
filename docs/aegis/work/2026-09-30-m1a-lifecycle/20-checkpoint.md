# M1a 拖曳生命週期與五層硬保險 - Checkpoint

## Current Checkpoint

- Current todo: T5 驗收跑者 verify-m1a.ps1 + R4 有界 spike
- Active slice: T5
- Completed todos:
- T4 生命週期與五層硬保險（ReleaseGuard 單一收層權威；合成手勢驗證 13/13）
- Evidence refs:
- src/Sidecar/Interaction/ReleaseGuard.cs; %TEMP%/sidecar-verify/t4.log
- Blocked on: none
- Next step: 把 T4 的合成驗證正式化為 tools/verify-m1a.ps1（S1-S4）

## Recent Checkpoint History

## Checkpoint Update

- Current todo: T4 生命週期與五層硬保險（Hooks/DragDetector/ReleaseGuard）
- Active slice: T4
- Completed todos:
- T1 產品骨架; T2 螢幕幾何; T3 命中層（多螢幕、預先建立隱藏、exstyle 斷言、單一實例）
- Evidence refs:
- src/Sidecar/Interaction/HitLayer.cs; src/Sidecar/Interaction/MonitorLayout.cs
- Blocked on: none
- Next step: 移植 Hooks/DragDetector 並新增 ReleaseGuard（唯一收層權威）
