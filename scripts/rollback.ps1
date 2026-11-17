# Reverte o código de src\ para uma tag anterior escolhida, atualiza a versão para uma nova tag
# (para o auto-updater entregar o rollback aos usuários), compila para validar, faz commit/tag/push
# e opcionalmente marca uma release problemática como pré-lançamento no GitHub.
#
# Uso:
#   .\scripts\rollback.ps1                                   # modo interativo (lista tags)
#   .\scripts\rollback.ps1 -TargetTag v3.2.0                 # gera próxima tag automática
#   .\scripts\rollback.ps1 -TargetTag v3.2.0 -NewTag v3.2.3 -MarkBadTag v3.2.2

param(
    [string]$TargetTag,
    [string]$NewTag,
    [string]$MarkBadTag,
    [switch]$DryRun,
    [switch]$NoPush,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -ne 'main') {
        throw "O rollback deve ser executado na branch 'main' (atual: '$branch')."
    }

    $dirty = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw "Falha ao verificar status do git." }
    if ($dirty -and -not $DryRun) {
        throw "O repositório tem alterações locais não commitadas. Faça commit ou stash antes de rodar rollback."
    }

    if (-not $DryRun -and -not $NoPush) {
        Write-Host "Sincronizando main e tags com origin..."
        git fetch origin main --tags --quiet
        if ($LASTEXITCODE -ne 0) { throw "Falha ao sincronizar com origin." }
        $behind = (git rev-list --count "HEAD..origin/main").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Falha ao comparar HEAD com origin/main." }
        if ([int]$behind -gt 0) {
            throw "Sua branch local 'main' está $behind commit(s) atrás de origin/main. Execute 'git pull' antes."
        }
        $ahead = (git rev-list --count "origin/main..HEAD").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Falha ao comparar origin/main com HEAD." }
        if ([int]$ahead -gt 0) {
            throw "Sua branch local 'main' tem $ahead commit(s) ainda não enviados para origin/main. Faça push ou revise antes do rollback."
        }
    }

    $allTags = @(git tag --list "v*" | Where-Object { $_ -match '^v(\d+)\.(\d+)(?:\.(\d+))?$' })
    if ($LASTEXITCODE -ne 0) { throw "Falha ao listar tags git." }
    $parsedTags = @($allTags | ForEach-Object {
        $verText = $_.Substring(1)
        if ($verText -notmatch '^\d+\.\d+\.\d+$') { $verText = "$verText.0" }
        [PSCustomObject]@{ Tag = $_; Ver = [Version]$verText }
    } | Sort-Object Ver -Descending)

    if ($parsedTags.Count -eq 0) {
        throw "Nenhuma tag v* encontrada no repositório."
    }

    if ([string]::IsNullOrWhiteSpace($TargetTag)) {
        Write-Host "`nTags disponíveis para rollback:" -ForegroundColor Cyan
        $top = @($parsedTags | Select-Object -First 15)
        for ($i = 0; $i -lt $top.Count; $i++) {
            $msg = git log -1 --format="%cs %s" $top[$i].Tag
            Write-Host ("  [{0}] {1,-8} - {2}" -f ($i + 1), $top[$i].Tag, $msg)
        }
        $choice = Read-Host "`nDigite o número ou o nome exato da tag de destino (ex.: $($top[0].Tag))"
        if ($choice -match '^\d+$') {
            $idx = [int]$choice - 1
            if ($idx -lt 0 -or $idx -ge $top.Count) { throw "Opção inválida." }
            $TargetTag = $top[$idx].Tag
        } else {
            $TargetTag = $choice.Trim()
        }
    }
    if (-not $TargetTag.StartsWith('v')) { $TargetTag = "v$TargetTag" }
    $existingTarget = git tag --list $TargetTag
    if ($LASTEXITCODE -ne 0) { throw "Falha ao consultar tag '$TargetTag'." }
    if (-not $existingTarget) { throw "Tag '$TargetTag' não encontrada." }

    $highestVer = $parsedTags[0].Ver
    if ([string]::IsNullOrWhiteSpace($NewTag)) {
        $NewTag = "v{0}.{1}.{2}" -f $highestVer.Major, $highestVer.Minor, ($highestVer.Build + 1)
    }
    if (-not $NewTag.StartsWith('v')) { $NewTag = "v$NewTag" }
    $newVer = $NewTag.TrimStart('v')
    if ($newVer -notmatch '^\d+\.\d+\.\d+$') {
        throw "NewTag precisa seguir o formato vX.Y.Z (ex.: v3.2.3). Recebido: $NewTag"
    }
    if ([Version]$newVer -le $highestVer) {
        throw "NewTag ($NewTag) precisa ser estritamente maior que a maior tag existente (v$highestVer) para o auto-updater aplicar o rollback."
    }
    $existingNew = git tag --list $NewTag
    if ($LASTEXITCODE -ne 0) { throw "Falha ao consultar tag '$NewTag'." }
    if ($existingNew) { throw "A tag '$NewTag' já existe." }

    if (-not [string]::IsNullOrWhiteSpace($MarkBadTag) -and -not $MarkBadTag.StartsWith('v')) {
        $MarkBadTag = "v$MarkBadTag"
    }

    Write-Host "`nPlano de Rollback:" -ForegroundColor Yellow
    Write-Host "  Restaurar código (src\) de : $TargetTag"
    Write-Host "  Publicar como nova versão   : $NewTag (Program.Version = $newVer)"
    if ($MarkBadTag) { Write-Host "  Marcar como prerelease      : $MarkBadTag" }
    Write-Host "  Push atômico para origin    : $(-not $NoPush)"

    if ($DryRun) {
        Write-Host "`n[DryRun] Nenhuma alteração foi feita." -ForegroundColor Green
        return
    }

    if (-not $Force) {
        $confirm = Read-Host "`nConfirmar rollback para $TargetTag como $NewTag? (s/N)"
        if ($confirm -notin @('s', 'S', 'sim', 'SIM')) {
            Write-Host "Cancelado."
            return
        }
    }

    $restoredOk = $false
    try {
        # Substitui apenas src\ mantendo scripts\rollback.ps1 e .github\workflows\ intactos
        git rm -rf --ignore-unmatch src | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Falha ao limpar src\ antes do checkout." }
        git checkout $TargetTag -- src
        if ($LASTEXITCODE -ne 0) { throw "Falha ao restaurar src\ de $TargetTag." }

        $appCs = Join-Path $repoRoot "src\App.cs"
        if (-not (Test-Path $appCs)) { throw "src\App.cs não encontrado em $TargetTag." }
        $content = [System.IO.File]::ReadAllText($appCs, [System.Text.Encoding]::UTF8)
        $content = [regex]::Replace($content, '\[assembly:\s*AssemblyVersion\("[^"]+"\)\]', "[assembly: AssemblyVersion(`"$newVer.0`")]")
        $content = [regex]::Replace($content, '\[assembly:\s*AssemblyFileVersion\("[^"]+"\)\]', "[assembly: AssemblyFileVersion(`"$newVer.0`")]")
        $content = [regex]::Replace($content, '(public\s+const\s+string\s+Version\s*=\s*")[^"]+(")', "`${1}$newVer`${2}")
        [System.IO.File]::WriteAllText($appCs, $content, (New-Object System.Text.UTF8Encoding $false))

        Write-Host "Compilando código restaurado para validar..." -ForegroundColor Cyan
        & (Join-Path $repoRoot "src\build.ps1") -Target All
        if ($LASTEXITCODE -ne 0) { throw "build.ps1 falhou ($LASTEXITCODE)." }

        git add -A src
        if ($LASTEXITCODE -ne 0) { throw "git add falhou ($LASTEXITCODE)." }
        git commit -m "$NewTag`: Rollback do código para $TargetTag"
        if ($LASTEXITCODE -ne 0) { throw "git commit falhou ($LASTEXITCODE)." }
        git tag $NewTag
        if ($LASTEXITCODE -ne 0) { throw "git tag falhou ($LASTEXITCODE)." }
        $restoredOk = $true
    }
    finally {
        if (-not $restoredOk) {
            Write-Warning "Revertendo alterações parciais em src\..."
            git reset --hard HEAD | Out-Null
        }
    }

    if (-not $NoPush) {
        git push --atomic origin main $NewTag
        if ($LASTEXITCODE -ne 0) { throw "git push --atomic falhou ($LASTEXITCODE)." }
        Write-Host "Push concluído! Release $NewTag será publicada pelo GitHub Actions." -ForegroundColor Green
        if ($MarkBadTag -and (Get-Command gh -ErrorAction SilentlyContinue)) {
            gh release edit $MarkBadTag --prerelease
            if ($LASTEXITCODE -eq 0) {
                Write-Host "Release $MarkBadTag marcada como prerelease." -ForegroundColor Yellow
            } else {
                Write-Warning "gh release edit retornou código $LASTEXITCODE para $MarkBadTag."
            }
        }
    } else {
        Write-Host "Commit e tag $NewTag criados localmente (-NoPush ativo)." -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
