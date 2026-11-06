# GitHub 與發版：你只需要做一次，之後一行指令

> 這份文件是給**你**看的，不是給程式看的。目標：讓 Confern 有兩個固定網址——
> 一個放「有沒有新版本」那幾行 JSON，一個放兩個安裝檔——而你不必學會 git。
>
> 寫於 2026-11-04。當時 repo 還完全在本機（沒有 remote），`gh` 沒有安裝。

## 一、GitHub 在這件事裡到底負責什麼

不是「上傳程式碼」，是**兩個固定地址**：

| 用途 | 網址長什麼樣 | 誰會來讀 |
|---|---|---|
| 更新清單 | `https://raw.githubusercontent.com/<你>/confern/main/update-paid.json` | 每一台裝了付費版的電腦（24 小時一次） |
| 安裝檔下載 | `https://github.com/<你>/confern/releases/download/v0.2.0/Confern-Setup-0.2.0.exe` | 點「有新版本」的人 |

為什麼不能用你自己的電腦當那個網址：它會關機、會換 IP、在家用網路後面外面連不進來。
而更新檢查失敗**是靜默的**（刻意的設計），所以你根本不會發現沒人在更新。

## 二、你要做的事（一次，大約 5 分鐘）

1. **註冊帳號**：<https://github.com/signup>，用你現有的 email 就好，免費方案足夠。
2. **建立一個空 repo**：登入後右上角 `+` → `New repository`
   - Repository name：`confern`（小寫）
   - 選 **Public**（我們的授權是 MIT、免費版要開源，公開剛好一致）
   - **不要**勾 Add a README / .gitignore / license（我們本機已經有了，勾了會撞）
   - 按 `Create repository`
3. 把那個頁面給我看（上面會寫 `https://github.com/<你的帳號>/confern.git`），
   我就能把本機的 repo 接上去。
4. 第一次 `git push` 時會**跳出瀏覽器**要你登入並按一次 `Authorize`。
   那是 Git for Windows 內建的 Git Credential Manager，登入一次之後就記住了。

> 你不需要記任何 git 指令，也不需要碰命令列。這四步做完，剩下的都是我跑。

## 三、之後每次發版的樣子

你只要說「**發版 0.2.0，改了 XXX**」，我跑：

```
powershell -File tools\release.ps1 -Notes "改了 XXX"
```

它會做這些事（順序固定，任一步失敗就停）：

1. 讀 `tools\release.config.json`（你的帳號與 repo 名稱，只填一次）
2. 建置**付費版安裝檔**（把 `update-paid.json` 的網址種進使用者的設定檔）
3. 建置**免費版安裝檔**（種 `update-free.json` 的網址）
4. 算兩個 `.exe` 的大小與 sha256
5. 產生 `dist\release\update-paid.json` 與 `update-free.json`
6. 產生 `RELEASE-NOTES.md` 與 `SHA256SUMS.txt`
7. 把「接下來要下的指令」印出來（檔名與雜湊都已經填好）

然後是兩個動作：

- **我把兩個 manifest 提交並推到 `main`**：`git push origin main`
- **安裝檔要掛到 release 上**：有兩種方式
  - 裝了 `gh`：`gh release create v0.2.0 dist\release\*.exe --notes-file dist\release\RELEASE-NOTES.md`
  - 沒裝：開 `https://github.com/<你>/confern/releases/new`，選 tag `v0.2.0`，
    把 `dist\release\` 裡的兩個 `.exe` 拖進「Attach binaries」，貼上 `RELEASE-NOTES.md` 的內容

### 為什麼清單要**先**推上去，安裝檔才能掛

manifest 裡的 `url` 是**可以事先算出來的**（`releases/download/v0.2.0/<檔名>`），但那個網址要等
release 建好才會通。順序反過來（先掛檔案、後推清單）會有一個空窗期：使用者在那幾分鐘內檢查會
看到「有新版本」卻點不到東西。先推清單的話，最壞情況是「還沒有人收到通知」——安靜地晚一點，
而不是壞掉。

## 四、兩個版本、兩份清單

`update-paid.json` 與 `update-free.json` 是**兩份獨立的檔案**，各自指到自己那一版的安裝檔。
理由很直白：**免費版永遠不該被指到付費安裝檔**。一份清單只服務一種版本，就不會發生
「你點了下載，拿到一個要你付錢的東西」。

| 檔案 | 誰會讀 | 內容差異 |
|---|---|---|
| `update-paid.json` | 付費版（有去背） | 指向 `Confern-Setup-*.exe` |
| `update-free.json` | 免費開源版 | 指向 `Confern-Free-Setup-*.exe` |

## 五、常見狀況

**「我改了 repo 名字怎麼辦」**
改 `tools/release.config.json` 的 `repo`，重跑一次 `release.ps1`。網址是編在**安裝檔**裡的
（種進使用者的設定檔），所以舊版的使用者仍然會去問舊網址——舊網址如果 404，他們的檢查會
靜默失敗（不會壞，只是收不到通知）。真的搬家就留一個轉址，或發一版新的把網址換過去。

**「使用者已經有 settings.json，會被蓋掉嗎」**
不會。種子檔的旗標是 `onlyifdoesntexist`（只在**第一次**安裝時寫），而它自己也只帶
`UpdateUrl` 一個欄位，其餘一律用程式的預設值。升級不會動到使用者調過的觸發鍵或畫質。

**「我不想讓它檢查更新」**
把 `%LOCALAPPDATA%\Confern\settings.json` 的 `UpdateUrl` 清空，或 `CheckUpdates` 設 `false`。
兩個都做的話它就是完全離線的程式。

**「release 頁面一定要公開嗎」**
安裝檔要能被下載，就得公開（或至少讓下載連結可用）。這與授權一致：收費靠的是金鑰，不是藏檔案。

## 六、真的發過一次之後補的（v0.1.1，2026-11-06）

上面那些是設計；以下是實際按下去之後才知道的。**目前線上的版本是 `v0.1.1`**。

### 完整順序（照這個貼就好，四步）

```powershell
# 0. 先殺掉常駐的 Confern，否則安裝演練會被自己的托盤行程干擾
Get-Process Confern -EA SilentlyContinue | Stop-Process -Force

# 1. 改版本：src\Sidecar\Sidecar.csproj 的 <Version>0.1.1</Version>
#    說明寫進 dist\release\notes.txt（UTF-8），不要用 -Notes 傳中文（PowerShell 5.1 會用 ANSI 碼頁傳參數）
powershell -File tools\release.ps1 -Version 0.1.1

# 2. 清單先推（複製 dist\release\update-*.json 到 repo 根目錄）
git add -A; git commit -m "release: v0.1.1"
git tag v0.1.1
git push origin main
git push origin refs/tags/v0.1.1        # ← 這一行不能省，見下面第 1 點

# 3. 掛安裝檔
gh release create v0.1.1 dist\release\Confern-Setup-0.1.1.exe `
   dist\release\Confern-Free-Setup-0.1.1.exe `
   --title "Confern 0.1.1" --notes-file dist\release\RELEASE-NOTES.md
```

### 兩個實際踩到的坑

**1. 輕量標籤不會被 `--follow-tags` 推上去。**
`git tag v0.1.1` 建的是**輕量**標籤；`git push --follow-tags` 只推 annotated 標籤，所以遠端
只有 `main` 沒有標籤，接著 `gh release create` 找不到 tag 就失敗。要嘛用 `git tag -a`，
要嘛明講 `git push origin refs/tags/v0.1.1`。

**2. 驗收套件裡不要硬寫版本號。**
`tools\verify-cli.ps1` 的 S7.3 原本寫死「`--version` 印出 `0.1.0`」，版本一升就變紅——而且
**「升版本」與「產品真的壞掉」在總表上長得一模一樣**，很容易誤判成自己改壞了。現在改成從
`src\Sidecar\Sidecar.csproj` 讀 `<Version>` 當單一真相來源，訊息會同時印出實際值與期望值。

### 發完之後怎麼確認（三個都要看）

```powershell
# (a) 清單在網路上是新的
curl.exe -s https://raw.githubusercontent.com/bboyjokerd-max/Confern/main/update-paid.json

# (b) 安裝檔下載得到（HEAD 200，大小對得上清單的 size）
# (c) 產品自己說沒有更新了（state=current，latest 等於本機版本）
Confern.exe --check-updates --update-url https://raw.githubusercontent.com/bboyjokerd-max/Confern/main/update-paid.json
```

`--check-updates` 若在**開發機**上跑，會說「no url configured」——那是對的：網址是安裝檔
種進使用者設定檔的，開發機的 `settings.json` 沒有那兩行，所以要自己帶 `--update-url`。

