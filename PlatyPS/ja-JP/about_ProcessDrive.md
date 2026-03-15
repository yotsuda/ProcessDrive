---
title: about_ProcessDrive
---

# about_ProcessDrive

## Short Description
Windows プロセスツリーを PowerShell ドライブとしてナビゲートするプロバイダです。

## Long Description
ProcessDrive は NavigationCmdletProvider を実装し、Windows のプロセスツリーを
ドライブとしてマウントします。cd, dir, Get-Item, Remove-Item などの標準コマンドで
プロセスを操作できます。Process Explorer (procexp.exe) の CLI 代替として設計されています。

## ドライブのマウント

```powershell
New-ProcDrive          # Proc:\ を作成
New-ProcDrive MyProc   # カスタム名
```

## プロセスツリー

```
Proc:\
├── chrome_21236\           # プロセス (Name_PID)
│   ├── chrome_18176\       # 子プロセス
│   ├── [Modules]           # 仮想フォルダ: ロード済み DLL
│   ├── [Threads]           # 仮想フォルダ: スレッド
│   ├── [Services]          # 仮想フォルダ: 関連サービス
│   └── [Network]           # 仮想フォルダ: TCP/UDP 接続
├── devenv_24032\
└── explorer_37760\
```

## ナビゲーション

```powershell
cd Proc:\                       # ルートに移動
dir                             # プロセス一覧
dir -Recurse                    # 全プロセスツリー
cd chrome_21236                 # プロセスに移動 (Tab 補完可)
cd Modules                      # 仮想フォルダに移動
cd ..                           # 親に戻る
cd \                            # ルートに戻る
```

## プロセスの詳細

```powershell
Get-Item Proc:\devenv_24032 | Format-List *
```

メモリ詳細、CPU 時間、I/O 統計、ファイルバージョンなどの全プロパティを表示します。

## プロセスの検索

```powershell
dir Proc:\ -Include note* -Recurse        # ツリー全体から検索
dir Proc:\ -Include note* -Recurse -Force  # キャッシュ更新して検索
```

## プロセスの終了

```powershell
Remove-Item Proc:\notepad_1234              # プロセスを終了
Remove-Item Proc:\chrome_21236 -Recurse     # プロセスツリーを終了
```

## キャッシュ

プロセスツリー、Modules、Services は 10 秒間キャッシュされます。
`dir -Force` でキャッシュを破棄して最新のデータを取得できます。
Threads と Network は毎回ライブデータを取得します。

## パイプライン

```powershell
# メモリ上位プロセス
dir Proc:\ | Sort-Object MemMB -Descending | Select-Object -First 10

# 特定の DLL を使用しているプロセスを検索
dir Proc:\ | % { dir "Proc:\$($_.PSChildName)\Modules" -EA Ignore } | ? Name -like '*gdi*'

# Established 状態の TCP 接続を持つプロセス
dir Proc:\ | % {
    $n = $_.Name
    dir "Proc:\$($_.PSChildName)\Network" -EA Ignore |
        Select-Object @{N='Process';E={$n}}, Protocol, LocalAddress, RemoteAddress, State
} | ? State -eq 'Established'
```
