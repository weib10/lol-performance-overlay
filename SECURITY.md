# Security

## 測試版定位

這是未簽章的朋友測試版，不應被視為安全廠商、Riot Games 或任何第三方的安全認證。請只使用此 repository 的 prerelease 成品，並核對 README 與說明頁中的 SHA-256。

## 程式行為

目前程式碼的網路與本機資料範圍如下：

- `127.0.0.1`：唯讀取得 League Client 階段、選角與本場即時資料。
- `https://ddragon.leagueoflegends.com`：下載公開的英雄／物品靜態資料與圖示。
- 沒有玩家資料上傳、遙測、廣告或本工具自己的遠端服務。
- League Client 臨時本機通行資訊不寫入硬碟或 log。
- 不注入遊戲、不讀取遊戲記憶體、不模擬輸入、不修改遊戲檔案。
- 對外發布的單檔 EXE 不嵌入本專案 PDB 或開發者本機建置路徑。

## 回報安全問題

請直接私下聯絡 repository 擁有者，說明版本、重現步驟與影響。不要在 Issue、截圖或附件中貼出帳號密碼、驗證碼、League Client 臨時通行資訊或其他憑證。
