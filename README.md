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
dotnet run -- server --window "メモ帳" --listen http://0.0.0.0:5080 --fps 10
```

画質と負荷を調整する場合:

```powershell
dotnet run -- server --window "メモ帳" --fps 15 --bitrate-kbps 2500 --scale 0.75
```

- `--fps`: フレームレートです。範囲は `1` から `30` です。
- `--bitrate-kbps`: VP8 の目標ビットレートです。未指定の場合はエンコーダ既定値を使います。
- `--scale`: キャプチャ解像度の倍率です。範囲は `0.1` から `1.0` です。

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

## 注意

- WebRTC の通信確立用に、サーバーは簡易 WebSocket signaling エンドポイント `/signal` を持ちます。
- 映像コーデックは VP8 です。
- サーバー起動中に別プロセスを `dotnet run` で起動すると、ネイティブ DLL がロックされることがあります。開発中は `dotnet build` 後に `dotnet run --no-build -- ...` またはビルド済み exe を直接使うと安定します。
- NAT 越えや TURN は未設定です。まずは同一 LAN または同一 PC での利用を想定しています。
- 保護されたウィンドウ、管理者権限の違うウィンドウ、GPU オーバーレイなどはキャプチャできない場合があります。
