#requires -Version 7.0
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = ([xml](Get-Content -LiteralPath (Join-Path $sourceRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$manuals = @(
    @{ Source='user-guide.md'; Output='使用说明.html'; Title='DeskPlan 图文操作手册' },
    @{ Source='quick-start.md'; Output='快速入门.html'; Title='DeskPlan 快速入门' }
)
foreach ($manual in $manuals) {
    $body = (ConvertFrom-Markdown -LiteralPath (Join-Path $sourceRoot ('docs/' + $manual.Source))).Html
    # Embed documentation screenshots so each HTML file also works as a standalone download.
    $body = [regex]::Replace($body, 'src="images/([^"]+)"', {
        param($match)
        $imagePath = Join-Path $sourceRoot ('docs/images/' + $match.Groups[1].Value)
        if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) { throw "缺少手册图片：$imagePath" }
        'src="data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($imagePath)) + '"'
    })
    # Supplementary documents are online; all operating steps and FAQ remain in this HTML.
    $body = [regex]::Replace($body, 'href="([^"#:]+\.md)"', {
        param($match)
        'href="https://github.com/yanlREN/DeskPlan/blob/main/docs/' + $match.Groups[1].Value + '"'
    })
    $toc = ([regex]::Matches($body, '<h2 id="([^"]+)">(.+?)</h2>') | ForEach-Object {
        '<li><a href="#' + $_.Groups[1].Value + '">' + $_.Groups[2].Value + '</a></li>'
    }) -join ''
    $html = @"
<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>$($manual.Title)</title>
<style>
:root{color-scheme:light;--ink:#20314a;--accent:#238881}*{box-sizing:border-box}
body{margin:0;background:#f2f6f8;color:var(--ink);font:16px/1.85 "Segoe UI","Microsoft YaHei",sans-serif}
header{background:#173a42;color:white;padding:26px max(24px,calc((100% - 940px)/2));letter-spacing:1px}
header span{display:block;font-size:13px;color:#bbd9d8;letter-spacing:0}
main{max-width:1000px;margin:28px auto 60px;padding:32px 48px;background:white;border-radius:16px;box-shadow:0 8px 28px #183e4910}
h1{font-size:32px;line-height:1.35;margin:12px 0 20px}h2{font-size:24px;padding-top:26px;margin-top:38px;border-top:1px solid #dce8ec;scroll-margin-top:24px}
h3{font-size:18px;margin:24px 0 8px}p,li{overflow-wrap:anywhere}a{color:#137a74;text-underline-offset:3px}
nav{background:#edf7f4;border:1px solid #d9ede7;border-radius:12px;padding:16px 24px;margin-bottom:28px}nav ol{columns:2;margin-bottom:4px}nav li{break-inside:avoid}
table{border-collapse:collapse;width:100%;font-size:15px;margin:20px 0}th,td{border:1px solid #dce5ec;padding:10px 13px;text-align:left;vertical-align:top}th{background:#edf5f4}
code{font:0.9em Consolas,monospace;background:#edf3f5;border-radius:4px;padding:2px 5px}pre{white-space:pre-wrap;background:#edf3f5;padding:18px;border-radius:8px}
img{display:block;max-width:100%;max-height:700px;width:auto;height:auto;margin:26px auto;border:1px solid #dde6ec;border-radius:10px}
blockquote{border-left:4px solid var(--accent);margin:18px 0;padding:8px 20px;background:#f2f8f7}footer{font-size:13px;color:#637d89;margin-top:40px}
@media(max-width:650px){main{margin:12px;padding:24px 18px}h1{font-size:27px}nav ol{columns:1}td,th{padding:8px}header{padding:20px}}
@media print{body{background:white;font-size:11pt}header{background:white;color:var(--ink);padding:0}header span{color:#555}main{margin:0;padding:0;box-shadow:none}nav{display:none}h2,h3{break-after:avoid}tr,img{break-inside:avoid}img{max-height:550px}a{color:inherit}}
</style></head><body>
<header><strong>DESKPLAN</strong><span>离线桌面计划 · 本手册可直接保存、离线阅读或打印</span></header>
<main><nav aria-label="目录"><strong>阅读目录</strong><ol>$toc</ol></nav>$body
<footer>DeskPlan · 文档随 v$version 发布。截图为演示数据。软件与项目文档采用 MIT 许可证。</footer></main>
</body></html>
"@
    Set-Content -LiteralPath (Join-Path $Destination $manual.Output) -Value $html -Encoding utf8
}
