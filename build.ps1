<#
  一键出安装包:清理 -> 多文件发布 -> 签名 exe -> 打安装包 -> 签名安装包 -> 输出 sha256
  用法:  powershell -ExecutionPolicy Bypass -File build.ps1

  你只需关心最后打印的那一个安装包文件(installer\out\...）。
  bin\ obj\ publish\ 都是自动生成的中间产物,不用管(已 gitignore)。
#>
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Push-Location $root
try {
    Write-Host "[1/5] 清理旧产物" -ForegroundColor Cyan
    'bin', 'obj', 'publish', 'installer\out' | ForEach-Object { Remove-Item -Recurse -Force $_ -ErrorAction SilentlyContinue }

    Write-Host "[2/5] 多文件发布 -> publish\" -ForegroundColor Cyan
    dotnet publish BnetSwitch.csproj -c Release -r win-x64 --self-contained false -o publish -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }
    Remove-Item publish\*.pdb -ErrorAction SilentlyContinue

    Write-Host "[3/5] 签名 exe" -ForegroundColor Cyan
    $signScript = Join-Path $root "codesign\sign.ps1"
    if (Test-Path $signScript) {
        & $signScript -Files "publish\BnetSwitch.exe" | Out-Null
    } else {
        Write-Host "  (无 codesign\sign.ps1,跳过签名 —— 开源构建将产出未签名安装包)" -ForegroundColor Yellow
    }

    Write-Host "[4/5] 打安装包" -ForegroundColor Cyan
    $iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) { throw "找不到 ISCC.exe: $iscc" }
    & $iscc installer\app.iss | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ISCC 打包失败" }

    Write-Host "[5/5] 签名安装包 + 算 sha256" -ForegroundColor Cyan
    $setup = Get-ChildItem installer\out\*.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (Test-Path $signScript) {
        & $signScript -Files $setup.FullName | Out-Null
    } else {
        Write-Host "  (跳过签名)" -ForegroundColor Yellow
    }
    $sha = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLower()

    Write-Host ""
    Write-Host "==== 完成 ====" -ForegroundColor Green
    Write-Host ("安装包 : " + $setup.FullName)
    Write-Host ("大小   : " + [math]::Round($setup.Length / 1MB, 2) + " MB")
    Write-Host ("sha256 : " + $sha)
}
finally { Pop-Location }
