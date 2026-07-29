# LoL 即時表現 Overlay

Windows 10／11 x64 的低干擾朋友測試版。程式會在選角與遊戲中讀取同一台電腦上由 League Client／遊戲提供的資料，將「這一場目前的相對表現」整理成圓點、精簡資訊條或十人面板。

這不是 Riot Games 官方程式，也不代表 Riot Games 的認可。

## 下載測試版

從 [v1.0.1-test prerelease](https://github.com/weib10/lol-performance-overlay/releases/tag/v1.0.1-test) 下載：

- `LoL-Performance-Overlay-Friend-Test-v1.0.1.zip`
- 解壓後先開啟 `先看這裡.html`
- 再執行 `LoL即時表現Overlay.exe`

檔案核對：

- ZIP SHA-256：`34335F2ED45D6F134ACD84E81B724149D20AC0F76A0A02C82974729C9D84317B`
- EXE SHA-256：`A6102D70CD4E4710EDE2311491357C70FC937BB8766F800351C628F58CD5949E`

完整的非技術使用、安全與移除說明也收錄在 [`docs/先看這裡.html`](docs/先看這裡.html)。

## 安全與隱私設計

- 不要求 Riot 帳號、密碼或驗證碼。
- 不注入遊戲、不讀取遊戲記憶體、不模擬輸入，也不修改 LoL 檔案。
- 本場玩家資料只在記憶體內用於即時計分，不寫入玩家歷史或上傳。
- 不查歷史戰績，也不還原選角階段原本匿名的玩家。
- League Client 的臨時本機通行資訊只存在記憶體。
- 唯一的外部下載來源是 Riot 的 `https://ddragon.leagueoflegends.com`，用於英雄、物品名稱與圖示。
- 顯示設定與公開靜態素材快取位於 `%LOCALAPPDATA%\LolPerformanceOverlay`。
- 「登入 Windows 後常駐」預設關閉，只有使用者主動啟用時才加入目前使用者的啟動項目。

未簽章的個人測試 EXE 可能觸發 SmartScreen。這個專案不宣稱第三方工具零風險；不接受非官方工具風險的人不應執行。

## 顯示模式

- `Dot`：遊戲開始時預設縮成圓點；綠／灰／紅代表我方領先、接近或落後。
- `Compact`：顯示雙方平均、分差及目前最高／最低表現英雄。
- `Expanded`：顯示十名玩家的英雄、本場分數、狀態與信心。
- 預設快捷鍵：`Ctrl+Shift+O`；若被占用會改用 `Ctrl+Shift+F9`。

分數只描述目前這場的相對表現，不是牌位、長期實力或勝率預測。

## 建置與測試

需求：.NET 8 SDK、Windows x64。

```powershell
dotnet test tests/LolPerformanceOverlay.Tests/LolPerformanceOverlay.Tests.csproj -c Release -warnaserror

dotnet publish src/LolPerformanceOverlay/LolPerformanceOverlay.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true
```

目前測試涵蓋評分模板、百分位與信心、解析容錯、匿名選角、安全 Snapshot 邊界及快捷鍵解析。

## 資料流

```text
League Client／遊戲
  → 127.0.0.1 本機唯讀資料
  → 記憶體內計分與平滑
  → OverlaySnapshot（不含原始 KDA、等級、CS、死亡時間）
  → WPF Overlay

Riot 靜態素材服務
  → 英雄／物品資料與圖示
  → 本機快取
```

## 免責聲明

LoL 即時表現 Overlay is not endorsed by Riot Games and does not reflect the views or opinions of Riot Games or anyone officially involved in producing or managing Riot Games properties. Riot Games and all associated properties are trademarks or registered trademarks of Riot Games, Inc.
