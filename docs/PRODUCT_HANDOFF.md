# LoL 即時表現 Overlay：產品與 Codex 交接文件

更新日期：2026-08-10

## 1. 交接目的

這份文件交給下一個 Codex 使用。它不是單純的程式架構摘要，而是後續產品工作的基準：目前有什麼、使用者實際覺得哪裡難用、可能的根因、應該往哪裡走，以及什麼條件達成後才能稱為真正可用的正式版。

使用者目前沒有完整的介面設計答案，這不是阻塞條件。下一個 Codex 的責任是主動觀察、量測、提出替代方案、使用 Replay 驗證，再和真實使用結果迭代，而不是等待使用者列出每一個小問題。

## 2. 一句話產品目標

做一個幾乎不打擾 LoL、朋友下載後能直接理解和使用，同時分開呈現「本場即時表現」與「可解釋的歷史近期狀態／風格」的 Windows Overlay。

成功時應有以下感覺：

- 平常忘記它存在，需要時一秒看懂。
- 想移動、展開、縮小、找回或關閉時，不需要猜哪裡能點。
- 不會因為 Overlay 更新而讓遊戲或桌面感覺卡頓。
- 朋友不需要安裝 SDK、輸入帳密、看命令列或理解 Riot 本機介面。
- 下載包、說明、安全邊界與移除方式都清楚可信。

## 3. 目前狀態

- Repository：`https://github.com/weib10/lol-performance-overlay`
- 主要分支：`main`
- 現有公開基準：`v1.0.1-test` prerelease
- 平台：Windows 10／11 x64
- 技術：.NET 8、WPF、自包含單檔 EXE
- 顯示模式：Dot、Compact、Expanded
- 資料來源：League Client 本機資料、遊戲內 `127.0.0.1:2999`、Riot 靜態素材
- 歷史資料：尚未實作；官方 Riot API、OP.GG 公開資料與合成資料 adapter 均列入評估
- 現有自動測試：17 個測試案例
- 現有朋友包：EXE + 完全離線 HTML
- 現有 Release 不應直接重新命名為正式版；它是比較修正前後行為的基準。

目前已知還沒有足夠證據證明：真實長時間對局順暢、多螢幕與 DPI 完整、Client 重啟可靠、一般朋友能無協助完成所有操作，以及乾淨環境可重現打包。

## 4. 使用者實測後的核心回饋

使用者的結論不是「少幾個功能」，而是距離真正好用仍很遠：

1. 整體用起來太卡。
2. 視窗只能在左上角有文字的狹小區域拖動，拖曳入口不直覺。
3. 縮成 Dot 後完全不能拖動。
4. 存在很多類似的小摩擦；每一個單看都不大，累積後讓產品特別難用。
5. 使用者暫時無法列完所有問題，希望 Codex 能從多個角度主動審視，而不是只修被點名的項目。
6. 最終要能自動打包成朋友下載就能用的 package，不再長期稱為測試版。
7. 使用者沒有禁止 OP.GG 或歷史戰績；希望能用歷史資料描述玩家強度、近期狀態和風格，若不同來源差異不大，可以選成本較低且資訊足夠的方案。
8. 歷史功能可以先用假資料完整測試，但最終正式版必須接上可用、合規且能降級的 live 資料來源，不能停在 mock demo。
9. 2026-08-11 希望看到的是能實際下載、操作和驗收的完整候選版本，不是只交分析、介面稿或半成品。

後續不應把這些拆成互不相干的零碎 bug。它們共同指向兩個 P0 問題：

- Overlay 互動模型沒有被當成一個完整產品設計。
- 更新／渲染流程沒有以低延遲、低配置和 UI 執行緒安全為核心。

## 5. 已知實作與可能根因

以下是從目前程式碼得到的高可信假設；下一個 Codex 必須先用 profiler、計時與操作重現確認，不能把假設直接當結論。

### 5.1 畫面更新可能造成卡頓

`OverlayWindow.ApplySnapshot()` 每收到一次 snapshot 就呼叫 `Render()`，而 `Render()` 會依模式重新建立整棵 WPF visual tree。遊戲資料目前約每秒更新一次，即使顯示內容變化很小，也會重新建立 Border、Grid、TextBlock、Button、Image 等物件。

`ChampionVisual()` 每次渲染還會重新：

- 檢查圖片檔案是否存在。
- 建立 `BitmapImage`。
- 從磁碟載入並解碼圖片。
- 建立新的 Image、Clip 與視覺物件。

這可能造成持續配置、圖片解碼、GC 和 UI thread 停頓。Expanded 模式十名玩家的負擔最大，但 Dot 模式目前也會因每個 snapshot 重新建立按鈕。

### 5.2 資料流程可能回到 UI 執行緒

Session loop 從 WPF startup 啟動，async iterator、HTTP await、JSON parsing、英雄資料處理、評分與 snapshot 套用之間沒有一個明確的 background-processing seam。需要量測哪些 continuation 實際在 UI thread 上執行。

目標不是到處加 `Task.Run`，而是建立清楚的更新 module：背景取得與計算、合併／節流、最後只把最小 UI diff 送到 Dispatcher。

### 5.3 拖曳與 click-through 是脆弱的 Tag 規則

目前 WPF hit testing 透過 `Tag = "Drag"` 和 `Tag = "Interactive"` 配合 `WM_NCHITTEST`：

- Header 才被標成 Drag。
- 一般內容會回傳 click-through。
- Dot 的圓點本身是 Interactive button。

因此 Dot 沒有剩餘可拖曳區；Compact／Expanded 的可拖曳感受也被限制在 Header，而且按鈕、文字、背景和 click-through 之間沒有清楚的一致手勢。

這不應只靠在更多元素上補 Tag。需要一個能測試的 pointer interaction state machine，明確處理：

- 按下位置。
- 移動距離門檻。
- click 與 drag 的區分。
- mouse capture。
- 模式切換是否應發生。
- 拖曳結束後的位置保存與螢幕校正。
- 位置鎖定時的 click-through 行為。

### 5.4 歷史資料的產品與資料來源 concern

歷史資料不是被禁止的方向，但不能直接把「抓得到」等同於「適合放進公開朋友版」。目前需要處理：

- Riot 官方 API 適合取得牌位與 Match-V5 對局資料，但 API key 不得放入要散布的 EXE；development key 會過期，personal key 也不能供公開 alpha／beta 使用。公開產品通常需要 Production Key，以及能保護 key 的 server-side 或等價架構。
- Riot 的遊戲完整性規則禁止分析刻意隱藏的玩家，也禁止自製 MMR／ELO 等官方天梯替代品。因此歷史功能應呈現官方牌位、近期樣本、風格與不確定性，而不是宣稱算出真正實力或隱藏分數。
- OP.GG 第一方 Help Center 表示一般不禁止低頻 crawling／scraping，但其網站使用條款又有禁止 scraping 的文字。兩者衝突，需保守處理、標註來源並避免把它當唯一正式依賴。
- OP.GG 沒有把內部遊戲資料 API 提供給第三方；抓頁面或未公開端點會有版面／schema 變更、封鎖、資料新鮮度、rate limiting 和維護成本。
- 同時查十名玩家可能拖慢進場並製造額外流量。歷史載入必須非阻塞、有 timeout、cache、stale 標記、部分結果與完全失效 fallback。
- 不同 queue 的資料不能不加區分地比較。ARAM、單雙排、彈性與一般對戰應分開呈現；樣本太少時顯示「資料不足」，不能硬算結論。

完整來源與政策研究記錄在 `docs/HISTORICAL_DATA_RESEARCH.md`。下一個 Codex 應以該文件和最新第一方規則為準，政策有變動時更新研究記錄。

## 6. 目標互動原則

下一個 Codex 應先提出至少兩種完整互動方案並用 Replay 做原型比較。以下是產品要求，不是唯一指定版面：

### Dot

- 圓點本體任何位置都能按住拖曳。
- 移動未超過約 4–6 px 才視為 click 並展開。
- 開始拖曳後不得同時觸發模式切換。
- hover／游標或短提示要讓人知道可以拖。
- 不能只靠顏色傳達領先狀態；tooltip 或可展開資訊需有文字。

### Compact／Expanded

- 除了真正的 Button、Slider、TextBox 等控制項外，卡片大部分背景都應可拖曳。
- 提供可看見但不搶注意力的拖曳 affordance，例如細小 grip 或拖曳游標。
- 可選擇「鎖定位置」；鎖定後才讓非控制區完全 click-through。
- 縮小、展開、設定與關閉的控制目標不能過小。
- 面板切換尺寸後不能跳到螢幕外。

### 找回與失敗復原

- 系統匣永遠能顯示／重設位置／開啟設定／結束。
- 快捷鍵衝突時要有可理解的提示，不能只靜默改成另一組。
- 多螢幕拔除、DPI 改變或解析度改變後要自動回到可見工作區。

## 7. 效能目標與量測

先建立可重現的 Replay benchmark，再優化。至少記錄：

- snapshot 取得到 UI 呈現的耗時。
- Dispatcher 排隊時間與 UI update 時間。
- 每次更新配置量、Gen 0/1/2 GC 次數。
- 圖片實際解碼次數。
- Dot、Compact、Expanded 各自的 CPU 與記憶體。
- 拖曳時的 frame pacing 或肉眼可見停頓。

第一個正式版的建議門檻：

- Dot 閒置時平均 CPU 低於 1%，Expanded 更新時平均低於 2%（在文件記錄參考硬體）。
- 一次 UI 更新的 P95 低於 50 ms，不出現超過 100 ms 的可見停頓。
- 拖曳期間維持接近螢幕更新率，沒有明顯跳格。
- 30 分鐘 Replay／真實對局後，記憶體不呈現持續線性成長；暖機後增幅建議小於 10 MB。
- 相同圖示載入一次後必須快取，不因每秒 snapshot 重複解碼。
- snapshot 內容沒有可見變化時，不重建 UI。

數字若因合理技術限制需要調整，必須留下量測、硬體與理由，不能直接刪除門檻。

## 8. 建議的 module 與 seam

這不是要求增加大量 interface，而是把真正複雜、會被多處使用和需要替換測試的行為集中起來。

### Overlay presentation module

提供小的 interface，例如接受新的 `OverlaySnapshot`、切換模式、顯示／隱藏與重設位置；implementation 保有長生命週期 visual tree，只更新變化的 view state。呼叫者不需要知道 WPF control 如何組成。

### Overlay interaction module

把 pointer event 轉成 `Click`、`BeginDrag`、`DragTo`、`EndDrag` 等 observable outcome。正式 adapter 接 WPF／Win32，測試 adapter 輸入座標與時間。這個 seam 應能直接驗證 Dot click-vs-drag 和位置鎖定。

### Session update pipeline module

把 session frame 取得、評分、去重／合併與 UI dispatch 組合成一個深 module。正式 adapter 使用 League 本機資料，Replay adapter 用固定時間或可控制 clock。UI 只看到節流後的 snapshot。

### Historical profile module

提供小的歷史 profile interface，回傳官方牌位、queue-specific 近期樣本、可解釋的風格向量、來源、新鮮度與信心。至少有 Synthetic adapter、正式 live adapter 和 unavailable fallback。OP.GG 或 Riot API 的 transport、HTML／JSON schema、rate limit、cache 與 retry 都留在 implementation 裡，不洩漏到 UI。

### Package builder

在 repository 內提供一個有單一入口的打包 module／腳本。它隱藏 publish、文件雜湊替換、ZIP、掃描和 manifest 細節；本機與 CI 呼叫同一個入口，避免兩套流程漂移。

不要為只有一個 implementation 且沒有測試替身的行為建立空洞 interface。需要 seam 時，至少要有正式 adapter 和測試／Replay adapter。

## 9. 優先工作順序

### P0：讓 Overlay 本身可操作且不卡

1. 建立 Replay 效能基準與簡單 profiling 輸出。
2. 重現 Dot、Compact、Expanded 的拖曳與焦點行為。
3. 原型比較至少兩種 click／drag／click-through 模型。
4. 將資料取得與計算移出 UI thread。
5. 改成長生命週期 visual tree 或 binding，只更新變化欄位。
6. 加入圖片快取與 snapshot 去重／合併。
7. 加入三模式互動自動測試與 30 分鐘 soak test。

### P1：真實使用可靠性

1. 真實 ChampSelect → Loading → InGame → EndOfGame 驗收。
2. LoL 未啟動、Client 重啟、2999 短暫失效後自動恢復。
3. 多螢幕、不同 DPI、解析度切換與螢幕拔除。
4. 快捷鍵衝突提示、位置鎖定、系統匣重設位置。
5. 遊戲全螢幕無邊框下不搶焦點、不攔截不該攔截的輸入。

### P1：歷史近期狀態與風格

1. 先用 Synthetic adapter 建立完整 UI、fixture 與極端情況測試。
2. 定義 queue-specific profile：官方牌位、近期勝負／表現樣本、常用英雄／位置、激進度、生存、團隊參與和發育傾向；每一項都要能解釋來源。
3. 本場分數與歷史 profile 分開顯示；不得產生自製 MMR／ELO，也不得用單一「強／爛」標籤遮蔽樣本與不確定性。
4. 若已有 Riot Production API 架構或 OP.GG 明確允許，接上一個可公開使用的 live adapter；若沒有，不得為了趕日期偷偷 scraping，改交付 `PolicyDisabled`／`Unavailable` 狀態和「在瀏覽器開啟 OP.GG」的選用連結。
5. 對十名可見玩家做併發上限、cache、timeout、partial result、stale result、rate-limit 和完全離線測試。
6. 歷史來源失效不得拖慢或破壞本機即時 Overlay。

### P1：可重現打包與發布

1. 把現有 repository 外的打包流程搬進 `scripts/` 或等價目錄。
2. 一個命令完成 restore、test、publish、HTML、hash、scan 和 ZIP。
3. GitHub Actions 使用同一入口產生 candidate artifact。
4. 版本由單一來源產生，Assembly、User-Agent、README、HTML、ZIP、tag 和 Release 一致。
5. 在乾淨 Windows 使用者帳號驗證下載、解壓、SmartScreen、啟動、結束與移除。

### P2：正式產品整理

1. 補上明確的開源或使用授權 `LICENSE`。
2. 評估程式碼簽章；若暫時無法簽章，必須和非技術使用者實測 SmartScreen 流程。
3. 將特殊英雄 override 移到有 schema、可驗證、可隨版本更新的資料檔。
4. 提供只包含非敏感狀態的手動診斷複製功能，不自動上傳。
5. 完成 friend-facing 文件的可用性測試與文字精簡。

## 10. 正式版發布門檻

只有下列條件全部完成，才發布第一個不含 `test`／`測試版`／`prerelease` 的版本：

### 功能與 UX

- 三種模式都可直覺拖曳；Dot click 與 drag 不互相誤觸。
- 系統匣、快捷鍵、設定、重設位置與結束都可由非技術使用者完成。
- 選角、載入、遊戲中、結束生命週期至少在兩台不同電腦完成真實驗收。
- 兩種 DPI／解析度與多螢幕情境通過。
- 一名未參與開發的朋友能只看 `先看這裡.html` 完成安裝、操作、找回、退出和移除。
- 啟用 live 歷史 provider 時，profile 清楚顯示資料來源、queue、樣本數、新鮮度與信心，且和本場分數有明顯視覺區隔；沒有合規 provider 時，功能必須明確隱藏或顯示 unavailable，不能假裝已有真人資料。

### 效能與可靠性

- 達到第 7 節量測門檻，或有經記錄且合理的新門檻。
- 30 分鐘 Replay 與至少一場完整真實對局無持續記憶體成長或可見卡頓。
- Client／2999 暫時失效會顯示白話狀態並自行恢復。
- Overlay 不搶 LoL 焦點，不阻擋正常遊戲輸入。

### 安全與隱私

- Overlay view model、朋友文件、fixture、log 和 package 不含原始 KDA、等級、CS、死亡時間、物品價值、LCU token 或真實測試 Riot ID。
- 網路目的地僅包含本機 loopback、文件允許的 Riot 網域與經政策審查後列入 allowlist 的歷史資料來源。
- 無注入、記憶體讀取、自動輸入、匿名身分還原或自製 MMR／ELO。
- Riot API key、OP.GG session、cookie 或其他秘密不進入 EXE、ZIP、log、fixture 或公開設定。
- Production 不使用 Synthetic adapter 冒充真人歷史資料；live provider 失效時明確顯示 unavailable／stale 並保持核心 Overlay 可用。
- EXE／ZIP 完成敏感字串、PDB、本機路徑與非預期檔案掃描。

### 打包與交付

- 乾淨 clone 後能以文件中的一個命令產生 package。
- CI 與本機使用相同的 package builder。
- ZIP 頂層只有 EXE 和 HTML，且 HTML 完全離線、自含素材。
- SHA-256 由打包流程自動產生並寫入 HTML／Release，不允許人工複製造成漂移。
- 發布版本、檔名、tag、Assembly 與文件一致。
- Release 不標記 prerelease，標題與檔名不再出現「測試版」。
- 若 EXE 未簽章，SmartScreen 風險有明確說明且通過非技術使用者驗收；若要擴大到朋友圈以外，優先完成可信任簽章。

## 11. 非目標與不可退讓界線

- 不要求安裝 OP.GG App；但可以在符合第一方規則、低頻、標註來源且可降級的前提下使用 OP.GG 公開資料。
- 可以讀取歷史戰績並推測近期狀態／風格，但不將本場表現分或自製歷史指標宣稱為官方牌位、隱藏 MMR、ELO、確定的玩家實力或勝率。
- 不顯示本機資料端點無法可靠取得的全員傷害、承傷、治療量。
- 不破解或還原 Riot 原本隱藏的選角玩家。
- 不為了更快取得資料而注入遊戲、讀記憶體或模擬操作。
- 不自動上傳玩家資料、診斷資料或使用行為。

## 12. 下一個 Codex 的建議第一個任務

可直接使用以下提示：

> 完整閱讀 AGENTS.md、docs/PRODUCT_HANDOFF.md、docs/HISTORICAL_DATA_RESEARCH.md、README.md 與 SECURITY.md。目標是在 2026-08-11 交付可下載、可操作、可驗收的完整候選版本，不要停在分析。先用 Replay 量測並修正 P0：Dot click／drag、面板大範圍拖曳、UI thread、長生命週期 UI、圖片快取和 snapshot 去重。並行以 Synthetic history adapter 完成歷史近期狀態／風格的 UI、schema、樣本不足與失效測試。若已有 Riot Production API 或 OP.GG 書面允許，再接 live adapter；否則交付清楚的 unavailable／PolicyDisabled 狀態和可選的瀏覽器 OP.GG 連結，禁止為趕日期偷偷 scraping、把 API key 放進 EXE、分析匿名玩家或產生自製 MMR／ELO。最後把 repository 內的一鍵 test／publish／hash／scan／ZIP 流程做完，產生 candidate package，跑自動測試與必要 Replay／真實驗收，依正式發布門檻列出仍未通過的項目。不要僅靠改名宣稱正式版。

## 13. 重要檔案導覽

- `src/LolPerformanceOverlay/App.xaml.cs`：生命週期、phase 行為、session loop。
- `src/LolPerformanceOverlay/UI/OverlayWindow.cs`：目前渲染、拖曳、click-through 的主要問題區。
- `src/LolPerformanceOverlay/Core/PerformanceScorer.cs`：評分、信心與 EMA。
- `src/LolPerformanceOverlay/Infrastructure/LeagueSessionSource.cs`：LCU／2999 polling 與 reconnect。
- `src/LolPerformanceOverlay/Infrastructure/DataDragonProvider.cs`：靜態資料與圖片快取。
- `src/LolPerformanceOverlay/Infrastructure/ReplaySessionSource.cs`：離線 UX／效能測試基礎。
- `tests/LolPerformanceOverlay.Tests/`：現有 17 個核心與解析測試。
- `docs/先看這裡.html`：朋友實際看到的離線說明。
- `docs/HISTORICAL_DATA_RESEARCH.md`：Riot API、OP.GG 與歷史資料來源的第一方政策研究。
- `SECURITY.md`：公開安全邊界。

## 14. 交接時應持續更新的紀錄

下一個 Codex 每完成一個里程碑，應在本文件或獨立 changelog 補上：

- 修改前問題與可重現步驟。
- 採用方案及沒有採用的替代方案。
- 量測環境與修改前後數字。
- 新增／更新的自動測試。
- 真實對局與朋友測試結果。
- 距離正式發布門檻仍未完成的項目。

不要把「已修一個 bug」等同於「產品已可用」。每次里程碑都要重新從 AGENTS.md 的九個角度審視。
