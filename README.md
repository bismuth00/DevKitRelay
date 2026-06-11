# DevKitRelay

C# / WebRTC で、指定した Windows ウィンドウの画面を配信し、別プロセスのクライアントで受信表示するサンプルです。

この実装は WebRTC の DataChannel に JPEG フレームを流します。映像コーデックのネイティブ依存を避けるため、導入しやすい構成にしています。

## 必要環境

- Windows
- .NET 10 SDK
- NuGet に接続できる環境

## ビルド

```powershell
dotnet restore
dotnet build
```

## ウィンドウ一覧

```powershell
dotnet run -- list-windows
```

## サーバー

ウィンドウタイトルの一部を指定して配信します。

```powershell
dotnet run -- server --window "メモ帳" --listen http://0.0.0.0:5080 --fps 10 --jpeg-quality 70
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
- NAT 越えや TURN は未設定です。まずは同一 LAN または同一 PC での利用を想定しています。
- 保護されたウィンドウ、管理者権限の違うウィンドウ、GPU オーバーレイなどはキャプチャできない場合があります。
