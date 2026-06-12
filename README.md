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

統合テストは Windows 上で `notepad.exe`、サーバー、クライアントを実プロセスとして起動し、WebRTC video track の受信、初回フレームのデコード、クライアントウィンドウの自動リサイズを確認します。

## ウィンドウ一覧

```powershell
dotnet run -- list-windows
```

## サーバー

ウィンドウタイトルの一部を指定して配信します。

```powershell
dotnet run -- server --window "メモ帳" --listen http://0.0.0.0:5080 --fps 10
```

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
- NAT 越えや TURN は未設定です。まずは同一 LAN または同一 PC での利用を想定しています。
- 保護されたウィンドウ、管理者権限の違うウィンドウ、GPU オーバーレイなどはキャプチャできない場合があります。
