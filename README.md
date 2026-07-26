# DevKitRelay

C# / WebRTC で、指定した Windows ウィンドウの画面を動画ストリームとして配信し、別プロセスのクライアントで受信表示するサンプルです。

サーバーは指定ウィンドウを BGR raw frame としてキャプチャし、VP8 にエンコードして WebRTC の video track で送信します。クライアントは受信した VP8 video track をデコードして WinForms に表示します。

## 必要環境

- Windows
- .NET 10 SDK
- NuGet に接続できる環境

## ビルド

```powershell
dotnet restore
dotnet build
```

## テスト

```powershell
dotnet test DevKitRelay.sln
```

統合テストは Windows 上で `notepad.exe`、サーバー、クライアントを実プロセスとして起動し、WebRTC video track の受信、初回フレームのデコード、クライアントウィンドウの自動リサイズを確認します。テストで起動した Notepad は終了時に閉じます。

## ウィンドウ一覧

```powershell
dotnet run -- list-windows
```

## サーバー

ウィンドウタイトルの一部を指定して配信します。

```powershell
dotnet run -- server --window "メモ帳" --listen http://0.0.0.0:5080 --fps 30
```

画質と負荷を調整する場合:

```powershell
dotnet run -- server --window "メモ帳" --fps 30 --bitrate-kbps 6000 --scale 1.0
```

- `--fps`: フレームレートです。範囲は `1` から `60`、既定は `30` です。
- `--bitrate-kbps`: VP8 の目標ビットレートです。未指定の場合はフレームサイズと fps から自動算出します（1920x1080@30 で約 6200kbps）。範囲は `1` から `100000` です。
- `--scale`: キャプチャ解像度の倍率です。範囲は `0.1` から `1.0` です。`1.0` 未満にすると縮小して送信するため、クライアント側で拡大されて画質が落ちます。負荷が問題にならない限り `1.0` を推奨します。

サーバーは 1 秒ごとに実測値を出力します。画質調整の判断に使ってください。

```
Video send: 29.8 fps, capture=12.3 ms, encode=8.1 ms, 6032 kbps, avg frame=25301 bytes
```

- `fps`: 実測フレームレートです。`--fps` に届かない場合はキャプチャ/エンコードが間に合っていません。`--scale` を下げるか `--cpu-used` を上げます。
- `capture` / `encode`: 1 フレームあたりの所要時間です。合計が `1000 / fps` を超えていると目標フレームレートは出ません。
- `q`: VP8 の量子化値（0〜63）です。**最大付近に張り付いている場合はビットレート不足**なので `--bitrate-kbps` を上げます。最小付近なら画質は頭打ちで、他がボトルネックです。

エンコーダの速度/画質は `--cpu-used` で調整します。負の値ほど高画質・低速、正の値ほど高速・低画質です（範囲 `-16`〜`16`、既定 `-6`）。

同じ PC で試す場合:

```powershell
dotnet run -- server --window "メモ帳"
```

## クライアント

```powershell
dotnet run -- client --server ws://127.0.0.1:5080/signal
```

動作確認などで自動終了したい場合:

```powershell
dotnet run -- client --server ws://127.0.0.1:5080/signal --duration 10
```

表示方法を変える場合:

```powershell
dotnet run -- client --server ws://127.0.0.1:5080/signal --display frame --filter nearest
```

- `--display`: `source`（既定）は元ウィンドウと同じ大きさで表示します。サーバー側で `--scale` を下げているときは拡大されるためぼやけます。`frame` は受信した解像度のまま等倍で表示するので拡大によるぼけがありません。
- `--filter`: 拡大縮小時の補間方法です。`bicubic`（既定）は文字向け、`nearest` はドット絵や輪郭を保ちたい場合に向きます。等倍表示時はどちらでも補間されません。

クライアントは映像が途切れると自動でキーフレームを要求するため、パケットロス後も画面が壊れたままになりません。

## 注意

- WebRTC の通信確立用に、サーバーは簡易 WebSocket signaling エンドポイント `/signal` を持ちます。
- 映像コーデックは VP8 です。libvpx を直接設定し、画面共有向けに screen-content モードと低い量子化下限を有効にしています。
- キャプチャは Windows Graphics Capture を使い、利用できない場合のみ PrintWindow にフォールバックします。起動時のログでどちらを使っているか確認できます。
- サーバー起動中に別プロセスを `dotnet run` で起動すると、ネイティブ DLL がロックされることがあります。開発中は `dotnet build` 後に `dotnet run --no-build -- ...` またはビルド済み exe を直接使うと安定します。
- NAT 越えや TURN は未設定です。まずは同一 LAN または同一 PC での利用を想定しています。
- 保護されたウィンドウ、管理者権限の違うウィンドウ、GPU オーバーレイなどはキャプチャできない場合があります。
