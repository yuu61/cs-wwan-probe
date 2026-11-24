# wwan-probe

Windows のモバイルブロードバンド (WWAN) モデム向けの LTE 信号監視ツール (TUI) です。C# (.NET 10) で書かれています。
実機で確認しているのは Fibocom L860-GL だけです。Quectel や Fibocom FM350 などは仕様上の対応です ([`docs/modem-support.md`](docs/modem-support.md) 参照)。

> 実機での確認は、C# へ移植する前の PowerShell 版で行ったものです。C# 版はモデムのない環境で、テストと、同じ入力に対する PowerShell 版との画面・CSV の一致を確認しています (PowerShell 版は git の履歴に残っています)。

## 機能

- **リアルタイム監視**: RSRP, RSRQ, SNR, 送受信レート, モデム温度の履歴グラフ表示
- **詳細情報の取得**: CA (キャリアアグリゲーション) 情報、利用可能な LTE バンド、RAT 情報の取得
- **2G/3G ダウングレード検知**: 偽基地局対策として、国内では利用されない 2G/3G へのダウングレードを検知して警告 (詳細は [`docs/downgrade-detection.md`](docs/downgrade-detection.md) 参照)
- **近隣セル情報の表示**: モデムの AT コマンド (L860-GL は MBIM の Intel AT Tunnel 経由の `AT+XMCI`) で近隣セル (Neighbor cells) の情報を取得・表示。AT で取れない場合は WinRT が報告する近隣セルを使用 (詳細は [`docs/neighbor-cells.md`](docs/neighbor-cells.md) 参照)
- **複数ベンダーの AT コマンド**: 起動時に AT の経路 (Intel / Fibocom / Compal / Quectel の MBIM サービス、または COM ポート) とコマンドセット (Intel `+X`、Quectel `+Q`、Fibocom `+GT`) を自動判定 (詳細は [`docs/modem-support.md`](docs/modem-support.md) 参照)
- **ハンドオーバー履歴**: LTE 主セルの変更を検出し、時刻と切り替え先の Cell ID・バンド・PCI などを表示
- **GPS / GNSS 表示**: `-Gps` で衛星測位の緯度・経度・高度・精度・速度・方位・HDOP / PDOP / VDOP・測位時刻を表示。未測位や取得不能も表示。`-Nmea` で捕捉衛星の一覧 (衛星系ごとに色分け、仰角・方位角・SNR・測位への使用) と、受信機の測位状態 (2D / 3D・測位品質・測位モード・使用衛星数・海抜高度とジオイド高) も表示 (詳細は [`docs/gps.md`](docs/gps.md) 参照)
- **CSV ログ出力**: 取得した情報を CSV ファイルに記録可能

## 必須要件

- **OS**: Windows 10 / 11
- **.NET**: .NET 10 SDK (ビルド・実行)。`global.json` で 10.0 系に固定しています
- **対象モデム**: Windows が認識する MBIM モデム。温度・SINR・CA・近隣セルなどは AT コマンドに対応したモデムのみ
  - 実機確認済み: Fibocom L860-GL (Intel XMM 7560)
  - 仕様上の対応 (未確認): Intel XMM 系、Quectel (RM5xx など `AT+QENG` 対応機)、Fibocom FM350-GL など `AT+GTCCINFO` 対応機
  - AT が使えないモデムでも、WinRT で取れるサービングセルの情報・履歴・CSV は表示・記録できます

WinRT (`MobileBroadbandModem`、`Geolocator`) はターゲットフレームワーク (`net10.0-windows10.0.26100.0`) から参照するので、追加のセットアップは不要です。

## 使い方

リポジトリ直下で実行すると、ビルドして TUI モニターを起動します。

```powershell
dotnet run -- [-Interval <秒>] [-Count <回数>] [-CsvPath "log.csv"] [-AtPort COM7] [-Gps] [-Nmea]
```

ビルド済みの実行ファイル (`dotnet build -c Release` の場合は `bin\Release\net10.0-windows10.0.26100.0\cs-wwan-probe.exe`) を直接起動しても同じです。

オプション名は大文字・小文字を区別せず、一意に決まる範囲で省略できます (`-Int 5` など)。`-Interval` / `-Count` / `-CsvPath` / `-AtPort` はこの順に位置でも指定でき、`-Name:値` の形も使えます。
存在しないオプション (綴りの誤りを含む) や範囲外の値を指定するとエラーで終了します (終了コード 1)。`-?` で使い方を表示します。

### 引数 (オプション)

- `-Interval`: 測定間隔 (秒、0〜86400)。デフォルトは `0` で、バックトゥバックサンプリング (カウンターの関係で約1秒間隔) を行います。
- `-Count`: 測定回数。デフォルトは `0` で無限ループします。
- `-CsvPath`: 指定すると、結果を CSV ファイルにログ出力します (UTF-8 BOM 付き、1 測定 1 行)。
- `-AtPort`: AT コマンドを MBIM ではなく指定の COM ポート (例: `COM7`) で送ります。ベンダードライバが AT ポートを公開しているモデム向けです。省略時は MBIM の AT サービスを自動で探します。
- `-Gps`: Windows の GNSS ドライバー経路で衛星測位を取得します。GPS 以外 (Wi-Fi・基地局・IP 等) の座標は表示・記録しません。Windows の位置情報サービスとデスクトップアプリの位置情報アクセスを有効にしてください。API の制約により特定のモデムは選択できません。省略時は GPS を取得しません。
- `-Nmea`: `-Gps` に加えて、GNSS ドライバーの NMEA から捕捉衛星の一覧を取得します。`-Nema` とも書けます。ドライバーが管理者権限を要求するため、起動時に UAC で受信用のヘルパー (同じ実行ファイルを `--nmea-helper` 付きで起動したもの) だけを昇格します (管理者ターミナルでは確認なし)。UAC を拒否しても LTE と GPS 欄の監視は続きます。衛星一覧は CSV には記録しません。

GPS を表示し、LTE 測定と一緒に CSV へ記録する例:

```powershell
dotnet run -- -Gps -CsvPath "log.csv"
```

### TUI での操作

TUI (Text User Interface) 画面起動中は以下のキー操作が可能です。

- `q` / `Esc` / `Ctrl+C` : 終了
- `p` : 一時停止 / 再開
- `r` : すぐに更新 (リフレッシュ)
- `R` (`Shift`+`r`) : すべての統計情報をリセット (ヒストリーグラフと min/avg/max、ハンドオーバー履歴と累計件数、2G/3G の検知履歴)。Caps Lock 中は `r` でもリセットになります
- `1`〜`6` : 各ヒストリーグラフの表示・非表示 (1:RSRP, 2:RSRQ, 3:SNR, 4:RX, 5:TX, 6:Temp)
- `g` : 全グラフの一括表示切り替え
- `h` : ハンドオーバー履歴の詳細一覧 / 通常画面を切り替え。詳細一覧では `↑` / `↓` で新しい / 古い履歴へ移動
- `s` : 衛星一覧 / 通常画面を切り替え (`-Nmea` 指定時)。一覧では `↑` / `↓` でスクロール。`h` の一覧とは排他
- `↑` / `↓` : ヒストリーグラフの高さ (行数) を増減 (1〜10 行、既定 2 行。1 行あたり 8 段階。Temp は常にその 1/2 (切り捨て、最低 1 行))

`LTE bands:` の各バンドは、最新の接続中セルと近隣セルの合計に応じて明るく表示します。0 個は従来の暗いグレー、1 個以上は表示対象バンドの最多セル数を白として RGB グラデーションで表示します。例えば B1 が 1 個、B18 が 2 個なら B1 は中間グレー (`#909090`)、B18 は白 (`#FFFFFF`) になります。最多が 1 個なら観測されたバンドはすべて白です。同じ EARFCN・PCI のセルは重複して数えません。物理的な基地局の台数ではなく、現在観測できるセル数を表し、測定ごとに明度を更新します。

測定はバックグラウンドで行うため、モデムの応答待ちや通信量の取得中もキー操作できます。
一時停止時に取得中の測定は完了後に反映され、次の自動測定から停止します。取得中の `r` はその測定の完了を待ちます。
`R` はすぐにリセットし、取得中の測定は完了後にリセット後の統計に含めます。
リセットしても、画面上の測定回数 (`-Count` の進捗)、最新の測定値、CSV ログはそのままです。

ハンドオーバー履歴は通常画面に直近3件、`h` の詳細一覧に最大100件を新しい順で表示します。
各履歴は「その時刻にどのセルへ切り替わったか」を表示します。詳細には検出時刻、切り替え先の PLMN・Cell ID・バンド・PCI・EARFCN・TAC・RSRP を表示します。
履歴と累計件数は起動中のみ保持し、`R` で 0 件に戻せます。リセット直前のセルは比較基準として残すため、リセットをまたいだ切り替えも検出します。標準出力への逐次出力にも直近3件が含まれます。

判定対象はモデムが返す先頭の LTE 接続セル（本ツールでの主セル）の PLMN と Cell ID の変更です。
CA の副セルの追加・削除、信号強度や TAC だけの変化は数えません。初回取得、取得失敗・圏外・識別情報不明の後は比較基準を設定し直します。
測定間のセル変更を観測するため、セル再選択と通信中のハンドオーバーは区別できず、測定間隔内の切り替えをすべて捕捉できるものではありません。

主セルの信号値が取得できない場合も、セルの識別情報は保持します。画面・信号履歴・CSV・ハンドオーバー判定は同じ主セルを使い、CA の副セルを代用しません。
取得不能な測定値は画面では `n/a`、CSV では空欄、履歴グラフでは欠測として扱います。通信量の実測ゼロは `0 B/s` と表示し、カウンター取得失敗は画面と CSV の `Error` 列に理由を記録します。
LTE セルを取得できない回も履歴の位置を残し、取得できた温度などのモデム全体の測定値は保持します。

標準入出力がリダイレクトされている場合は、プレーンテキストによる逐次出力 (幅 100 桁、ASCII のグラフ) にフォールバックします。
このときの `Ctrl+C` は実行中の測定の完了後に終了し (GPS・NMEA ヘルパーも停止します)、もう一度押すとすぐに終了します。

## ディレクトリ構成

```text
Program.cs                 エントリーポイント (TUI / プレーン出力の選択、NMEA ヘルパーの起動口)
CommandLine.cs             オプションの解析
Domain/                    ドメインルール (下記)
Infrastructure/            ハードウェア・OS・CSV との入出力 (下記)
Application/               ユースケース (下記)
Presentation/              画面 (下記)
cs-wwan-probe.Tests/       ハードウェア不要のテスト (xUnit)
docs/                      技術資料
cs-wwan-probe.csproj       本体のプロジェクト
cs-wwan-probe.slnx         本体とテストのソリューション
```

本ツールは Domain-Driven Design (DDD) 風のレイヤードアーキテクチャを採用しており、各フォルダーがそれぞれの責務に分かれています。
Domain は他のレイヤーに依存せず、Application が Infrastructure の取得結果にドメインルールを適用します。Presentation はその結果を表示します。

- **`Domain/`** : EARFCN とバンドの対応 (`Band.cs`)、信号の評価・統計 (`Signal.cs`)、ダウングレード判定 (`Downgrade.cs`)、セル同一性とハンドオーバー履歴 (`Handover.cs`)、正規化したセル・RAT 設定の型 (`Cells.cs`)
- **`Infrastructure/`** : WinRT の非同期待ち (`WinRt.cs`)、通信量の性能カウンター (PDH、`AdapterTraffic.cs`)、CSV (`CsvFile.cs`)
  - **`Modem/`** : モデムの WinRT 情報と MBIM / COM ポートでの AT 送受信 (`ModemDevice.cs`, `AtChannel.cs`)、AT の経路とコマンドセットの判定 (`ModemAt.cs`)、ベンダー別のコマンドセットと応答パーサー (`AtProfile.cs`, `AtResponse.cs`, `IntelStatus.cs`, `QuectelStatus.cs`, `FibocomStatus.cs`)、WinRT 値の正規化 (`SignalConversion.cs`)、1 回分の観測の組み立て (`ModemObservation.cs`)
  - **`Gnss/`** : GPS (`GpsReceiver.cs`, `Gps.cs`)、NMEA の解析 (`Nmea.cs`)、昇格ヘルパーとその状態ファイル (`NmeaReceiver.cs`, `NmeaHelper.cs`)、GNSS ドライバーの IOCTL (`GnssDevice.cs`)
- **`Application/`** : 主セル・副セルを明示した snapshot の作成とデバイス情報 (`Snapshot.cs`)、CSV の列と行 (`SnapshotLog.cs`)、セッション更新・履歴・2G/3G 記録とバックグラウンド測定 (`MonitorSession.cs`)
- **`Presentation/`** : 画面の組み立て (`Frame.cs`) と各セクション (`HistoryChart.cs`, `HandoverSection.cs`, `GnssSection.cs`)、ゲージ (`Gauge.cs`)、描画 (`ConsoleRenderer.cs`)、TUI とプレーン出力のループ (`TuiMonitor.cs`)

RAT の許可設定は `AllowedRats` (GSM / UMTS / LTE / NR) と表示文言を分け、2G/3G 許可の判定結果を Application から画面に渡します。

## 開発

```powershell
dotnet build cs-wwan-probe.slnx                             # 本体とテストのビルド
dotnet test cs-wwan-probe.slnx                              # テスト (モデム不要)
dotnet format cs-wwan-probe.slnx                            # 整形 (.editorconfig に従う)
dotnet format cs-wwan-probe.slnx --verify-no-changes        # 整形が必要なファイルの報告のみ
```

- テストはモデムの代わりに `IModemDevice` (WinRT と AT の送受信) と `ITrafficCounter` (通信量) の疑似実装を使い、本体の観測・snapshot・履歴・CSV・画面の処理をそのまま通します。AT の応答はマニュアルの例と L860-GL の実機応答をフィクスチャにしています
- TUI のループは `ITuiTerminal`、描画は `IConsoleSurface` を差し替えてテストします
- コードスタイルは `.editorconfig` にあります (明示的な型、ブロック形式の名前空間、名前空間の外の using、1 行に収まる本体は波括弧なし・複数行は波括弧必須、1 行に収まるメソッドは `=>`)
- 静的解析は全ルール有効です (`Directory.Build.props`: `AnalysisLevel=latest-all`、ビルド時のコードスタイル検査、警告はエラー扱い。`.editorconfig`: 全アナライザーを warning)。`dotnet build` が警告 0 で通ることと `dotnet format --verify-no-changes` が通ることの両方を確認します (JSON002 などエディター専用のルールは `dotnet format` だけが検査します)
- インラインの抑制 (`#pragma warning disable`、`[SuppressMessage]`、`#nullable disable`) はビルドエラーです (`Directory.Build.targets`)。違反はコードを直します。ルールを満たせない箇所だけ、理由を付けて `GlobalSuppressions.cs` (1 メンバー単位) か `.editorconfig` (ファイル範囲) に置きます
  - CA1031 (汎用例外の catch) は `Infrastructure/` と `Program.cs` で無効です。WinRT・MBIM・COM ポート・PDH・昇格ヘルパーの失敗を、モニターを止めずに各サンプルのエラー文字列として報告する設計のためです
  - テストプロジェクトでは CS1591 (XML ドキュメント) を無効にしています。xunit のテストクラスとメソッドは public である必要があるためです
- 本体の型は `internal` です (CA1515)。テストからは `InternalsVisibleTo` で参照します

## ドキュメント

より詳細な技術情報については `docs/` 以下の Markdown ファイルを参照してください。

- [2G/3G ダウングレード検知](docs/downgrade-detection.md)
- [近隣セル (Neighbors) の取得方法](docs/neighbor-cells.md)
- [対応モデム (L860-GL 以外)](docs/modem-support.md)
- [GPS / GNSS の取得と表示](docs/gps.md)
