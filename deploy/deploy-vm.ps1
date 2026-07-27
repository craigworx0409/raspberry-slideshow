[CmdletBinding()] param([string]$SshHost='root@YOUR-VM-IP',[switch]$SkipPublish)
$ErrorActionPreference='Stop'
# ---- per-app config (the only lines that differ between sites) ----
$Service='slideshow'; $Project='server/SlideshowServer.csproj'; $Dll='SlideshowServer.dll'; $Port=5002; $HealthPath='/health'
# -------------------------------------------------------------------
$RepoRoot=Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$PublishDir=Join-Path $RepoRoot 'publish-linux'
$ssh="$env:SystemRoot\System32\OpenSSH\ssh.exe"; $scp="$env:SystemRoot\System32\OpenSSH\scp.exe"
$tar="$env:SystemRoot\System32\tar.exe"   # native bsdtar, not git-bash GNU tar
if(-not $SkipPublish){
  Write-Host '==> dotnet publish (Release, linux-x64, framework-dependent)...' -ForegroundColor Cyan
  & dotnet publish (Join-Path $RepoRoot $Project) -c Release -r linux-x64 --self-contained false -o $PublishDir
  if($LASTEXITCODE -ne 0){throw 'publish failed'} }
if(-not (Test-Path $PublishDir)){throw "no publish dir: $PublishDir"}
Remove-Item "$PublishDir/appsettings.Development.json" -ErrorAction SilentlyContinue
Remove-Item "$PublishDir/app.db*" -ErrorAction SilentlyContinue
Remove-Item "$PublishDir/hub.db*" -ErrorAction SilentlyContinue
Write-Host '==> Packing + shipping...' -ForegroundColor Cyan
$tarPath=Join-Path $env:TEMP "$Service-deploy.tar.gz"
& $tar -czf $tarPath -C $PublishDir .; if($LASTEXITCODE -ne 0){throw 'tar failed'}
& $scp -o BatchMode=yes $tarPath "${SshHost}:/tmp/$Service-deploy.tar.gz"; if($LASTEXITCODE -ne 0){throw 'scp failed'}
Remove-Item $tarPath -ErrorAction SilentlyContinue
Write-Host '==> Swapping app dir + restarting...' -ForegroundColor Cyan
$remote=
  "set -e; rm -rf /srv/$Service/app.new; mkdir -p /srv/$Service/app.new; " +
  "tar -xzf /tmp/$Service-deploy.tar.gz -C /srv/$Service/app.new; rm /tmp/$Service-deploy.tar.gz; " +
  "systemctl stop $Service || true; rm -rf /srv/$Service/app.old; " +
  "if [ -d /srv/$Service/app ]; then mv /srv/$Service/app /srv/$Service/app.old; fi; " +
  "mv /srv/$Service/app.new /srv/$Service/app; chown -R ${Service}:${Service} /srv/$Service/app; " +
  "systemctl start $Service; echo -n 'service: '; systemctl is-active $Service; " +
  'code=000; for i in $(seq 1 30); do code=$(curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:' + "$Port$HealthPath" + ' || true); if [ "$code" = "200" ] || [ "$code" = "302" ]; then break; fi; sleep 2; done; ' +
  'echo "http:    $code"; if [ "$code" != "200" ] && [ "$code" != "302" ]; then exit 1; fi'
& $ssh -o BatchMode=yes $SshHost $remote; if($LASTEXITCODE -ne 0){throw 'remote swap failed'}
Write-Host '==> Deploy OK.' -ForegroundColor Green
