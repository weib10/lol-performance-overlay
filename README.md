# LoL 即時表現 Overlay

Windows 10／11 x64 的低干擾桌面 Overlay。程式唯讀取得同一台電腦上由 League Client／遊戲提供的資料，將「這一場目前的相對表現」整理成圓點、精簡資訊條或十人面板。

這不是 Riot Games 官方程式，也不代表 Riot Games 的認可。

## 目前交付狀態

Repository 目前產生的是**未簽章候選成品**，不是穩定 Release。Linux 能驗證跨平台邏輯、Windows cross-publish、掃描與 ZIP 契約；SmartScreen、WPF 焦點、真實拖曳手感、DPI／多螢幕、系統匣與完整 LoL 對局仍須在 Windows 10／11 真機驗收。

目前候選版本：`1.1.0`。這個數字由 [`Directory.Build.props`](Directory.Build.props) 控制；PackageBuilder 會拒絕 README、朋友 HTML、Windows manifest 或 EXE metadata 不一致的產物。

執行 `scripts/package.sh`（Linux）或 `scripts/package.ps1`（Windows）後，候選 ZIP 位於：

```text
outputs/LoL即時表現Overlay.zip
```

ZIP 解壓後只包含：

- `LoL即時表現Overlay.exe`
- `先看這裡.html`

朋友不需要 .NET SDK、命令列、管理員權限、開發者憑證或額外 App。請先閱讀 HTML，再執行 EXE。實際 EXE 與 ZIP SHA-256 由打包工具寫入 `outputs/SHA256SUMS.txt` 與 `outputs/package-manifest.json`；HTML 內的 EXE 雜湊也由同一次打包自動注入，不接受手動複製。

## 安全與隱私

- 不要求 Riot 帳號、密碼或驗證碼。
- 不注入遊戲、不讀取遊戲記憶體、不模擬輸入，也不修改 LoL 檔案。
- 本場玩家資料只在記憶體內用於即時計分，不寫成玩家歷史或上傳。
- 不還原選角階段原本匿名的玩家。
- League Client 的臨時本機通行資訊只存在記憶體。
- 程式對外只從 `https://ddragon.leagueoflegends.com` 下載 Riot 公開英雄／物品素材；OP.GG 只提供使用者主動開啟的普通瀏覽器連結，程式不抓取頁面。
- 顯示設定與公開素材快取位於 `%LOCALAPPDATA%\LolPerformanceOverlay`。
- 歷史資料 live provider 目前未啟用；正式 package 不會用 Synthetic provider 冒充真人資料，核心 Overlay 在歷史資料 unavailable／policy-disabled 時仍可使用。

未簽章 EXE 可能觸發 SmartScreen。第三方工具無法誠實宣稱零風險；不接受這項不確定性的人不應執行。完整邊界與回報方式見 [`SECURITY.md`](SECURITY.md)。

## 操作

- `Dot`：圓點任何位置都能按住拖曳；沒有超過移動門檻的短按才切換模式，拖曳不會同時觸發 click。
- `Compact`／`Expanded`：按鈕以外的大部分背景可拖曳。
- 「鎖定位置」開啟後，整個 Overlay 不接收滑鼠，避免攔截遊戲操作；以快捷鍵或系統匣切換顯示、解除鎖定或重設到可見螢幕範圍。
- 預設快捷鍵是 `Ctrl+Shift+O`；若被占用，設定會顯示實際可用組合。

分數只描述目前這一場的相對表現，不是官方牌位、長期實力、隱藏 MMR／ELO 或勝率預測。歷史近期狀態若未來啟用，必須與本場分數分開，並顯示來源、queue／mode、樣本數、取得時間和信心。

## 一鍵建置、測試與打包

需求：`global.json` 鎖定的官方 .NET SDK 8.0.423。Linux 可 cross-publish Windows x64 並 cross-build Windows-only tests；Windows CI 會實際執行這些 Windows-only tests。

Linux：

```bash
./scripts/package.sh
```

Windows PowerShell：

```powershell
./scripts/package.ps1
```

兩個入口都呼叫 [`eng/PackageBuilder`](eng/PackageBuilder)，依序完成 restore、可在該作業系統執行的測試、win-x64 自包含壓縮單檔 publish、離線 HTML、SHA-256、秘密／本機路徑／PDB／資料邊界／網域掃描，以及兩檔 ZIP 驗證。版本唯一來源是 [`Directory.Build.props`](Directory.Build.props)，檔名、allowlist 與掃描規則集中在 [`eng/package-config.json`](eng/package-config.json)。

GitHub Actions 的 [`windows-package.yml`](.github/workflows/windows-package.yml) 在 `windows-latest` 使用同一個 `scripts/package.ps1`，成功後上傳候選 ZIP、manifest 與 hashes；workflow 不會自動合併或建立正式 Release。

## 資料流

```text
League Client／遊戲
  → 本機唯讀資料
  → 背景解析、評分、去重與節流
  → OverlaySnapshot（不含原始 KDA、等級、CS、死亡時間或物品價值）
  → WPF 只更新可見變化

Riot 靜態素材服務
  → 英雄／物品資料與圖示
  → 本機快取
```

產品門檻、量測基準與仍待真機驗收項目見 [`docs/PRODUCT_HANDOFF.md`](docs/PRODUCT_HANDOFF.md)；歷史資料政策與來源取捨見 [`docs/HISTORICAL_DATA_RESEARCH.md`](docs/HISTORICAL_DATA_RESEARCH.md)。

## 免責聲明

LoL 即時表現 Overlay is not endorsed by Riot Games and does not reflect the views or opinions of Riot Games or anyone officially involved in producing or managing Riot Games properties. Riot Games and all associated properties are trademarks or registered trademarks of Riot Games, Inc.
