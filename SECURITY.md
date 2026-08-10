# Security

## 候選成品定位

目前 package 是未簽章候選成品，不是安全廠商、Riot Games 或任何第三方的安全認證。只使用可信來源提供的完整 ZIP，先閱讀 `先看這裡.html`，並核對 `outputs/SHA256SUMS.txt` 或 package manifest 中由同一次打包產生的 SHA-256。

Linux cross-build 成功只能證明程式與 Windows targeting toolchain 相容，不能證明 SmartScreen、WPF 焦點、拖曳、系統匣、Windows DPI／多螢幕或真實 LoL 互動已通過。

## 程式行為與資料邊界

- `127.0.0.1`／`localhost`：唯讀取得同一台電腦上的 League Client 階段、選角與本場即時資料。
- `https://ddragon.leagueoflegends.com`：下載 Riot 公開的英雄／物品靜態資料與圖示。
- League Client 的自簽 TLS 憑證只在 request URI 仍是精確 loopback host 時略過驗證；這組 client 禁止 redirect，不能拿來連任意遠端 HTTPS host。Data Dragon client 也禁止 redirect，所有 runtime URI 都先經 allowlist。
- OP.GG 只可由使用者主動交給預設瀏覽器開啟；程式不自動抓取、解析頁面或讀取 browser cookie／session。
- 沒有玩家資料上傳、遙測、廣告或本工具自己的遠端服務。
- League Client 臨時本機通行資訊不寫入硬碟或 log。
- 不注入遊戲、不讀取遊戲記憶體、不模擬輸入、不修改遊戲檔案，也不還原匿名玩家。
- Overlay 安全資料邊界不包含原始 KDA、等級、CS、死亡時間或物品價值。
- live 歷史 provider 未啟用時會顯示 unavailable／policy-disabled；Synthetic provider 只供測試與 Replay，不得在 package 中冒充真人資料。

## 打包防線

Repository 內的共用 PackageBuilder 會在建立 ZIP 前失敗攔截：

- Riot key、私鑰、寫死的 password／secret／cookie／session token 與實際本機通行 token。
- 真實人物樣式的 fixture Riot ID、開發者本機絕對路徑與 PDB 路徑。
- 原始對局欄位進入 Overlay snapshot／view model。
- 程式碼或朋友文件出現不在 allowlist 的 URL host。
- shipping source 出現遊戲 process memory read/write、remote thread／DLL injection、Windows/game hook、DirectX injection、driver I/O 或自動鍵鼠輸入能力。
- HTML 從遠端載入圖片、script、stylesheet、frame 或媒體。
- ZIP 不是恰好只含 EXE 與 HTML，或 publish 不是單一 EXE。

掃描降低誤交付風險，但不能替代 code review、Windows 真機驗收、惡意程式分析或可信程式碼簽章。

## 本機保存與移除

設定與 Riot 公開素材快取位於 `%LOCALAPPDATA%\LolPerformanceOverlay`。使用者從系統匣關閉 Windows 自動啟動、結束程式、刪除 EXE／HTML／ZIP，再刪除該資料夾，即可移除本工具資料。程式不安裝系統服務或驅動程式。

## 回報安全問題

請私下聯絡 repository 擁有者，說明版本、重現步驟與影響。不要在 Issue、截圖、log 或附件中貼出帳號密碼、驗證碼、League Client 臨時通行資訊、玩家真實 ID、cookie、session 或任何其他憑證。
